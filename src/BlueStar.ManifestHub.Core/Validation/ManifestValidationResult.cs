using BlueStar.ManifestHub.Core.Models;

namespace BlueStar.ManifestHub.Core.Validation;

/// <summary>
/// Result of a manifest validation operation.
/// </summary>
public sealed class ManifestValidationResult
{
    public bool IsValid { get; init; }
    public ManifestStatus Status { get; init; }
    public string? ErrorMessage { get; init; }

    public uint DepotId { get; init; }
    public ulong ManifestId { get; init; }
    public DateTimeOffset CreationTime { get; init; }

    public string Sha256 { get; init; } = string.Empty;
    public string Sha1 { get; init; } = string.Empty;

    public long SizeOriginal { get; init; }
    public long SizeCompressed { get; init; }

    public bool IsDecompressed { get; init; }
    public byte[]? DecompressedPayload { get; init; }

    public static ManifestValidationResult Success(
        uint depotId,
        ulong manifestId,
        DateTimeOffset creationTime,
        string sha256,
        string sha1,
        long sizeOriginal,
        long sizeCompressed,
        byte[]? decompressedPayload = null) =>
        new()
        {
            IsValid = true,
            Status = ManifestStatus.AvailableAndValidated,
            DepotId = depotId,
            ManifestId = manifestId,
            CreationTime = creationTime,
            Sha256 = sha256,
            Sha1 = sha1,
            SizeOriginal = sizeOriginal,
            SizeCompressed = sizeCompressed,
            IsDecompressed = decompressedPayload != null,
            DecompressedPayload = decompressedPayload
        };

    public static ManifestValidationResult Failure(
        ManifestStatus status,
        string errorMessage,
        uint depotId = 0,
        ulong manifestId = 0,
        string? sha256 = null,
        string? sha1 = null) =>
        new()
        {
            IsValid = false,
            Status = status,
            ErrorMessage = errorMessage,
            DepotId = depotId,
            ManifestId = manifestId,
            Sha256 = sha256 ?? string.Empty,
            Sha1 = sha1 ?? string.Empty
        };
}
