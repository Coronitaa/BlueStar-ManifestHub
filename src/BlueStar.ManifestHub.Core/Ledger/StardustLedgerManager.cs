using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Serialization;
using BlueStar.ManifestHub.Core.Storage;

namespace BlueStar.ManifestHub.Core.Ledger;

/// <summary>
/// Immutable, cryptographically hashed ledger for Stardust points.
/// Enforces pseudonymity (rejection of SteamIDs), validation-only accreditation,
/// artifact-level deduplication, and transaction idempotence.
/// </summary>
public sealed class StardustLedgerManager
{
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly StorageOptions _options;
    private readonly SemaphoreSlim _ledgerLock = new(1, 1);

    // SteamID detection patterns (unanchored to prevent wrapped/prefixed evasions)
    private static readonly Regex SteamId64Regex = new(@"(?:^|\D)(7656119\d{10})(?:\D|$)", RegexOptions.Compiled);
    private static readonly Regex SteamId2Regex = new(@"STEAM_[0-5]:[0-1]:\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SteamId3Regex = new(@"\[[UIMGCAS]:1:\d+\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Any17DigitRegex = new(@"\b(7656\d{13})\b", RegexOptions.Compiled);

    public StardustLedgerManager(StorageOptions? options = null)
    {
        _options = options ?? new StorageOptions();
        Directory.CreateDirectory(Path.Combine(_options.BasePath, _options.LedgerDirectoryName));
    }

    public string LedgerFilePath =>
        Path.Combine(_options.BasePath, _options.LedgerDirectoryName, "stardust-ledger.jsonl");

    /// <summary>
    /// Awards Stardust points for a validated manifest revision.
    /// Strict requirements:
    /// - Contributor must be pseudonymous (no SteamIDs).
    /// - Status must be AvailableAndValidated.
    /// - Artifact (DepotId, ManifestId) must not have already been rewarded.
    /// - Duplicate transactionId returns existing entry idempotently.
    /// </summary>
    public async Task<StardustLedgerEntry> AwardPointsForManifestAsync(
        string contributorPseudonym,
        uint depotId,
        ulong manifestId,
        ManifestStatus status,
        int points = 10,
        string? transactionId = null,
        string reason = "Manifest validated and ingested",
        CancellationToken cancellationToken = default)
    {
        // 1. Enforce pseudonymity
        ValidateContributorPseudonym(contributorPseudonym);

        // 2. Accreditation only upon validation
        if (status != ManifestStatus.AvailableAndValidated)
        {
            throw new LedgerValidationException(
                $"Stardust points can only be awarded for AvailableAndValidated manifests. Current status is '{status}'.");
        }

        if (points <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(points), "Points awarded must be positive.");
        }

        string artifactKey = $"manifest:{depotId}:{manifestId}";
        string txId = string.IsNullOrWhiteSpace(transactionId) ? Guid.NewGuid().ToString("N") : transactionId.Trim();

        await _ledgerLock.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAllEntriesInternalAsync(cancellationToken);

            // 3. Check transaction idempotency
            var existingTx = entries.FirstOrDefault(e => e.TransactionId == txId);
            if (existingTx != null)
            {
                return existingTx;
            }

            // 4. Reject duplicate rewards for the same artifact
            var existingArtifactReward = entries.FirstOrDefault(e => string.Equals(e.ArtifactKey, artifactKey, StringComparison.OrdinalIgnoreCase));
            if (existingArtifactReward != null)
            {
                throw new DuplicateArtifactRewardException(
                    artifactKey,
                    $"Points have already been awarded for artifact '{artifactKey}' in transaction '{existingArtifactReward.TransactionId}'. Duplicate rewards are prohibited.");
            }

            // 5. Hash chaining
            string previousHash = entries.Count > 0 ? entries[^1].EntryHash : GenesisHash;
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;

            string entryHash = ComputeEntryHash(previousHash, txId, contributorPseudonym, artifactKey, points, timestamp, reason);

            var newEntry = new StardustLedgerEntry
            {
                TransactionId = txId,
                ContributorPseudonym = contributorPseudonym,
                ArtifactKey = artifactKey,
                Points = points,
                Reason = reason,
                Timestamp = timestamp,
                PreviousHash = previousHash,
                EntryHash = entryHash
            };

            // 6. Append to immutable ledger JSONL
            string jsonLine = HubJsonSerializer.SerializeCompact(newEntry) + Environment.NewLine;
            await File.AppendAllTextAsync(LedgerFilePath, jsonLine, cancellationToken);

            return newEntry;
        }
        finally
        {
            _ledgerLock.Release();
        }
    }

