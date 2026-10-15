using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueStar.ManifestHub.Core.Ledger;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Receiver;
using BlueStar.ManifestHub.Core.Serialization;
using BlueStar.ManifestHub.Core.Storage;
using BlueStar.ManifestHub.Core.Webhooks;

namespace BlueStar.ManifestHub.Tests;

public sealed class RemoteReceiverTests : IAsyncLifetime
{
    private readonly string _testDir;
    private readonly ManifestStorageManager _storageManager;
    private readonly StardustLedgerManager _ledgerManager;
    private readonly WebhookProcessor _webhookProcessor;
    private readonly HubReceiverServer _server;
    private readonly HttpClient _client;
    private const string WebhookSecret = "bluestar_test_webhook_secret_key_12345";

    public RemoteReceiverTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("receiver_test_");
        var storageOptions = new StorageOptions { BasePath = _testDir };
        _storageManager = new ManifestStorageManager(storageOptions);
        _ledgerManager = new StardustLedgerManager(storageOptions);

        _webhookProcessor = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = Path.Combine(_testDir, "processed_webhooks.json"),
            SecretKey = WebhookSecret,
            RequireAuthentication = true
        });

        _server = new HubReceiverServer(
            _storageManager,
            _ledgerManager,
            _webhookProcessor,
            new HubReceiverOptions
            {
                Port = 0,
                Host = "127.0.0.1",
                WebhookSecretKey = WebhookSecret,
                RequireWebhookAuthentication = true,
                StardustRewardPoints = 100
            });

        _client = new HttpClient();
    }

    public async Task InitializeAsync()
    {
        await _server.StartAsync();
        _client.BaseAddress = new Uri(_server.BaseUrl);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        await _server.DisposeAsync();
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public async Task HealthAndStatus_ReturnHealthyStatus()
    {
        // 1. /health
        var healthResp = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);
        var healthBody = await healthResp.Content.ReadFromJsonAsync<HealthStatusResponse>();
        Assert.NotNull(healthBody);
        Assert.Equal("Healthy", healthBody.Status);
        Assert.Equal("1.0.0", healthBody.Version);

        // 2. /api/v1/status
        var statusResp = await _client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var statusBody = await statusResp.Content.ReadFromJsonAsync<HealthStatusResponse>();
        Assert.NotNull(statusBody);
        Assert.Equal("Healthy", statusBody.Status);
    }

    [Fact]
    public async Task PublishContribution_ValidManifest_SucceedsAndAwardsStardust()
    {
        uint appId = 730;
        uint depotId = 731;
        ulong manifestId = 1234567890123456UL;
        var creationTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId, timestamp: creationTime);
        string sha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string base64 = Convert.ToBase64String(manifestBytes);

        var submission = new ContributionSubmission
        {
            AnonymousContributorId = "anon_contributor_alpha",
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            ManifestHashSha256 = sha256,
            SanitizationAuditHash = "audit_clean_123",
            ManifestSizeBytes = manifestBytes.Length,
            PayloadBase64 = base64
        };

        var response = await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var receipt = await response.Content.ReadFromJsonAsync<PublicationReceipt>();
        Assert.NotNull(receipt);
        Assert.True(receipt.IsSuccess);
        Assert.Equal("Published", receipt.Status);
        Assert.Equal(100, receipt.StardustCredited);
        Assert.False(receipt.IsDuplicate);
        Assert.Equal(appId, receipt.AppId);
        Assert.Equal(depotId, receipt.DepotId);
        Assert.Equal(manifestId, receipt.ManifestId);
        Assert.False(string.IsNullOrWhiteSpace(receipt.VerificationHash));
        Assert.Equal(receipt.VerificationHash, receipt.CryptographicVerificationHash);

        // Verify manifest binary stored on disk
        string manifestPath = _storageManager.GetManifestPath(depotId, manifestId);
        Assert.True(File.Exists(manifestPath));
        byte[] storedBytes = await File.ReadAllBytesAsync(manifestPath);
        Assert.Equal(manifestBytes, storedBytes);

        // Verify sidecars
        string shaPath = _storageManager.GetShaSidecarPath(depotId, manifestId);
        string metaPath = _storageManager.GetMetaSidecarPath(depotId, manifestId);
        Assert.True(File.Exists(shaPath));
        Assert.True(File.Exists(metaPath));

        var meta = await _storageManager.GetManifestRevisionAsync(depotId, manifestId);
        Assert.NotNull(meta);
        Assert.Equal(creationTime.ToUnixTimeSeconds(), meta.CreationTime?.ToUnixTimeSeconds());
        Assert.Equal("anon_contributor_alpha", meta.ContributorPseudonym);

        // Verify ledger recorded 100 points
        int balance = await _ledgerManager.GetContributorBalanceAsync("anon_contributor_alpha");
        Assert.Equal(100, balance);

        // Verify builds/{AppId}.json created
        string buildPath = _storageManager.GetBuildSnapshotPath(appId);
        Assert.True(File.Exists(buildPath));
        var snapshots = await _storageManager.GetBuildSnapshotsAsync(appId);
        Assert.Single(snapshots);
        Assert.Equal(appId, snapshots[0].AppId);
        Assert.Equal(creationTime.ToUnixTimeSeconds(), snapshots[0].Timestamp.ToUnixTimeSeconds());
        Assert.Equal(manifestId, snapshots[0].DepotManifests[depotId]);

        // Verify indices/apps.json and indices/depots.json
        var appRecord = await _storageManager.GetAppRecordAsync(appId);
        Assert.NotNull(appRecord);
        Assert.Contains(depotId, appRecord.DepotIds);

        var depotRecord = await _storageManager.GetDepotRecordAsync(depotId);
        Assert.NotNull(depotRecord);
        Assert.Contains(manifestId, depotRecord.ManifestIds);
    }

    [Fact]
    public async Task PublishContribution_DuplicateSubmission_IdempotentWithoutDuplicatingPoints()
    {
        uint appId = 440;
        uint depotId = 441;
        ulong manifestId = 888877776666UL;

        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);
        string sha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string base64 = Convert.ToBase64String(manifestBytes);

        var submission = new ContributionSubmission
        {
            AnonymousContributorId = "anon_contributor_beta",
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            ManifestHashSha256 = sha256,
            ManifestSizeBytes = manifestBytes.Length,
            PayloadBase64 = base64
        };

        // First publication
        var firstResp = await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
        var firstReceipt = await firstResp.Content.ReadFromJsonAsync<PublicationReceipt>();
        Assert.NotNull(firstReceipt);
        Assert.Equal(100, firstReceipt.StardustCredited);
        Assert.False(firstReceipt.IsDuplicate);

        int balanceAfterFirst = await _ledgerManager.GetContributorBalanceAsync("anon_contributor_beta");
        Assert.Equal(100, balanceAfterFirst);

        // Duplicate submission
        var secondResp = await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
        Assert.Equal(HttpStatusCode.OK, secondResp.StatusCode);
        var secondReceipt = await secondResp.Content.ReadFromJsonAsync<PublicationReceipt>();
        Assert.NotNull(secondReceipt);
        Assert.True(secondReceipt.IsSuccess);
        Assert.True(secondReceipt.IsDuplicate);
        Assert.Equal("Duplicate", secondReceipt.Status);
        Assert.Equal(0, secondReceipt.StardustCredited); // 0 points on duplicate!

        // Balance must strictly remain 100
        int balanceAfterSecond = await _ledgerManager.GetContributorBalanceAsync("anon_contributor_beta");
        Assert.Equal(100, balanceAfterSecond);

        // Ledger must contain exactly 1 entry for this artifact
        var ledgerEntries = await _ledgerManager.ReadAllEntriesAsync();
        Assert.Single(ledgerEntries);
    }

    [Theory]
    [InlineData("76561198012345678")]
    [InlineData("STEAM_0:1:2345678")]
    [InlineData("[U:1:123456]")]
    [InlineData("steamcommunity.com/id/gamer")]
    [InlineData("user_with_76561198000000000_embedded")]
    public async Task PublishContribution_RejectsSteamIds_PseudonymityEnforced(string steamIdPseudonym)
    {
        uint depotId = 570;
        ulong manifestId = 555544443333UL;
        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);

        var submission = new ContributionSubmission
        {
            AnonymousContributorId = steamIdPseudonym,
            AppId = 570,
            DepotId = depotId,
            ManifestId = manifestId,
            PayloadBase64 = Convert.ToBase64String(manifestBytes)
        };

        var response = await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Steam", body, StringComparison.OrdinalIgnoreCase);

        // Verify nothing stored
        Assert.False(_storageManager.ManifestExists(depotId, manifestId));
    }

    [Fact]
    public async Task PublishContribution_RejectsInvalidMagicHeader()
    {
        uint depotId = 730;
        ulong manifestId = 999111222UL;

        // Byte array with wrong magic header
        byte[] corruptBytes = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var submission = new ContributionSubmission
        {
            AnonymousContributorId = "anon_contributor_gamma",
            AppId = 730,
            DepotId = depotId,
            ManifestId = manifestId,
            PayloadBase64 = Convert.ToBase64String(corruptBytes)
        };

        var response = await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("magic header", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishContribution_RejectsSha256Mismatch()
    {
        uint depotId = 730;
        ulong manifestId = 999333444UL;
        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);

        var submission = new ContributionSubmission
        {
            AnonymousContributorId = "anon_contributor_delta",
            AppId = 730,
            DepotId = depotId,
            ManifestId = manifestId,
            ManifestHashSha256 = "0000000000000000000000000000000000000000000000000000000000000000",
            PayloadBase64 = Convert.ToBase64String(manifestBytes)
        };

        var response = await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SHA-256 mismatch", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStardustBalance_ReturnsConfirmedBalance()
    {
        string contributor = "anon_tester_balance";
        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(731, 10101010UL);

        var submission = new ContributionSubmission
        {
            AnonymousContributorId = contributor,
            AppId = 730,
            DepotId = 731,
            ManifestId = 10101010UL,
            PayloadBase64 = Convert.ToBase64String(manifestBytes)
        };

        await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);

        // Query balance
        var balanceResp = await _client.GetAsync($"/api/v1/stardust/balance/{contributor}");
        Assert.Equal(HttpStatusCode.OK, balanceResp.StatusCode);

        var balanceBody = await balanceResp.Content.ReadFromJsonAsync<ContributorBalanceResponse>();
        Assert.NotNull(balanceBody);
        Assert.Equal(contributor, balanceBody.ContributorId);
        Assert.Equal(100, balanceBody.ConfirmedBalance);
        Assert.Equal(0, balanceBody.PendingBalance);
    }

    [Fact]
    public async Task GetStardustBalance_RejectsSteamIdFormat()
    {
        var response = await _client.GetAsync("/api/v1/stardust/balance/76561198012345678");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_ValidHmacSignature_ProcessesEventSuccessfully()
    {
        var payloadObj = new GameAddedWebhookPayload
        {
            EventId = "evt_wh_001",
            EventType = "game.added",
            AppId = 105600,
            Name = "Terraria",
            DepotIds = new[] { 105601u }
        };

        string json = JsonSerializer.Serialize(payloadObj);
        string signature = WebhookProcessor.ComputeHmacSha256(json, WebhookSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Hub-Signature-256", $"sha256={signature}");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<WebhookProcessingResult>();
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal("evt_wh_001", result.EventId);
    }

    [Fact]
    public async Task Webhook_InvalidHmacSignature_ReturnsUnauthorized()
    {
        var payloadObj = new GameAddedWebhookPayload
        {
            EventId = "evt_wh_002",
            EventType = "game.added",
            AppId = 105600,
            Name = "Terraria"
        };

        string json = JsonSerializer.Serialize(payloadObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Hub-Signature-256", "sha256=invalidhexsignature00000000000000000000000000000000000000000000");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_SecretHeaderAuthentication_Succeeds()
    {
        var payloadObj = new GameAddedWebhookPayload
        {
            EventId = "evt_wh_003",
            EventType = "game.added",
            AppId = 730,
            Name = "Counter-Strike 2"
        };

        string json = JsonSerializer.Serialize(payloadObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Webhook-Secret", WebhookSecret);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<WebhookProcessingResult>();
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Webhook_DuplicateEvent_ProcessesIdempotently()
    {
        var payloadObj = new GameAddedWebhookPayload
        {
            EventId = "evt_wh_duplicate_test",
            EventType = "game.added",
            AppId = 440,
            Name = "Team Fortress 2"
        };

        string json = JsonSerializer.Serialize(payloadObj);
        string sig = WebhookProcessor.ComputeHmacSha256(json, WebhookSecret);

        // First
        using (var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks"))
        {
            req1.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req1.Headers.Add("X-Hub-Signature-256", sig);
            var resp1 = await _client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
            var res1 = await resp1.Content.ReadFromJsonAsync<WebhookProcessingResult>();
            Assert.NotNull(res1);
            Assert.False(res1.IsDuplicate);
        }

        // Second (duplicate)
        using (var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks"))
        {
            req2.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req2.Headers.Add("X-Hub-Signature-256", sig);
            var resp2 = await _client.SendAsync(req2);
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
            var res2 = await resp2.Content.ReadFromJsonAsync<WebhookProcessingResult>();
            Assert.NotNull(res2);
            Assert.True(res2.IsDuplicate);
        }
    }

    [Fact]
    public async Task GetBuildSnapshot_ReturnsSnapshotJson_WhenExists()
    {
        uint appId = 730;
        uint depotId = 731;
        ulong manifestId = 777666555UL;
        byte[] bytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);

        var sub = new ContributionSubmission
        {
            AnonymousContributorId = "anon_build_test",
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            PayloadBase64 = Convert.ToBase64String(bytes)
        };

        await _client.PostAsJsonAsync("/api/v1/contributions/publish", sub);

        // Query /builds/{appId}.json
        var response = await _client.GetAsync($"/builds/{appId}.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        string json = await response.Content.ReadAsStringAsync();
        var snapshots = HubJsonSerializer.Deserialize<List<BuildSnapshot>>(json);
        Assert.NotNull(snapshots);
        Assert.Single(snapshots);
        Assert.Equal(appId, snapshots[0].AppId);
    }

    [Fact]
    public async Task GetBuildSnapshot_ReturnsNotFound_WhenDoesNotExist()
    {
        var response = await _client.GetAsync("/builds/999999.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCatalogIndex_ReturnsAppsJson()
    {
        // Bootstrap app index
        await _storageManager.BootstrapAppIndexIfMissingAsync();

        var response = await _client.GetAsync("/indices/apps.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        string json = await response.Content.ReadAsStringAsync();
        var apps = HubJsonSerializer.Deserialize<Dictionary<string, AppRecord>>(json);
        Assert.NotNull(apps);
        Assert.True(apps.Count >= 4);
        Assert.True(apps.ContainsKey("730"));
    }

    [Fact]
    public async Task RateLimiting_ExceedsRequestThreshold_Returns429TooManyRequests()
    {
        var customDir = TestManifestHelper.CreateTestDirectory("ratelimit_test_");
        try
        {
            var stOptions = new StorageOptions { BasePath = customDir };
            var storage = new ManifestStorageManager(stOptions);
            var ledger = new StardustLedgerManager(stOptions);
            var webhook = new WebhookProcessor();

            var options = new HubReceiverOptions
            {
                Port = 0,
                Host = "127.0.0.1",
                MaxRequestsPerMinutePerIp = 5,
                MaxContributionsPerMinutePerContributor = 3
            };

            await using var server = new HubReceiverServer(storage, ledger, webhook, options);
            await server.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };

            HttpResponseMessage? lastResponse = null;
            // Send requests exceeding contributor threshold (MaxContributionsPerMinutePerContributor = 3)
            for (int i = 0; i < 5; i++)
            {
                ulong manifestId = 1000000UL + (ulong)i;
                byte[] bytes = TestManifestHelper.CreateValidManifest(731, manifestId);
                var submission = new ContributionSubmission
                {
                    AnonymousContributorId = "anon_rate_test",
                    AppId = 730,
                    DepotId = 731,
                    ManifestId = manifestId,
                    PayloadBase64 = Convert.ToBase64String(bytes)
                };

                lastResponse = await client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
            }

            Assert.NotNull(lastResponse);
            Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
            Assert.True(lastResponse.Headers.Contains("Retry-After"));
        }
        finally
        {
            TestManifestHelper.CleanupTestDirectory(customDir);
        }
    }

    [Fact]
    public async Task PublishContribution_ExceedsMaxPayloadSizeBytes_Returns413PayloadTooLarge()
    {
        var customDir = TestManifestHelper.CreateTestDirectory("payload_limit_test_");
        try
        {
            var stOptions = new StorageOptions { BasePath = customDir };
            var storage = new ManifestStorageManager(stOptions);
            var ledger = new StardustLedgerManager(stOptions);
            var webhook = new WebhookProcessor();

            var options = new HubReceiverOptions
            {
                Port = 0,
                Host = "127.0.0.1",
                MaxPayloadSizeBytes = 1024 // 1 KB limit for testing
            };

            await using var server = new HubReceiverServer(storage, ledger, webhook, options);
            await server.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };

            // Generate payload exceeding 1024 bytes
            byte[] largeData = new byte[2048];
            Array.Fill<byte>(largeData, 0x5A);
            byte[] bytes = TestManifestHelper.CreateValidManifest(731, 2000000UL, payload: Encoding.UTF8.GetString(largeData), compress: false);

            var submission = new ContributionSubmission
            {
                AnonymousContributorId = "anon_large_payload",
                AppId = 730,
                DepotId = 731,
                ManifestId = 2000000UL,
                PayloadBase64 = Convert.ToBase64String(bytes)
            };

            var response = await client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        finally
        {
            TestManifestHelper.CleanupTestDirectory(customDir);
        }
    }

    [Fact]
    public async Task ReplayProtection_SameManifestByDifferentContributors_SecondAwardsZeroStardustAndDuplicateStatus()
    {
        uint appId = 730;
        uint depotId = 731;
        ulong manifestId = 333444555UL;
        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);
        string sha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string base64 = Convert.ToBase64String(manifestBytes);

        var sub1 = new ContributionSubmission
        {
            AnonymousContributorId = "anon_contributor_first",
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            ManifestHashSha256 = sha256,
            PayloadBase64 = base64
        };

        var sub2 = new ContributionSubmission
        {
            AnonymousContributorId = "anon_contributor_second",
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            ManifestHashSha256 = sha256,
            PayloadBase64 = base64
        };

        // First submission by contributor 1
        var resp1 = await _client.PostAsJsonAsync("/api/v1/contributions/publish", sub1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var receipt1 = await resp1.Content.ReadFromJsonAsync<PublicationReceipt>();
        Assert.NotNull(receipt1);
        Assert.Equal("Published", receipt1.Status);
        Assert.Equal(100, receipt1.StardustCredited);
        Assert.False(receipt1.IsDuplicate);

        int balance1 = await _ledgerManager.GetContributorBalanceAsync("anon_contributor_first");
        Assert.Equal(100, balance1);

        // Second submission of the same manifest by contributor 2 (replay / duplicate)
        var resp2 = await _client.PostAsJsonAsync("/api/v1/contributions/publish", sub2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var receipt2 = await resp2.Content.ReadFromJsonAsync<PublicationReceipt>();
        Assert.NotNull(receipt2);
        Assert.True(receipt2.IsSuccess);
        Assert.Equal("Duplicate", receipt2.Status);
        Assert.True(receipt2.IsDuplicate);
        Assert.Equal(0, receipt2.StardustCredited);

        int balance2 = await _ledgerManager.GetContributorBalanceAsync("anon_contributor_second");
        Assert.Equal(0, balance2);

        // Total ledger entries must remain exactly 1 for this artifact
        var entries = await _ledgerManager.ReadAllEntriesAsync();
        var artifactEntries = entries.Where(e => e.ArtifactKey == $"manifest:{depotId}:{manifestId}").ToList();
        Assert.Single(artifactEntries);
    }

    [Fact]
    public async Task Concurrency_TenConcurrentSubmissions_MaintainsLedgerConsistencyAndZeroCorruption()
    {
        uint appId = 730;
        uint depotId = 731;
        string contributor = "anon_concurrency_tester";

        var tasks = new Task<HttpResponseMessage>[10];
        for (int i = 0; i < 10; i++)
        {
            ulong manifestId = 5000000UL + (ulong)i;
            byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);
            string sha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();

            var submission = new ContributionSubmission
            {
                AnonymousContributorId = contributor,
                AppId = appId,
                DepotId = depotId,
                ManifestId = manifestId,
                ManifestHashSha256 = sha256,
                PayloadBase64 = Convert.ToBase64String(manifestBytes)
            };

            tasks[i] = Task.Run(async () =>
            {
                using var client = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };
                return await client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
            });
        }

        var responses = await Task.WhenAll(tasks);
        foreach (var resp in responses)
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var receipt = await resp.Content.ReadFromJsonAsync<PublicationReceipt>();
            Assert.NotNull(receipt);
            Assert.True(receipt.IsSuccess);
        }

        // Contributor balance must be exactly 10 * 100 = 1000
        int balance = await _ledgerManager.GetContributorBalanceAsync(contributor);
        Assert.Equal(1000, balance);

        // Verify ledger integrity (cryptographic hash chaining)
        var integrityResult = await _ledgerManager.VerifyIntegrityAsync();
        Assert.True(integrityResult.IsValid, "Stardust ledger hash chain must be strictly valid with zero corruption.");

        var entries = await _ledgerManager.ReadAllEntriesAsync();
        var contributorEntries = entries.Where(e => e.ContributorPseudonym == contributor).ToList();
        Assert.Equal(10, contributorEntries.Count);
    }

    [Fact]
    public async Task Concurrency_ConcurrentDuplicatesOfSameManifest_OnlyOneAwardsPoints()
    {
        uint appId = 730;
        uint depotId = 731;
        ulong manifestId = 6000000UL;
        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);
        string sha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string base64 = Convert.ToBase64String(manifestBytes);

        var tasks = new Task<PublicationReceipt?>[10];
        for (int i = 0; i < 10; i++)
        {
            string contributor = $"anon_concurrent_racer_{i}";
            var submission = new ContributionSubmission
            {
                AnonymousContributorId = contributor,
                AppId = appId,
                DepotId = depotId,
                ManifestId = manifestId,
                ManifestHashSha256 = sha256,
                PayloadBase64 = base64
            };

            tasks[i] = Task.Run(async () =>
            {
                using var client = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };
                var resp = await client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                return await resp.Content.ReadFromJsonAsync<PublicationReceipt>();
            });
        }

        var receipts = await Task.WhenAll(tasks);
        int publishedCount = receipts.Count(r => r != null && r.Status == "Published" && r.StardustCredited == 100 && !r.IsDuplicate);
        int duplicateCount = receipts.Count(r => r != null && r.Status == "Duplicate" && r.StardustCredited == 0 && r.IsDuplicate);

        Assert.Equal(1, publishedCount);
        Assert.Equal(9, duplicateCount);

        var integrityResult = await _ledgerManager.VerifyIntegrityAsync();
        Assert.True(integrityResult.IsValid);
    }

    [Fact]
    public async Task PartialFailure_InvalidManifestData_NoStardustCreditGrantedAndReturnsError()
    {
        string contributor = "anon_invalid_partial";
        var submission = new ContributionSubmission
        {
            AnonymousContributorId = contributor,
            AppId = 730,
            DepotId = 731,
            ManifestId = 7000001UL,
            PayloadBase64 = Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03, 0x04 }) // Invalid magic header
        };

        var response = await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify zero Stardust credited
        int balance = await _ledgerManager.GetContributorBalanceAsync(contributor);
        Assert.Equal(0, balance);

        // Verify ledger has zero entries for this contributor
        var entries = await _ledgerManager.ReadAllEntriesAsync();
        Assert.DoesNotContain(entries, e => e.ContributorPseudonym == contributor);
    }

    [Fact]
    public async Task PartialFailure_DiskError_NoStardustCreditGrantedAndReturns500Error()
    {
        uint depotId = 731;
        ulong manifestId = 7000002UL;
        string contributor = "anon_disk_error_tester";

        // Block manifest file creation by creating a directory with the same name as the target manifest file
        string manifestPath = _storageManager.GetManifestPath(depotId, manifestId);
        Directory.CreateDirectory(manifestPath);

        try
        {
            byte[] bytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);
            var submission = new ContributionSubmission
            {
                AnonymousContributorId = contributor,
                AppId = 730,
                DepotId = depotId,
                ManifestId = manifestId,
                PayloadBase64 = Convert.ToBase64String(bytes)
            };

            var response = await _client.PostAsJsonAsync("/api/v1/contributions/publish", submission);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            // Verify zero Stardust credited
            int balance = await _ledgerManager.GetContributorBalanceAsync(contributor);
            Assert.Equal(0, balance);

            // Verify ledger has zero entries for this contributor
            var entries = await _ledgerManager.ReadAllEntriesAsync();
            Assert.DoesNotContain(entries, e => e.ContributorPseudonym == contributor);
        }
        finally
        {
            if (Directory.Exists(manifestPath))
            {
                Directory.Delete(manifestPath);
            }
        }
    }

    [Fact]
    public async Task PublishContribution_UnknownDate_CreationTimeIsNullAndDoesNotCopyExistingTimestamp()
    {
        uint appId = 730;
        var existingTimestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // 1. Submit first manifest with known authentic timestamp
        byte[] manifest1 = TestManifestHelper.CreateValidManifest(731, 8000001UL, timestamp: existingTimestamp);
        var sub1 = new ContributionSubmission
        {
            AnonymousContributorId = "anon_date_tester_1",
            AppId = appId,
            DepotId = 731,
            ManifestId = 8000001UL,
            PayloadBase64 = Convert.ToBase64String(manifest1)
        };
        var resp1 = await _client.PostAsJsonAsync("/api/v1/contributions/publish", sub1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // 2. Submit second manifest with UNKNOWN date (includeMetadata: false)
        byte[] manifest2 = TestManifestHelper.CreateValidManifest(732, 8000002UL, timestamp: null, includeMetadata: false);
        var sub2 = new ContributionSubmission
        {
            AnonymousContributorId = "anon_date_tester_2",
            AppId = appId,
            DepotId = 732,
            ManifestId = 8000002UL,
            PayloadBase64 = Convert.ToBase64String(manifest2)
        };
        var resp2 = await _client.PostAsJsonAsync("/api/v1/contributions/publish", sub2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        // 3. Inspect build snapshots for App 730
        var snapshots = await _storageManager.GetBuildSnapshotsAsync(appId);
        Assert.Equal(2, snapshots.Count);

        // Find snapshot for manifest2 (BuildId 2)
        var secondSnapshot = snapshots.FirstOrDefault(s => s.BuildId == 2);
        Assert.NotNull(secondSnapshot);

        // Auditor Finding 4 verification:
        // Assert CreationTime is strictly null (not fabricated)
        Assert.Null(secondSnapshot.CreationTime);

        // Assert Timestamp was NOT copied from snapshot 1
        Assert.NotEqual(existingTimestamp, secondSnapshot.Timestamp);
        Assert.Equal(DateTimeOffset.UnixEpoch, secondSnapshot.Timestamp);

        // Assert it did not fabricate a complete build by copying depot 731
        Assert.False(secondSnapshot.DepotManifests.ContainsKey(731));
        Assert.True(secondSnapshot.DepotManifests.ContainsKey(732));
    }
}
