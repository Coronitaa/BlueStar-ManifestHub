using BlueStar.ManifestHub.Core.Ledger;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Storage;

namespace BlueStar.ManifestHub.Tests;

public class StardustLedgerTests : IDisposable
{
    private readonly string _testDir;
    private readonly StardustLedgerManager _ledger;

    public StardustLedgerTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("ledger_test_");
        _ledger = new StardustLedgerManager(new StorageOptions { BasePath = _testDir });
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public async Task AwardPointsForManifestAsync_AvailableAndValidated_AwardsPointsSuccessfully()
    {
        string pseudonym = "anon_contributor_01";
        uint depotId = 440;
        ulong manifestId = 1234567890123UL;

        var entry = await _ledger.AwardPointsForManifestAsync(
            pseudonym,
            depotId,
            manifestId,
            ManifestStatus.AvailableAndValidated,
            points: 15,
            reason: "Initial ingest of depot manifest");

        Assert.NotNull(entry);
        Assert.Equal(pseudonym, entry.ContributorPseudonym);
        Assert.Equal($"manifest:{depotId}:{manifestId}", entry.ArtifactKey);
        Assert.Equal(15, entry.Points);
        Assert.NotEmpty(entry.EntryHash);
        Assert.NotEmpty(entry.PreviousHash);

        int balance = await _ledger.GetContributorBalanceAsync(pseudonym);
        Assert.Equal(15, balance);

        var integrity = await _ledger.VerifyIntegrityAsync();
        Assert.True(integrity.IsValid);
        Assert.Equal(1, integrity.EntryCount);
    }

    [Theory]
    [InlineData(ManifestStatus.Pending)]
    [InlineData(ManifestStatus.Missing)]
    [InlineData(ManifestStatus.Corrupted)]
    [InlineData(ManifestStatus.Inaccessible)]
    [InlineData(ManifestStatus.Restricted)]
    [InlineData(ManifestStatus.Superseded)]
    [InlineData(ManifestStatus.Unverified)]
    public async Task AwardPointsForManifestAsync_UnvalidatedStatus_ThrowsLedgerValidationException(ManifestStatus invalidStatus)
    {
        string pseudonym = "anon_contributor_02";

        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            _ledger.AwardPointsForManifestAsync(
                pseudonym,
                440,
                99999999UL,
                invalidStatus,
                points: 10));

