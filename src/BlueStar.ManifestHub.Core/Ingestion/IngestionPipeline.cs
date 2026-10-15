using BlueStar.ManifestHub.Core.Ingestion.Adapters;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Storage;
using BlueStar.ManifestHub.Core.Validation;

namespace BlueStar.ManifestHub.Core.Ingestion;

/// <summary>
/// Orchestrates manifest ingestion from multiple upstream providers.
/// Discovers manifests, deduplicates against local repository storage, acquires binaries
/// with automatic provider failover, validates payload integrity, and publishes in batches.
/// </summary>
public sealed class IngestionPipeline
{
    private readonly List<IManifestSourceAdapter> _adapters;
    private readonly ManifestValidator _validator;
    private readonly ManifestStorageManager _storageManager;
    private readonly IngestionPipelineOptions _options;

    public IngestionPipeline(
        IEnumerable<IManifestSourceAdapter> adapters,
        ManifestStorageManager storageManager,
        ManifestValidator? validator = null,
        IngestionPipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(storageManager);

        _adapters = adapters.OrderBy(a => a.Priority).ToList();
        _storageManager = storageManager;
        _validator = validator ?? new ManifestValidator();
        _options = options ?? new IngestionPipelineOptions();
    }

    public IReadOnlyList<IManifestSourceAdapter> Adapters => _adapters;

    /// <summary>
    /// Executes ingestion for an application: discovers manifests, deduplicates, downloads, validates, and publishes.
    /// </summary>
    public async Task<IngestionBatchSummary> IngestAppAsync(uint appId, CancellationToken cancellationToken = default)
    {
        var summary = new IngestionBatchSummary();
        var discoveredMap = new Dictionary<(uint DepotId, ulong ManifestId), DiscoveredManifest>();

        // 1. Discover across adapters by priority
        foreach (var adapter in _adapters)
        {
            if (!adapter.IsAvailable)
            {
                continue;
            }

            try
            {
                var manifests = await adapter.DiscoverManifestsAsync(appId, cancellationToken);
                foreach (var m in manifests)
                {
                    var key = (m.DepotId, m.ManifestId);
                    if (!discoveredMap.ContainsKey(key))
                    {
                        discoveredMap[key] = m;
                    }
                }
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"Adapter '{adapter.SourceId}' discovery failed for app {appId}: {ex.Message}");
            }
        }

        summary.TotalDiscovered = discoveredMap.Count;

