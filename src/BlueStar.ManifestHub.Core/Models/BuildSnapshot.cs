using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Record representing an immutable snapshot of an application build at a point in time.
/// </summary>
public sealed class BuildSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("buildId")]
    public ulong BuildId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Authentic manifest/build creation timestamp when known; strictly null if absent.
    /// UtcNow MUST NOT be used as a proxy for creation time.
    /// </summary>
    [JsonPropertyName("creationTime")]
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>
    /// Certainty level of the date: Verified (from Authentic Valve protobuf), Estimated, or Unknown.
    /// </summary>
    [JsonPropertyName("dateCertainty")]
    public DateCertainty DateQuality { get; init; } = DateCertainty.Unknown;

    /// <summary>
    /// Completeness status of the snapshot depots.
    /// </summary>
    [JsonPropertyName("completeness")]
    public SnapshotCompleteness Completeness { get; init; } = SnapshotCompleteness.Complete;

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "public";

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Mapping of DepotId to ManifestId for this build snapshot.
    /// </summary>
    [JsonPropertyName("depotManifests")]
    public IReadOnlyDictionary<uint, ulong> DepotManifests { get; init; } = new Dictionary<uint, ulong>();

    private readonly string? _patchDate;

    /// <summary>
    /// Patch release date string (YYYY-MM-DD) or authentic date identifier.
    /// Derived automatically from CreationTime only when verified.
    /// </summary>
    [JsonPropertyName("patchDate")]
    public string PatchDate
    {
        get
        {
            if (!string.IsNullOrEmpty(_patchDate))
                return _patchDate;

            if (CreationTime.HasValue)
                return CreationTime.Value.UtcDateTime.ToString("yyyy-MM-dd");

            if (Timestamp > DateTimeOffset.UnixEpoch)
                return Timestamp.UtcDateTime.ToString("yyyy-MM-dd");

            return string.Empty;
        }
        init => _patchDate = value;
    }

    /// <summary>
    /// Explicit list of DepotIds whose ManifestId changed or was introduced compared to the chronologically preceding build snapshot.
    /// </summary>
    [JsonPropertyName("changedDepots")]
    public IReadOnlyList<uint> ChangedDepots { get; init; } = Array.Empty<uint>();

    /// <summary>
    /// Display date string for UI/CLI presentation. Displays "Fecha desconocida" / "Unknown date" when no authentic date exists.
    /// </summary>
    [JsonIgnore]
    public string DisplayDate => CreationTime.HasValue
        ? CreationTime.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
        : "Unknown date";

    [JsonIgnore]
    public string DisplayDateEs => CreationTime.HasValue
        ? CreationTime.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
        : "Fecha desconocida";

    public string GetDisplayDate(string lang = "en") =>
        lang.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? DisplayDateEs : DisplayDate;
}
