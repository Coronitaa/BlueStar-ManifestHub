using System.Text;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Validation;

namespace BlueStar.ManifestHub.Tests;

public class ValidationTests
{
    private readonly ManifestValidator _validator = new();

    [Fact]
    public void Validate_ValidManifest_SucceedsWithExtractedMetadata()
    {
        uint depotId = 441;
        ulong manifestId = 9876543210987654321UL;
        DateTimeOffset timestamp = new(2025, 6, 15, 12, 30, 0, TimeSpan.Zero);
        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId, timestamp, "Sample Depot Data", compress: true);

        string expectedSha256 = ManifestValidator.ComputeSha256(manifestBytes);
        string expectedSha1 = ManifestValidator.ComputeSha1(manifestBytes);

        var result = _validator.Validate(manifestBytes, depotId, manifestId, expectedSha256, expectedSha1);

        Assert.True(result.IsValid);
        Assert.Equal(ManifestStatus.AvailableAndValidated, result.Status);
        Assert.Equal(depotId, result.DepotId);
        Assert.Equal(manifestId, result.ManifestId);
        Assert.Equal(timestamp.ToUnixTimeSeconds(), result.CreationTime.ToUnixTimeSeconds());
        Assert.Equal(expectedSha256, result.Sha256);
        Assert.Equal(expectedSha1, result.Sha1);
        Assert.True(result.IsDecompressed);
        Assert.NotNull(result.DecompressedPayload);
    }

    [Fact]
    public void Validate_ValidManifestUncompressed_Succeeds()
    {
        uint depotId = 550;
        ulong manifestId = 112233445566778899UL;
        byte[] manifestBytes = TestManifestHelper.CreateValidManifest(depotId, manifestId, compress: false);

        var result = _validator.Validate(manifestBytes, depotId, manifestId);

        Assert.True(result.IsValid);
        Assert.Equal(ManifestStatus.AvailableAndValidated, result.Status);
    }

    [Fact]
    public void Validate_ZeroLengthFile_RejectsWithCorruptedStatus()
    {
        byte[] emptyBytes = Array.Empty<byte>();

        var result = _validator.Validate(emptyBytes, 440, 12345);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NullData_RejectsWithCorruptedStatus()
    {
        var result = _validator.Validate(null, 440, 12345);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>404 Not Found</title></head><body><h1>404 Not Found</h1></body></html>")]
    [InlineData("<html><body><h1>403 Forbidden</h1></body></html>")]
    [InlineData("{\"error\": \"Manifest not found on CDN\", \"code\": 404}")]
    [InlineData("404 Not Found - The specified blob does not exist.")]
    [InlineData("Access Denied: You do not have permissions to download this manifest.")]
    public void Validate_HtmlOrHttpErrorPayload_RejectsWithAppropriateStatus(string errorText)
    {
        byte[] payload = Encoding.UTF8.GetBytes(errorText);

        var result = _validator.Validate(payload, 440, 12345);

        Assert.False(result.IsValid);
        Assert.True(result.Status is ManifestStatus.Corrupted or ManifestStatus.Missing);
        Assert.Contains("HTML/HTTP error", result.ErrorMessage);
    }

    [Fact]
    public void Validate_CorruptedRandomBytes_RejectsWithCorruptedStatus()
    {
        byte[] garbage = new byte[128];
        Random.Shared.NextBytes(garbage);

        var result = _validator.Validate(garbage, 440, 12345);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
        Assert.Contains("magic", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_DepotIdMismatch_RejectsWithCorruptedStatus()
    {
        uint actualDepotId = 440;
        uint wrongExpectedDepotId = 730;
        ulong manifestId = 123456789;
        byte[] bytes = TestManifestHelper.CreateValidManifest(actualDepotId, manifestId);

        var result = _validator.Validate(bytes, expectedDepotId: wrongExpectedDepotId, expectedManifestId: manifestId);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
        Assert.Contains("Depot ID mismatch", result.ErrorMessage);
    }

    [Fact]
    public void Validate_ManifestIdMismatch_RejectsWithCorruptedStatus()
    {
        uint depotId = 440;
        ulong actualManifestId = 1111111111;
        ulong wrongExpectedManifestId = 9999999999;
        byte[] bytes = TestManifestHelper.CreateValidManifest(depotId, actualManifestId);

        var result = _validator.Validate(bytes, expectedDepotId: depotId, expectedManifestId: wrongExpectedManifestId);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
        Assert.Contains("Manifest ID mismatch", result.ErrorMessage);
    }

    [Fact]
    public void Validate_Sha256Mismatch_RejectsWithCorruptedStatus()
    {
        byte[] bytes = TestManifestHelper.CreateValidManifest(440, 12345);
        string incorrectSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

        var result = _validator.Validate(bytes, 440, 12345, expectedSha256: incorrectSha256);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
        Assert.Contains("SHA-256 integrity check failed", result.ErrorMessage);
    }

    [Fact]
    public void Validate_Sha1Mismatch_RejectsWithCorruptedStatus()
    {
        byte[] bytes = TestManifestHelper.CreateValidManifest(440, 12345);
        string incorrectSha1 = "0000000000000000000000000000000000000000";

        var result = _validator.Validate(bytes, 440, 12345, expectedSha1: incorrectSha1);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
        Assert.Contains("SHA-1 integrity check failed", result.ErrorMessage);
    }

    [Fact]
    public void Validate_WithoutMetadataTimestamp_UsesFallbackTimestamp()
    {
        // Create manifest without metadata section
        byte[] payload = Encoding.UTF8.GetBytes("Test Payload");
        byte[] bytes = ManifestValidator.CreateSyntheticManifest(440, 12345, DateTimeOffset.UtcNow, payload, compressPayload: false, includeMetadata: false);

        DateTimeOffset fallback = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = _validator.Validate(bytes, 440, 12345, fallbackTimestamp: fallback);

        Assert.True(result.IsValid);
        Assert.Equal(fallback, result.CreationTime);
    }

    [Fact]
    public async Task ValidateFileAsync_ValidFile_ReturnsSuccess()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            byte[] bytes = TestManifestHelper.CreateValidManifest(440, 12345);
            await File.WriteAllBytesAsync(tempFile, bytes);

            var result = await _validator.ValidateFileAsync(tempFile, 440, 12345);
            Assert.True(result.IsValid);
            Assert.Equal(ManifestStatus.AvailableAndValidated, result.Status);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ValidateFileAsync_NonExistentFile_ReturnsMissingStatus()
    {
        string nonExistent = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.manifest");

        var result = await _validator.ValidateFileAsync(nonExistent, 440, 12345);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Missing, result.Status);
    }

    [Fact]
    public void Validate_TruncatedManifestHeader_ReturnsCorruptedStatus()
    {
        // Declares 64 KB payload in header but only contains 12 bytes total
        byte[] truncatedBytes = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(truncatedBytes.AsSpan(0, 4), ManifestValidator.ManifestPayloadMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(truncatedBytes.AsSpan(4, 4), 65536u); // Declared length
        truncatedBytes[8] = 0x78;
        truncatedBytes[9] = 0x9C;

        var result = _validator.Validate(truncatedBytes, 440, 12345);

        Assert.False(result.IsValid);
        Assert.Equal(ManifestStatus.Corrupted, result.Status);
        Assert.Contains("truncated/corrupt", result.ErrorMessage);
    }

    [Fact]
    public void Validate_MalformedProtobufVarintOverflow_TerminatesSafelyWithoutHang()
    {
        // Malformed protobuf section with length varint exceeding int.MaxValue
        byte[] malformedBytes = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(malformedBytes.AsSpan(0, 4), ManifestValidator.ManifestMetadataMagic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(malformedBytes.AsSpan(4, 4), 16u);
        // Field 1 wire type 2: tag = (1 << 3) | 2 = 10 (0x0A)
        malformedBytes[8] = 0x0A;
        // Varint with high bit set (causing overflow if not checked unsigned)
        malformedBytes[9] = 0xFF;
        malformedBytes[10] = 0xFF;
        malformedBytes[11] = 0xFF;
        malformedBytes[12] = 0xFF;
        malformedBytes[13] = 0x7F;

        var result = _validator.Validate(malformedBytes, 440, 12345);

        // Must complete promptly without hanging in infinite loop
        Assert.False(result.IsValid);
    }
}
