using System.Text.Json.Serialization;
using BlueStar.ManifestHub.Core.Ingestion;

namespace BlueStar.ManifestHub.Core.Webhooks;

/// <summary>
/// Pair representing a depot and a specific manifest ID.
/// </summary>
public sealed class DepotManifestPair
{
    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; init; }
}

/// <summary>
/// Detail of an updated depot revision within an app update event.
/// </summary>
public sealed class DepotUpdateItem
{
    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; init; }

    [JsonPropertyName("previousManifestId")]
    public ulong? PreviousManifestId { get; init; }
}

/// <summary>
/// Webhook event payload for newly added games/applications.
/// </summary>
public sealed class GameAddedWebhookPayload
{
    [JsonPropertyName("eventId")]
    public string EventId { get; init; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "game.added";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("depotIds")]
    public IReadOnlyList<uint> DepotIds { get; init; } = Array.Empty<uint>();

    [JsonPropertyName("manifests")]
    public IReadOnlyList<DepotManifestPair>? Manifests { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Webhook event payload for updated games, new builds, or changed depot manifests.
/// </summary>
public sealed class GameUpdatedWebhookPayload
{
    [JsonPropertyName("eventId")]
    public string EventId { get; init; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "game.updated";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("buildId")]
    public ulong? BuildId { get; init; }

    [JsonPropertyName("updatedDepots")]
    public IReadOnlyList<DepotUpdateItem> UpdatedDepots { get; init; } = Array.Empty<DepotUpdateItem>();

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Structured telemetry payload for API usage and audit logs reported back to DepotBox.
/// </summary>
public sealed class ApiUsageLogPayload
{
    [JsonPropertyName("logId")]
    public string LogId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = string.Empty;

    [JsonPropertyName("httpMethod")]
    public string HttpMethod { get; init; } = "GET";

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; } = 200;

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; init; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; init; }

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}

/// <summary>
/// Result returned from processing an incoming webhook event.
/// </summary>
public sealed class WebhookProcessingResult
{
    public bool IsSuccess { get; init; }
    public bool IsDuplicate { get; init; }
    public string EventId { get; init; } = string.Empty;
    public string? EventType { get; init; }
    public uint AppId { get; init; }
    public string? Message { get; init; }
    public IngestionBatchSummary? IngestionSummary { get; init; }

    public static WebhookProcessingResult Success(
        string eventId,
        string eventType,
        uint appId,
        string message = "Processed successfully",
        IngestionBatchSummary? summary = null) =>
        new()
        {
            IsSuccess = true,
            IsDuplicate = false,
            EventId = eventId,
            EventType = eventType,
            AppId = appId,
            Message = message,
            IngestionSummary = summary
        };

    public static WebhookProcessingResult Duplicate(string eventId, string eventType, uint appId) =>
        new()
        {
            IsSuccess = true,
            IsDuplicate = true,
            EventId = eventId,
            EventType = eventType,
            AppId = appId,
            Message = "Duplicate event ignored (idempotent)."
        };

    public static WebhookProcessingResult Failure(string eventId, string? eventType, string error) =>
        new()
        {
            IsSuccess = false,
            IsDuplicate = false,
            EventId = eventId,
            EventType = eventType,
            Message = error
        };
}
