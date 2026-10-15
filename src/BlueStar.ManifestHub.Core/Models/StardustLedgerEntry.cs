using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Models;

/// <summary>
/// Immutable ledger entry recording Stardust points earned by anonymous contributors.
/// </summary>
public sealed class StardustLedgerEntry
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("transactionId")]
    public string TransactionId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Pseudonymous identity of contributor (e.g., token, pseudonym hash). SteamID is strictly forbidden.
    /// </summary>
    [JsonPropertyName("contributorPseudonym")]
    public string ContributorPseudonym { get; init; } = string.Empty;

    /// <summary>
    /// Key of the validated artifact, formatted as manifest:{depotId}:{manifestId}.
    /// </summary>
    [JsonPropertyName("artifactKey")]
    public string ArtifactKey { get; init; } = string.Empty;

    [JsonPropertyName("points")]
    public int Points { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Cryptographic SHA-256 hash chaining to the preceding entry in the ledger.
    /// </summary>
    [JsonPropertyName("previousHash")]
    public string PreviousHash { get; init; } = string.Empty;

    /// <summary>
    /// Cryptographic SHA-256 hash over this entry's fields + previousHash.
    /// </summary>
    [JsonPropertyName("entryHash")]
    public string EntryHash { get; init; } = string.Empty;
}