        int balance = await _ledger.GetContributorBalanceAsync(pseudonym);
        Assert.Equal(0, balance);
    }

    [Fact]
    public async Task AwardPointsForManifestAsync_DuplicateArtifact_RejectsWithDuplicateArtifactRewardException()
    {
        uint depotId = 730;
        ulong manifestId = 44444444UL;

        // First contributor ingests artifact
        await _ledger.AwardPointsForManifestAsync(
            "contributor_alpha",
            depotId,
            manifestId,
            ManifestStatus.AvailableAndValidated,
            points: 10);

        // Second contributor attempts to submit identical artifact
        var ex = await Assert.ThrowsAsync<DuplicateArtifactRewardException>(() =>
            _ledger.AwardPointsForManifestAsync(
                "contributor_beta",
                depotId,
                manifestId,
                ManifestStatus.AvailableAndValidated,
                points: 10));

        Assert.Equal($"manifest:{depotId}:{manifestId}", ex.ArtifactKey);
    }

    [Fact]
    public async Task AwardPointsForManifestAsync_IdenticalTransactionId_ReturnsExistingEntryIdempotently()
    {
        string txId = "tx_deterministic_uuid_12345";
        string pseudonym = "anon_idempotent_user";
        uint depotId = 440;
        ulong manifestId = 77777777UL;

        var entry1 = await _ledger.AwardPointsForManifestAsync(
            pseudonym,
            depotId,
            manifestId,
            ManifestStatus.AvailableAndValidated,
            points: 20,
            transactionId: txId);

        // Replay same transaction
        var entry2 = await _ledger.AwardPointsForManifestAsync(
            pseudonym,
            depotId,
            manifestId,
            ManifestStatus.AvailableAndValidated,
            points: 20,
            transactionId: txId);

        Assert.Equal(entry1.TransactionId, entry2.TransactionId);
        Assert.Equal(entry1.EntryHash, entry2.EntryHash);

        // Balance should only be credited once (20 points, not 40)
        int balance = await _ledger.GetContributorBalanceAsync(pseudonym);
        Assert.Equal(20, balance);
    }

    [Theory]
    [InlineData("76561198012345678")]                  // SteamID64
    [InlineData("user-76561198012345678")]             // Wrapped SteamID64
    [InlineData("steam:76561198012345678")]            // Prefixed SteamID64
    [InlineData("https://steamcommunity.com/profiles/76561198012345678")] // Steam profile URL
    [InlineData("STEAM_0:1:12345678")]                  // SteamID2
    [InlineData("STEAM_1:0:87654321")]                  // SteamID2
    [InlineData("[U:1:12345678]")]                      // SteamID3
    [InlineData("node_[U:1:12345678]")]                 // Wrapped SteamID3
    [InlineData("user_steamid_123")]                    // Contains 'steamid'
    [InlineData("")]                                    // Empty
    [InlineData("   ")]                                 // Whitespace
    public async Task AwardPointsForManifestAsync_SteamIdOrEmpty_ThrowsInvalidContributorIdentityException(string invalidIdentity)
    {
        await Assert.ThrowsAsync<InvalidContributorIdentityException>(() =>
            _ledger.AwardPointsForManifestAsync(
                invalidIdentity,
                440,
                112233UL,
                ManifestStatus.AvailableAndValidated,
                points: 10));
    }

    [Fact]
    public async Task PublishManifestAsync_RejectsSteamIdContributorPseudonym()
    {
        var storage = new Core.Storage.ManifestStorageManager(new Core.Storage.StorageOptions { BasePath = _testDir });
        byte[] validManifest = TestManifestHelper.CreateValidManifest(440, 99999);

        // Attempt publishing with a wrapped SteamID as contributor pseudonym
        var result = await storage.PublishManifestAsync(
            validManifest,
            440,
            99999,
            contributorPseudonym: "steam:76561198012345678");

        Assert.False(result.IsSuccess);
        Assert.Equal(ManifestStatus.Restricted, result.Status);
        Assert.Contains("Invalid contributor identity", result.Message);
    }

    [Fact]
    public async Task VerifyIntegrityAsync_TamperedLedgerFile_DetectsTampering()
    {
        // 1. Award multiple points in sequence
        await _ledger.AwardPointsForManifestAsync("contributor_01", 100, 1001, ManifestStatus.AvailableAndValidated, 10);
        await _ledger.AwardPointsForManifestAsync("contributor_02", 100, 1002, ManifestStatus.AvailableAndValidated, 15);
        await _ledger.AwardPointsForManifestAsync("contributor_03", 100, 1003, ManifestStatus.AvailableAndValidated, 20);

        var initialIntegrity = await _ledger.VerifyIntegrityAsync();
        Assert.True(initialIntegrity.IsValid);
        Assert.Equal(3, initialIntegrity.EntryCount);

        // 2. Tamper with the ledger file (change points on line 2)
        string[] lines = await File.ReadAllLinesAsync(_ledger.LedgerFilePath);
        Assert.Contains("\"points\":15", lines[1]);
        lines[1] = lines[1].Replace("\"points\":15", "\"points\":999");
        await File.WriteAllLinesAsync(_ledger.LedgerFilePath, lines);

        // 3. Integrity check must now fail
        var tamperedIntegrity = await _ledger.VerifyIntegrityAsync();
        Assert.False(tamperedIntegrity.IsValid);
        Assert.Contains("tampering", tamperedIntegrity.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
