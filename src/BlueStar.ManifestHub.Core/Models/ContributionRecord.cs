using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Record representing an individual contribution submitted by an anonymous contributor.
/// </summary>
public sealed class ContributionRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("contributionId")]
    public string ContributionId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Pseudonymous contributor identifier. SteamID is strictly prohibited.
    /// </summary>
    [JsonPropertyName("contributorPseudonym")]
    public string ContributorPseudonym { get; init; } = string.Empty;

    [JsonPropertyName("depotId")]
    public uint DepotId { get; init; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; init; }

    [JsonPropertyName("submittedAt")]
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("validationStatus")]
    public ManifestStatus ValidationStatus { get; init; } = ManifestStatus.Unverified;

    [JsonPropertyName("stardustPointsAwarded")]
    public int StardustPointsAwarded { get; init; }

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;
}
