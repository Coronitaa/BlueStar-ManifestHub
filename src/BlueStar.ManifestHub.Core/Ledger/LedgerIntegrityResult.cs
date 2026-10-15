namespace BlueStar.ManifestHub.Core.Ledger;

/// <summary>
/// Result of verifying the cryptographic integrity of the Stardust Ledger.
/// </summary>
public sealed class LedgerIntegrityResult
{
    public bool IsValid { get; init; }
    public int EntryCount { get; init; }
    public string? ErrorMessage { get; init; }
    public string? LastBlockHash { get; init; }

    public static LedgerIntegrityResult Success(int count, string? lastHash) =>
        new()
        {
            IsValid = true,
            EntryCount = count,
            LastBlockHash = lastHash
        };

    public static LedgerIntegrityResult Failure(string errorMessage, int verifiedCount, string? lastHash = null) =>
        new()
        {
            IsValid = false,
            EntryCount = verifiedCount,
            ErrorMessage = errorMessage,
            LastBlockHash = lastHash
        };
}
