using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Record representing manifest coverage metrics for an application or depot.
/// </summary>
public sealed class CoverageRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("totalRevisions")]
    public int TotalRevisions { get; init; }

    [JsonPropertyName("availableRevisions")]
    public int AvailableRevisions { get; init; }

    [JsonPropertyName("missingRevisions")]
    public int MissingRevisions { get; init; }

    [JsonPropertyName("coverageRatio")]
    public double CoverageRatio => TotalRevisions > 0 
        ? Math.Round((double)AvailableRevisions / TotalRevisions, 4) 
        : 0.0;

    [JsonPropertyName("latestAvailableManifestId")]
    public ulong? LatestAvailableManifestId { get; init; }

    [JsonPropertyName("lastChecked")]
    public DateTimeOffset LastChecked { get; init; } = DateTimeOffset.UtcNow;
}
