using System.Text.Json;
using BlueStar.ManifestHub.Core.Ingestion;
using BlueStar.ManifestHub.Core.Ingestion.Adapters;

namespace BlueStar.ManifestHub.Core.Webhooks;

/// <summary>
/// Configuration options for WebhookProcessor.
/// </summary>
public sealed class WebhookProcessorOptions
{
    public string ProcessedEventsFilePath { get; set; } =
        Path.Combine(Path.GetTempPath(), "bluestar_processed_webhooks.json");

    public int MaxRetainedEventIds { get; set; } = 10_000;
}

/// <summary>
/// Processes incoming webhook events for added and updated games as high-priority signals.
/// Enforces idempotent processing and deduplication by persisting processed event IDs.
/// Rejects malformed or invalid payloads and executes priority ingestion.
/// </summary>
public sealed class WebhookProcessor
{
    private readonly IngestionPipeline? _pipeline;
    private readonly WebhookProcessorOptions _options;
    private readonly HashSet<string> _processedEventIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    public WebhookProcessor(
        IngestionPipeline? pipeline = null,
        WebhookProcessorOptions? options = null)
    {
        _pipeline = pipeline;
        _options = options ?? new WebhookProcessorOptions();
        LoadProcessedEvents();
    }

    /// <summary>
    /// Processes a raw JSON payload string and routes it to the corresponding event handler.
    /// </summary>
    public async Task<WebhookProcessingResult> ProcessRawJsonAsync(
        string rawJson,
        string? eventTypeHeader = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return WebhookProcessingResult.Failure(string.Empty, eventTypeHeader, "Payload body is empty.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            return WebhookProcessingResult.Failure(string.Empty, eventTypeHeader, $"Malformed JSON payload: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            string? eventType = eventTypeHeader;
            if (string.IsNullOrWhiteSpace(eventType) && root.TryGetProperty("eventType", out var etProp))
            {
                eventType = etProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(eventType))
            {
                return WebhookProcessingResult.Failure(string.Empty, null, "Missing required 'eventType' attribute.");
            }

            if (eventType.Equals("game.added", StringComparison.OrdinalIgnoreCase) ||
                eventType.Equals("app.added", StringComparison.OrdinalIgnoreCase))
            {
                GameAddedWebhookPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<GameAddedWebhookPayload>(rawJson);
                }
                catch (Exception ex)
                {
                    return WebhookProcessingResult.Failure(string.Empty, eventType, $"Failed to deserialize game.added payload: {ex.Message}");
                }

                if (payload == null)
                {
                    return WebhookProcessingResult.Failure(string.Empty, eventType, "Payload could not be parsed.");
                }

                return await ProcessGameAddedAsync(payload, cancellationToken);
            }

            if (eventType.Equals("game.updated", StringComparison.OrdinalIgnoreCase) ||
                eventType.Equals("app.updated", StringComparison.OrdinalIgnoreCase))
            {
                GameUpdatedWebhookPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<GameUpdatedWebhookPayload>(rawJson);
                }
                catch (Exception ex)
                {
                    return WebhookProcessingResult.Failure(string.Empty, eventType, $"Failed to deserialize game.updated payload: {ex.Message}");
                }

                if (payload == null)
                {
                    return WebhookProcessingResult.Failure(string.Empty, eventType, "Payload could not be parsed.");
                }

                return await ProcessGameUpdatedAsync(payload, cancellationToken);
            }

