using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Storage;

namespace BlueStar.ManifestHub.Tests;

public class SnapshotAndCoverageTests : IDisposable
{
    private readonly string _testDir;
    private readonly ManifestStorageManager _storage;

    public SnapshotAndCoverageTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("snapshot_test_");
        _storage = new ManifestStorageManager(new StorageOptions { BasePath = _testDir });
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public async Task BuildSnapshots_SaveAndRetrieve_OrdersByTimestampDescending()
    {
        uint appId = 440;

        var snapshot1 = new BuildSnapshot
        {
            AppId = appId,
            BuildId = 10001,
            Timestamp = DateTimeOffset.UtcNow.AddHours(-2),
            Branch = "public",
            Description = "Older Build",
            DepotManifests = new Dictionary<uint, ulong> { [441] = 111111111UL }
        };

        var snapshot2 = new BuildSnapshot
        {
            AppId = appId,
            BuildId = 10002,
            Timestamp = DateTimeOffset.UtcNow,
            Branch = "public",
            Description = "Latest Build",
            DepotManifests = new Dictionary<uint, ulong> { [441] = 222222222UL, [442] = 333333333UL }
        };

        await _storage.SaveBuildSnapshotAsync(snapshot1);
        await _storage.SaveBuildSnapshotAsync(snapshot2);

        var allSnapshots = await _storage.GetBuildSnapshotsAsync(appId);
        Assert.Equal(2, allSnapshots.Count);
        Assert.Equal((ulong)10002, allSnapshots[0].BuildId);
        Assert.Equal((ulong)10001, allSnapshots[1].BuildId);

        var latest = await _storage.GetLatestBuildSnapshotAsync(appId);
        Assert.NotNull(latest);
        Assert.Equal((ulong)10002, latest.BuildId);
        Assert.Equal(2, latest.DepotManifests.Count);
    }

    [Fact]
    public async Task CoverageRecord_SaveAndRetrieve_CalculatesRatioProperly()
    {
        uint appId = 730;
        uint depotId = 731;

        var coverage = new CoverageRecord
        {
            AppId = appId,
            DepotId = depotId,
            TotalRevisions = 10,
            AvailableRevisions = 8,
            MissingRevisions = 2,
            LatestAvailableManifestId = 999999999UL
        };

        Assert.Equal(0.8, coverage.CoverageRatio);

        await _storage.UpdateCoverageRecordAsync(coverage);

        var retrieved = await _storage.GetCoverageRecordAsync(appId, depotId);
        Assert.NotNull(retrieved);
        Assert.Equal(appId, retrieved.AppId);
        Assert.Equal(depotId, retrieved.DepotId);
        Assert.Equal(10, retrieved.TotalRevisions);
        Assert.Equal(8, retrieved.AvailableRevisions);
        Assert.Equal(2, retrieved.MissingRevisions);
        Assert.Equal(0.8, retrieved.CoverageRatio);
    }

    [Fact]
    public async Task ComputeAndSaveCoverageAsync_EvaluatesPhysicalPresenceAccurately()
    {
        uint appId = 440;
        uint depotId = 441;

        // Publish 2 out of 3 manifests
        ulong manifest1 = 10001UL;
        ulong manifest2 = 10002UL;
        ulong manifest3 = 10003UL;

        await _storage.PublishManifestAsync(TestManifestHelper.CreateValidManifest(depotId, manifest1), depotId, manifest1);
        await _storage.PublishManifestAsync(TestManifestHelper.CreateValidManifest(depotId, manifest2), depotId, manifest2);

        var knownIds = new List<ulong> { manifest1, manifest2, manifest3 };

        var record = await _storage.ComputeAndSaveCoverageAsync(appId, depotId, knownIds);

        Assert.Equal(3, record.TotalRevisions);
        Assert.Equal(2, record.AvailableRevisions);
        Assert.Equal(1, record.MissingRevisions);
        Assert.Equal(0.6667, record.CoverageRatio);
        Assert.Equal(manifest2, record.LatestAvailableManifestId);

        var saved = await _storage.GetCoverageRecordAsync(appId, depotId);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.AvailableRevisions);
    }

    [Fact]
    public async Task CatalogIndices_AppAndDepotRegistration_PersistsCorrectly()
    {
        var app = new AppRecord
        {
            AppId = 440,
            Name = "Team Fortress 2",
            DepotIds = new List<uint> { 441, 442, 443 },
            Metadata = new Dictionary<string, string> { ["genre"] = "Action" }
        };

        await _storage.RegisterAppAsync(app);

        var retrievedApp = await _storage.GetAppRecordAsync(440);
        Assert.NotNull(retrievedApp);
        Assert.Equal("Team Fortress 2", retrievedApp.Name);
        Assert.Equal(3, retrievedApp.DepotIds.Count);

        // Test depot index automatically updated on publish
        ulong manifestId = 55555UL;
        await _storage.PublishManifestAsync(TestManifestHelper.CreateValidManifest(441, manifestId), 441, manifestId);

        var depotRecord = await _storage.GetDepotRecordAsync(441);
        Assert.NotNull(depotRecord);
        Assert.Equal((uint)441, depotRecord.DepotId);
        Assert.Equal(manifestId, depotRecord.LatestManifestId);
        Assert.Contains(manifestId, depotRecord.ManifestIds);
    }
}