        // 2. Process discovered manifests
        await IngestDiscoveredInternalAsync(discoveredMap.Values, appId, summary, cancellationToken);
        return summary;
    }

    /// <summary>
    /// Executes ingestion for a specific depot.
    /// </summary>
    public async Task<IngestionBatchSummary> IngestDepotAsync(uint depotId, uint appId = 0, CancellationToken cancellationToken = default)
    {
        var summary = new IngestionBatchSummary();
        var discoveredMap = new Dictionary<(uint DepotId, ulong ManifestId), DiscoveredManifest>();

        foreach (var adapter in _adapters)
        {
            if (!adapter.IsAvailable)
            {
                continue;
            }

            try
            {
                var manifests = await adapter.DiscoverManifestsForDepotAsync(depotId, cancellationToken);
                foreach (var m in manifests)
                {
                    var key = (m.DepotId, m.ManifestId);
                    if (!discoveredMap.ContainsKey(key))
                    {
                        discoveredMap[key] = m;
                    }
                }
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"Adapter '{adapter.SourceId}' depot discovery failed for depot {depotId}: {ex.Message}");
            }
        }

        summary.TotalDiscovered = discoveredMap.Count;
        await IngestDiscoveredInternalAsync(discoveredMap.Values, appId, summary, cancellationToken);
        return summary;
    }

    /// <summary>
    /// Ingests a single manifest by depotId and manifestId.
    /// </summary>
    public async Task<IngestionBatchSummary> IngestManifestAsync(
        uint depotId,
        ulong manifestId,
        uint appId = 0,
        string? expectedSha256 = null,
        DateTimeOffset? creationTime = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = new DiscoveredManifest
        {
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            ExpectedSha256 = expectedSha256,
            CreationTime = creationTime,
            SourceId = "Direct"
        };

        var summary = new IngestionBatchSummary { TotalDiscovered = 1 };
        await IngestDiscoveredInternalAsync(new[] { manifest }, appId, summary, cancellationToken);
        return summary;
    }

    /// <summary>
    /// Ingests an arbitrary collection of discovered manifests.
    /// </summary>
    public async Task<IngestionBatchSummary> IngestManifestsAsync(
        IEnumerable<DiscoveredManifest> manifests,
        uint appId = 0,
        CancellationToken cancellationToken = default)
    {
        var list = manifests.ToList();
        var summary = new IngestionBatchSummary { TotalDiscovered = list.Count };
        await IngestDiscoveredInternalAsync(list, appId, summary, cancellationToken);
        return summary;
    }

    private async Task IngestDiscoveredInternalAsync(
        IEnumerable<DiscoveredManifest> manifests,
        uint defaultAppId,
        IngestionBatchSummary summary,
        CancellationToken cancellationToken)
    {
        var toAcquire = new List<DiscoveredManifest>();
        var affectedDepots = new Dictionary<uint, HashSet<ulong>>();

        // 1. Deduplication check against local storage
        foreach (var m in manifests)
        {
            if (!affectedDepots.TryGetValue(m.DepotId, out var set))
            {
                set = new HashSet<ulong>();
                affectedDepots[m.DepotId] = set;
            }
            set.Add(m.ManifestId);

            if (_storageManager.ManifestExists(m.DepotId, m.ManifestId))
            {
                summary.AlreadyStored++;
            }
            else
            {
                toAcquire.Add(m);
            }
        }

        // 2. Acquire and publish missing manifests in batches
        int batchSize = Math.Max(1, _options.BatchSize);
        for (int i = 0; i < toAcquire.Count; i += batchSize)
        {
            var batch = toAcquire.Skip(i).Take(batchSize);
            foreach (var m in batch)
            {
                bool acquired = false;

                // Try each adapter in priority order (failover)
                foreach (var adapter in _adapters)
                {
                    if (!adapter.IsAvailable)
                    {
                        continue;
                    }

                    byte[]? bytes = null;
                    try
                    {
                        bytes = await adapter.DownloadManifestAsync(m.DepotId, m.ManifestId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        summary.Errors.Add($"Adapter '{adapter.SourceId}' download failed for {m.DepotId}:{m.ManifestId}: {ex.Message}");
                    }

                    if (bytes == null || bytes.Length == 0)
                    {
                        continue; // try next adapter in priority chain
                    }

                    // Validate binary
                    var validation = _validator.Validate(
                        bytes,
                        expectedDepotId: m.DepotId,
                        expectedManifestId: m.ManifestId,
                        expectedSha256: m.ExpectedSha256,
                        expectedSha1: m.ExpectedSha1,
                        fallbackTimestamp: m.CreationTime);

                    if (!validation.IsValid)
                    {
                        summary.Errors.Add($"Validation failed for {m.DepotId}:{m.ManifestId} from '{adapter.SourceId}': {validation.ErrorMessage}");
                        continue; // try next adapter if downloaded bytes were invalid/corrupt
                    }

                    // Publish to storage
                    var pubResult = await _storageManager.PublishManifestAsync(
                        manifestBytes: bytes,
                        depotId: m.DepotId,
                        manifestId: m.ManifestId,
                        source: adapter.SourceId,
                        contributorPseudonym: _options.DefaultContributorPseudonym,
                        expectedSha256: m.ExpectedSha256,
                        expectedSha1: m.ExpectedSha1,
                        fallbackTimestamp: m.CreationTime,
                        cancellationToken: cancellationToken);

                    if (pubResult.IsSuccess && pubResult.Revision != null)
                    {
                        summary.PublishedRevisions.Add(pubResult.Revision);
                        summary.SuccessfullyIngested++;
                        acquired = true;

                        if (_options.StopOnFirstSuccessfulDownload)
                        {
                            break;
                        }
                    }
                    else
                    {
                        summary.Errors.Add($"Publishing {m.DepotId}:{m.ManifestId} failed: {pubResult.Message}");
                    }
                }

                if (!acquired)
                {
                    summary.Failed++;
                }
            }
        }

        // 3. Update coverage & depot indices for all affected depots
        foreach (var (depotId, manifestIds) in affectedDepots)
        {
            var existingDepot = await _storageManager.GetDepotRecordAsync(depotId, cancellationToken);
            if (existingDepot != null)
            {
                foreach (var id in existingDepot.ManifestIds)
                {
                    manifestIds.Add(id);
                }
            }

            uint parentAppId = defaultAppId > 0 ? defaultAppId : (existingDepot?.AppId ?? 0);
            if (parentAppId > 0)
            {
                await _storageManager.ComputeAndSaveCoverageAsync(parentAppId, depotId, manifestIds, cancellationToken);
            }
        }
    }
}