            return WebhookProcessingResult.Failure(string.Empty, eventType, $"Unsupported event type '{eventType}'.");
        }
    }

    /// <summary>
    /// Processes a Game Added event. Idempotent by EventId.
    /// </summary>
    public async Task<WebhookProcessingResult> ProcessGameAddedAsync(
        GameAddedWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Validation
        if (string.IsNullOrWhiteSpace(payload.EventId))
        {
            return WebhookProcessingResult.Failure(string.Empty, payload.EventType, "EventId is required and cannot be empty.");
        }

        if (payload.AppId == 0)
        {
            return WebhookProcessingResult.Failure(payload.EventId, payload.EventType, "AppId must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            return WebhookProcessingResult.Failure(payload.EventId, payload.EventType, "Game Name is required and cannot be empty.");
        }

        // Idempotency check
        if (await IsEventProcessedAsync(payload.EventId, cancellationToken))
        {
            return WebhookProcessingResult.Duplicate(payload.EventId, payload.EventType, payload.AppId);
        }

        // Dispatch priority ingestion
        IngestionBatchSummary? summary = null;
        try
        {
            if (_pipeline != null)
            {
                if (payload.Manifests != null && payload.Manifests.Count > 0)
                {
                    var discovered = payload.Manifests.Select(m => new DiscoveredManifest
                    {
                        AppId = payload.AppId,
                        DepotId = m.DepotId,
                        ManifestId = m.ManifestId,
                        SourceId = "WebhookPriority"
                    });
                    summary = await _pipeline.IngestManifestsAsync(discovered, payload.AppId, cancellationToken);
                }
                else
                {
                    summary = await _pipeline.IngestAppAsync(payload.AppId, cancellationToken);
                }
            }

            // Record event on success so failures can be retried by the sender
            await TryRecordEventAsync(payload.EventId, cancellationToken);
        }
        catch (Exception ex)
        {
            return WebhookProcessingResult.Failure(payload.EventId, payload.EventType, $"Pipeline ingestion failed: {ex.Message}");
        }

        return WebhookProcessingResult.Success(
            payload.EventId,
            payload.EventType,
            payload.AppId,
            $"Game added: {payload.Name} ({payload.AppId}) queued for priority ingestion.",
            summary);
    }

    /// <summary>
    /// Processes a Game Updated event. Idempotent by EventId.
    /// </summary>
    public async Task<WebhookProcessingResult> ProcessGameUpdatedAsync(
        GameUpdatedWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Validation
        if (string.IsNullOrWhiteSpace(payload.EventId))
        {
            return WebhookProcessingResult.Failure(string.Empty, payload.EventType, "EventId is required and cannot be empty.");
        }

        if (payload.AppId == 0)
        {
            return WebhookProcessingResult.Failure(payload.EventId, payload.EventType, "AppId must be greater than 0.");
        }

        // Idempotency check
        if (await IsEventProcessedAsync(payload.EventId, cancellationToken))
        {
            return WebhookProcessingResult.Duplicate(payload.EventId, payload.EventType, payload.AppId);
        }

        // Dispatch priority ingestion
        IngestionBatchSummary? summary = null;
        try
        {
            if (_pipeline != null)
            {
                if (payload.UpdatedDepots != null && payload.UpdatedDepots.Count > 0)
                {
                    var discovered = payload.UpdatedDepots.Select(d => new DiscoveredManifest
                    {
                        AppId = payload.AppId,
                        DepotId = d.DepotId,
                        ManifestId = d.ManifestId,
                        SourceId = "WebhookPriority"
                    });
                    summary = await _pipeline.IngestManifestsAsync(discovered, payload.AppId, cancellationToken);
                }
                else
                {
                    summary = await _pipeline.IngestAppAsync(payload.AppId, cancellationToken);
                }
            }

            // Record event on success so failures can be retried by the sender
            await TryRecordEventAsync(payload.EventId, cancellationToken);
        }
        catch (Exception ex)
        {
            return WebhookProcessingResult.Failure(payload.EventId, payload.EventType, $"Pipeline ingestion failed: {ex.Message}");
        }

        return WebhookProcessingResult.Success(
            payload.EventId,
            payload.EventType,
            payload.AppId,
            $"Game update for App {payload.AppId} queued for priority ingestion.",
            summary);
    }

    public async Task<bool> IsEventProcessedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            using (await AcquireCrossProcessLockAsync(cancellationToken))
            {
                SyncFromDisk();
                return _processedEventIds.Contains(eventId);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<bool> TryRecordEventAsync(string eventId, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            using (await AcquireCrossProcessLockAsync(cancellationToken))
            {
                SyncFromDisk();
                if (_processedEventIds.Contains(eventId))
                {
                    return false;
                }

                _processedEventIds.Add(eventId);
                if (_processedEventIds.Count > _options.MaxRetainedEventIds)
                {
                    // Evict oldest if capacity exceeded
                    string first = _processedEventIds.First();
                    _processedEventIds.Remove(first);
                }

                await SaveProcessedEventsAtomicAsync(cancellationToken);
                return true;
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void LoadProcessedEvents()
    {
        SyncFromDisk();
    }

    private void SyncFromDisk()
    {
        if (File.Exists(_options.ProcessedEventsFilePath))
        {
            try
            {
                string json = File.ReadAllText(_options.ProcessedEventsFilePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        foreach (var id in list)
                        {
                            _processedEventIds.Add(id);
                        }
                    }
                }
            }
            catch
            {
                // Best effort sync
            }
        }
    }

    private async Task SaveProcessedEventsAtomicAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_options.ProcessedEventsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = _options.ProcessedEventsFilePath + ".tmp_" + Guid.NewGuid().ToString("N");
            string json = JsonSerializer.Serialize(_processedEventIds.ToList(), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tempFile, json, cancellationToken);
            File.Move(tempFile, _options.ProcessedEventsFilePath, overwrite: true);
        }
        catch
        {
            // Best effort persistence
        }
    }

    private async Task<IDisposable> AcquireCrossProcessLockAsync(CancellationToken cancellationToken)
    {
        string lockFilePath = _options.ProcessedEventsFilePath + ".lock";
        string? dir = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var timeout = TimeSpan.FromSeconds(30);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var fileStream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                return fileStream;
            }
            catch (IOException)
            {
                if (stopwatch.Elapsed > timeout)
                {
                    throw new TimeoutException($"Timed out after {timeout.TotalSeconds}s waiting for webhook cross-process lock: {lockFilePath}");
                }

                await Task.Delay(25, cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
    }
}
