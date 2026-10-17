using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BlueStar.ManifestHarvester.Cli;

public sealed record SteamAccountInfo(
    uint AccountId,
    ulong SteamId64,
    string AccountName,
    string PersonaName,
    string UserDataDirectory
);

public static class SteamLocator
{
    public static string? FindSteamPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath")?.ToString();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path.Replace('/', '\\')))
                {
                    return Path.GetFullPath(path.Replace('/', '\\'));
                }
            }
            catch { }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                var path = key?.GetValue("InstallPath")?.ToString();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
            catch { }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                var path = key?.GetValue("InstallPath")?.ToString();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
            catch { }
        }

        // Standard default paths
        string[] standardPaths =
        [
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        ];

        foreach (var standard in standardPaths)
        {
            if (Directory.Exists(standard))
            {
                return Path.GetFullPath(standard);
            }
        }

        return null;
    }

    public static (bool IsRunning, int? ProcessId) GetSteamProcessStatus()
    {
        try
        {
            var processes = Process.GetProcessesByName("steam");
            if (processes.Length > 0)
            {
                return (true, processes[0].Id);
            }
        }
        catch { }

        return (false, null);
    }

    public static SteamAccountInfo? GetActiveAccount(string steamPath)
    {
        uint activeAccountId = 0;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var activeKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                var val = activeKey?.GetValue("ActiveUser");
                if (val is int intVal && intVal > 0)
                {
                    activeAccountId = (uint)intVal;
                }
                else if (val is long longVal && longVal > 0)
                {
                    activeAccountId = (uint)longVal;
                }
            }
            catch { }
        }

        string personaName = "Steam User";
        string accountName = "unknown";
        ulong steamId64 = 0;

        // Try reading loginusers.vdf
        var loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (File.Exists(loginUsersPath))
        {
            try
            {
                var text = File.ReadAllText(loginUsersPath);
                var users = ParseLoginUsers(text);

                if (activeAccountId > 0)
                {
                    // Convert 32-bit AccountID to SteamID64: SteamID64 = 76561197960265728 + AccountId
                    ulong expectedSteamId64 = 76561197960265728UL + activeAccountId;
                    if (users.TryGetValue(expectedSteamId64, out var foundUser))
                    {
                        steamId64 = expectedSteamId64;
                        personaName = foundUser.PersonaName;
                        accountName = foundUser.AccountName;
                    }
                }

                if (steamId64 == 0 && users.Count > 0)
                {
                    // Pick the most recently logged in user
                    var mostRecent = users.Values.OrderByDescending(u => u.Timestamp).First();
                    steamId64 = mostRecent.SteamId64;
                    personaName = mostRecent.PersonaName;
                    accountName = mostRecent.AccountName;
                    if (activeAccountId == 0 && steamId64 > 76561197960265728UL)
                    {
                        activeAccountId = (uint)(steamId64 - 76561197960265728UL);
                    }
                }
            }
            catch { }
        }

        // Fallback: check userdata directories
        if (activeAccountId == 0)
        {
            var userdataDir = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userdataDir))
            {
                var dirs = Directory.GetDirectories(userdataDir);
                foreach (var dir in dirs)
                {
                    var name = Path.GetFileName(dir);
                    if (uint.TryParse(name, out var id) && id > 0)
                    {
                        activeAccountId = id;
                        steamId64 = 76561197960265728UL + id;
                        break;
                    }
                }
            }
        }

        string userDataDirectory = activeAccountId > 0
            ? Path.Combine(steamPath, "userdata", activeAccountId.ToString())
            : Path.Combine(steamPath, "userdata");

        return new SteamAccountInfo(activeAccountId, steamId64, accountName, personaName, userDataDirectory);
    }

    private sealed record LoginUserEntry(ulong SteamId64, string AccountName, string PersonaName, long Timestamp);

    private static Dictionary<ulong, LoginUserEntry> ParseLoginUsers(string vdfText)
    {
        var result = new Dictionary<ulong, LoginUserEntry>();

        var userBlocks = Regex.Matches(vdfText, @"""(\d{17})""\s*\{([^}]+)\}", RegexOptions.Singleline);
        foreach (Match block in userBlocks)
        {
            if (ulong.TryParse(block.Groups[1].Value, out var sid64))
            {
                var body = block.Groups[2].Value;

                var accMatch = Regex.Match(body, @"""AccountName""\s*""([^""]+)""", RegexOptions.IgnoreCase);
                var perMatch = Regex.Match(body, @"""PersonaName""\s*""([^""]+)""", RegexOptions.IgnoreCase);
                var timeMatch = Regex.Match(body, @"""Timestamp""\s*""(\d+)""", RegexOptions.IgnoreCase);

                string acc = accMatch.Success ? accMatch.Groups[1].Value : "unknown";
                string per = perMatch.Success ? perMatch.Groups[1].Value : "Steam User";
                long time = timeMatch.Success && long.TryParse(timeMatch.Groups[1].Value, out var t) ? t : 0;

                result[sid64] = new LoginUserEntry(sid64, acc, per, time);
            }
        }

        return result;
    }
}
