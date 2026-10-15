using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Record representing an application tracked by the Manifest Hub.
/// </summary>
public sealed class AppRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("depotIds")]
    public IReadOnlyList<uint> DepotIds { get; init; } = Array.Empty<uint>();

    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
