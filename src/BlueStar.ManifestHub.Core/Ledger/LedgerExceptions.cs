namespace BlueStar.ManifestHub.Core.Ledger;

/// <summary>
/// Base exception for Stardust Ledger operations.
/// </summary>
public class LedgerException : Exception
{
    public LedgerException(string message) : base(message) { }
    public LedgerException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when contributor identity violates pseudonymity or contains a SteamID.
/// </summary>
public sealed class InvalidContributorIdentityException : LedgerException
{
    public InvalidContributorIdentityException(string message) : base(message) { }
}

/// <summary>
/// Thrown when an attempt is made to award points for an unvalidated or corrupt manifest.
/// </summary>
public sealed class LedgerValidationException : LedgerException
{
    public LedgerValidationException(string message) : base(message) { }
}

/// <summary>
/// Thrown when an artifact has already been rewarded in the ledger.
/// </summary>
public sealed class DuplicateArtifactRewardException : LedgerException
{
    public string ArtifactKey { get; }

    public DuplicateArtifactRewardException(string artifactKey, string message) : base(message)
    {
        ArtifactKey = artifactKey;
    }
}
