using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Event generated when a depot change is detected or reported via webhook.
/// </summary>
public sealed class DepotChangeEvent
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("eventId")]
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("appId")]
    public uint? AppId { get; init; }

    [JsonPropertyName("previousManifestId")]
    public ulong? PreviousManifestId { get; init; }

    [JsonPropertyName("newManifestId")]
    public ulong NewManifestId { get; init; }

    [JsonPropertyName("detectedAt")]
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "Webhook";

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
