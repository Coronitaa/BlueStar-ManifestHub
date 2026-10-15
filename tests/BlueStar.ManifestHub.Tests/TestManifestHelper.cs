using System.Text;
using BlueStar.ManifestHub.Core.Validation;

namespace BlueStar.ManifestHub.Tests;

public static class TestManifestHelper
{
    public static byte[] CreateValidManifest(
        uint depotId = 440,
        ulong manifestId = 1234567890123456789UL,
        DateTimeOffset? timestamp = null,
        string payload = "Hello BlueStar Depot Chunks",
        bool compress = true,
        bool includeMetadata = true)
    {
        return ManifestValidator.CreateSyntheticManifest(
            depotId,
            manifestId,
            timestamp ?? DateTimeOffset.UtcNow,
            Encoding.UTF8.GetBytes(payload),
            compressPayload: compress,
            includeMetadata: includeMetadata);
    }

    public static string CreateTestDirectory(string prefix = "hub_test_")
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void CleanupTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors on temp
            }
        }
    }
}
