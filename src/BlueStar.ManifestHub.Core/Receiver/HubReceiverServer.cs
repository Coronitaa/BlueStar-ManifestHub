using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueStar.ManifestHub.Core.Ledger;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Serialization;
using BlueStar.ManifestHub.Core.Storage;
using BlueStar.ManifestHub.Core.Validation;
using BlueStar.ManifestHub.Core.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueStar.ManifestHub.Core.Receiver;

/// <summary>
/// Operational Remote Receiver HTTP service for BlueStar Manifest Hub.
/// Exposes endpoints for manifest contribution submissions, Stardust balance queries,
/// HMAC-authenticated webhooks, build snapshots, catalog indices, and health checks.
/// </summary>
public sealed class HubReceiverServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ManifestStorageManager _storageManager;
    private readonly StardustLedgerManager _ledgerManager;
    private readonly WebhookProcessor _webhookProcessor;
    private readonly HubReceiverOptions _options;
    private readonly SlidingWindowRateLimiter _ipRateLimiter;
    private readonly SlidingWindowRateLimiter _contributorRateLimiter;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyedLocks = new();
    private int _boundPort;
    private string _baseUrl = string.Empty;

    public HubReceiverServer(
        ManifestStorageManager storageManager,
        StardustLedgerManager ledgerManager,
        WebhookProcessor webhookProcessor,
        HubReceiverOptions? options = null)
    {
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _ledgerManager = ledgerManager ?? throw new ArgumentNullException(nameof(ledgerManager));
        _webhookProcessor = webhookProcessor ?? throw new ArgumentNullException(nameof(webhookProcessor));
        _options = options ?? new HubReceiverOptions();

        _ipRateLimiter = new SlidingWindowRateLimiter(TimeSpan.FromMinutes(1));
        _contributorRateLimiter = new SlidingWindowRateLimiter(TimeSpan.FromMinutes(1));

        _boundPort = _options.Port;
        _baseUrl = $"http://{_options.Host}:{_boundPort}";

        _app = BuildApplication();
    }

    public HubReceiverServer(
        StorageOptions? storageOptions = null,
        HubReceiverOptions? receiverOptions = null,
        WebhookProcessorOptions? webhookOptions = null)
    {
        var stOptions = storageOptions ?? new StorageOptions();
        var storageManager = new ManifestStorageManager(stOptions);
        var ledgerManager = new StardustLedgerManager(stOptions);

        var wbOptions = webhookOptions ?? new WebhookProcessorOptions();
        if (receiverOptions != null)
        {
            if (!string.IsNullOrEmpty(receiverOptions.WebhookSecretKey))
            {
                wbOptions.SecretKey = receiverOptions.WebhookSecretKey;
            }
            wbOptions.RequireAuthentication = receiverOptions.RequireWebhookAuthentication;
        }

        var webhookProcessor = new WebhookProcessor(options: wbOptions);

        _storageManager = storageManager;
        _ledgerManager = ledgerManager;
        _webhookProcessor = webhookProcessor;
        _options = receiverOptions ?? new HubReceiverOptions();

        _ipRateLimiter = new SlidingWindowRateLimiter(TimeSpan.FromMinutes(1));
        _contributorRateLimiter = new SlidingWindowRateLimiter(TimeSpan.FromMinutes(1));

        _boundPort = _options.Port;
        _baseUrl = $"http://{_options.Host}:{_boundPort}";

        _app = BuildApplication();
    }

    public int Port => _boundPort;
    public string BaseUrl => _baseUrl;
    public WebApplication App => _app;
    public ManifestStorageManager StorageManager => _storageManager;
    public StardustLedgerManager LedgerManager => _ledgerManager;
    public WebhookProcessor WebhookProcessor => _webhookProcessor;

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();

        // Reduce log verbosity during runs/tests
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            // Allow unlimited Kestrel-level request body so that our custom
            // size-checking middleware and endpoints can cleanly return HTTP 413 Payload Too Large
            serverOptions.Limits.MaxRequestBodySize = null;
        });

        builder.WebHost.UseUrls($"http://{_options.Host}:{_options.Port}");

        var app = builder.Build();

        // Security Middleware 1: IP-based sliding window rate limiting
        app.Use(async (context, next) =>
        {
            // Health checks bypass rate limiting to prevent load balancers dropping healthy nodes
            if (context.Request.Path == "/health" || context.Request.Path == "/api/v1/status")
            {
                await next();
                return;
            }

            string clientIp = GetClientIp(context);
            if (!_ipRateLimiter.TryAcquire(clientIp, _options.MaxRequestsPerMinutePerIp, out int retryAfterSeconds))
            {
                context.Response.Headers.Append("Retry-After", retryAfterSeconds.ToString());
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new
                    {
                        error = $"Rate limit exceeded for IP '{clientIp}'. Maximum {_options.MaxRequestsPerMinutePerIp} requests per minute allowed.",
                        isSuccess = false
                    }));
                return;
            }

            await next();
        });

        // Security Middleware 2: Content-Length payload size enforcement
        app.Use(async (context, next) =>
        {
            if (context.Request.ContentLength.HasValue &&
                context.Request.ContentLength.Value > _options.MaxPayloadSizeBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new
                    {
                        error = $"Request payload exceeds maximum allowed size of {_options.MaxPayloadSizeBytes} bytes.",
                        isSuccess = false
                    }));
                return;
            }

            await next();
        });

        MapEndpoints(app);

        return app;
    }

    private void MapEndpoints(WebApplication app)
    {
        // 1. Health check & status
        app.MapGet("/health", () => Results.Ok(new HealthStatusResponse()));
        app.MapGet("/api/v1/status", () => Results.Ok(new HealthStatusResponse()));

        // 2. Contributions publication endpoint
        app.MapPost("/api/v1/contributions/publish", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            // Stream size validation
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[65536];
            int bytesRead;
            long totalBytes = 0;

            while ((bytesRead = await context.Request.Body.ReadAsync(chunk, 0, chunk.Length, cancellationToken)) > 0)
            {
                totalBytes += bytesRead;
                if (totalBytes > _options.MaxPayloadSizeBytes)
                {
                    return Results.Json(new
                    {
                        error = $"Request payload exceeds maximum allowed size of {_options.MaxPayloadSizeBytes} bytes.",
                        isSuccess = false
                    }, statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                buffer.Write(chunk, 0, bytesRead);
            }

            buffer.Position = 0;
            ContributionSubmission? submission;
            try
            {
                submission = await JsonSerializer.DeserializeAsync<ContributionSubmission>(
                    buffer,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON payload: {ex.Message}", isSuccess = false });
            }

            if (submission == null)
            {
                return Results.BadRequest(new { error = "Submission payload is empty.", isSuccess = false });
            }

            // 1. Validate anonymous contributor pseudonym (reject SteamIDs)
            if (string.IsNullOrWhiteSpace(submission.AnonymousContributorId))
            {
                return Results.BadRequest(new { error = "AnonymousContributorId is required.", isSuccess = false });
            }

            try
            {
                StardustLedgerManager.ValidateContributorPseudonym(submission.AnonymousContributorId);
            }
            catch (InvalidContributorIdentityException ex)
            {
                return Results.BadRequest(new { error = ex.Message, isSuccess = false });
            }

            // 2. Anti-spam / Rate limit per Contributor Pseudonym
            if (!_contributorRateLimiter.TryAcquire(submission.AnonymousContributorId, _options.MaxContributionsPerMinutePerContributor, out int retryAfterContributor))
            {
                context.Response.Headers.Append("Retry-After", retryAfterContributor.ToString());
                return Results.Json(new
                {
                    error = $"Rate limit exceeded for contributor '{submission.AnonymousContributorId}'. Maximum {_options.MaxContributionsPerMinutePerContributor} contributions per minute allowed.",
                    isSuccess = false
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            // 3. Validate IDs
            if (submission.DepotId == 0)
            {
                return Results.BadRequest(new { error = "DepotId must be greater than 0.", isSuccess = false });
            }

            if (submission.ManifestId == 0)
            {
                return Results.BadRequest(new { error = "ManifestId must be greater than 0.", isSuccess = false });
            }

            // 4. Validate Base64 payload
            if (string.IsNullOrWhiteSpace(submission.PayloadBase64))
            {
                return Results.BadRequest(new { error = "PayloadBase64 is required.", isSuccess = false });
            }

            byte[] payloadBytes;
            try
            {
                payloadBytes = Convert.FromBase64String(submission.PayloadBase64);
            }
            catch
            {
                return Results.BadRequest(new { error = "Invalid Base64 payload data.", isSuccess = false });
            }

            if (payloadBytes.Length > _options.MaxPayloadSizeBytes)
            {
                return Results.Json(new
                {
                    error = $"Manifest binary payload exceeds maximum allowed size of {_options.MaxPayloadSizeBytes} bytes.",
                    isSuccess = false
                }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            // 5. Validate Steam Protobuf magic header [0xD0, 0x17, 0xF6, 0x71]
            if (payloadBytes.Length < 4 ||
                payloadBytes[0] != 0xD0 ||
                payloadBytes[1] != 0x17 ||
                payloadBytes[2] != 0xF6 ||
                payloadBytes[3] != 0x71)
            {
                return Results.BadRequest(new
                {
                    error = "Invalid Steam manifest binary: magic header [0xD0, 0x17, 0xF6, 0x71] expected.",
                    isSuccess = false
                });
            }

            // 6. SHA-256 integrity check
            byte[] computedHashBytes = SHA256.HashData(payloadBytes);
            string computedSha256 = Convert.ToHexString(computedHashBytes).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(submission.ManifestHashSha256) &&
                !string.Equals(computedSha256, submission.ManifestHashSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    error = $"SHA-256 mismatch. Expected '{submission.ManifestHashSha256}', computed '{computedSha256}'.",
                    isSuccess = false
                });
            }

            // 7. Concurrency lock per depot/manifest
            string artifactLockKey = $"manifest:{submission.DepotId}:{submission.ManifestId}";
            using (await AcquireLockAsync(artifactLockKey, cancellationToken))
            {
                // Replay check & publication via StorageManager
                PublishResult publishResult;
                try
                {
                    publishResult = await _storageManager.PublishManifestAsync(
                        manifestBytes: payloadBytes,
                        depotId: submission.DepotId,
                        manifestId: submission.ManifestId,
                        source: "RemoteContribution",
                        contributorPseudonym: submission.AnonymousContributorId,
                        expectedSha256: computedSha256,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    return Results.Json(new
                    {
                        error = $"Manifest storage publication failed: {ex.Message}",
                        isSuccess = false
                    }, statusCode: StatusCodes.Status500InternalServerError);
                }

                if (!publishResult.IsSuccess)
                {
                    return Results.BadRequest(new
                    {
                        error = publishResult.Message ?? "Manifest publication failed.",
                        isSuccess = false
                    });
                }

                // 8. Directly invoke StardustLedgerManager for confirmed publication with idempotency & replay protection
                int stardustCredited = 0;
                bool isDuplicate = publishResult.IsDuplicate;

                if (!isDuplicate)
                {
                    try
                    {
                        var ledgerEntry = await _ledgerManager.AwardPointsForManifestAsync(
                            contributorPseudonym: submission.AnonymousContributorId,
                            depotId: submission.DepotId,
                            manifestId: submission.ManifestId,
                            status: ManifestStatus.AvailableAndValidated,
                            points: _options.StardustRewardPoints,
                            reason: $"Contribution for depot {submission.DepotId} manifest {submission.ManifestId}",
                            cancellationToken: cancellationToken);

                        stardustCredited = ledgerEntry.Points;
                    }
                    catch (DuplicateArtifactRewardException)
                    {
                        isDuplicate = true;
                        stardustCredited = 0;
                    }
                    catch (Exception ex)
                    {
                        return Results.Json(new
                        {
                            error = $"Ledger reward allocation failed: {ex.Message}",
                            isSuccess = false
                        }, statusCode: StatusCodes.Status500InternalServerError);
                    }
                }

                // 9. Update builds/{AppId}.json and catalog indices (protected by per-app lock)
                if (submission.AppId > 0)
                {
                    using (await AcquireLockAsync($"app:{submission.AppId}", cancellationToken))
                    {
                        // Update indices/apps.json
                        var existingApp = await _storageManager.GetAppRecordAsync(submission.AppId, cancellationToken);
                        var depotSet = new HashSet<uint>(existingApp?.DepotIds ?? Array.Empty<uint>()) { submission.DepotId };
                        var appRecord = new AppRecord
                        {
                            AppId = submission.AppId,
                            Name = existingApp?.Name ?? $"App {submission.AppId}",
                            DepotIds = depotSet.OrderBy(x => x).ToList(),
                            LastUpdated = DateTimeOffset.UtcNow,
                            Metadata = existingApp?.Metadata ?? new Dictionary<string, string>()
                        };
                        await _storageManager.UpdateAppRecordAsync(appRecord, cancellationToken);

                        // Section 6 requirement:
                        // "Los snapshots históricos deben representar builds reales con depots y ManifestGID asociados
                        // según metadatos fiables. No crear builds sintéticos por cada contribución individual.
                        // Distinguir fecha de publicación auténtica, fecha de ingestión y fecha desconocida.
                        // No heredar fechas antiguas ni utilizar UtcNow como fecha de lanzamiento."
                        // Do NOT synthesize fake 1-depot BuildSnapshots on each individual manifest contribution publish.
                        // Update depot records and coverage index accurately instead.
                        // Real build snapshots should only be created when authentic multi-depot build metadata is supplied.
                        if (submission.AuthenticBuildSnapshot != null &&
                            submission.AuthenticBuildSnapshot.DepotManifests != null &&
                            submission.AuthenticBuildSnapshot.DepotManifests.Count > 1)
                        {
                            await _storageManager.SaveBuildSnapshotAsync(submission.AuthenticBuildSnapshot, cancellationToken);
                        }

                        // Update indices/coverage.json
                        var depotRecord = await _storageManager.GetDepotRecordAsync(submission.DepotId, cancellationToken);
                        var manifestIds = depotRecord?.ManifestIds ?? new[] { submission.ManifestId };
                        await _storageManager.ComputeAndSaveCoverageAsync(submission.AppId, submission.DepotId, manifestIds, cancellationToken);
                    }
                }

                // 10. Generate receipt with cryptographic verification hash
                string receiptId = Guid.NewGuid().ToString("N");
                DateTimeOffset receiptTimestamp = DateTimeOffset.UtcNow;
                string verificationHash = PublicationReceipt.ComputeVerificationHash(
                    receiptId, submission.AppId, submission.DepotId, submission.ManifestId, receiptTimestamp, computedSha256);

                string receiptStatus = isDuplicate ? "Duplicate" : "Published";

                var receipt = new PublicationReceipt
                {
                    ReceiptId = receiptId,
                    Status = receiptStatus,
                    IsSuccess = true,
                    StardustCredited = isDuplicate ? 0 : (stardustCredited > 0 ? stardustCredited : _options.StardustRewardPoints),
                    Timestamp = receiptTimestamp,
                    AppId = submission.AppId,
                    DepotId = submission.DepotId,
                    ManifestId = submission.ManifestId,
                    VerificationHash = verificationHash,
                    IsDuplicate = isDuplicate
                };

                return Results.Ok(receipt);
            }
        });

        // 3. Stardust contributor balance query endpoint
        app.MapGet("/api/v1/stardust/balance/{contributorId}", async (string contributorId, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(contributorId))
            {
                return Results.BadRequest(new { error = "Contributor ID cannot be empty.", isSuccess = false });
            }

            try
            {
                StardustLedgerManager.ValidateContributorPseudonym(contributorId);
            }
            catch (InvalidContributorIdentityException ex)
            {
                return Results.BadRequest(new { error = ex.Message, isSuccess = false });
            }

            int balance = await _ledgerManager.GetContributorBalanceAsync(contributorId, cancellationToken);
            return Results.Ok(new ContributorBalanceResponse
            {
                ContributorId = contributorId,
                ConfirmedBalance = balance,
                PendingBalance = 0
            });
        });

        // 4. Webhooks endpoint with HMAC verification
        app.MapPost("/api/v1/webhooks", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            string rawBody = await reader.ReadToEndAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return Results.BadRequest(new { error = "Webhook body is empty.", isSuccess = false });
            }

            string? signature = request.Headers["X-Hub-Signature-256"].FirstOrDefault() 
                                ?? request.Headers["X-Hub-Signature"].FirstOrDefault()
                                ?? request.Headers["X-Signature-256"].FirstOrDefault();

            string? secret = request.Headers["X-Webhook-Secret"].FirstOrDefault() 
                             ?? request.Headers["X-Secret"].FirstOrDefault();

            string? eventType = request.Headers["X-Event-Type"].FirstOrDefault();

            var result = await _webhookProcessor.ProcessEventAsync(
                rawBody,
                eventTypeHeader: eventType,
                signatureHeader: signature,
                secretHeader: secret,
                cancellationToken: cancellationToken);

            if (!result.IsSuccess)
            {
                if (result.Message?.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
                }

                return Results.BadRequest(result);
            }

            return Results.Ok(result);
        });

        // 5. Build snapshot query endpoint
        app.MapGet("/builds/{appId}.json", async (string appId, CancellationToken cancellationToken) =>
        {
            if (!uint.TryParse(appId, out uint numericAppId))
            {
                return Results.BadRequest(new { error = "Invalid AppId." });
            }

            string path = _storageManager.GetBuildSnapshotPath(numericAppId);
            if (!File.Exists(path))
            {
                return Results.NotFound(new { error = $"Build snapshot not found for App {numericAppId}." });
            }

            string json = await File.ReadAllTextAsync(path, cancellationToken);
            return Results.Content(json, "application/json");
        });

        // 6. Catalog index query endpoints
        app.MapGet("/indices/apps.json", async (CancellationToken cancellationToken) =>
        {
            string path = _storageManager.GetAppIndexPath();
            if (!File.Exists(path))
            {
                return Results.NotFound(new { error = "Catalog index apps.json not found." });
            }

            string json = await File.ReadAllTextAsync(path, cancellationToken);
            return Results.Content(json, "application/json");
        });

        app.MapGet("/indices/coverage.json", async (CancellationToken cancellationToken) =>
        {
            string path = _storageManager.GetCoverageIndexPath();
            if (!File.Exists(path))
            {
                return Results.NotFound(new { error = "Catalog index coverage.json not found." });
            }

            string json = await File.ReadAllTextAsync(path, cancellationToken);
            return Results.Content(json, "application/json");
        });

        app.MapGet("/indices/depots.json", async (CancellationToken cancellationToken) =>
        {
            string path = _storageManager.GetDepotIndexPath();
            if (!File.Exists(path))
            {
                return Results.NotFound(new { error = "Catalog index depots.json not found." });
            }

            string json = await File.ReadAllTextAsync(path, cancellationToken);
            return Results.Content(json, "application/json");
        });

        // 7. Bulk / Targeted coverage query endpoint
        app.MapPost("/api/v1/coverage/query", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            CoverageQueryRequest? request = null;
            try
            {
                if (context.Request.ContentLength is > 0 or null)
                {
                    request = await JsonSerializer.DeserializeAsync<CoverageQueryRequest>(
                        context.Request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON payload: {ex.Message}", isSuccess = false });
            }

            var specificPairs = request?.Manifests?.Select(m => (m.DepotId, m.ManifestId)).ToList();
            var results = await _storageManager.QueryExistingManifestsAsync(
                request?.AppIds,
                request?.DepotIds,
                specificPairs,
                cancellationToken);

            var pairsList = new List<CoverageManifestItem>();
            var depotMap = new Dictionary<string, List<ulong>>();

            foreach (var (dId, mId) in results)
            {
                pairsList.Add(new CoverageManifestItem { DepotId = dId, ManifestId = mId });
                string dKey = dId.ToString();
                if (!depotMap.TryGetValue(dKey, out var mList))
                {
                    mList = new List<ulong>();
                    depotMap[dKey] = mList;
                }
                if (!mList.Contains(mId))
                {
                    mList.Add(mId);
                }
            }

            return Results.Ok(new CoverageQueryResponse
            {
                IsSuccess = true,
                ExistingManifests = pairsList,
                DepotManifests = depotMap
            });
        });

        // 8. Authentic multi-depot build snapshot registration endpoint
        app.MapPost("/api/v1/builds/snapshot", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            BuildSnapshot? snapshot;
            try
            {
                snapshot = await JsonSerializer.DeserializeAsync<BuildSnapshot>(
                    context.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON payload: {ex.Message}", isSuccess = false });
            }

            if (snapshot == null || snapshot.AppId == 0 || snapshot.BuildId == 0)
            {
                return Results.BadRequest(new { error = "Valid AppId and BuildId are required.", isSuccess = false });
            }

            if (snapshot.DepotManifests == null || snapshot.DepotManifests.Count <= 1)
            {
                return Results.BadRequest(new
                {
                    error = "Real build snapshots must represent authentic builds with multiple associated depots and ManifestGIDs. Individual depot contributions must not create synthetic single-depot build snapshots.",
                    isSuccess = false
                });
            }

            using (await AcquireLockAsync($"app:{snapshot.AppId}", cancellationToken))
            {
                DateTimeOffset snapshotTimestamp = snapshot.CreationTime ?? (snapshot.Timestamp != DateTimeOffset.UtcNow ? snapshot.Timestamp : DateTimeOffset.UnixEpoch);

                var normalizedSnapshot = new BuildSnapshot
                {
                    AppId = snapshot.AppId,
                    BuildId = snapshot.BuildId,
                    Timestamp = snapshotTimestamp,
                    CreationTime = snapshot.CreationTime,
                    Branch = string.IsNullOrWhiteSpace(snapshot.Branch) ? "public" : snapshot.Branch,
                    Description = snapshot.Description,
                    DepotManifests = snapshot.DepotManifests
                };

                await _storageManager.SaveBuildSnapshotAsync(normalizedSnapshot, cancellationToken);

                var existingApp = await _storageManager.GetAppRecordAsync(snapshot.AppId, cancellationToken);
                var depotSet = new HashSet<uint>(existingApp?.DepotIds ?? Array.Empty<uint>());
                foreach (var d in snapshot.DepotManifests.Keys)
                {
                    depotSet.Add(d);
                }

                var appRecord = new AppRecord
                {
                    AppId = snapshot.AppId,
                    Name = existingApp?.Name ?? $"App {snapshot.AppId}",
                    DepotIds = depotSet.OrderBy(x => x).ToList(),
                    LastUpdated = DateTimeOffset.UtcNow,
                    Metadata = existingApp?.Metadata ?? new Dictionary<string, string>()
                };
                await _storageManager.UpdateAppRecordAsync(appRecord, cancellationToken);

                return Results.Ok(new { isSuccess = true, buildId = snapshot.BuildId, appId = snapshot.AppId });
            }
        });
    }

    /// <summary>
    /// Starts the remote receiver server.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _app.StartAsync(cancellationToken);

        // Resolve assigned port (especially when port 0 is used)
        var serverAddresses = _app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>();
        var address = serverAddresses?.Addresses.FirstOrDefault();
        if (!string.IsNullOrEmpty(address))
        {
            _baseUrl = address.TrimEnd('/');
            if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri))
            {
                _boundPort = uri.Port;
            }
        }
    }

    /// <summary>
    /// Stops the remote receiver server.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _app.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for application shutdown.
    /// </summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        return _app.WaitForShutdownAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        foreach (var sem in _keyedLocks.Values)
        {
            sem.Dispose();
        }
        _keyedLocks.Clear();
    }

    private async Task<IDisposable> AcquireLockAsync(string key, CancellationToken cancellationToken)
    {
        var sem = _keyedLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken);
        return new LockReleaser(sem);
    }

    private sealed class LockReleaser : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        public LockReleaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => _sem.Release();
    }

    private static string GetClientIp(HttpContext context)
    {
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var firstIp = forwardedFor.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstIp))
            {
                return firstIp;
            }
        }

        var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realIp))
        {
            return realIp.Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    }
}

/// <summary>
/// Thread-safe in-memory sliding window rate limiter.
/// </summary>
internal sealed class SlidingWindowRateLimiter
{
    private readonly ConcurrentDictionary<string, List<long>> _tracker = new();
    private readonly TimeSpan _window;

    public SlidingWindowRateLimiter(TimeSpan window)
    {
        _window = window;
    }

    public bool TryAcquire(string key, int limit, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (limit <= 0)
        {
            return true; // Unlimited / disabled
        }

        long nowTicks = Environment.TickCount64;
        long windowMs = (long)_window.TotalMilliseconds;
        long cutoff = nowTicks - windowMs;

        var timestamps = _tracker.GetOrAdd(key, _ => new List<long>());
        lock (timestamps)
        {
            timestamps.RemoveAll(t => t < cutoff);

            if (timestamps.Count >= limit)
            {
                long oldest = timestamps[0];
                long remainingMs = (oldest + windowMs) - nowTicks;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remainingMs / 1000.0));
                return false;
            }

            timestamps.Add(nowTicks);
            return true;
        }
    }
}
