using BlueStar.ManifestHub.Core.Ingestion;
using BlueStar.ManifestHub.Core.Ingestion.Adapters;
using BlueStar.ManifestHub.Core.Receiver;
using BlueStar.ManifestHub.Core.Storage;
using BlueStar.ManifestHub.Core.Validation;

namespace BlueStar.ManifestHub.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("BlueStar Manifest Hub - Ingestion Tool");

        string repoRoot = Directory.GetCurrentDirectory();
        var storageManager = new ManifestStorageManager(new StorageOptions { BasePath = repoRoot });
        var validator = new ManifestValidator();

        var rateGovernor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            RequestsPerMinute = RateGovernorOptions.DefaultRequestsPerMinute,
            StateFilePath = Path.Combine(Path.GetTempPath(), "bluestar_ratelimit_state.json")
        });

        string? apiKey = Environment.GetEnvironmentVariable("DEPOTBOX_API_KEY");
        var adapters = new List<IManifestSourceAdapter>
        {
            new DepotBoxSourceAdapter(rateGovernor, new DepotBoxOptions { ApiKey = apiKey, AllowBulkIngestion = false }),
            new ManifestHub2SourceAdapter(),
            new K25SourceAdapter()
        };

        var pipeline = new IngestionPipeline(adapters, storageManager, validator);

        // Check for server/receiver mode
        bool isServerMode = args.Any(a => a.Equals("--serve", StringComparison.OrdinalIgnoreCase) ||
                                          a.Equals("--receiver", StringComparison.OrdinalIgnoreCase));
        if (isServerMode)
        {
            int port = HubReceiverOptions.DefaultPort;
            string host = "0.0.0.0";

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i].Equals("--port", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-p", StringComparison.OrdinalIgnoreCase)) &&
                    i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                {
                    port = p;
                    i++;
                }
                else if (args[i].Equals("--host", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    host = args[i + 1];
                    i++;
                }
            }

            var ledgerManager = new BlueStar.ManifestHub.Core.Ledger.StardustLedgerManager(new StorageOptions { BasePath = repoRoot });
            string? webhookSecret = Environment.GetEnvironmentVariable("WEBHOOK_SECRET");
            var webhookProcessor = new BlueStar.ManifestHub.Core.Webhooks.WebhookProcessor(pipeline, new BlueStar.ManifestHub.Core.Webhooks.WebhookProcessorOptions
            {
                SecretKey = webhookSecret,
                RequireAuthentication = !string.IsNullOrEmpty(webhookSecret)
            });

            await using var receiverServer = new BlueStar.ManifestHub.Core.Receiver.HubReceiverServer(
                storageManager,
                ledgerManager,
                webhookProcessor,
                new BlueStar.ManifestHub.Core.Receiver.HubReceiverOptions
                {
                    Port = port,
                    Host = host,
                    WebhookSecretKey = webhookSecret,
                    RequireWebhookAuthentication = !string.IsNullOrEmpty(webhookSecret)
                });

            Console.WriteLine($"[RECEIVER] Starting BlueStar Manifest Hub remote receiver on {host}:{port}...");
            await receiverServer.StartAsync();
            Console.WriteLine($"[RECEIVER] Server running at {receiverServer.BaseUrl}. Ready for contributions and webhooks.");
            await receiverServer.WaitForShutdownAsync();
            return 0;
        }

        // Bootstrap catalog index if indices/apps.json does not exist
        string appsPath = storageManager.GetAppIndexPath();
        if (!File.Exists(appsPath))
        {
            Console.WriteLine($"[BOOTSTRAP] Catalog index not found at '{appsPath}'. Initializing base seed population...");
            string seedPath = Path.Combine(repoRoot, "seeds", "apps.json");
            IEnumerable<BlueStar.ManifestHub.Core.Models.AppRecord>? seeds = null;
            if (File.Exists(seedPath))
            {
                try
                {
                    string seedJson = await File.ReadAllTextAsync(seedPath);
                    var dict = BlueStar.ManifestHub.Core.Serialization.HubJsonSerializer.Deserialize<Dictionary<string, BlueStar.ManifestHub.Core.Models.AppRecord>>(seedJson);
                    if (dict != null && dict.Count > 0)
                    {
                        seeds = dict.Values;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Could not parse seed file at {seedPath}: {ex.Message}");
                }
            }

            int count = await storageManager.BootstrapAppIndexIfMissingAsync(seeds);
            Console.WriteLine($"[BOOTSTRAP] Successfully bootstrapped catalog index with {count} base applications.");
        }

        var appIdsToIngest = new List<uint>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--app" && i + 1 < args.Length && uint.TryParse(args[i + 1], out uint aId))
            {
                appIdsToIngest.Add(aId);
                i++;
            }
        }

        if (appIdsToIngest.Count == 0)
        {
            // By default or --ingest-all: check apps in catalog index if available
            if (File.Exists(appsPath))
            {
                Console.WriteLine($"Reading apps from catalog index: {appsPath}");
                try
                {
                    string json = await File.ReadAllTextAsync(appsPath);
                    var apps = BlueStar.ManifestHub.Core.Serialization.HubJsonSerializer.Deserialize<Dictionary<string, BlueStar.ManifestHub.Core.Models.AppRecord>>(json);
                    if (apps != null && apps.Count > 0)
                    {
                        appIdsToIngest.AddRange(apps.Values.Select(a => a.AppId).Distinct());
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Could not parse catalog index at {appsPath}: {ex.Message}");
                }
            }
        }

        if (appIdsToIngest.Count == 0)
        {
            Console.Error.WriteLine("[ERROR] No applications found or specified to process. Aborting with exit code 1.");
            return 1;
        }

        Console.WriteLine($"Ingesting manifests for {appIdsToIngest.Count} applications...");
        int totalDiscovered = 0;
        int totalPublished = 0;
        int totalAlreadyStored = 0;
        int totalFailed = 0;

        foreach (uint appId in appIdsToIngest)
        {
            Console.WriteLine($"[INGEST] Processing AppId {appId}...");
            var result = await pipeline.IngestAppAsync(appId);
            Console.WriteLine($"[INGEST] Discovered: {result.TotalDiscovered}, AlreadyStored: {result.AlreadyStored}, Ingested: {result.SuccessfullyIngested}, Failed: {result.Failed}");
            if (result.HasErrors)
            {
                foreach (var err in result.Errors)
                {
                    Console.WriteLine($"  [WARN] {err}");
                }
            }

            totalDiscovered += result.TotalDiscovered;
            totalPublished += result.SuccessfullyIngested;
            totalAlreadyStored += result.AlreadyStored;
            totalFailed += result.Failed;
        }

        int totalRejected = totalAlreadyStored + totalFailed;

        // Structured summary table
        Console.WriteLine();
        Console.WriteLine("+----------------------------------------------------------+");
        Console.WriteLine("|                MANIFEST INGESTION SUMMARY                |");
        Console.WriteLine("+-----------------------------------+----------------------+");
        Console.WriteLine($"| Applications Processed            | {appIdsToIngest.Count,20} |");
        Console.WriteLine($"| Total Discovered                  | {totalDiscovered,20} |");
        Console.WriteLine($"| Total Published                   | {totalPublished,20} |");
        Console.WriteLine($"| Total Rejected                    | {totalRejected,20} |");
        Console.WriteLine($"|   - Already Stored                | {totalAlreadyStored,20} |");
        Console.WriteLine($"|   - Failed / Invalid              | {totalFailed,20} |");
        Console.WriteLine("+-----------------------------------+----------------------+");
        Console.WriteLine();

        // CRITICAL: Do NOT declare success (exit code 0) if all sources returned zero discovered manifests across all processed apps!
        if (totalDiscovered == 0)
        {
            Console.Error.WriteLine("[ERROR] All ingestion sources returned zero discovered manifests across all processed applications.");
            Console.Error.WriteLine("[ERROR] Ingestion failed. Exiting with exit code 1.");
            return 1;
        }

        if (totalPublished == 0 && totalAlreadyStored == 0 && totalFailed > 0)
        {
            Console.Error.WriteLine("[ERROR] Ingestion failed: all discovered manifests failed validation or acquisition.");
            return 1;
        }

        Console.WriteLine("[SUCCESS] Ingestion completed successfully.");
        return 0;
    }
}
