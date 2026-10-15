using System.Text;
using BlueStar.ManifestHub.Core.Ingestion;
using BlueStar.ManifestHub.Core.Ingestion.Adapters;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Storage;
using BlueStar.ManifestHub.Core.Validation;

namespace BlueStar.ManifestHub.Tests;

public sealed class IngestionPipelineTests : IDisposable
{
    private readonly string _testDir;
    private readonly ManifestStorageManager _storageManager;
    private readonly ManifestValidator _validator;

    public IngestionPipelineTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("pipeline_test_");
        _storageManager = new ManifestStorageManager(new StorageOptions { BasePath = _testDir });
        _validator = new ManifestValidator();
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public async Task MultiSourceFailover_WhenPrimaryProviderFails_FallsOverToSecondary()
    {
        uint depotId = 440;
        ulong manifestId = 111222333444555UL;
        byte[] validBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);

        // Primary adapter (Priority 10) is DOWN / throws
        var primaryMock = new MockSourceAdapter("DepotBox", 10)
        {
            DownloadFunc = (dId, mId) => throw new HttpRequestException("DepotBox cluster unavailable (503).")
        };

        // Secondary adapter (Priority 20) succeeds
        var secondaryMock = new MockSourceAdapter("ManifestHub2", 20)
        {
            DownloadFunc = (dId, mId) => Task.FromResult<byte[]?>(validBytes)
        };

        // Tertiary adapter (Priority 30) should not be called
        var tertiaryMock = new MockSourceAdapter("K25", 30)
        {
            DownloadFunc = (dId, mId) => Task.FromResult<byte[]?>(validBytes)
        };

        var pipeline = new IngestionPipeline(
            new[] { tertiaryMock, primaryMock, secondaryMock }, // passed out-of-order to test priority sorting
            _storageManager,
            _validator);

        var manifest = new DiscoveredManifest
        {
            AppId = 440,
            DepotId = depotId,
            ManifestId = manifestId,
            SourceId = "DiscoveryFeed"
        };

        var summary = await pipeline.IngestManifestsAsync(new[] { manifest });

        Assert.Equal(1, summary.TotalDiscovered);
        Assert.Equal(1, summary.SuccessfullyIngested);
        Assert.Equal(0, summary.Failed);
        Assert.Single(summary.PublishedRevisions);
        Assert.Equal("ManifestHub2", summary.PublishedRevisions[0].Source);

