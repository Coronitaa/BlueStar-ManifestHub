using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Record representing an ingestion provider or data source.
/// </summary>
public sealed class SourceRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = "DepotBox";

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; } = true;

    [JsonPropertyName("priority")]
    public int Priority { get; init; } = 100;

    [JsonPropertyName("lastSync")]
    public DateTimeOffset LastSync { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("config")]
    public IReadOnlyDictionary<string, string> Config { get; init; } = new Dictionary<string, string>();
}
