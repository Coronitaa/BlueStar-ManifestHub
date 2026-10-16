using System.Text.Json;
using BlueStar.ManifestHub.Cli;
using BlueStar.ManifestHub.Core.Demand;
using BlueStar.ManifestHub.Core.Ledger;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Serialization;
using BlueStar.ManifestHub.Core.Storage;
using Xunit;

namespace BlueStar.ManifestHub.Tests;

public sealed class DemandAndDeltaTests : IDisposable
{
    private readonly string _testDir;
    private readonly ManifestStorageManager _storage;
    private readonly StardustLedgerManager _ledger;
    private readonly DemandEngine _demandEngine;

    public DemandAndDeltaTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("demand_delta_test_");
        var options = new StorageOptions { BasePath = _testDir };
        _storage = new ManifestStorageManager(options);
        _storage.EnsureDirectoryStructure();
        _ledger = new StardustLedgerManager(options);
        _demandEngine = new DemandEngine(_storage, defaultReward: 100);
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    #region Part 1: Versionado por Fecha de Parche y Cálculo de Deltas

    [Fact]
    public void BuildSnapshot_PatchDate_DerivesFromCreationTimeOrTimestamp()
    {
        var creationDate = new DateTimeOffset(2024, 6, 15, 14, 30, 0, TimeSpan.Zero);
        var snapshot1 = new BuildSnapshot
        {
            AppId = 440,
            BuildId = 1,
            CreationTime = creationDate
        };
        Assert.Equal("2024-06-15", snapshot1.PatchDate);

        var timestampDate = new DateTimeOffset(2023, 11, 20, 8, 0, 0, TimeSpan.Zero);
        var snapshot2 = new BuildSnapshot
        {
            AppId = 440,
            BuildId = 2,
            Timestamp = timestampDate
        };
        Assert.Equal("2023-11-20", snapshot2.PatchDate);

        var snapshot3 = new BuildSnapshot
        {
            AppId = 440,
            BuildId = 3,
            CreationTime = creationDate,
            PatchDate = "custom-2025-01-01"
        };
        Assert.Equal("custom-2025-01-01", snapshot3.PatchDate);
    }

    [Fact]
    public async Task BuildSnapshot_ChangedDepots_CalculatesDeltaChronologically()
    {
        uint appId = 440;

        // Version 1: Initial release with Depot 441 and Depot 442
        var snapshot1 = new BuildSnapshot
        {
            AppId = appId,
            BuildId = 101,
            CreationTime = new DateTimeOffset(2024, 1, 10, 0, 0, 0, TimeSpan.Zero),
            Timestamp = new DateTimeOffset(2024, 1, 10, 0, 0, 0, TimeSpan.Zero),
            DepotManifests = new Dictionary<uint, ulong>
            {
                [441] = 111111UL,
                [442] = 222222UL
            }
        };

        // Version 2: Depot 441 changed manifest; Depot 442 unchanged; Depot 443 added
        var snapshot2 = new BuildSnapshot
        {
            AppId = appId,
            BuildId = 102,
            CreationTime = new DateTimeOffset(2024, 2, 10, 0, 0, 0, TimeSpan.Zero),
            Timestamp = new DateTimeOffset(2024, 2, 10, 0, 0, 0, TimeSpan.Zero),
            DepotManifests = new Dictionary<uint, ulong>
            {
                [441] = 999999UL, // Changed
                [442] = 222222UL, // Unchanged
                [443] = 333333UL  // New
            }
        };

        // Version 3: Only Depot 442 changed
        var snapshot3 = new BuildSnapshot
        {
            AppId = appId,
            BuildId = 103,
            CreationTime = new DateTimeOffset(2024, 3, 10, 0, 0, 0, TimeSpan.Zero),
            Timestamp = new DateTimeOffset(2024, 3, 10, 0, 0, 0, TimeSpan.Zero),
            DepotManifests = new Dictionary<uint, ulong>
            {
                [441] = 999999UL, // Unchanged
                [442] = 888888UL, // Changed
                [443] = 333333UL  // Unchanged
            }
        };

        await _storage.SaveBuildSnapshotAsync(snapshot1);
        await _storage.SaveBuildSnapshotAsync(snapshot2);
        await _storage.SaveBuildSnapshotAsync(snapshot3);

        var retrieved = await _storage.GetBuildSnapshotsAsync(appId);
        Assert.Equal(3, retrieved.Count);

        // Snapshots are stored descending by timestamp: [v3, v2, v1]
        var v3 = retrieved[0];
        var v2 = retrieved[1];
        var v1 = retrieved[2];

        // V1: All depots are newly introduced
        Assert.Equal(2, v1.ChangedDepots.Count);
        Assert.Contains(441u, v1.ChangedDepots);
        Assert.Contains(442u, v1.ChangedDepots);

        // V2: 441 changed, 443 introduced. 442 was unchanged.
        Assert.Equal(2, v2.ChangedDepots.Count);
        Assert.Contains(441u, v2.ChangedDepots);
        Assert.Contains(443u, v2.ChangedDepots);
        Assert.DoesNotContain(442u, v2.ChangedDepots);

        // V3: Only 442 changed. 441 and 443 were unchanged.
        Assert.Single(v3.ChangedDepots);
        Assert.Contains(442u, v3.ChangedDepots);
        Assert.DoesNotContain(441u, v3.ChangedDepots);
        Assert.DoesNotContain(443u, v3.ChangedDepots);
    }

