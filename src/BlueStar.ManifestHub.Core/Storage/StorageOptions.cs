namespace BlueStar.ManifestHub.Core.Storage;

/// <summary>
/// Configuration options for Manifest Hub storage.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>
    /// Base root directory for storage.
    /// </summary>
    public string BasePath { get; set; } = Directory.GetCurrentDirectory();

    public string ManifestsDirectoryName { get; set; } = "manifests";
    public string BuildsDirectoryName { get; set; } = "builds";
    public string IndicesDirectoryName { get; set; } = "indices";
    public string StagingDirectoryName { get; set; } = "staging";
    public string LedgerDirectoryName { get; set; } = "ledger";
}
