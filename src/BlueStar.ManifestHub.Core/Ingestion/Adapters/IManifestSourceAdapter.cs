namespace BlueStar.ManifestHub.Core.Ingestion.Adapters;

/// <summary>
/// Common contract for upstream manifest source adapters.
/// Adapters discover manifests for apps/depots and download manifest binaries.
/// </summary>
public interface IManifestSourceAdapter
{
    /// <summary>
    /// Unique identifier for this source (e.g., "DepotBox", "ManifestHub2", "K25").
    /// </summary>
    string SourceId { get; }

    /// <summary>
    /// Type/classification of this source (e.g., "DepotBoxApi", "GitHubRaw", "K25Mirror").
    /// </summary>
    string SourceType { get; }

    /// <summary>
    /// Ingestion priority. Lower number indicates higher priority (e.g. 10 > 20 > 30).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Indicates whether the source is currently healthy and operational.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Discovers all known manifests associated with an application.
    /// </summary>
    Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsAsync(uint appId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers all known manifests for a specific depot.
    /// </summary>
    Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsForDepotAsync(uint depotId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the raw binary bytes for a given depot and manifest ID.
    /// Returns null if the manifest is not found (404) or unavailable.
    /// </summary>
    Task<byte[]?> DownloadManifestAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default);
}