    [Fact]
    public async Task BuildSnapshot_GetByPatchDate_ReturnsMatchingSnapshot()
    {
        uint appId = 730;

        var snap = new BuildSnapshot
        {
            AppId = appId,
            BuildId = 5001,
            CreationTime = new DateTimeOffset(2024, 5, 25, 12, 0, 0, TimeSpan.Zero),
            DepotManifests = new Dictionary<uint, ulong> { [731] = 55555UL }
        };

        await _storage.SaveBuildSnapshotAsync(snap);

        var found = await _storage.GetSnapshotByPatchDateAsync(appId, "2024-05-25");
        Assert.NotNull(found);
        Assert.Equal((ulong)5001, found.BuildId);

        var notFound = await _storage.GetSnapshotByPatchDateAsync(appId, "2020-01-01");
        Assert.Null(notFound);
    }

    #endregion

    #region Part 2: Motor de Demanda (indices/demand.json)

    [Fact]
    public async Task DemandEngine_GenerateDemand_IdentifiesMissingManifests()
    {
        uint appId = 440;
        uint depotId = 441;
        ulong missingManifest1 = 111111111UL;
        ulong missingManifest2 = 222222222UL;
        ulong presentManifest = 333333333UL;

        // Register App
        await _storage.RegisterAppAsync(new AppRecord
        {
            AppId = appId,
            Name = "Team Fortress 2",
            DepotIds = new[] { depotId }
        });

        // Register BuildSnapshot with missingManifest1
        await _storage.SaveBuildSnapshotAsync(new BuildSnapshot
        {
            AppId = appId,
            BuildId = 1,
            Timestamp = DateTimeOffset.UtcNow,
            DepotManifests = new Dictionary<uint, ulong> { [depotId] = missingManifest1 }
        });

        // Place presentManifest on disk
        byte[] manifestData = TestManifestHelper.CreateValidManifest(depotId, presentManifest);
        await _storage.PublishManifestAsync(manifestData, depotId, presentManifest, source: "test");

        // Update depot index with both presentManifest and missingManifest2
        var depotRecord = new DepotRecord
        {
            DepotId = depotId,
            AppId = appId,
            LatestManifestId = missingManifest2,
            ManifestIds = new[] { presentManifest, missingManifest2 }
        };
        // Update depot index by saving
        string depotPath = _storage.GetDepotIndexPath();
        var depotDict = new Dictionary<string, DepotRecord> { [depotId.ToString()] = depotRecord };
        await File.WriteAllTextAsync(depotPath, HubJsonSerializer.Serialize(depotDict));

        // Generate demand index
        var demandIndex = await _demandEngine.GenerateDemandAsync(new[] { appId });

        Assert.NotNull(demandIndex);
        Assert.Equal(2, demandIndex.TotalMissing);
        Assert.Equal(2, demandIndex.Items.Count);

        // Missing manifests must be present
        Assert.Contains(demandIndex.Items, d => d.DepotId == depotId && d.ManifestId == missingManifest1);
        Assert.Contains(demandIndex.Items, d => d.DepotId == depotId && d.ManifestId == missingManifest2);

        // Present manifest must NOT be in demand
        Assert.DoesNotContain(demandIndex.Items, d => d.ManifestId == presentManifest);

        // Verify StardustReward
        Assert.All(demandIndex.Items, d => Assert.Equal(100, d.StardustReward));

        // Verify grouped Apps structure
        string appKey = appId.ToString();
        string depotKey = depotId.ToString();
        Assert.True(demandIndex.Apps.ContainsKey(appKey));
        Assert.True(demandIndex.Apps[appKey].ContainsKey(depotKey));
        Assert.Equal(2, demandIndex.Apps[appKey][depotKey].Count);

        // Verify persisted file exists on disk
        Assert.True(File.Exists(_storage.GetDemandIndexPath()));
    }

    [Fact]
    public async Task DemandEngine_RemoveDemand_UpdatesDemandIndexAccurately()
    {
        uint appId = 440;
        uint depotId = 441;
        ulong manifestId = 7777777UL;

        var demandIndex = DemandIndex.FromRecords(new[]
        {
            new DemandRecord { AppId = appId, DepotId = depotId, ManifestId = manifestId, StardustReward = 100 }
        });
        await _storage.SaveDemandIndexAsync(demandIndex);

        var initial = await _demandEngine.GetDemandIndexAsync();
        Assert.NotNull(initial);
        Assert.Equal(1, initial.TotalMissing);

        bool removed = await _demandEngine.RemoveDemandAsync(depotId, manifestId);
        Assert.True(removed);

        var updated = await _demandEngine.GetDemandIndexAsync();
        Assert.NotNull(updated);
        Assert.Equal(0, updated.TotalMissing);
        Assert.Empty(updated.Items);
    }

