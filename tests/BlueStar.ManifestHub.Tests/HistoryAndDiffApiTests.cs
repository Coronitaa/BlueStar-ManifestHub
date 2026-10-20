using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Storage;

namespace BlueStar.ManifestHub.Tests;

public sealed class HistoryAndDiffApiTests : IDisposable
{
    private readonly string _testDir;
    private readonly ManifestStorageManager _storage;

    public HistoryAndDiffApiTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("history_diff_test_");
        _storage = new ManifestStorageManager(new StorageOptions { BasePath = _testDir });
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public async Task GetBuildHistoryPaged_FiltersByBranchAndPaginatesCorrectly()
    {
        uint appId = 730;

        for (int i = 1; i <= 5; i++)
        {
            var snap = new BuildSnapshot
            {
                AppId = appId,
                BuildId = (ulong)(1000 + i),
                CreationTime = DateTimeOffset.UtcNow.AddDays(-i),
                Timestamp = DateTimeOffset.UtcNow.AddDays(-i),
                Branch = i % 2 == 0 ? "beta" : "public",
                DepotManifests = new Dictionary<uint, ulong> { [731] = (ulong)(5000 + i) }
            };
            await _storage.SaveBuildSnapshotAsync(snap);
        }

        // Query public branch, page 1, size 2
        var page1 = await _storage.GetBuildHistoryPagedAsync(appId, branch: "public", page: 1, pageSize: 2);
        Assert.Equal(appId, page1.AppId);
        Assert.Equal("public", page1.Branch);
        Assert.Equal(3, page1.TotalItems);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(2, page1.Items.Count);
        Assert.All(page1.Items, s => Assert.Equal("public", s.Branch));

        // Page 2
        var page2 = await _storage.GetBuildHistoryPagedAsync(appId, branch: "public", page: 2, pageSize: 2);
        Assert.Single(page2.Items);
    }

    [Fact]
    public async Task CompareSnapshots_CorrectlyCategorizesDepots()
    {
        uint appId = 10;
        ulong baseBuildId = 100;
        ulong targetBuildId = 200;

        // Base build: depots 11 (m1), 12 (m2), 13 (m3)
        var baseSnap = new BuildSnapshot
        {
            AppId = appId,
            BuildId = baseBuildId,
            Branch = "public",
            Completeness = SnapshotCompleteness.Complete,
            DepotManifests = new Dictionary<uint, ulong>
            {
                [11] = 11111UL,
                [12] = 22222UL,
                [13] = 33333UL
            }
        };

        // Target build:
        // 11 has same manifest 11111 -> Unchanged
        // 12 has new manifest 99999 -> Modified
        // 13 is missing in target -> Removed
        // 14 is new in target -> Added
        var targetSnap = new BuildSnapshot
        {
            AppId = appId,
            BuildId = targetBuildId,
            Branch = "public",
            Completeness = SnapshotCompleteness.Complete,
            DepotManifests = new Dictionary<uint, ulong>
            {
                [11] = 11111UL,
                [12] = 99999UL,
                [14] = 44444UL
            }
        };

        await _storage.SaveBuildSnapshotAsync(baseSnap);
        await _storage.SaveBuildSnapshotAsync(targetSnap);

        var diff = await _storage.CompareSnapshotsAsync(appId, baseBuildId, targetBuildId);

        Assert.Equal(appId, diff.AppId);
        Assert.Equal(baseBuildId, diff.BaseBuildId);
        Assert.Equal(targetBuildId, diff.TargetBuildId);
        Assert.True(diff.IsCompleteComparison);

        Assert.Single(diff.Unchanged);
        Assert.Equal(11u, diff.Unchanged[0].DepotId);
        Assert.Equal(DepotDiffStatus.Unchanged, diff.Unchanged[0].Status);

        Assert.Single(diff.Modified);
        Assert.Equal(12u, diff.Modified[0].DepotId);
        Assert.Equal(22222UL, diff.Modified[0].BaseManifestId);
        Assert.Equal(99999UL, diff.Modified[0].TargetManifestId);
        Assert.Equal(DepotDiffStatus.Modified, diff.Modified[0].Status);

        Assert.Single(diff.Added);
        Assert.Equal(14u, diff.Added[0].DepotId);
        Assert.Equal(44444UL, diff.Added[0].TargetManifestId);
        Assert.Equal(DepotDiffStatus.Added, diff.Added[0].Status);

        Assert.Single(diff.Removed);
        Assert.Equal(13u, diff.Removed[0].DepotId);
        Assert.Equal(DepotDiffStatus.Removed, diff.Removed[0].Status);
    }

    [Fact]
    public async Task CompareSnapshots_PartialSnapshot_SetsIsCompleteComparisonFalse()
    {
        uint appId = 440;
        ulong baseBuildId = 50;
        ulong targetBuildId = 60;

        var baseSnap = new BuildSnapshot
        {
            AppId = appId,
            BuildId = baseBuildId,
            Completeness = SnapshotCompleteness.PartialDelta,
            DepotManifests = new Dictionary<uint, ulong> { [441] = 1UL }
        };

        var targetSnap = new BuildSnapshot
        {
            AppId = appId,
            BuildId = targetBuildId,
            Completeness = SnapshotCompleteness.Complete,
            DepotManifests = new Dictionary<uint, ulong> { [441] = 2UL }
        };

        await _storage.SaveBuildSnapshotAsync(baseSnap);
        await _storage.SaveBuildSnapshotAsync(targetSnap);

        var diff = await _storage.CompareSnapshotsAsync(appId, baseBuildId, targetBuildId);

        Assert.False(diff.IsCompleteComparison);
        Assert.NotNull(diff.WarningMessage);
    }

    [Fact]
    public async Task UpdateDepotIndex_ResolvesAppId_NotZero()
    {
        uint appId = 730;
        uint depotId = 731;

        // Register app in catalog index
        await _storage.UpdateAppRecordAsync(new AppRecord
        {
            AppId = appId,
            Name = "Counter-Strike 2",
            DepotIds = new List<uint> { depotId }
        });

        // Publish/update depot index
        await _storage.UpdateDepotIndexAsync(depotId, 8888888UL);

        var depotRecord = await _storage.GetDepotRecordAsync(depotId);
        Assert.NotNull(depotRecord);
        Assert.Equal(appId, depotRecord.AppId);
        Assert.True(depotRecord.AppId > 0, "DepotRecord.AppId must not be 0 when associated with an app.");
    }
}
