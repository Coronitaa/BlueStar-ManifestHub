using System.Text.RegularExpressions;

namespace BlueStar.ManifestHarvester.Cli;

public static class LocalLicenseReader
{
    public static HashSet<uint> GetOwnedAppIds(string steamPath, uint activeAccountId = 0)
    {
        var appIds = new HashSet<uint>();

        // 1. Inspect userdata/<activeAccountId>/config/localconfig.vdf
        if (activeAccountId > 0)
        {
            var userLocalConfig = Path.Combine(steamPath, "userdata", activeAccountId.ToString(), "config", "localconfig.vdf");
            if (File.Exists(userLocalConfig))
            {
                ExtractAppIdsFromVdfFile(userLocalConfig, appIds);
            }

            var userSharedConfig = Path.Combine(steamPath, "userdata", activeAccountId.ToString(), "7", "remote", "sharedconfig.vdf");
            if (File.Exists(userSharedConfig))
            {
                ExtractAppIdsFromVdfFile(userSharedConfig, appIds);
            }
        }
        else
        {
            // Scan all users in userdata
            var userdata = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userdata))
            {
                foreach (var userDir in Directory.GetDirectories(userdata))
                {
                    var cfg = Path.Combine(userDir, "config", "localconfig.vdf");
                    if (File.Exists(cfg))
                    {
                        ExtractAppIdsFromVdfFile(cfg, appIds);
                    }
                }
            }
        }

        // 2. Inspect config/config.vdf
        var mainConfig = Path.Combine(steamPath, "config", "config.vdf");
        if (File.Exists(mainConfig))
        {
            ExtractAppIdsFromVdfFile(mainConfig, appIds);
        }

        // 3. Inspect appcache/packageinfo.vdf and appcache/appinfo.vdf
        var appcacheDir = Path.Combine(steamPath, "appcache");
        if (Directory.Exists(appcacheDir))
        {
            ParseAppCacheFile(Path.Combine(appcacheDir, "packageinfo.vdf"), appIds);
            ParseAppCacheFile(Path.Combine(appcacheDir, "appinfo.vdf"), appIds);
        }

        // 4. Inspect installed appmanifest_*.acf
        var steamappsDir = Path.Combine(steamPath, "steamapps");
        if (Directory.Exists(steamappsDir))
        {
            foreach (var acf in Directory.GetFiles(steamappsDir, "appmanifest_*.acf"))
            {
                var match = Regex.Match(Path.GetFileName(acf), @"appmanifest_(\d+)\.acf", RegexOptions.IgnoreCase);
                if (match.Success && uint.TryParse(match.Groups[1].Value, out var installedId) && installedId > 0)
                {
                    appIds.Add(installedId);
                }
            }
        }

        return appIds;
    }

    private static void ExtractAppIdsFromVdfFile(string filePath, HashSet<uint> appIds)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            ExtractAppIdsFromText(content, appIds);
        }
        catch { }
    }

    public static void ExtractAppIdsFromText(string text, HashSet<uint> appIds)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Matches quoted numbers like "550", "480", "250900"
        var matches = Regex.Matches(text, @"""(\d{1,7})""");
        foreach (Match m in matches)
        {
            if (uint.TryParse(m.Groups[1].Value, out var appId) && appId > 0 && appId < 50_000_000)
            {
                // Filter out non-AppID small numbers (common VDF flags like 0, 1, 2)
                if (appId > 5)
                {
                    appIds.Add(appId);
                }
            }
        }
    }

    private static void ParseAppCacheFile(string filePath, HashSet<uint> appIds)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 8)
            {
                var magic = BitConverter.ToUInt32(bytes, 0);
                // Valve binary magic for appinfo.vdf (0x07564427, 0x07564428, 0x07564429)
                if (magic == 0x07564427 || magic == 0x07564428 || magic == 0x07564429)
                {
                    int offset = 8;
                    while (offset + 8 <= bytes.Length)
                    {
                        var appId = BitConverter.ToUInt32(bytes, offset);
                        if (appId == 0) break;
                        var entrySize = BitConverter.ToUInt32(bytes, offset + 4);

                        if (appId > 5 && appId < 50_000_000)
                        {
                            appIds.Add(appId);
                        }

                        if (entrySize == 0 || offset + 8 + entrySize > bytes.Length)
                        {
                            offset += 8;
                        }
                        else
                        {
                            offset += 8 + (int)entrySize;
                        }
                    }
                }
                else
                {
                    // Fallback to text parsing
                    ExtractAppIdsFromText(File.ReadAllText(filePath), appIds);
                }
            }
        }
        catch { }
    }
}
