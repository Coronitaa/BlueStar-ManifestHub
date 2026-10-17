using BlueStar.ManifestHarvester.Cli;

namespace BlueStar.ManifestHub.Tests;

public class ManifestHarvesterTests
{
    [Fact]
    public void ValidateMagicHeaderBytes_ValidProtobufMagic_ReturnsTrue()
    {
        byte[] validMagic = [0xD0, 0x17, 0xF6, 0x71, 0x00, 0x12, 0x34];
        Assert.True(SteamManifestHarvester.ValidateMagicHeaderBytes(validMagic));
    }

    [Fact]
    public void ValidateMagicHeaderBytes_InvalidMagic_ReturnsFalse()
    {
        byte[] invalidMagic = [0x00, 0x00, 0x00, 0x00];
        Assert.False(SteamManifestHarvester.ValidateMagicHeaderBytes(invalidMagic));

        byte[] tooShort = [0xD0, 0x17];
        Assert.False(SteamManifestHarvester.ValidateMagicHeaderBytes(tooShort));
    }

    [Fact]
    public void ExtractAppIdsFromText_ValidVdfFormat_ExtractsExpectedAppIds()
    {
        string vdfContent = @"
""UserLocalConfigStore""
{
    ""Software""
    {
        ""Valve""
        {
            ""Steam""
            {
                ""apps""
                {
                    ""480""
                    {
                        ""LastPlayed"" ""1600000000""
                    }
                    ""550""
                    {
                        ""LastPlayed"" ""1600001000""
                    }
                    ""250900""
                    {
                        ""LastPlayed"" ""1600002000""
                    }
                }
            }
        }
    }
}";
        var appIds = new HashSet<uint>();
        LocalLicenseReader.ExtractAppIdsFromText(vdfContent, appIds);

        Assert.Contains(480u, appIds);
        Assert.Contains(550u, appIds);
        Assert.Contains(250900u, appIds);
        Assert.DoesNotContain(0u, appIds);
        Assert.DoesNotContain(1u, appIds);
    }

    [Fact]
    public void ImportExistingFromCache_CopiesValidManifestsAndRespectsFilter()
    {
        var tempSteam = Path.Combine(Path.GetTempPath(), "SteamHarvesterTest_" + Guid.NewGuid().ToString("N"));
        var depotCache = Path.Combine(tempSteam, "depotcache");
        var outputDir = Path.Combine(tempSteam, "manifests");

        Directory.CreateDirectory(depotCache);
        Directory.CreateDirectory(outputDir);

        try
        {
            // Create a valid manifest (with 0xD0, 0x17, 0xF6, 0x71 header)
            var validFile = Path.Combine(depotCache, "481_123456789.manifest");
            byte[] validData = [0xD0, 0x17, 0xF6, 0x71, 0x08, 0x90, 0x03, 0x10];
            File.WriteAllBytes(validFile, validData);

            // Create an invalid file
            var invalidFile = Path.Combine(depotCache, "481_999999999.manifest");
            byte[] invalidData = [0x01, 0x02, 0x03, 0x04];
            File.WriteAllBytes(invalidFile, invalidData);

            // Import all
            var results = SteamManifestHarvester.ImportExistingFromCache(tempSteam, outputDir);

            Assert.Single(results);
            Assert.Equal(481u, results[0].DepotId);
            Assert.Equal(123456789UL, results[0].ManifestId);
            Assert.True(results[0].Success);

            // Verify file copied to manifests/481/123456789.manifest
            var expectedDest = Path.Combine(outputDir, "481", "123456789.manifest");
            Assert.True(File.Exists(expectedDest));
        }
        finally
        {
            if (Directory.Exists(tempSteam))
            {
                Directory.Delete(tempSteam, recursive: true);
            }
        }
    }

    [Fact]
    public void SteamLocator_FindSteamPath_DoesNotThrow()
    {
        var exception = Record.Exception(() => SteamLocator.FindSteamPath());
        Assert.Null(exception);
    }

    [Fact]
    public void SteamLocator_GetSteamProcessStatus_DoesNotThrow()
    {
        var exception = Record.Exception(() => SteamLocator.GetSteamProcessStatus());
        Assert.Null(exception);
    }
}
