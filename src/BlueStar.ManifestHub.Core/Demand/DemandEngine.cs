using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Storage;

namespace BlueStar.ManifestHub.Core.Demand;

/// <summary>
/// Engine responsible for calculating manifest demand by comparing known catalog
/// depot/manifest entries and build snapshots against locally stored manifests,
/// generating and maintaining indices/demand.json.
/// </summary>
public sealed class DemandEngine
{
    private readonly ManifestStorageManager _storageManager;
    private readonly int _defaultReward;

    public DemandEngine(ManifestStorageManager storageManager, int defaultReward = 100)
    {
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _defaultReward = defaultReward > 0 ? defaultReward : 100;
    }

    /// <summary>
    /// Generates indices/demand.json by identifying all known manifests across applications
    /// that are not locally present in the Hub manifests/ directory.
    /// </summary>
    public async Task<DemandIndex> GenerateDemandAsync(
        IEnumerable<uint>? targetAppIds = null,
        CancellationToken cancellationToken = default)
    {
        var records = new List<DemandRecord>();
        var seenPairs = new HashSet<(uint depotId, ulong manifestId)>();

        var appIds = new HashSet<uint>();
        if (targetAppIds != null)
        {
            foreach (var a in targetAppIds)
            {
                if (a > 0) appIds.Add(a);
            }
        }

        if (appIds.Count == 0)
        {
            var apps = await _storageManager.GetAllAppRecordsAsync(cancellationToken);
            foreach (var a in apps)
            {
                appIds.Add(a.AppId);
            }

            if (appIds.Count == 0)
            {
                foreach (var app in ManifestStorageManager.DefaultBaseApps)
                {
                    appIds.Add(app.AppId);
                }
            }
        }

        // Get all depot records once to associate depots with apps
        var allDepotRecords = await _storageManager.GetAllDepotRecordsAsync(cancellationToken);
        var depotRecordMap = allDepotRecords.Values.ToDictionary(d => d.DepotId);

        foreach (uint appId in appIds)
        {
            var appRecord = await _storageManager.GetAppRecordAsync(appId, cancellationToken);
            var depotIds = new HashSet<uint>(appRecord?.DepotIds ?? Array.Empty<uint>());

            // Collect depots from depot records mapped to this app
            foreach (var d in allDepotRecords.Values.Where(r => r.AppId == appId))
            {
                depotIds.Add(d.DepotId);
            }

            // Collect manifests from historical build snapshots for this app
            var snapshots = await _storageManager.GetBuildSnapshotsAsync(appId, cancellationToken);
            foreach (var snap in snapshots)
            {
                if (snap.DepotManifests != null)
                {
                    foreach (var (dId, mId) in snap.DepotManifests)
                    {
                        depotIds.Add(dId);
                        if (dId > 0 && mId > 0 && !_storageManager.ManifestExists(dId, mId))
                        {
                            if (seenPairs.Add((dId, mId)))
                            {
                                records.Add(new DemandRecord
                                {
                                    AppId = appId,
                                    DepotId = dId,
                                    ManifestId = mId,
                                    StardustReward = _defaultReward,
                                    DetectedAt = DateTimeOffset.UtcNow
                                });
                            }
                        }
                    }
                }
            }

            // Inspect known manifests from depot records
            foreach (uint depotId in depotIds)
            {
                if (depotRecordMap.TryGetValue(depotId, out var depotRecord))
                {
                    var manifestIds = new HashSet<ulong>(depotRecord.ManifestIds ?? Array.Empty<ulong>());
                    if (depotRecord.LatestManifestId.HasValue && depotRecord.LatestManifestId.Value > 0)
                    {
                        manifestIds.Add(depotRecord.LatestManifestId.Value);
                    }

                    foreach (ulong mId in manifestIds)
                    {
                        if (depotId > 0 && mId > 0 && !_storageManager.ManifestExists(depotId, mId))
                        {
                            if (seenPairs.Add((depotId, mId)))
                            {
                                records.Add(new DemandRecord
                                {
                                    AppId = appId,
                                    DepotId = depotId,
                                    ManifestId = mId,
                                    StardustReward = _defaultReward,
                                    DetectedAt = DateTimeOffset.UtcNow
                                });
                            }
                        }
                    }
                }
            }
        }

        var demandIndex = DemandIndex.FromRecords(records);
        await _storageManager.SaveDemandIndexAsync(demandIndex, cancellationToken);
        return demandIndex;
    }

    /// <summary>
    /// Retrieves the current demand index from storage.
    /// </summary>
    public Task<DemandIndex?> GetDemandIndexAsync(CancellationToken cancellationToken = default) =>
        _storageManager.GetDemandIndexAsync(cancellationToken);

    /// <summary>
    /// Gets a specific demand record if present in the demand index.
    /// </summary>
    public async Task<DemandRecord?> GetDemandAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default)
    {
        var index = await GetDemandIndexAsync(cancellationToken);
        if (index == null) return null;
        return index.Items.FirstOrDefault(d => d.DepotId == depotId && d.ManifestId == manifestId);
    }

    /// <summary>
    /// Removes a satisfied manifest from the demand index.
    /// </summary>
    public Task<bool> RemoveDemandAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default) =>
        _storageManager.RemoveDemandAsync(depotId, manifestId, cancellationToken);

    /// <summary>
    /// Retrieves the Stardust reward for a missing manifest from the demand index,
    /// or returns the fallback reward if not specifically recorded.
    /// </summary>
    public async Task<int> GetStardustRewardForManifestAsync(
        uint depotId,
        ulong manifestId,
        int fallbackReward = 100,
        CancellationToken cancellationToken = default)
    {
        var record = await GetDemandAsync(depotId, manifestId, cancellationToken);
        return record?.StardustReward ?? fallbackReward;
    }
}