        Assert.Equal(1, primaryMock.DownloadCallCount);
        Assert.Equal(1, secondaryMock.DownloadCallCount);
        Assert.Equal(0, tertiaryMock.DownloadCallCount); // tertiary never reached
    }

    [Fact]
    public async Task Deduplication_SkipsAlreadyStoredManifestsWithoutDownloading()
    {
        uint depotId = 440;
        ulong manifestIdExisting = 100001UL;
        ulong manifestIdNew = 100002UL;

        // Pre-store the existing manifest
        byte[] existingBytes = TestManifestHelper.CreateValidManifest(depotId, manifestIdExisting);
        await _storageManager.PublishManifestAsync(existingBytes, depotId, manifestIdExisting, source: "PreExisting");

        byte[] newBytes = TestManifestHelper.CreateValidManifest(depotId, manifestIdNew);

        var adapterMock = new MockSourceAdapter("DepotBox", 10)
        {
            DownloadFunc = (dId, mId) => Task.FromResult<byte[]?>(newBytes)
        };

        var pipeline = new IngestionPipeline(new[] { adapterMock }, _storageManager, _validator);

        var manifests = new[]
        {
            new DiscoveredManifest { AppId = 440, DepotId = depotId, ManifestId = manifestIdExisting },
            new DiscoveredManifest { AppId = 440, DepotId = depotId, ManifestId = manifestIdNew }
        };

        var summary = await pipeline.IngestManifestsAsync(manifests);

        Assert.Equal(2, summary.TotalDiscovered);
        Assert.Equal(1, summary.AlreadyStored);
        Assert.Equal(1, summary.SuccessfullyIngested);
        Assert.Equal(0, summary.Failed);

        // Adapter was only invoked for the missing manifest, NOT the existing one
        Assert.Equal(1, adapterMock.DownloadCallCount);
    }

    [Fact]
    public async Task CorruptPayloadFailover_WhenPrimaryReturnsCorruptBytes_FallsOverToSecondary()
    {
        uint depotId = 730;
        ulong manifestId = 777888999UL;
        byte[] validBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);
        byte[] htmlErrorBytes = Encoding.UTF8.GetBytes("<html><body>502 Bad Gateway</body></html>");

        var corruptPrimary = new MockSourceAdapter("DepotBox", 10)
        {
            DownloadFunc = (dId, mId) => Task.FromResult<byte[]?>(htmlErrorBytes)
        };

        var healthySecondary = new MockSourceAdapter("K25", 30)
        {
            DownloadFunc = (dId, mId) => Task.FromResult<byte[]?>(validBytes)
        };

        var pipeline = new IngestionPipeline(
            new[] { healthySecondary, corruptPrimary },
            _storageManager,
            _validator);

        var manifest = new DiscoveredManifest { AppId = 730, DepotId = depotId, ManifestId = manifestId };
        var summary = await pipeline.IngestManifestsAsync(new[] { manifest });

        Assert.Equal(1, summary.SuccessfullyIngested);
        Assert.Equal(0, summary.Failed);
        Assert.Equal("K25", summary.PublishedRevisions[0].Source);
    }

    [Fact]
    public async Task BatchPublication_WritesFilesUpdatesIndicesAndCoverage()
    {
        uint appId = 440;
        uint depot1 = 441;
        uint depot2 = 442;

        ulong m1 = 101UL;
        ulong m2 = 102UL;
        ulong m3 = 201UL;

        byte[] bytes1 = TestManifestHelper.CreateValidManifest(depot1, m1);
        byte[] bytes2 = TestManifestHelper.CreateValidManifest(depot1, m2);
        byte[] bytes3 = TestManifestHelper.CreateValidManifest(depot2, m3);

        var adapter = new MockSourceAdapter("BatchAdapter", 10)
        {
            DiscoverFunc = (aId) => Task.FromResult<IReadOnlyList<DiscoveredManifest>>(new List<DiscoveredManifest>
            {
                new() { AppId = appId, DepotId = depot1, ManifestId = m1 },
                new() { AppId = appId, DepotId = depot1, ManifestId = m2 },
                new() { AppId = appId, DepotId = depot2, ManifestId = m3 }
            }),
            DownloadFunc = (dId, mId) =>
            {
                if (dId == depot1 && mId == m1) return Task.FromResult<byte[]?>(bytes1);
                if (dId == depot1 && mId == m2) return Task.FromResult<byte[]?>(bytes2);
                if (dId == depot2 && mId == m3) return Task.FromResult<byte[]?>(bytes3);
                return Task.FromResult<byte[]?>(null);
            }
        };

        var pipeline = new IngestionPipeline(new[] { adapter }, _storageManager, _validator);

        var summary = await pipeline.IngestAppAsync(appId);

        Assert.Equal(3, summary.TotalDiscovered);
        Assert.Equal(3, summary.SuccessfullyIngested);
        Assert.Equal(0, summary.Failed);

        // Verify storage files exist
        Assert.True(File.Exists(_storageManager.GetManifestPath(depot1, m1)));
        Assert.True(File.Exists(_storageManager.GetShaSidecarPath(depot1, m1)));
        Assert.True(File.Exists(_storageManager.GetMetaSidecarPath(depot1, m1)));

        // Verify depot index
        var depotRecord = await _storageManager.GetDepotRecordAsync(depot1);
        Assert.NotNull(depotRecord);
        Assert.Contains(m1, depotRecord.ManifestIds);
        Assert.Contains(m2, depotRecord.ManifestIds);

        // Verify coverage record
        var coverage = await _storageManager.GetCoverageRecordAsync(appId, depot1);
        Assert.NotNull(coverage);
        Assert.Equal(2, coverage.TotalRevisions);
        Assert.Equal(2, coverage.AvailableRevisions);
        Assert.Equal(0, coverage.MissingRevisions);
    }

    [Fact]
    public async Task IngestAppAsync_SavesBuildSnapshot_WithDepotManifestsMapping()
    {
        uint appId = 440;
        uint depot1 = 441;
        uint depot2 = 442;
        ulong m1 = 10001UL;
        ulong m2 = 20002UL;

        byte[] bytes1 = TestManifestHelper.CreateValidManifest(depot1, m1);
        byte[] bytes2 = TestManifestHelper.CreateValidManifest(depot2, m2);

        var adapter = new MockSourceAdapter("SnapshotAdapter", 10)
        {
            DiscoverFunc = (aId) => Task.FromResult<IReadOnlyList<DiscoveredManifest>>(new List<DiscoveredManifest>
            {
                new() { AppId = appId, DepotId = depot1, ManifestId = m1, Branch = "public" },
                new() { AppId = appId, DepotId = depot2, ManifestId = m2, Branch = "public" }
            }),
            DownloadFunc = (dId, mId) =>
            {
                if (dId == depot1 && mId == m1) return Task.FromResult<byte[]?>(bytes1);
                if (dId == depot2 && mId == m2) return Task.FromResult<byte[]?>(bytes2);
                return Task.FromResult<byte[]?>(null);
            }
        };

        var pipeline = new IngestionPipeline(new[] { adapter }, _storageManager, _validator);

        var summary = await pipeline.IngestAppAsync(appId);

        Assert.Equal(2, summary.SuccessfullyIngested);

        var latestSnapshot = await _storageManager.GetLatestBuildSnapshotAsync(appId);
        Assert.NotNull(latestSnapshot);
        Assert.Equal(appId, latestSnapshot.AppId);
        Assert.Equal("public", latestSnapshot.Branch);
        Assert.True(latestSnapshot.BuildId > 0);
        Assert.Equal(2, latestSnapshot.DepotManifests.Count);
        Assert.Equal(m1, latestSnapshot.DepotManifests[depot1]);
        Assert.Equal(m2, latestSnapshot.DepotManifests[depot2]);
    }

    [Fact]
    public async Task HistoricalBuildSnapshots_MultipleIngestions_PreservedAndAccumulated()
    {
        uint appId = 730;
        uint depotId = 731;
        ulong m1 = 11111UL;
        ulong m2 = 22222UL;

        byte[] bytes1 = TestManifestHelper.CreateValidManifest(depotId, m1);
        byte[] bytes2 = TestManifestHelper.CreateValidManifest(depotId, m2);

        var adapter = new MockSourceAdapter("HistoryAdapter", 10)
        {
            DownloadFunc = (dId, mId) =>
            {
                if (dId == depotId && mId == m1) return Task.FromResult<byte[]?>(bytes1);
                if (dId == depotId && mId == m2) return Task.FromResult<byte[]?>(bytes2);
                return Task.FromResult<byte[]?>(null);
            }
        };

        var pipeline = new IngestionPipeline(new[] { adapter }, _storageManager, _validator);

        // First build ingestion
        var firstManifest = new DiscoveredManifest
        {
            AppId = appId,
            DepotId = depotId,
            ManifestId = m1,
            BuildId = 10001
        };
        await pipeline.IngestManifestsAsync(new[] { firstManifest }, appId);

        // Second build ingestion with distinct BuildId
        var secondManifest = new DiscoveredManifest
        {
            AppId = appId,
            DepotId = depotId,
            ManifestId = m2,
            BuildId = 10002
        };
        await pipeline.IngestManifestsAsync(new[] { secondManifest }, appId);

        var allSnapshots = await _storageManager.GetBuildSnapshotsAsync(appId);
        Assert.Equal(2, allSnapshots.Count);
        Assert.Contains(allSnapshots, s => s.BuildId == 10001);
        Assert.Contains(allSnapshots, s => s.BuildId == 10002);

        var latest = await _storageManager.GetLatestBuildSnapshotAsync(appId);
        Assert.NotNull(latest);
        Assert.Equal(m2, latest.DepotManifests[depotId]);
    }

    [Fact]
    public async Task IngestAppAsync_UpdatesAppIndex_WithDepotIdsAndLastUpdated()
    {
        uint appId = 570;
        uint depot1 = 571;
        uint depot2 = 572;
        ulong m1 = 5001UL;
        ulong m2 = 5002UL;

        byte[] bytes1 = TestManifestHelper.CreateValidManifest(depot1, m1);
        byte[] bytes2 = TestManifestHelper.CreateValidManifest(depot2, m2);

        var adapter = new MockSourceAdapter("AppIndexAdapter", 10)
        {
            DiscoverFunc = (aId) => Task.FromResult<IReadOnlyList<DiscoveredManifest>>(new List<DiscoveredManifest>
            {
                new() { AppId = appId, DepotId = depot1, ManifestId = m1 },
                new() { AppId = appId, DepotId = depot2, ManifestId = m2 }
            }),
            DownloadFunc = (dId, mId) =>
            {
                if (dId == depot1 && mId == m1) return Task.FromResult<byte[]?>(bytes1);
                if (dId == depot2 && mId == m2) return Task.FromResult<byte[]?>(bytes2);
                return Task.FromResult<byte[]?>(null);
            }
        };

        var pipeline = new IngestionPipeline(new[] { adapter }, _storageManager, _validator);

        DateTimeOffset beforeRun = DateTimeOffset.UtcNow.AddSeconds(-2);
        await pipeline.IngestAppAsync(appId);

        var appRecord = await _storageManager.GetAppRecordAsync(appId);
        Assert.NotNull(appRecord);
        Assert.Equal(appId, appRecord.AppId);
        Assert.Contains(depot1, appRecord.DepotIds);
        Assert.Contains(depot2, appRecord.DepotIds);
        Assert.True(appRecord.LastUpdated >= beforeRun);
    }

    [Fact]
    public async Task ManifestRevisions_PreserveOriginalProtobufDates_WithoutInventedDates()
    {
        uint depotId = 105600;
        ulong manifestWithMeta = 333444UL;
        ulong manifestNoMeta = 555666UL;

        DateTimeOffset authenticDate = new(2025, 8, 20, 14, 30, 0, TimeSpan.Zero);

        // 1. Manifest WITH Protobuf metadata containing authentic timestamp
        byte[] bytesWithMeta = TestManifestHelper.CreateValidManifest(depotId, manifestWithMeta, authenticDate);

        // 2. Manifest WITHOUT Protobuf metadata section and NO fallback timestamp
        byte[] rawPayload = Encoding.UTF8.GetBytes("Synthetic raw manifest content");
        byte[] bytesNoMeta = ManifestValidator.CreateSyntheticManifest(
            depotId,
            manifestNoMeta,
            DateTimeOffset.UtcNow,
            rawPayload,
            compressPayload: true,
            includeMetadata: false);

        var adapter = new MockSourceAdapter("DateAdapter", 10)
        {
            DownloadFunc = (dId, mId) =>
            {
                if (dId == depotId && mId == manifestWithMeta) return Task.FromResult<byte[]?>(bytesWithMeta);
                if (dId == depotId && mId == manifestNoMeta) return Task.FromResult<byte[]?>(bytesNoMeta);
                return Task.FromResult<byte[]?>(null);
            }
        };

        var pipeline = new IngestionPipeline(new[] { adapter }, _storageManager, _validator);

        var manifests = new[]
        {
            new DiscoveredManifest { AppId = 105600, DepotId = depotId, ManifestId = manifestWithMeta },
            new DiscoveredManifest { AppId = 105600, DepotId = depotId, ManifestId = manifestNoMeta }
        };

        await pipeline.IngestManifestsAsync(manifests, 105600);

        var revWithMeta = await _storageManager.GetManifestRevisionAsync(depotId, manifestWithMeta);
        Assert.NotNull(revWithMeta);
        Assert.NotNull(revWithMeta.CreationTime);
        Assert.Equal(authenticDate.ToUnixTimeSeconds(), revWithMeta.CreationTime.Value.ToUnixTimeSeconds());

        var revNoMeta = await _storageManager.GetManifestRevisionAsync(depotId, manifestNoMeta);
        Assert.NotNull(revNoMeta);
        // Regla crítica: sin evidencia fidedigna de timestamp, no se inventan fechas
        Assert.Null(revNoMeta.CreationTime);
    }

    [Fact]
    public async Task GidOrdering_PreservesIndependentRevisions_DoesNotSortGidsAsDates()
    {
        uint appId = 440;
        uint depotId = 441;
        // Manifest with numerically HIGHER GID but OLDER authentic timestamp
        ulong higherGidOlderDate = 999999999999UL;
        DateTimeOffset olderTimestamp = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Manifest with numerically LOWER GID but NEWER authentic timestamp
        ulong lowerGidNewerDate = 111111UL;
        DateTimeOffset newerTimestamp = new(2025, 10, 1, 0, 0, 0, TimeSpan.Zero);

        byte[] olderBytes = TestManifestHelper.CreateValidManifest(depotId, higherGidOlderDate, olderTimestamp);
        byte[] newerBytes = TestManifestHelper.CreateValidManifest(depotId, lowerGidNewerDate, newerTimestamp);

        var adapter = new MockSourceAdapter("GidOrderingAdapter", 10)
        {
            DownloadFunc = (dId, mId) =>
            {
                if (dId == depotId && mId == higherGidOlderDate) return Task.FromResult<byte[]?>(olderBytes);
                if (dId == depotId && mId == lowerGidNewerDate) return Task.FromResult<byte[]?>(newerBytes);
                return Task.FromResult<byte[]?>(null);
            }
        };

        var pipeline = new IngestionPipeline(new[] { adapter }, _storageManager, _validator);

        var manifests = new[]
        {
            new DiscoveredManifest { AppId = appId, DepotId = depotId, ManifestId = higherGidOlderDate },
            new DiscoveredManifest { AppId = appId, DepotId = depotId, ManifestId = lowerGidNewerDate }
        };

        await pipeline.IngestManifestsAsync(manifests, appId);

        // The active manifest chosen for the build snapshot must be the one with newer authentic timestamp,
        // NOT the one with numerically higher GID
        var snapshot = await _storageManager.GetLatestBuildSnapshotAsync(appId);
        Assert.NotNull(snapshot);
        Assert.Equal(lowerGidNewerDate, snapshot.DepotManifests[depotId]);

        // Both revisions must be preserved independently in storage
        Assert.True(_storageManager.ManifestExists(depotId, higherGidOlderDate));
        Assert.True(_storageManager.ManifestExists(depotId, lowerGidNewerDate));
    }

    private sealed class MockSourceAdapter : IManifestSourceAdapter
    {
        public MockSourceAdapter(string sourceId, int priority, bool isAvailable = true)
        {
            SourceId = sourceId;
            Priority = priority;
            IsAvailable = isAvailable;
        }

        public string SourceId { get; }
        public string SourceType => "Mock";
        public int Priority { get; }
        public bool IsAvailable { get; set; }

        public int DiscoverCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }

        public Func<uint, Task<IReadOnlyList<DiscoveredManifest>>>? DiscoverFunc { get; set; }
        public Func<uint, ulong, Task<byte[]?>>? DownloadFunc { get; set; }

        public Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsAsync(uint appId, CancellationToken cancellationToken = default)
        {
            DiscoverCallCount++;
            return DiscoverFunc != null
                ? DiscoverFunc(appId)
                : Task.FromResult<IReadOnlyList<DiscoveredManifest>>(Array.Empty<DiscoveredManifest>());
        }

        public Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsForDepotAsync(uint depotId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<DiscoveredManifest>>(Array.Empty<DiscoveredManifest>());
        }

        public Task<byte[]?> DownloadManifestAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default)
        {
            DownloadCallCount++;
            return DownloadFunc != null
                ? DownloadFunc(depotId, manifestId)
                : Task.FromResult<byte[]?>(null);
        }
    }
}
