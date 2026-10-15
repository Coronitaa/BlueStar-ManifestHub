using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Record representing a specific revision of a depot manifest.
/// </summary>
public sealed class ManifestRevision
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; init; }

    [JsonPropertyName("creationTime")]
    public DateTimeOffset? CreationTime { get; init; }

    [JsonPropertyName("sizeOriginal")]
    public long SizeOriginal { get; init; }

    [JsonPropertyName("sizeCompressed")]
    public long SizeCompressed { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string Sha1 { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public ManifestStatus Status { get; init; } = ManifestStatus.Unverified;

    [JsonPropertyName("ingestedAt")]
    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "Manual";

    [JsonPropertyName("contributorPseudonym")]
    public string ContributorPseudonym { get; init; } = "anonymous";

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
