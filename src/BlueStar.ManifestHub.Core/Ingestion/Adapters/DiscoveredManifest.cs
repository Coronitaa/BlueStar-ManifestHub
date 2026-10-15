using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Ingestion.Adapters;

/// <summary>
/// Represents a manifest revision discovered from an upstream provider or catalog feed.
/// </summary>
public sealed class DiscoveredManifest
{
    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; init; }

    [JsonPropertyName("expectedSha256")]
    public string? ExpectedSha256 { get; init; }

    [JsonPropertyName("expectedSha1")]
    public string? ExpectedSha1 { get; init; }

    [JsonPropertyName("creationTime")]
    public DateTimeOffset? CreationTime { get; init; }

    [JsonPropertyName("sizeOriginal")]
    public long? SizeOriginal { get; init; }

    [JsonPropertyName("sizeCompressed")]
    public long? SizeCompressed { get; init; }

    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = string.Empty;
}
