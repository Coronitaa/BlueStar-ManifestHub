using System.Text;
using BlueStar.ManifestHub.Core.Ledger;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Serialization;
using BlueStar.ManifestHub.Core.Validation;

namespace BlueStar.ManifestHub.Core.Storage;

/// <summary>
/// Manages structured local file storage for manifests, sidecars, build snapshots, and catalog indices.
/// Guarantees idempotency and strict deduplication by (DepotId, ManifestId).
/// </summary>
public sealed class ManifestStorageManager
{
    private readonly StorageOptions _options;
    private readonly ManifestValidator _validator;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    public ManifestStorageManager(StorageOptions? options = null, ManifestValidator? validator = null)
    {
        _options = options ?? new StorageOptions();
        _validator = validator ?? new ManifestValidator();
        EnsureDirectoryStructure();
    }

    public string BasePath => _options.BasePath;

    public void EnsureDirectoryStructure()
    {
        Directory.CreateDirectory(Path.Combine(_options.BasePath, _options.ManifestsDirectoryName));
        Directory.CreateDirectory(Path.Combine(_options.BasePath, _options.BuildsDirectoryName));
        Directory.CreateDirectory(Path.Combine(_options.BasePath, _options.IndicesDirectoryName));
        Directory.CreateDirectory(Path.Combine(_options.BasePath, _options.StagingDirectoryName));
        Directory.CreateDirectory(Path.Combine(_options.BasePath, _options.LedgerDirectoryName));
    }

    #region Path Resolution

    public string GetManifestDirectory(uint depotId) =>
        Path.Combine(_options.BasePath, _options.ManifestsDirectoryName, depotId.ToString());

    public string GetManifestPath(uint depotId, ulong manifestId) =>
        Path.Combine(GetManifestDirectory(depotId), $"{manifestId}.manifest");

    public string GetShaSidecarPath(uint depotId, ulong manifestId) =>
        Path.Combine(GetManifestDirectory(depotId), $"{manifestId}.sha");

    public string GetMetaSidecarPath(uint depotId, ulong manifestId) =>
        Path.Combine(GetManifestDirectory(depotId), $"{manifestId}.meta.json");

    public string GetBuildSnapshotPath(uint appId) =>
        Path.Combine(_options.BasePath, _options.BuildsDirectoryName, $"{appId}.json");

    public string GetDepotIndexPath() =>
        Path.Combine(_options.BasePath, _options.IndicesDirectoryName, "depots.json");

    public string GetAppIndexPath() =>
        Path.Combine(_options.BasePath, _options.IndicesDirectoryName, "apps.json");

    public string GetCoverageIndexPath() =>
        Path.Combine(_options.BasePath, _options.IndicesDirectoryName, "coverage.json");

    public string GetStagingDirectory() =>
        Path.Combine(_options.BasePath, _options.StagingDirectoryName);

    #endregion

    #region Manifest Publication & Deduplication

