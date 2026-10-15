using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Serialization;

namespace BlueStar.ManifestHub.Core.Validation;

/// <summary>
/// Validator for Steam depot manifest files.
/// Enforces magic number 0x71F617D0, Deflate/ZLib decompression, SHA-256 and SHA-1 integrity,
/// timestamp extraction with fallback, depot/manifest ID matching, and rejection of corrupt/HTML/404 payloads.
/// </summary>
public sealed class ManifestValidator
{
    public const uint ManifestPayloadMagic = 0x71F617D0; // 1911953360
    public const uint ManifestMetadataMagic = 0x1F4812BE; // 524817086
    public const uint ManifestSignatureMagic = 0x1B846177; // 461486103
    public const uint ManifestEndOfManifestMagic = 0x32C41BEB; // 851711403

    // Common HTML / error signatures in ASCII/UTF-8
    private static readonly string[] HtmlOrErrorPrefixes =
    {
        "<!doctype",
        "<html",
        "<html>",
        "<head",
        "<?xml",
        "404 not found",
        "403 forbidden",
        "502 bad gateway",
        "500 internal server error",
        "{\"error\"",
        "{\"message\"",
        "access denied",
        "unauthorized"
    };

    /// <summary>
    /// Validates a manifest byte buffer.
    /// </summary>
    public ManifestValidationResult Validate(
        byte[]? manifestBytes,
        uint expectedDepotId = 0,
        ulong expectedManifestId = 0,
        string? expectedSha256 = null,
        string? expectedSha1 = null,
        DateTimeOffset? fallbackTimestamp = null)
    {
        if (manifestBytes == null || manifestBytes.Length == 0)
        {
            return ManifestValidationResult.Failure(
                ManifestStatus.Corrupted,
                "Manifest is empty (zero bytes).",
                expectedDepotId,
                expectedManifestId);
        }

        // 1. Check for HTML or HTTP error payloads
        if (IsHtmlOrHttpError(manifestBytes, out string detectedErrorSnippet))
        {
            ManifestStatus status = detectedErrorSnippet.Contains("404") 
                ? ManifestStatus.Missing 
                : ManifestStatus.Corrupted;

            return ManifestValidationResult.Failure(
                status,
                $"File contains HTML/HTTP error response instead of binary manifest: '{detectedErrorSnippet}'.",
                expectedDepotId,
                expectedManifestId);
        }

        // Minimum length check (at least 4 bytes for magic number)
        if (manifestBytes.Length < 4)
        {
            return ManifestValidationResult.Failure(
                ManifestStatus.Corrupted,
                $"File size ({manifestBytes.Length} bytes) is too small to contain a valid manifest header.",
                expectedDepotId,
                expectedManifestId);
        }

        // 2. Compute SHA-256 and SHA-1 of the raw manifest
        string sha256 = ComputeSha256(manifestBytes);
        string sha1 = ComputeSha1(manifestBytes);

        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(sha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return ManifestValidationResult.Failure(
                ManifestStatus.Corrupted,
                $"SHA-256 integrity check failed. Expected '{expectedSha256}', computed '{sha256}'.",
                expectedDepotId,
                expectedManifestId,
                sha256,
                sha1);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha1) &&
            !string.Equals(sha1, expectedSha1.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return ManifestValidationResult.Failure(
                ManifestStatus.Corrupted,
                $"SHA-1 integrity check failed. Expected '{expectedSha1}', computed '{sha1}'.",
                expectedDepotId,
                expectedManifestId,
                sha256,
                sha1);
        }

        // 3. Inspect chunks and detect magic 0x71F617D0 or decompression
        byte[] activeBytes = manifestBytes;
        bool magicFound = false;
        byte[]? decompressedPayload = null;
        ParsedManifestMetadata? extractedMeta = null;

        // Try inspecting raw stream first
        if (TryExtractSections(activeBytes, out bool hasPayloadMagic, out ParsedManifestMetadata? rawMeta, out byte[]? rawDecompressedPayload))
        {
            magicFound = hasPayloadMagic;
            extractedMeta = rawMeta;
            decompressedPayload = rawDecompressedPayload;
        }

        // If not found directly, the entire file might be compressed (Deflate or Zlib)
        if (!magicFound)
        {
            if (TryDecompressStream(manifestBytes, out byte[] decompressedOuter))
            {
                if (TryExtractSections(decompressedOuter, out bool outerHasPayload, out ParsedManifestMetadata? outerMeta, out byte[]? outerPayload))
                {
                    magicFound = outerHasPayload;
                    extractedMeta = outerMeta;
                    decompressedPayload = outerPayload ?? decompressedOuter;
                    activeBytes = decompressedOuter;
                }
            }
        }

        if (!magicFound)
        {
            return ManifestValidationResult.Failure(
                ManifestStatus.Corrupted,
                $"Missing required Steam depot manifest magic header 0x{ManifestPayloadMagic:X8} or payload section is truncated/corrupt.",
                expectedDepotId,
                expectedManifestId,
                sha256,
                sha1);
        }

        // 4. Verify DepotId and ManifestId
        uint depotId = expectedDepotId;
        ulong manifestId = expectedManifestId;

        if (extractedMeta != null)
        {
            if (extractedMeta.DepotId != 0)
            {
                if (expectedDepotId != 0 && extractedMeta.DepotId != expectedDepotId)
                {
                    return ManifestValidationResult.Failure(
                        ManifestStatus.Corrupted,
                        $"Depot ID mismatch: manifest payload contains depot {extractedMeta.DepotId}, but expected {expectedDepotId}.",
                        extractedMeta.DepotId,
                        manifestId,
                        sha256,
                        sha1);
                }
                depotId = extractedMeta.DepotId;
            }

            if (extractedMeta.ManifestGid != 0)
            {
                if (expectedManifestId != 0 && extractedMeta.ManifestGid != expectedManifestId)
                {
                    return ManifestValidationResult.Failure(
                        ManifestStatus.Corrupted,
                        $"Manifest ID mismatch: manifest payload contains GID {extractedMeta.ManifestGid}, but expected {expectedManifestId}.",
                        depotId,
                        extractedMeta.ManifestGid,
                        sha256,
                        sha1);
                }
                manifestId = extractedMeta.ManifestGid;
            }
        }

        // 5. Extract timestamp with fallback
        DateTimeOffset timestamp;
        if (extractedMeta != null && extractedMeta.CreationTimeUnix > 0)
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(extractedMeta.CreationTimeUnix);
        }
        else
        {
            timestamp = fallbackTimestamp ?? DateTimeOffset.UtcNow;
        }