    /// <summary>
    /// Validates that the contributor identity is a valid pseudonym and NOT a SteamID.
    /// </summary>
    public static void ValidateContributorPseudonym(string pseudonym)
    {
        if (string.IsNullOrWhiteSpace(pseudonym))
        {
            throw new InvalidContributorIdentityException("Contributor pseudonym cannot be empty or whitespace.");
        }

        string trimmed = pseudonym.Trim();

        if (SteamId64Regex.IsMatch(trimmed) ||
            SteamId2Regex.IsMatch(trimmed) ||
            SteamId3Regex.IsMatch(trimmed) ||
            trimmed.Contains("steamid", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("profiles/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("steam:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidContributorIdentityException(
                $"Identity '{trimmed}' resembles a Valve SteamID or Steam account format. SteamIDs are strictly prohibited in BlueStar Manifest Hub to safeguard user privacy. Use an anonymous pseudonym or hash.");
        }

        // Additional safeguard: inspect any 17-digit numeric substrings for SteamID64 range
        var matches = Any17DigitRegex.Matches(trimmed);
        foreach (Match match in matches)
        {
            if (ulong.TryParse(match.Value, out ulong numericId) &&
                numericId >= 76561197960265728UL && numericId <= 76561202255233023UL)
            {
                throw new InvalidContributorIdentityException(
                    $"Identity '{trimmed}' embeds a SteamID64 identifier ({numericId}). Strictly prohibited to preserve user privacy.");
            }
        }
    }

    /// <summary>
    /// Computes deterministic SHA-256 hash for an entry chained to previous hash.
    /// </summary>
    public static string ComputeEntryHash(
        string previousHash,
        string transactionId,
        string contributorPseudonym,
        string artifactKey,
        int points,
        DateTimeOffset timestamp,
        string reason)
    {
        string raw = $"{previousHash}|{transactionId}|{contributorPseudonym}|{artifactKey}|{points}|{timestamp.ToUnixTimeSeconds()}|{reason}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies cryptographic integrity of the entire ledger chain.
    /// </summary>
    public async Task<LedgerIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LedgerFilePath))
        {
            return LedgerIntegrityResult.Success(0, GenesisHash);
        }

        await _ledgerLock.WaitAsync(cancellationToken);
        try
        {
            var lines = await File.ReadAllLinesAsync(LedgerFilePath, cancellationToken);
            string expectedPreviousHash = GenesisHash;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                StardustLedgerEntry? entry;
                try
                {
                    entry = HubJsonSerializer.Deserialize<StardustLedgerEntry>(line);
                }
                catch (Exception ex)
                {
                    return LedgerIntegrityResult.Failure($"Corrupt JSON on line {i + 1}: {ex.Message}", i);
                }

                if (entry == null)
                {
                    return LedgerIntegrityResult.Failure($"Null entry on line {i + 1}.", i);
                }

                if (!string.Equals(entry.PreviousHash, expectedPreviousHash, StringComparison.OrdinalIgnoreCase))
                {
                    return LedgerIntegrityResult.Failure(
                        $"Broken hash chain at line {i + 1}: Expected PreviousHash '{expectedPreviousHash}', found '{entry.PreviousHash}'.",
                        i,
                        entry.PreviousHash);
                }

                string computedHash = ComputeEntryHash(
                    entry.PreviousHash,
                    entry.TransactionId,
                    entry.ContributorPseudonym,
                    entry.ArtifactKey,
                    entry.Points,
                    entry.Timestamp,
                    entry.Reason);

                if (!string.Equals(entry.EntryHash, computedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return LedgerIntegrityResult.Failure(
                        $"Hash tampering detected at line {i + 1}: Stored EntryHash '{entry.EntryHash}', computed '{computedHash}'.",
                        i,
                        entry.EntryHash);
                }

                expectedPreviousHash = entry.EntryHash;
            }

            return LedgerIntegrityResult.Success(lines.Length, expectedPreviousHash);
        }
        finally
        {
            _ledgerLock.Release();
        }
    }

    /// <summary>
    /// Returns all ledger entries for a given contributor pseudonym.
    /// </summary>
    public async Task<IReadOnlyList<StardustLedgerEntry>> GetEntriesForContributorAsync(
        string contributorPseudonym,
        CancellationToken cancellationToken = default)
    {
        var all = await ReadAllEntriesAsync(cancellationToken);
        return all.Where(e => string.Equals(e.ContributorPseudonym, contributorPseudonym, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Calculates current Stardust balance for a contributor.
    /// </summary>
    public async Task<int> GetContributorBalanceAsync(string contributorPseudonym, CancellationToken cancellationToken = default)
    {
        var entries = await GetEntriesForContributorAsync(contributorPseudonym, cancellationToken);
        return entries.Sum(e => e.Points);
    }

    /// <summary>
    /// Reads all entries from the ledger.
    /// </summary>
    public async Task<IReadOnlyList<StardustLedgerEntry>> ReadAllEntriesAsync(CancellationToken cancellationToken = default)
    {
        await _ledgerLock.WaitAsync(cancellationToken);
        try
        {
            return await ReadAllEntriesInternalAsync(cancellationToken);
        }
        finally
        {
            _ledgerLock.Release();
        }
    }

    private async Task<List<StardustLedgerEntry>> ReadAllEntriesInternalAsync(CancellationToken cancellationToken)
    {
        var list = new List<StardustLedgerEntry>();
        if (!File.Exists(LedgerFilePath))
            return list;

        string[] lines = await File.ReadAllLinesAsync(LedgerFilePath, cancellationToken);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var entry = HubJsonSerializer.Deserialize<StardustLedgerEntry>(line.Trim());
            if (entry != null)
            {
                list.Add(entry);
            }
        }
        return list;
    }
}
