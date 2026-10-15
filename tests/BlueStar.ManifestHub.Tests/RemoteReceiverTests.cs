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
}
