using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Receiver;

/// <summary>
/// Model for manifest contribution submission payload.
/// </summary>
public sealed class ContributionSubmission
{
    [JsonPropertyName("anonymousContributorId")]
    public string AnonymousContributorId { get; set; } = string.Empty;

    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    [JsonPropertyName("depotId")]
    public uint DepotId { get; set; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; set; }

    [JsonPropertyName("manifestHashSha256")]
    public string? ManifestHashSha256 { get; set; }

    [JsonPropertyName("sanitizationAuditHash")]
    public string? SanitizationAuditHash { get; set; }

    [JsonPropertyName("manifestSizeBytes")]
    public long ManifestSizeBytes { get; set; }

    [JsonPropertyName("payloadBase64")]
    public string PayloadBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Verifiable receipt returned upon contribution publication.
/// </summary>
public sealed class PublicationReceipt
{
    [JsonPropertyName("receiptId")]
    public string ReceiptId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Published";

    [JsonPropertyName("isSuccess")]
    public bool IsSuccess { get; set; } = true;

    [JsonPropertyName("stardustCredited")]
    public int StardustCredited { get; set; } = 100;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    [JsonPropertyName("depotId")]
    public uint DepotId { get; set; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; set; }

    [JsonPropertyName("verificationHash")]
    public string VerificationHash { get; set; } = string.Empty;

    [JsonPropertyName("cryptographicVerificationHash")]
    public string CryptographicVerificationHash => VerificationHash;

    [JsonPropertyName("isDuplicate")]
    public bool IsDuplicate { get; set; }

    public static string ComputeVerificationHash(
        string receiptId,
        uint appId,
        uint depotId,
        ulong manifestId,
        DateTimeOffset timestamp,
        string manifestSha256)
    {
        string raw = $"{receiptId}:{appId}:{depotId}:{manifestId}:{timestamp.ToUnixTimeSeconds()}:{manifestSha256}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Response model for contributor Stardust balance query.
/// </summary>
public sealed class ContributorBalanceResponse
{
    [JsonPropertyName("contributorId")]
    public string ContributorId { get; set; } = string.Empty;

    [JsonPropertyName("confirmedBalance")]
    public int ConfirmedBalance { get; set; }

    [JsonPropertyName("confirmedPoints")]
    public int ConfirmedPoints => ConfirmedBalance;

    [JsonPropertyName("pendingBalance")]
    public int PendingBalance { get; set; } = 0;

    [JsonPropertyName("pendingPoints")]
    public int PendingPoints => PendingBalance;
}

/// <summary>
/// Status model for service health check.
/// </summary>
public sealed class HealthStatusResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "Healthy";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";
}
