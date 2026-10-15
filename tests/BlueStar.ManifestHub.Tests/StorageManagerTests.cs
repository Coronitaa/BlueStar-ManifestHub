using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Storage;
using BlueStar.ManifestHub.Core.Validation;

namespace BlueStar.ManifestHub.Tests;

public class StorageManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly ManifestStorageManager _storage;

    public StorageManagerTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("storage_test_");
        _storage = new ManifestStorageManager(new StorageOptions { BasePath = _testDir });
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public async Task PublishManifestAsync_CreatesStructuredFilesAndSidecars()
    {
        uint depotId = 440;
        ulong manifestId = 1234567890UL;
        byte[] bytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);

        var result = await _storage.PublishManifestAsync(bytes, depotId, manifestId, source: "DepotBox", contributorPseudonym: "anon_test");

        Assert.True(result.IsSuccess);
        Assert.False(result.IsDuplicate);
        Assert.Equal(ManifestStatus.AvailableAndValidated, result.Status);

        string expectedManifestPath = Path.Combine(_testDir, "manifests", "440", "1234567890.manifest");
        string expectedShaPath = Path.Combine(_testDir, "manifests", "440", "1234567890.sha");
        string expectedMetaPath = Path.Combine(_testDir, "manifests", "440", "1234567890.meta.json");

        Assert.True(File.Exists(expectedManifestPath));
        Assert.True(File.Exists(expectedShaPath));
        Assert.True(File.Exists(expectedMetaPath));

        // Verify SHA sidecar contains SHA256 and SHA1
        string shaContent = await File.ReadAllTextAsync(expectedShaPath);
        Assert.Contains("SHA256:", shaContent);
        Assert.Contains("SHA1:", shaContent);

        // Verify Meta sidecar
        var revision = await _storage.GetManifestRevisionAsync(depotId, manifestId);
        Assert.NotNull(revision);
        Assert.Equal(depotId, revision.DepotId);
        Assert.Equal(manifestId, revision.ManifestId);
        Assert.Equal(ManifestStatus.AvailableAndValidated, revision.Status);
        Assert.Equal("DepotBox", revision.Source);
        Assert.Equal("anon_test", revision.ContributorPseudonym);
    }

    [Fact]
    public async Task PublishManifestAsync_DuplicateManifest_DeduplicatesIdempotently()
    {
        uint depotId = 730;
        ulong manifestId = 5555555555UL;
        byte[] bytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);

        // First publication
        var firstResult = await _storage.PublishManifestAsync(bytes, depotId, manifestId);
        Assert.True(firstResult.IsSuccess);
        Assert.False(firstResult.IsDuplicate);

        DateTime manifestFileTime = File.GetLastWriteTimeUtc(firstResult.ManifestPath!);

        // Second publication of identical (depotId, manifestId)
        var secondResult = await _storage.PublishManifestAsync(bytes, depotId, manifestId);
        Assert.True(secondResult.IsSuccess);
        Assert.True(secondResult.IsDuplicate);
        Assert.Equal(ManifestStatus.AvailableAndValidated, secondResult.Status);
        Assert.Contains("deduplicated", secondResult.Message, StringComparison.OrdinalIgnoreCase);

        // Verify the file was not overwritten
        DateTime afterTime = File.GetLastWriteTimeUtc(secondResult.ManifestPath!);
        Assert.Equal(manifestFileTime, afterTime);
    }

    [Fact]
    public async Task PublishManifestAsync_CorruptPayload_RejectsWithoutWriting()
    {
        uint depotId = 440;
        ulong manifestId = 88888888UL;
        byte[] invalidBytes = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        var result = await _storage.PublishManifestAsync(invalidBytes, depotId, manifestId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
        Assert.False(_storage.ManifestExists(depotId, manifestId));
    }

    [Fact]
    public async Task PublishManifestAsync_CleansUpStagingFileUponCompletion()
    {
        uint depotId = 440;
        ulong manifestId = 99999UL;
        byte[] bytes = TestManifestHelper.CreateValidManifest(depotId, manifestId);

        await _storage.PublishManifestAsync(bytes, depotId, manifestId);

        string stagingDir = _storage.GetStagingDirectory();
        string[] remainingStagingFiles = Directory.GetFiles(stagingDir);
        Assert.Empty(remainingStagingFiles);
    }

    [Fact]
    public async Task PublishManifestAsync_ForceOverwrite_ReplacesExistingFile()
    {
        uint depotId = 440;
        ulong manifestId = 12345UL;
        byte[] bytes1 = TestManifestHelper.CreateValidManifest(depotId, manifestId, payload: "Version 1");
        byte[] bytes2 = TestManifestHelper.CreateValidManifest(depotId, manifestId, payload: "Version 2");

        var first = await _storage.PublishManifestAsync(bytes1, depotId, manifestId);
        Assert.False(first.IsDuplicate);

        var second = await _storage.PublishManifestAsync(bytes2, depotId, manifestId, force: true);
        Assert.True(second.IsSuccess);
        Assert.False(second.IsDuplicate);

        var revision = await _storage.GetManifestRevisionAsync(depotId, manifestId);
        Assert.NotNull(revision);
        Assert.Equal(ManifestValidator.ComputeSha256(bytes2), revision.Sha256);
    }

    [Fact]
    public async Task BootstrapAppIndexIfMissingAsync_InitializesCatalogWithBaseApps()
    {
        string appIndexPath = _storage.GetAppIndexPath();
        Assert.False(File.Exists(appIndexPath));

        int bootstrapped = await _storage.BootstrapAppIndexIfMissingAsync();
        Assert.Equal(4, bootstrapped);
        Assert.True(File.Exists(appIndexPath));

        var cs2 = await _storage.GetAppRecordAsync(730);
        Assert.NotNull(cs2);
        Assert.Equal("Counter-Strike 2", cs2.Name);
        Assert.Contains(731u, cs2.DepotIds);

        var dota = await _storage.GetAppRecordAsync(570);
        Assert.NotNull(dota);
        Assert.Equal("Dota 2", dota.Name);

        var tf2 = await _storage.GetAppRecordAsync(440);
        Assert.NotNull(tf2);
        Assert.Equal("Team Fortress 2", tf2.Name);

        var terraria = await _storage.GetAppRecordAsync(105600);
        Assert.NotNull(terraria);
        Assert.Equal("Terraria", terraria.Name);

        // Calling again when already exists should be idempotent and return 0
        int secondRun = await _storage.BootstrapAppIndexIfMissingAsync();
        Assert.Equal(0, secondRun);
    }
}