    /// <summary>
    /// Publishes a manifest binary to the repository with validation and strict deduplication.
    /// </summary>
    public async Task<PublishResult> PublishManifestAsync(
        byte[] manifestBytes,
        uint depotId,
        ulong manifestId,
        string source = "Manual",
        string contributorPseudonym = "anonymous",
        string? expectedSha256 = null,
        string? expectedSha1 = null,
        DateTimeOffset? fallbackTimestamp = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (depotId == 0)
        {
            return PublishResult.Failure(ManifestStatus.Corrupted, "DepotId must be greater than 0.");
        }

        if (manifestId == 0)
        {
            return PublishResult.Failure(ManifestStatus.Corrupted, "ManifestId must be greater than 0.");
        }

        // Validate contributor identity to prevent SteamID leakage in sidecars
        if (!string.IsNullOrWhiteSpace(contributorPseudonym))
        {
            try
            {
                StardustLedgerManager.ValidateContributorPseudonym(contributorPseudonym);
            }
            catch (Exception ex)
            {
                return PublishResult.Failure(ManifestStatus.Restricted, $"Invalid contributor identity: {ex.Message}");
            }
        }

        // 1. Validate manifest contents
        ManifestValidationResult validation = _validator.Validate(
            manifestBytes,
            depotId,
            manifestId,
            expectedSha256,
            expectedSha1,
            fallbackTimestamp);

        if (!validation.IsValid)
        {
            return PublishResult.Failure(validation.Status, validation.ErrorMessage ?? "Manifest validation failed.");
        }

        string manifestPath = GetManifestPath(depotId, manifestId);
        string shaPath = GetShaSidecarPath(depotId, manifestId);
        string metaPath = GetMetaSidecarPath(depotId, manifestId);

        // 2. Check strict deduplication
        if (File.Exists(manifestPath) && !force)
        {
            ManifestRevision? existingRevision = null;
            if (File.Exists(metaPath))
            {
                try
                {
                    string existingJson = await File.ReadAllTextAsync(metaPath, cancellationToken);
                    existingRevision = HubJsonSerializer.Deserialize<ManifestRevision>(existingJson);
                }
                catch
                {
                    // If meta corrupt, regenerate below
                }
            }

            if (existingRevision == null)
            {
                existingRevision = new ManifestRevision
                {
                    DepotId = depotId,
                    ManifestId = manifestId,
                    CreationTime = validation.CreationTime,
                    SizeOriginal = validation.SizeOriginal,
                    SizeCompressed = validation.SizeCompressed,
                    Sha256 = validation.Sha256,
                    Sha1 = validation.Sha1,
                    Status = ManifestStatus.AvailableAndValidated,
                    IngestedAt = File.GetCreationTimeUtc(manifestPath),
                    Source = source,
                    ContributorPseudonym = contributorPseudonym
                };
                await File.WriteAllTextAsync(metaPath, HubJsonSerializer.Serialize(existingRevision), cancellationToken);
            }

            return PublishResult.Success(existingRevision, manifestPath, isDuplicate: true);
        }

        // 3. Staging and atomic placement
        Directory.CreateDirectory(GetManifestDirectory(depotId));
        string stagingFile = Path.Combine(GetStagingDirectory(), $"{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(stagingFile, manifestBytes, cancellationToken);

            // Atomic move/replace
            File.Move(stagingFile, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(stagingFile))
            {
                try { File.Delete(stagingFile); } catch { /* best effort */ }
            }
        }

        // 4. Create .sha sidecar
        var sbSha = new StringBuilder();
        sbSha.AppendLine($"SHA256: {validation.Sha256}");
        sbSha.AppendLine($"SHA1: {validation.Sha1}");
        await File.WriteAllTextAsync(shaPath, sbSha.ToString(), cancellationToken);

        // 5. Create .meta.json sidecar
        var revision = new ManifestRevision
        {
            DepotId = depotId,
            ManifestId = manifestId,
            CreationTime = validation.CreationTime,
            SizeOriginal = validation.SizeOriginal,
            SizeCompressed = validation.SizeCompressed,
            Sha256 = validation.Sha256,
            Sha1 = validation.Sha1,
            Status = ManifestStatus.AvailableAndValidated,
            IngestedAt = DateTimeOffset.UtcNow,
            Source = source,
            ContributorPseudonym = contributorPseudonym
        };
        await File.WriteAllTextAsync(metaPath, HubJsonSerializer.Serialize(revision), cancellationToken);

        // 6. Update depot index
        await UpdateDepotIndexAsync(depotId, manifestId, cancellationToken);

        return PublishResult.Success(revision, manifestPath, isDuplicate: false);
    }

    /// <summary>
    /// Checks if a manifest exists locally.
    /// </summary>
    public bool ManifestExists(uint depotId, ulong manifestId) =>
        File.Exists(GetManifestPath(depotId, manifestId));

