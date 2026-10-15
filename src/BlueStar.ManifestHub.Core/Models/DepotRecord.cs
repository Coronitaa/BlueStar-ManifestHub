using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Record representing a depot associated with an application.
/// </summary>
public sealed class DepotRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("latestManifestId")]
    public ulong? LatestManifestId { get; init; }

    [JsonPropertyName("manifestIds")]
    public IReadOnlyList<ulong> ManifestIds { get; init; } = Array.Empty<ulong>();

    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
}
