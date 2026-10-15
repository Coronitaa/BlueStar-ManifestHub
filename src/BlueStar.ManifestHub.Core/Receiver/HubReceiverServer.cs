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

        builder.WebHost.UseUrls($"http://{_options.Host}:{_options.Port}");

        var app = builder.Build();

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
            ContributionSubmission? submission;
            try
            {
                submission = await JsonSerializer.DeserializeAsync<ContributionSubmission>(
                    context.Request.Body,
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

            // 2. Validate IDs
            if (submission.DepotId == 0)
            {
                return Results.BadRequest(new { error = "DepotId must be greater than 0.", isSuccess = false });
            }

            if (submission.ManifestId == 0)
            {
                return Results.BadRequest(new { error = "ManifestId must be greater than 0.", isSuccess = false });
            }

            // 3. Validate Base64 payload
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

            // 4. Validate Steam Protobuf magic header [0xD0, 0x17, 0xF6, 0x71]
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

            // 5. SHA-256 integrity check
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

            // 6. Publish manifest via StorageManager
            var publishResult = await _storageManager.PublishManifestAsync(
                manifestBytes: payloadBytes,
                depotId: submission.DepotId,
                manifestId: submission.ManifestId,
                source: "RemoteContribution",
                contributorPseudonym: submission.AnonymousContributorId,
                expectedSha256: computedSha256,
                cancellationToken: cancellationToken);

            if (!publishResult.IsSuccess)
            {
                return Results.BadRequest(new
                {
                    error = publishResult.Message ?? "Manifest publication failed.",
                    isSuccess = false
                });
            }

            // 7. Directly invoke StardustLedgerManager for confirmed publication with idempotency
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
            }

            // 8. Update builds/{AppId}.json and catalog indices
            if (submission.AppId > 0)
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

                // Update builds/{AppId}.json
                var existingSnapshots = await _storageManager.GetBuildSnapshotsAsync(submission.AppId, cancellationToken);
                ulong buildId = existingSnapshots.Count > 0 ? existingSnapshots.Max(s => s.BuildId) + 1 : 1UL;
                var depotManifests = new Dictionary<uint, ulong>();
                if (existingSnapshots.Count > 0)
                {
                    foreach (var (k, v) in existingSnapshots[0].DepotManifests)
                    {
                        depotManifests[k] = v;
                    }
                }
                depotManifests[submission.DepotId] = submission.ManifestId;

                // Authentic creation timestamp from manifest revision
                DateTimeOffset? authenticTime = publishResult.Revision?.CreationTime;
                if (!authenticTime.HasValue && existingSnapshots.Count > 0)
                {
                    authenticTime = existingSnapshots[0].Timestamp;
                }

                var snapshot = new BuildSnapshot
                {
                    AppId = submission.AppId,
                    BuildId = buildId,
                    Timestamp = authenticTime ?? DateTimeOffset.UnixEpoch,
                    CreationTime = authenticTime,
                    Branch = "public",
                    Description = $"Build snapshot for App {submission.AppId}",
                    DepotManifests = depotManifests
                };
                await _storageManager.SaveBuildSnapshotAsync(snapshot, cancellationToken);

                // Update indices/coverage.json
                var depotRecord = await _storageManager.GetDepotRecordAsync(submission.DepotId, cancellationToken);
                var manifestIds = depotRecord?.ManifestIds ?? new[] { submission.ManifestId };
                await _storageManager.ComputeAndSaveCoverageAsync(submission.AppId, submission.DepotId, manifestIds, cancellationToken);
            }

            // 9. Generate receipt with cryptographic verification hash
            string receiptId = Guid.NewGuid().ToString("N");
            DateTimeOffset receiptTimestamp = DateTimeOffset.UtcNow;
            string verificationHash = PublicationReceipt.ComputeVerificationHash(
                receiptId, submission.AppId, submission.DepotId, submission.ManifestId, receiptTimestamp, computedSha256);

            var receipt = new PublicationReceipt
            {
                ReceiptId = receiptId,
                Status = "Published",
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

        // 6. Catalog index query endpoint
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
    }
}