    /// <summary>
    /// Retrieves revision metadata for a stored manifest.
    /// </summary>
    public async Task<ManifestRevision?> GetManifestRevisionAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default)
    {
        string metaPath = GetMetaSidecarPath(depotId, manifestId);
        if (!File.Exists(metaPath))
            return null;

        string json = await File.ReadAllTextAsync(metaPath, cancellationToken);
        return HubJsonSerializer.Deserialize<ManifestRevision>(json);
    }

    #endregion

    #region Build Snapshots

    /// <summary>
    /// Appends or updates a build snapshot for an application.
    /// </summary>
    public async Task SaveBuildSnapshotAsync(BuildSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.AppId == 0) throw new ArgumentException("AppId must be positive.", nameof(snapshot));
        if (snapshot.BuildId == 0) throw new ArgumentException("BuildId must be positive.", nameof(snapshot));

        string snapshotPath = GetBuildSnapshotPath(snapshot.AppId);

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            List<BuildSnapshot> snapshots = new();
            if (File.Exists(snapshotPath))
            {
                string json = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
                var existing = HubJsonSerializer.Deserialize<List<BuildSnapshot>>(json);
                if (existing != null)
                {
                    snapshots.AddRange(existing);
                }
            }

            // Replace existing if same BuildId, else prepend/append
            int idx = snapshots.FindIndex(s => s.BuildId == snapshot.BuildId);
            if (idx >= 0)
            {
                snapshots[idx] = snapshot;
            }
            else
            {
                snapshots.Insert(0, snapshot);
            }

            // Order by timestamp descending
            snapshots.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

            string serialized = HubJsonSerializer.Serialize(snapshots);
            await File.WriteAllTextAsync(snapshotPath, serialized, cancellationToken);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Gets all historical build snapshots for an application.
    /// </summary>
    public async Task<IReadOnlyList<BuildSnapshot>> GetBuildSnapshotsAsync(uint appId, CancellationToken cancellationToken = default)
    {
        string snapshotPath = GetBuildSnapshotPath(appId);
        if (!File.Exists(snapshotPath))
            return Array.Empty<BuildSnapshot>();

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            string json = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
            return HubJsonSerializer.Deserialize<List<BuildSnapshot>>(json) ?? (IReadOnlyList<BuildSnapshot>)Array.Empty<BuildSnapshot>();
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Gets the most recent build snapshot for an application.
    /// </summary>
    public async Task<BuildSnapshot?> GetLatestBuildSnapshotAsync(uint appId, CancellationToken cancellationToken = default)
    {
        var snapshots = await GetBuildSnapshotsAsync(appId, cancellationToken);
        return snapshots.FirstOrDefault();
    }

    #endregion

    #region Coverage Records

    /// <summary>
    /// Saves or updates coverage metrics for an application and depot.
    /// </summary>
    public async Task UpdateCoverageRecordAsync(CoverageRecord coverage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        string coveragePath = GetCoverageIndexPath();

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, CoverageRecord> records = new();
            if (File.Exists(coveragePath))
            {
                string json = await File.ReadAllTextAsync(coveragePath, cancellationToken);
                var existing = HubJsonSerializer.Deserialize<Dictionary<string, CoverageRecord>>(json);
                if (existing != null)
                {
                    records = existing;
                }
            }

            string key = $"{coverage.AppId}:{coverage.DepotId}";
            records[key] = coverage;

            await File.WriteAllTextAsync(coveragePath, HubJsonSerializer.Serialize(records), cancellationToken);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Gets a specific coverage record for an AppId and DepotId.
    /// </summary>
    public async Task<CoverageRecord?> GetCoverageRecordAsync(uint appId, uint depotId, CancellationToken cancellationToken = default)
    {
        string coveragePath = GetCoverageIndexPath();
        if (!File.Exists(coveragePath))
            return null;

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            string json = await File.ReadAllTextAsync(coveragePath, cancellationToken);
            var records = HubJsonSerializer.Deserialize<Dictionary<string, CoverageRecord>>(json);
            if (records != null && records.TryGetValue($"{appId}:{depotId}", out var rec))
            {
                return rec;
            }
            return null;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Gets all coverage records for a specific application.
    /// </summary>
    public async Task<IReadOnlyList<CoverageRecord>> GetCoverageRecordsForAppAsync(uint appId, CancellationToken cancellationToken = default)
    {
        string coveragePath = GetCoverageIndexPath();
        if (!File.Exists(coveragePath))
            return Array.Empty<CoverageRecord>();

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            string json = await File.ReadAllTextAsync(coveragePath, cancellationToken);
            var records = HubJsonSerializer.Deserialize<Dictionary<string, CoverageRecord>>(json);
            if (records == null)
                return Array.Empty<CoverageRecord>();

            return records.Values.Where(c => c.AppId == appId).ToList();
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Computes and stores coverage for a depot based on known manifest IDs and local presence.
    /// </summary>
    public async Task<CoverageRecord> ComputeAndSaveCoverageAsync(
        uint appId,
        uint depotId,
        IReadOnlyCollection<ulong> knownManifestIds,
        CancellationToken cancellationToken = default)
    {
        int total = knownManifestIds.Count;
        int available = 0;
        ulong? latestAvailable = null;

        foreach (ulong id in knownManifestIds)
        {
            if (ManifestExists(depotId, id))
            {
                available++;
                if (latestAvailable == null || id > latestAvailable.Value)
                {
                    latestAvailable = id;
                }
            }
        }

        int missing = Math.Max(0, total - available);

        var record = new CoverageRecord
        {
            AppId = appId,
            DepotId = depotId,
            TotalRevisions = total,
            AvailableRevisions = available,
            MissingRevisions = missing,
            LatestAvailableManifestId = latestAvailable,
            LastChecked = DateTimeOffset.UtcNow
        };

        await UpdateCoverageRecordAsync(record, cancellationToken);
        return record;
    }

    #endregion

    #region Catalog Indices (Depots & Apps)

    private async Task UpdateDepotIndexAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken)
    {
        string depotIndexPath = GetDepotIndexPath();

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, DepotRecord> depots = new();
            if (File.Exists(depotIndexPath))
            {
                string json = await File.ReadAllTextAsync(depotIndexPath, cancellationToken);
                var existing = HubJsonSerializer.Deserialize<Dictionary<string, DepotRecord>>(json);
                if (existing != null)
                {
                    depots = existing;
                }
            }

            string key = depotId.ToString();
            List<ulong> manifestList = new();
            uint appId = 0;
            string name = string.Empty;

            if (depots.TryGetValue(key, out var currentRecord))
            {
                manifestList.AddRange(currentRecord.ManifestIds);
                appId = currentRecord.AppId;
                name = currentRecord.Name;
            }

            if (!manifestList.Contains(manifestId))
            {
                manifestList.Add(manifestId);
                manifestList.Sort();
            }

            ulong latest = manifestList.Count > 0 ? manifestList[^1] : manifestId;

            depots[key] = new DepotRecord
            {
                DepotId = depotId,
                AppId = appId,
                Name = string.IsNullOrEmpty(name) ? $"Depot {depotId}" : name,
                LatestManifestId = latest,
                ManifestIds = manifestList,
                LastUpdated = DateTimeOffset.UtcNow
            };

            await File.WriteAllTextAsync(depotIndexPath, HubJsonSerializer.Serialize(depots), cancellationToken);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<DepotRecord?> GetDepotRecordAsync(uint depotId, CancellationToken cancellationToken = default)
    {
        string depotIndexPath = GetDepotIndexPath();
        if (!File.Exists(depotIndexPath))
            return null;

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            string json = await File.ReadAllTextAsync(depotIndexPath, cancellationToken);
            var depots = HubJsonSerializer.Deserialize<Dictionary<string, DepotRecord>>(json);
            if (depots != null && depots.TryGetValue(depotId.ToString(), out var rec))
            {
                return rec;
            }
            return null;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task RegisterAppAsync(AppRecord app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        string appIndexPath = GetAppIndexPath();

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, AppRecord> apps = new();
            if (File.Exists(appIndexPath))
            {
                string json = await File.ReadAllTextAsync(appIndexPath, cancellationToken);
                var existing = HubJsonSerializer.Deserialize<Dictionary<string, AppRecord>>(json);
                if (existing != null)
                {
                    apps = existing;
                }
            }

            apps[app.AppId.ToString()] = app;
            await File.WriteAllTextAsync(appIndexPath, HubJsonSerializer.Serialize(apps), cancellationToken);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<AppRecord?> GetAppRecordAsync(uint appId, CancellationToken cancellationToken = default)
    {
        string appIndexPath = GetAppIndexPath();
        if (!File.Exists(appIndexPath))
            return null;

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            string json = await File.ReadAllTextAsync(appIndexPath, cancellationToken);
            var apps = HubJsonSerializer.Deserialize<Dictionary<string, AppRecord>>(json);
            if (apps != null && apps.TryGetValue(appId.ToString(), out var rec))
            {
                return rec;
            }
            return null;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    #endregion
}
