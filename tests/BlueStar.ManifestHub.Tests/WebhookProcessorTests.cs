using BlueStar.ManifestHub.Core.Webhooks;

namespace BlueStar.ManifestHub.Tests;

public sealed class WebhookProcessorTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _stateFilePath;

    public WebhookProcessorTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("webhook_test_");
        _stateFilePath = Path.Combine(_testDir, "processed_webhooks.json");
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public async Task ProcessGameAdded_ValidPayload_Succeeds()
    {
        var processor = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = _stateFilePath
        });

        var payload = new GameAddedWebhookPayload
        {
            EventId = "evt_add_001",
            EventType = "game.added",
            AppId = 730,
            Name = "Counter-Strike 2",
            DepotIds = new[] { 731u, 732u }
        };

        var result = await processor.ProcessGameAddedAsync(payload);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsDuplicate);
        Assert.Equal("evt_add_001", result.EventId);
        Assert.Equal(730u, result.AppId);
        Assert.True(await processor.IsEventProcessedAsync("evt_add_001"));
    }

    [Fact]
    public async Task ProcessGameUpdated_ValidPayload_Succeeds()
    {
        var processor = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = _stateFilePath
        });

        var payload = new GameUpdatedWebhookPayload
        {
            EventId = "evt_upd_001",
            EventType = "game.updated",
            AppId = 440,
            Name = "Team Fortress 2",
            BuildId = 1234567,
            UpdatedDepots = new[]
            {
                new DepotUpdateItem { DepotId = 441, ManifestId = 987654321UL }
            }
        };

        var result = await processor.ProcessGameUpdatedAsync(payload);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsDuplicate);
        Assert.Equal("evt_upd_001", result.EventId);
        Assert.Equal(440u, result.AppId);
        Assert.True(await processor.IsEventProcessedAsync("evt_upd_001"));
    }

    [Fact]
    public async Task Idempotency_DuplicateEventId_IsSafelyDiscarded()
    {
        var processor = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = _stateFilePath
        });

        var payload = new GameAddedWebhookPayload
        {
            EventId = "evt_dup_001",
            EventType = "game.added",
            AppId = 570,
            Name = "Dota 2"
        };

        var firstResult = await processor.ProcessGameAddedAsync(payload);
        Assert.True(firstResult.IsSuccess);
        Assert.False(firstResult.IsDuplicate);

        // Process same event again
        var secondResult = await processor.ProcessGameAddedAsync(payload);
        Assert.True(secondResult.IsSuccess);
        Assert.True(secondResult.IsDuplicate);
        Assert.Equal("evt_dup_001", secondResult.EventId);
    }

    [Fact]
    public async Task Rejection_InvalidPayloads_ReturnsFailure()
    {
        var processor = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = _stateFilePath
        });

        // 1. Missing EventId
        var noIdResult = await processor.ProcessGameAddedAsync(new GameAddedWebhookPayload
        {
            EventId = "",
            AppId = 730,
            Name = "CS2"
        });
        Assert.False(noIdResult.IsSuccess);
        Assert.Contains("EventId", noIdResult.Message);

        // 2. Invalid AppId (0)
        var zeroAppResult = await processor.ProcessGameAddedAsync(new GameAddedWebhookPayload
        {
            EventId = "evt_bad_app",
            AppId = 0,
            Name = "CS2"
        });
        Assert.False(zeroAppResult.IsSuccess);
        Assert.Contains("AppId", zeroAppResult.Message);

        // 3. Empty Game Name for added game
        var noNameResult = await processor.ProcessGameAddedAsync(new GameAddedWebhookPayload
        {
            EventId = "evt_bad_name",
            AppId = 730,
            Name = "  "
        });
        Assert.False(noNameResult.IsSuccess);
        Assert.Contains("Name", noNameResult.Message);

        // 4. Invalid AppId for updated game
        var badUpdateResult = await processor.ProcessGameUpdatedAsync(new GameUpdatedWebhookPayload
        {
            EventId = "evt_bad_upd",
            AppId = 0
        });
        Assert.False(badUpdateResult.IsSuccess);
        Assert.Contains("AppId", badUpdateResult.Message);
    }

    [Fact]
    public async Task ProcessRawJson_ParsesAndDispatchesCorrectly()
    {
        var processor = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = _stateFilePath
        });

        // Valid raw game.added JSON
        string jsonAdded = """
        {
            "eventId": "evt_raw_001",
            "eventType": "game.added",
            "appId": 105600,
            "name": "Terraria"
        }
        """;

        var resAdded = await processor.ProcessRawJsonAsync(jsonAdded);
        Assert.True(resAdded.IsSuccess);
        Assert.False(resAdded.IsDuplicate);
        Assert.Equal(105600u, resAdded.AppId);

        // Malformed JSON
        var resMalformed = await processor.ProcessRawJsonAsync("{ not valid json");
        Assert.False(resMalformed.IsSuccess);
        Assert.Contains("Malformed", resMalformed.Message);

        // Empty body
        var resEmpty = await processor.ProcessRawJsonAsync("");
        Assert.False(resEmpty.IsSuccess);

        // Unsupported eventType
        string jsonUnknown = """
        {
            "eventId": "evt_raw_unk",
            "eventType": "unknown.custom.event",
            "appId": 105600
        }
        """;
        var resUnknown = await processor.ProcessRawJsonAsync(jsonUnknown);
        Assert.False(resUnknown.IsSuccess);
        Assert.Contains("Unsupported", resUnknown.Message);
    }

    [Fact]
    public async Task IdempotencyPersistence_SurvivesAcrossProcessorInstances()
    {
        var proc1 = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = _stateFilePath
        });

        await proc1.ProcessGameAddedAsync(new GameAddedWebhookPayload
        {
            EventId = "evt_persistent_999",
            AppId = 730,
            Name = "Counter-Strike 2"
        });

        // Create second processor pointing to same state file
        var proc2 = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = _stateFilePath
        });

        Assert.True(await proc2.IsEventProcessedAsync("evt_persistent_999"));

        // Duplicate delivery on new processor instance
        var duplicateResult = await proc2.ProcessGameAddedAsync(new GameAddedWebhookPayload
        {
            EventId = "evt_persistent_999",
            AppId = 730,
            Name = "Counter-Strike 2"
        });

        Assert.True(duplicateResult.IsSuccess);
        Assert.True(duplicateResult.IsDuplicate);
    }

    [Fact]
    public async Task ConcurrentInstances_SynchronizeCorrectlyWithoutCorruption()
    {
        int concurrentWorkers = 5;
        int eventsPerWorker = 10;

        var tasks = Enumerable.Range(0, concurrentWorkers).Select(workerId => Task.Run(async () =>
        {
            var processor = new WebhookProcessor(options: new WebhookProcessorOptions
            {
                ProcessedEventsFilePath = _stateFilePath
            });

            for (int i = 0; i < eventsPerWorker; i++)
            {
                var payload = new GameAddedWebhookPayload
                {
                    EventId = $"evt_worker_{workerId}_{i}",
                    AppId = 730,
                    Name = $"Worker {workerId} Game {i}"
                };

                var res = await processor.ProcessGameAddedAsync(payload);
                Assert.True(res.IsSuccess);
            }
        }));

        await Task.WhenAll(tasks);

        var verifyProcessor = new WebhookProcessor(options: new WebhookProcessorOptions
        {
            ProcessedEventsFilePath = _stateFilePath
        });

        for (int workerId = 0; workerId < concurrentWorkers; workerId++)
        {
            for (int i = 0; i < eventsPerWorker; i++)
            {
                string eventId = $"evt_worker_{workerId}_{i}";
                Assert.True(await verifyProcessor.IsEventProcessedAsync(eventId));
            }
        }
    }
}