    #endregion

    #region Part 3: Ledger de Stardust y Persistencia en stardust.json

    [Fact]
    public async Task StardustLedger_PersistsToStardustJson_AndVerifiesIntegrity()
    {
        string contributor = "anon_explorer_42";
        uint depotId = 441;
        ulong manifestId = 88888888UL;

        var entry = await _ledger.AwardPointsForManifestAsync(
            contributorPseudonym: contributor,
            depotId: depotId,
            manifestId: manifestId,
            status: ManifestStatus.AvailableAndValidated,
            points: 100,
            reason: "Demand satisfied");

        Assert.NotNull(entry);
        Assert.Equal(100, entry.Points);

        // Check that ledger/stardust.json exists
        Assert.True(File.Exists(_ledger.StardustJsonPath));

        // Read and deserialize ledger/stardust.json directly
        string json = await File.ReadAllTextAsync(_ledger.StardustJsonPath);
        var entries = HubJsonSerializer.Deserialize<List<StardustLedgerEntry>>(json);
        Assert.NotNull(entries);
        Assert.Single(entries);
        Assert.Equal(entry.TransactionId, entries[0].TransactionId);
        Assert.Equal(contributor, entries[0].ContributorPseudonym);

        // Verify integrity
        var integrity = await _ledger.VerifyIntegrityAsync();
        Assert.True(integrity.IsValid);
        Assert.Equal(1, integrity.EntryCount);
    }

    #endregion

    #region Part 4: CLI --verify-contribution y --generate-demand

    [Fact]
    public async Task Program_VerifyContribution_ValidManifest_SucceedsAndAwardsStardust()
    {
        uint appId = 440;
        uint depotId = 441;
        ulong manifestId = 987654321UL;

        // Populate demand index with this manifest
        var initialDemand = DemandIndex.FromRecords(new[]
        {
            new DemandRecord { AppId = appId, DepotId = depotId, ManifestId = manifestId, StardustReward = 150 }
        });
        await _storage.SaveDemandIndexAsync(initialDemand);

        // Create valid synthetic manifest file in temp staging location
        byte[] manifestData = TestManifestHelper.CreateValidManifest(depotId, manifestId);
        string tempManifestFile = Path.Combine(_testDir, "incoming_test.manifest");
        await File.WriteAllBytesAsync(tempManifestFile, manifestData);

        // Run CLI command in the test directory context
        string previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testDir);

            string[] cliArgs = new[]
            {
                "--verify-contribution", tempManifestFile,
                "--contributor", "anon_hero_77",
                "--app", appId.ToString(),
                "--depot", depotId.ToString()
            };

            int exitCode = await Program.Main(cliArgs);
            Assert.Equal(0, exitCode);

            // Canonical path must exist
            string canonicalPath = _storage.GetManifestPath(depotId, manifestId);
            Assert.True(File.Exists(canonicalPath));

            // Sidecars must exist
            Assert.True(File.Exists(_storage.GetShaSidecarPath(depotId, manifestId)));
            Assert.True(File.Exists(_storage.GetMetaSidecarPath(depotId, manifestId)));

            // Input file must have been moved / cleaned up
            Assert.False(File.Exists(tempManifestFile));

            // Demand index must have removed the satisfied manifest
            var updatedDemand = await _storage.GetDemandIndexAsync();
            Assert.NotNull(updatedDemand);
            Assert.DoesNotContain(updatedDemand.Items, d => d.DepotId == depotId && d.ManifestId == manifestId);

            // Stardust points (150 from demand) must be credited in ledger
            int balance = await _ledger.GetContributorBalanceAsync("anon_hero_77");
            Assert.Equal(150, balance);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    [Fact]
    public async Task Program_VerifyContribution_RejectsSteamIdContributor()
    {
        byte[] manifestData = TestManifestHelper.CreateValidManifest(441, 12345);
        string tempFile = Path.Combine(_testDir, "test_steamid.manifest");
        await File.WriteAllBytesAsync(tempFile, manifestData);

        string previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testDir);

            string[] cliArgs = new[]
            {
                "--verify-contribution", tempFile,
                "--contributor", "76561198012345678" // Prohibited SteamID
            };

            int exitCode = await Program.Main(cliArgs);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    [Fact]
    public async Task Program_VerifyContribution_RejectsCorruptManifest()
    {
        string corruptFile = Path.Combine(_testDir, "corrupted.manifest");
        await File.WriteAllBytesAsync(corruptFile, new byte[] { 0x00, 0x11, 0x22, 0x33 });

        string previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testDir);

            string[] cliArgs = new[]
            {
                "--verify-contribution", corruptFile,
                "--contributor", "anon_user_99"
            };

            int exitCode = await Program.Main(cliArgs);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    [Fact]
    public async Task Program_GenerateDemand_GeneratesDemandIndexFile()
    {
        string previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testDir);

            string[] cliArgs = new[] { "--generate-demand" };
            int exitCode = await Program.Main(cliArgs);
            Assert.Equal(0, exitCode);

            Assert.True(File.Exists(_storage.GetDemandIndexPath()));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    #endregion
}