        long originalSize = extractedMeta?.DiskOriginalSize > 0 
            ? (long)extractedMeta.DiskOriginalSize 
            : (decompressedPayload?.Length ?? manifestBytes.Length);

        long compressedSize = extractedMeta?.DiskCompressedSize > 0 
            ? (long)extractedMeta.DiskCompressedSize 
            : manifestBytes.Length;

        return ManifestValidationResult.Success(
            depotId,
            manifestId,
            timestamp,
            sha256,
            sha1,
            originalSize,
            compressedSize,
            decompressedPayload);
    }

    /// <summary>
    /// Validates a manifest file on disk.
    /// </summary>
    public async Task<ManifestValidationResult> ValidateFileAsync(
        string filePath,
        uint expectedDepotId = 0,
        ulong expectedManifestId = 0,
        string? expectedSha256 = null,
        string? expectedSha1 = null,
        DateTimeOffset? fallbackTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return ManifestValidationResult.Failure(
                ManifestStatus.Missing,
                $"Manifest file not found at path '{filePath}'.",
                expectedDepotId,
                expectedManifestId);
        }

        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        DateTimeOffset fallback = fallbackTimestamp ?? File.GetLastWriteTimeUtc(filePath);
        return Validate(bytes, expectedDepotId, expectedManifestId, expectedSha256, expectedSha1, fallback);
    }

    /// <summary>
    /// Inspects concatenated binary manifest sections:
    /// [Magic (4 bytes LE)][Length (4 bytes LE)][Payload...]
    /// </summary>
    private static bool TryExtractSections(
        byte[] data,
        out bool payloadMagicFound,
        out ParsedManifestMetadata? metadata,
        out byte[]? decompressedPayload)
    {
        payloadMagicFound = false;
        metadata = null;
        decompressedPayload = null;

        if (data.Length < 4)
            return false;

        int offset = 0;
        while (offset + 8 <= data.Length)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            offset += 8;

            if ((ulong)offset + length > (ulong)data.Length)
            {
                // Truncated section — file is incomplete or corrupt
                return false;
            }

            ReadOnlySpan<byte> sectionData = data.AsSpan(offset, (int)length);

            if (magic == ManifestPayloadMagic)
            {
                payloadMagicFound = true;
                // Decompress payload if compressed with Deflate / ZLib
                if (TryDecompressStream(sectionData.ToArray(), out byte[] decompressed))
                {
                    decompressedPayload = decompressed;
                }
                else
                {
                    decompressedPayload = sectionData.ToArray();
                }
            }
            else if (magic == ManifestMetadataMagic)
            {
                metadata = ProtobufMiniParser.ParseMetadata(sectionData);
            }

            offset += (int)length;
        }

        return payloadMagicFound;
    }

    /// <summary>
    /// Attempts to decompress bytes using ZLibStream, falling back to DeflateStream.
    /// </summary>
    public static bool TryDecompressStream(byte[] compressedBytes, out byte[] decompressed)
    {
        decompressed = Array.Empty<byte>();
        if (compressedBytes.Length < 2)
            return false;

        // Try ZLibStream first
        try
        {
            using var msIn = new MemoryStream(compressedBytes);
            using var zlib = new ZLibStream(msIn, CompressionMode.Decompress);
            using var msOut = new MemoryStream();
            zlib.CopyTo(msOut);
            if (msOut.Length > 0)
            {
                decompressed = msOut.ToArray();
                return true;
            }
        }
        catch
        {
            // Fallback to DeflateStream
        }

        // Try raw DeflateStream (RFC 1951)
        try
        {
            using var msIn = new MemoryStream(compressedBytes);
            using var deflate = new DeflateStream(msIn, CompressionMode.Decompress);
            using var msOut = new MemoryStream();
            deflate.CopyTo(msOut);
            if (msOut.Length > 0)
            {
                decompressed = msOut.ToArray();
                return true;
            }
        }
        catch
        {
            // Not compressed or decompression failed
        }

        return false;
    }

    /// <summary>
    /// Checks whether the buffer starts with HTML or HTTP error strings.
    /// </summary>
    private static bool IsHtmlOrHttpError(byte[] data, out string detectedSnippet)
    {
        detectedSnippet = string.Empty;
        int checkLen = Math.Min(data.Length, 256);
        string prefix = Encoding.UTF8.GetString(data, 0, checkLen).TrimStart();

        foreach (string pattern in HtmlOrErrorPrefixes)
        {
            if (prefix.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                prefix.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                detectedSnippet = prefix.Length > 60 ? prefix[..60] + "..." : prefix;
                return true;
            }
        }

        return false;
    }

    public static string ComputeSha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha1(byte[] data)
    {
        byte[] hash = SHA1.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Utility to generate a valid synthetic Steam depot manifest file.
    /// Useful for testing, tooling, and local generation.
    /// </summary>
    public static byte[] CreateSyntheticManifest(
        uint depotId,
        ulong manifestId,
        DateTimeOffset timestamp,
        byte[]? rawPayload = null,
        bool compressPayload = true,
        bool includeMetadata = true)
    {
        using var ms = new MemoryStream();

        byte[] payloadBytes = rawPayload ?? Encoding.UTF8.GetBytes($"BlueStar synthetic depot {depotId} manifest {manifestId}");
        byte[] finalPayload;

        if (compressPayload)
        {
            using var compMs = new MemoryStream();
            using (var zlib = new ZLibStream(compMs, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(payloadBytes);
            }
            finalPayload = compMs.ToArray();
        }
        else
        {
            finalPayload = payloadBytes;
        }

        // Section 1: PROTOBUF_PAYLOAD_MAGIC (0x71F617D0)
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], ManifestPayloadMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)finalPayload.Length);
        ms.Write(header);
        ms.Write(finalPayload);

        // Section 2: PROTOBUF_METADATA_MAGIC (0x1F4812BE)
        if (includeMetadata)
        {
            var meta = new ParsedManifestMetadata
            {
                DepotId = depotId,
                ManifestGid = manifestId,
                CreationTimeUnix = (uint)timestamp.ToUnixTimeSeconds(),
                DiskOriginalSize = (ulong)payloadBytes.Length,
                DiskCompressedSize = (ulong)finalPayload.Length,
                UniqueChunks = 1
            };
            byte[] metaBytes = ProtobufMiniParser.SerializeMetadata(meta);

            BinaryPrimitives.WriteUInt32LittleEndian(header[..4], ManifestMetadataMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)metaBytes.Length);
            ms.Write(header);
            ms.Write(metaBytes);
        }

        // Section 3: PROTOBUF_ENDOFMANIFEST_MAGIC (0x32C41BEB)
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], ManifestEndOfManifestMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0);
        ms.Write(header);

        return ms.ToArray();
    }
}
