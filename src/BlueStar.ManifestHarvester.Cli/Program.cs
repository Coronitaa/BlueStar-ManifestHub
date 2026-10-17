using System.Text.Json;
using BlueStar.ManifestHub.Core.Demand;
using BlueStar.ManifestHub.Core.Ledger;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Serialization;
using BlueStar.ManifestHub.Core.Storage;

namespace BlueStar.ManifestHarvester.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var steamPath = SteamLocator.FindSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] Steam installation could not be located on this machine.");
            Console.ResetColor();
            return 1;
        }

        var (isRunning, pid) = SteamLocator.GetSteamProcessStatus();
        var account = SteamLocator.GetActiveAccount(steamPath);

        string repoRoot = Directory.GetCurrentDirectory();
        // If repoRoot doesn't contain indices/ or manifests/, check parent
        if (!Directory.Exists(Path.Combine(repoRoot, "manifests")) && Directory.Exists(Path.Combine(repoRoot, "..", "..", "manifests")))
        {
            repoRoot = Path.GetFullPath(Path.Combine(repoRoot, "..", ".."));
        }

        if (args.Length == 0 || args[0].Equals("--interactive", StringComparison.OrdinalIgnoreCase) || args[0].Equals("-i", StringComparison.OrdinalIgnoreCase))
        {
            return await RunInteractiveModeAsync(steamPath, isRunning, pid, account, repoRoot);
        }

        var command = args[0].ToLowerInvariant();
        return command switch
        {
            "download" or "harvest" => await HandleDownloadCommandAsync(args, steamPath, isRunning, repoRoot),
            "demand" or "from-demand" => await HandleDemandCommandAsync(args, steamPath, isRunning, account, repoRoot),
            "import-cache" => HandleImportCacheCommand(args, steamPath, repoRoot),
            "list-owned" => HandleListOwnedCommand(args, steamPath, account),
            "status" => HandleStatusCommand(steamPath, isRunning, pid, account, repoRoot),
            "help" or "--help" or "-h" => ShowHelp(),
            _ => ShowUnknownCommand(command)
        };
    }

    private static void PrintBanner(string steamPath, bool isRunning, int? pid, SteamAccountInfo? account)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================================");
        Console.WriteLine("        BlueStar Steam Client Manifest Harvester (Standalone)    ");
        Console.WriteLine("=================================================================");
        Console.ResetColor();

        Console.Write("Steam Status: ");
        if (isRunning)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"RUNNING (PID: {pid})");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("STOPPED (Please launch Steam and log in)");
        }
        Console.ResetColor();

        Console.WriteLine($"Steam Path:   {steamPath}");
        if (account != null && account.AccountId > 0)
        {
            Console.WriteLine($"Active User:  {account.PersonaName} (AccountID: {account.AccountId}, SteamID64: {account.SteamId64})");
        }
        else
        {
            Console.WriteLine("Active User:  No active session detected.");
        }
        Console.WriteLine("-----------------------------------------------------------------");
    }

    private static async Task<int> RunInteractiveModeAsync(
        string steamPath,
        bool isRunning,
        int? pid,
        SteamAccountInfo? account,
        string repoRoot)
    {
        while (true)
        {
            PrintBanner(steamPath, isRunning, pid, account);

            Console.WriteLine("Select an action:");
            Console.WriteLine("  [1] Download specific Manifest (--app, --depot, --manifest)");
            Console.WriteLine("  [2] Harvest missing manifests from demand.json (matching owned games)");
            Console.WriteLine("  [3] Import already-downloaded manifests from Steam depotcache");
            Console.WriteLine("  [4] List owned AppIDs in active Steam account (local cache)");
            Console.WriteLine("  [5] System Status & Diagnostics");
            Console.WriteLine("  [0] Exit");
            Console.Write("\nChoice > ");

            var key = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (key)
            {
                case "1":
                    await InteractiveDownloadAsync(steamPath, isRunning, repoRoot);
                    break;
                case "2":
                    await HandleDemandCommandAsync(["demand"], steamPath, isRunning, account, repoRoot);
                    break;
                case "3":
                    HandleImportCacheCommand(["import-cache"], steamPath, repoRoot);
                    break;
                case "4":
                    HandleListOwnedCommand(["list-owned"], steamPath, account);
                    break;
                case "5":
                    HandleStatusCommand(steamPath, isRunning, pid, account, repoRoot);
                    break;
                case "0" or "exit" or "q":
                    return 0;
                default:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Invalid option.");
                    Console.ResetColor();
                    break;
            }

            Console.WriteLine("\nPress Enter to return to menu...");
            Console.ReadLine();
            Console.Clear();
        }
    }

    private static async Task<int> InteractiveDownloadAsync(string steamPath, bool isRunning, string repoRoot)
    {
        if (!isRunning)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[Warning] Steam is not running. Launch Steam client first so it can process downloads.");
            Console.ResetColor();
        }

        Console.Write("Enter AppID (e.g. 480 for Spacewar, 10 for Counter-Strike): ");
        if (!uint.TryParse(Console.ReadLine()?.Trim(), out var appId) || appId == 0)
        {
            Console.WriteLine("Invalid AppID.");
            return 1;
        }

        Console.Write("Enter DepotID: ");
        if (!uint.TryParse(Console.ReadLine()?.Trim(), out var depotId) || depotId == 0)
        {
            Console.WriteLine("Invalid DepotID.");
            return 1;
        }

        Console.Write("Enter ManifestID: ");
        if (!ulong.TryParse(Console.ReadLine()?.Trim(), out var manifestId) || manifestId == 0)
        {
            Console.WriteLine("Invalid ManifestID.");
            return 1;
        }

        string destDir = Path.Combine(repoRoot, "manifests");
        return await ExecuteDownloadAsync(steamPath, appId, depotId, manifestId, destDir, TimeSpan.FromSeconds(30), repoRoot);
    }

    private static async Task<int> HandleDownloadCommandAsync(string[] args, string steamPath, bool isRunning, string repoRoot)
    {
        uint appId = 0;
        uint depotId = 0;
        ulong manifestId = 0;
        string destDir = Path.Combine(repoRoot, "manifests");
        int timeoutSec = 30;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--app", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && uint.TryParse(args[i + 1], out var a))
            {
                appId = a;
                i++;
            }
            else if (args[i].Equals("--depot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && uint.TryParse(args[i + 1], out var d))
            {
                depotId = d;
                i++;
            }
            else if (args[i].Equals("--manifest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && ulong.TryParse(args[i + 1], out var m))
            {
                manifestId = m;
                i++;
            }
            else if (args[i].Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                destDir = args[i + 1];
                i++;
            }
            else if (args[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var t))
            {
                timeoutSec = t;
                i++;
            }
        }

        if (appId == 0 || depotId == 0 || manifestId == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Missing required arguments. Usage:");
            Console.WriteLine("  dotnet run -- download --app <AppId> --depot <DepotId> --manifest <ManifestId> [--output manifests] [--timeout 30]");
            Console.ResetColor();
            return 1;
        }

        return await ExecuteDownloadAsync(steamPath, appId, depotId, manifestId, destDir, TimeSpan.FromSeconds(timeoutSec), repoRoot);
    }

    private static async Task<int> ExecuteDownloadAsync(
        string steamPath,
        uint appId,
        uint depotId,
        ulong manifestId,
        string destDir,
        TimeSpan timeout,
        string repoRoot)
    {
        Console.WriteLine($"\n[Harvester] Starting harvest for AppId={appId}, DepotId={depotId}, ManifestId={manifestId}...");
        Console.WriteLine($"[Harvester] Destination directory: {destDir}");

        var result = await SteamManifestHarvester.HarvestAsync(
            steamPath,
            appId,
            depotId,
            manifestId,
            destDir,
            timeout);

        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n[SUCCESS] Manifest downloaded and verified successfully!");
            Console.ResetColor();
            Console.WriteLine($"  DepotId:     {result.DepotId}");
            Console.WriteLine($"  ManifestId:  {result.ManifestId}");
            Console.WriteLine($"  Size:        {result.FileSizeBytes:N0} bytes");
            Console.WriteLine($"  SHA-256:     {result.Sha256Hash}");
            Console.WriteLine($"  Saved Path:  {result.FilePath}");
            Console.WriteLine($"  Source:      {(result.FromCache ? "Steam DepotCache (Instant)" : "Downloaded via Steam Client Console")}");

            // Check if present in demand.json and update
            TryUpdateDemandAndLedger(repoRoot, depotId, manifestId, result.Sha256Hash);

            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n[FAILED] Manifest download failed:");
            Console.WriteLine($"  {result.ErrorMessage}");
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task<int> HandleDemandCommandAsync(
        string[] args,
        string steamPath,
        bool isRunning,
        SteamAccountInfo? account,
        string repoRoot)
    {
        string demandPath = Path.Combine(repoRoot, "indices", "demand.json");
        string destDir = Path.Combine(repoRoot, "manifests");
        string contributor = account?.PersonaName ?? "Coronita";
        int timeoutSec = 30;
        bool autoConfirm = false;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--demand-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                demandPath = args[i + 1];
                i++;
            }
            else if (args[i].Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                destDir = args[i + 1];
                i++;
            }
            else if (args[i].Equals("--contributor", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                contributor = args[i + 1];
                i++;
            }
            else if (args[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var t))
            {
                timeoutSec = t;
                i++;
            }
            else if (args[i].Equals("--yes", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                autoConfirm = true;
            }
        }

        if (!File.Exists(demandPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Demand index not found at '{demandPath}'. Run `dotnet run -- --generate-demand` in ManifestHub first.");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"Loading demand from: {demandPath}");
        DemandIndex? demandIndex;
        try
        {
            var json = File.ReadAllText(demandPath);
            demandIndex = HubJsonSerializer.Deserialize<DemandIndex>(json);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Could not parse demand index: {ex.Message}");
            Console.ResetColor();
            return 1;
        }

        if (demandIndex == null || demandIndex.Demands.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All manifests are satisfied! Zero missing demands in demand.json.");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine($"Total missing manifests in demand.json: {demandIndex.Demands.Count}");

        // Discover local owned AppIDs without any network requests
        Console.WriteLine("Reading owned Steam AppIDs from local client cache (zero HTTP requests)...");
        var ownedAppIds = LocalLicenseReader.GetOwnedAppIds(steamPath, account?.AccountId ?? 0);
        Console.WriteLine($"Owned AppIDs found locally: {ownedAppIds.Count}");

        // Match missing demands against owned apps
        var matchingDemands = demandIndex.Demands.Values
            .Where(d => ownedAppIds.Contains(d.AppId) || d.AppId == 0)
            .ToList();

        if (matchingDemands.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n[Notice] None of the missing manifests in demand.json belong to your currently owned Steam games.");
            Console.WriteLine("Pending missing demands:");
            foreach (var d in demandIndex.Demands.Values.Take(10))
            {
                Console.WriteLine($"  - AppId: {d.AppId}, DepotId: {d.DepotId}, ManifestId: {d.ManifestId} (Reward: {d.StardustReward} Stardust)");
            }
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\nFound {matchingDemands.Count} missing manifest(s) you own and can download directly via Steam Client:");
        Console.ResetColor();

        int totalReward = 0;
        foreach (var m in matchingDemands)
        {
            Console.WriteLine($"  • AppId: {m.AppId,-7} Depot: {m.DepotId,-7} Manifest: {m.ManifestId,-20} Reward: +{m.StardustReward} Stardust");
            totalReward += m.StardustReward;
        }
        Console.WriteLine($"Total potential reward: {totalReward} Stardust");

        if (!autoConfirm)
        {
            Console.Write("\nDo you want to download these manifests now via Steam? [Y/n]: ");
            var resp = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(resp) && !resp.Equals("y", StringComparison.OrdinalIgnoreCase) && !resp.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Operation cancelled by user.");
                return 0;
            }
        }

        int harvestedCount = 0;
        int earnedStardust = 0;

        foreach (var m in matchingDemands)
        {
            Console.WriteLine($"\nDownloading App {m.AppId}, Depot {m.DepotId}, Manifest {m.ManifestId}...");
            var result = await SteamManifestHarvester.HarvestAsync(
                steamPath,
                m.AppId,
                m.DepotId,
                m.ManifestId,
                destDir,
                TimeSpan.FromSeconds(timeoutSec));

            if (result.Success)
            {
                harvestedCount++;
                earnedStardust += m.StardustReward;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[OK] Successfully harvested Depot {m.DepotId} ({result.FileSizeBytes:N0} bytes)");
                Console.ResetColor();

                TryUpdateDemandAndLedger(repoRoot, m.DepotId, m.ManifestId, result.Sha256Hash, contributor, m.StardustReward);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {result.ErrorMessage}");
                Console.ResetColor();
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n=======================================================");
        Console.WriteLine($"Harvest Complete! Harvested: {harvestedCount}/{matchingDemands.Count}");
        Console.WriteLine($"Total Stardust Earned: +{earnedStardust} to '{contributor}'");
        Console.WriteLine($"=======================================================");
        Console.ResetColor();

        return 0;
    }

    private static int HandleImportCacheCommand(string[] args, string steamPath, string repoRoot)
    {
        string destDir = Path.Combine(repoRoot, "manifests");
        bool demandOnly = false;
        string demandPath = Path.Combine(repoRoot, "indices", "demand.json");

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                destDir = args[i + 1];
                i++;
            }
            else if (args[i].Equals("--demand-only", StringComparison.OrdinalIgnoreCase))
            {
                demandOnly = true;
            }
        }

        HashSet<(uint DepotId, ulong ManifestId)>? requestedDemands = null;
        if (demandOnly && File.Exists(demandPath))
        {
            try
            {
                var dIndex = HubJsonSerializer.Deserialize<DemandIndex>(File.ReadAllText(demandPath));
                if (dIndex != null)
                {
                    requestedDemands = dIndex.Demands.Values
                        .Select(d => (d.DepotId, d.ManifestId))
                        .ToHashSet();
                }
            }
            catch { }
        }

        Console.WriteLine($"Scanning Steam depotcache: {Path.Combine(steamPath, "depotcache")}...");
        var imported = SteamManifestHarvester.ImportExistingFromCache(
            steamPath,
            destDir,
            filter: (depot, manifest) => requestedDemands == null || requestedDemands.Contains((depot, manifest)),
            overwrite: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[Done] Imported {imported.Count} verified manifest(s) into '{destDir}':");
        Console.ResetColor();

        foreach (var item in imported)
        {
            Console.WriteLine($"  - Depot {item.DepotId}, Manifest {item.ManifestId} ({item.FileSizeBytes:N0} bytes)");
            TryUpdateDemandAndLedger(repoRoot, item.DepotId, item.ManifestId, item.Sha256Hash);
        }

        return 0;
    }

    private static int HandleListOwnedCommand(string[] args, string steamPath, SteamAccountInfo? account)
    {
        string? filter = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                filter = args[i + 1];
                i++;
            }
        }

        Console.WriteLine("Reading owned AppIDs from local Steam files (zero HTTP requests)...");
        var appIds = LocalLicenseReader.GetOwnedAppIds(steamPath, account?.AccountId ?? 0);

        var sorted = appIds.OrderBy(x => x).ToList();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            sorted = sorted.Where(x => x.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\nFound {sorted.Count} owned Steam AppIDs locally for {account?.PersonaName ?? "user"}:");
        Console.ResetColor();

        // Print in clean rows of 8
        for (int i = 0; i < sorted.Count; i += 8)
        {
            var chunk = sorted.Skip(i).Take(8);
            Console.WriteLine(string.Join(",\t", chunk));
        }

        return 0;
    }

    private static int HandleStatusCommand(string steamPath, bool isRunning, int? pid, SteamAccountInfo? account, string repoRoot)
    {
        PrintBanner(steamPath, isRunning, pid, account);

        var depotCachePath = Path.Combine(steamPath, "depotcache");
        int depotCacheCount = Directory.Exists(depotCachePath)
            ? Directory.GetFiles(depotCachePath, "*.manifest").Length
            : 0;

        var manifestsPath = Path.Combine(repoRoot, "manifests");
        int repoManifestCount = Directory.Exists(manifestsPath)
            ? Directory.GetFiles(manifestsPath, "*.manifest", SearchOption.AllDirectories).Length
            : 0;

        var demandPath = Path.Combine(repoRoot, "indices", "demand.json");
        int missingDemandCount = 0;
        if (File.Exists(demandPath))
        {
            try
            {
                var dIndex = HubJsonSerializer.Deserialize<DemandIndex>(File.ReadAllText(demandPath));
                missingDemandCount = dIndex?.Demands.Count ?? 0;
            }
            catch { }
        }

        Console.WriteLine("Storage & Cache Diagnostics:");
        Console.WriteLine($"  - Steam depotcache:   {depotCachePath} ({depotCacheCount} manifests cached)");
        Console.WriteLine($"  - Hub manifests dir:  {manifestsPath} ({repoManifestCount} manifests in Hub)");
        Console.WriteLine($"  - Hub demand.json:    {demandPath} ({missingDemandCount} missing manifests pending)");
        Console.WriteLine();

        return 0;
    }

    private static void TryUpdateDemandAndLedger(
        string repoRoot,
        uint depotId,
        ulong manifestId,
        string? sha256,
        string? contributor = null,
        int rewardPoints = 100)
    {
        try
        {
            var demandPath = Path.Combine(repoRoot, "indices", "demand.json");
            if (File.Exists(demandPath))
            {
                var dIndex = HubJsonSerializer.Deserialize<DemandIndex>(File.ReadAllText(demandPath));
                if (dIndex != null)
                {
                    string demandKey = $"{depotId}:{manifestId}";
                    var removedKey = dIndex.Demands.Keys.FirstOrDefault(k => k.EndsWith($":{depotId}:{manifestId}") || k == demandKey);
                    if (removedKey != null)
                    {
                        dIndex.Demands.Remove(removedKey);
                        dIndex.TotalMissing = dIndex.Demands.Count;
                        dIndex.LastUpdated = DateTimeOffset.UtcNow;
                        File.WriteAllText(demandPath, HubJsonSerializer.Serialize(dIndex));
                        Console.WriteLine($"[DemandEngine] Satisfied and removed {depotId}:{manifestId} from indices/demand.json");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(contributor))
            {
                var ledgerManager = new StardustLedgerManager(new StorageOptions { BasePath = repoRoot });
                ledgerManager.AwardPointsForManifestAsync(
                    contributor,
                    depotId,
                    manifestId,
                    ManifestStatus.AvailableAndValidated,
                    points: rewardPoints).GetAwaiter().GetResult();
                Console.WriteLine($"[StardustLedger] Credited +{rewardPoints} Stardust to '{contributor}' in ledger/stardust.json");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Non-critical error updating demand/ledger: {ex.Message}");
        }
    }

    private static int ShowHelp()
    {
        Console.WriteLine(@"BlueStar Steam Manifest Harvester (Standalone CLI)

Usage:
  dotnet run --project src/BlueStar.ManifestHarvester.Cli -- [command] [options]
  or use: .\harvest.bat [command] [options]

Commands:
  download, harvest   Download a manifest by AppID, DepotID, and ManifestID via Steam Client
  demand              Scan indices/demand.json and download missing manifests for owned games
  import-cache        Import manifests already cached in Steam's depotcache folder
  list-owned          List all AppIDs owned by the active Steam account (zero HTTP requests)
  status              Display Steam process, active user, and Hub repository status
  help                Show this help message

Options for 'download':
  --app <AppId>           Steam Application ID (e.g. 480)
  --depot <DepotId>       Steam Depot ID
  --manifest <ManifestId> Steam Manifest ID
  --output <dir>          Output folder (default: manifests/)
  --timeout <seconds>     Timeout waiting for Steam download (default: 30)

Options for 'demand':
  --demand-file <file>    Path to demand.json (default: indices/demand.json)
  --output <dir>          Output folder (default: manifests/)
  --contributor <name>    Pseudonym for Stardust reward credit
  --yes, -y               Automatically confirm downloads without interactive prompt
");
        return 0;
    }

    private static int ShowUnknownCommand(string command)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Unknown command '{command}'. Use 'help' to see available commands.");
        Console.ResetColor();
        return 1;
    }
}
