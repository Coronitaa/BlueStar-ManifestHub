using BlueStar.ManifestHub.Core.Demand;
using BlueStar.ManifestHub.Core.Ingestion;
using BlueStar.ManifestHub.Core.Ingestion.Adapters;
using BlueStar.ManifestHub.Core.Ledger;
using BlueStar.ManifestHub.Core.Models;
using BlueStar.ManifestHub.Core.Receiver;
using BlueStar.ManifestHub.Core.Serialization;
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
        storageManager.EnsureDirectoryStructure();
        var validator = new ManifestValidator();
        var demandEngine = new DemandEngine(storageManager);

        // Check for --verify-contribution mode
        bool isVerifyContribution = false;
        string? verifyManifestPath = null;
        string? contributor = null;
        uint verifyAppId = 0;
        uint verifyDepotId = 0;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--verify-contribution", StringComparison.OrdinalIgnoreCase))
            {
                isVerifyContribution = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    verifyManifestPath = args[i + 1];
                    i++;
                }
            }
            else if (args[i].Equals("--contributor", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                contributor = args[i + 1];
                i++;
            }
            else if (args[i].Equals("--app", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && uint.TryParse(args[i + 1], out uint aId))
            {
                verifyAppId = aId;
            }
            else if (args[i].Equals("--depot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && uint.TryParse(args[i + 1], out uint dId))
            {
                verifyDepotId = dId;
                i++;
            }
        }

        if (isVerifyContribution)
        {
            if (string.IsNullOrWhiteSpace(verifyManifestPath))
            {
                var errorReceipt = new
                {
                    isSuccess = false,
                    status = "Corrupted",
                    error = "Missing manifest file path for --verify-contribution."
                };
                Console.WriteLine(HubJsonSerializer.Serialize(errorReceipt));
                return 1;
            }

            if (!File.Exists(verifyManifestPath))
            {
                var errorReceipt = new
                {
                    isSuccess = false,
                    status = "Missing",
                    error = $"Manifest file not found at '{verifyManifestPath}'."
                };
                Console.WriteLine(HubJsonSerializer.Serialize(errorReceipt));
                return 1;
            }

            string contributorPseudonym = string.IsNullOrWhiteSpace(contributor) ? "anonymous_contributor" : contributor.Trim();
            try
            {
                StardustLedgerManager.ValidateContributorPseudonym(contributorPseudonym);
            }
            catch (InvalidContributorIdentityException ex)
            {
                var errorReceipt = new
                {
                    isSuccess = false,
                    status = "Restricted",
                    error = ex.Message
                };
                Console.WriteLine(HubJsonSerializer.Serialize(errorReceipt));
                return 1;
            }

            byte[] manifestBytes;
            try
            {
                manifestBytes = await File.ReadAllBytesAsync(verifyManifestPath);
            }
            catch (Exception ex)
            {
                var errorReceipt = new
                {
                    isSuccess = false,
                    status = "Inaccessible",
                    error = $"Could not read manifest file: {ex.Message}"
                };
                Console.WriteLine(HubJsonSerializer.Serialize(errorReceipt));
                return 1;
            }

            var valResult = validator.Validate(manifestBytes, expectedDepotId: verifyDepotId, expectedManifestId: 0);
            if (!valResult.IsValid)
            {
                var errorReceipt = new
                {
                    isSuccess = false,
                    status = valResult.Status.ToString(),
                    error = valResult.ErrorMessage
                };
                Console.WriteLine(HubJsonSerializer.Serialize(errorReceipt));
                return 1;
            }

            uint depotId = valResult.DepotId > 0 ? valResult.DepotId : verifyDepotId;
            ulong manifestId = valResult.ManifestId;

            if (depotId == 0)
            {
                var errorReceipt = new
                {
                    isSuccess = false,
                    status = "Corrupted",
                    error = "Could not determine DepotId from manifest protobuf payload or --depot argument."
                };
                Console.WriteLine(HubJsonSerializer.Serialize(errorReceipt));
                return 1;
            }

            if (manifestId == 0)
            {
                var errorReceipt = new
                {
                    isSuccess = false,
                    status = "Corrupted",
                    error = "Could not determine ManifestId (GID) from manifest protobuf payload."
                };
                Console.WriteLine(HubJsonSerializer.Serialize(errorReceipt));
                return 1;
            }

            string canonicalPath = storageManager.GetManifestPath(depotId, manifestId);
            var pubResult = await storageManager.PublishManifestAsync(
                manifestBytes,
                depotId,
                manifestId,
                source: "contribution",
                contributorPseudonym: contributorPseudonym);

            if (!pubResult.IsSuccess)
            {
                var errorReceipt = new
                {
                    isSuccess = false,
                    status = pubResult.Status.ToString(),
                    error = pubResult.Message
                };
                Console.WriteLine(HubJsonSerializer.Serialize(errorReceipt));
                return 1;
            }

            string fullSource = Path.GetFullPath(verifyManifestPath);
            string fullCanonical = Path.GetFullPath(canonicalPath);
            if (!string.Equals(fullSource, fullCanonical, StringComparison.OrdinalIgnoreCase) && File.Exists(fullSource))
            {
                try { File.Delete(fullSource); } catch { /* best effort */ }
            }

            int rewardPoints = await demandEngine.GetStardustRewardForManifestAsync(depotId, manifestId, fallbackReward: 100);
            await demandEngine.RemoveDemandAsync(depotId, manifestId);

            var ledgerManager = new StardustLedgerManager(new StorageOptions { BasePath = repoRoot });
            int stardustCredited = 0;
            bool isDuplicate = pubResult.IsDuplicate;
            string? txId = null;

            if (!isDuplicate)
            {
                try
                {
                    var ledgerEntry = await ledgerManager.AwardPointsForManifestAsync(
                        contributorPseudonym: contributorPseudonym,
                        depotId: depotId,
                        manifestId: manifestId,
                        status: ManifestStatus.AvailableAndValidated,
                        points: rewardPoints,
                        reason: $"Contribution verified via CLI for depot {depotId} manifest {manifestId}");

                    stardustCredited = ledgerEntry.Points;
                    txId = ledgerEntry.TransactionId;
                }
                catch (DuplicateArtifactRewardException)
                {
                    isDuplicate = true;
                    stardustCredited = 0;
                }
            }

            uint appId = verifyAppId;
            if (appId == 0)
            {
                var appRec = await storageManager.GetAppRecordByDepotIdAsync(depotId);
                if (appRec != null)
                {
                    appId = appRec.AppId;
                }
            }
            if (appId > 0)
            {
                var depotRecord = await storageManager.GetDepotRecordAsync(depotId);
                var manifestIds = depotRecord?.ManifestIds ?? new[] { manifestId };
                await storageManager.ComputeAndSaveCoverageAsync(appId, depotId, manifestIds);
            }

            var receipt = new
            {
                isSuccess = true,
                status = isDuplicate ? "Duplicate" : "Published",
                receiptId = txId ?? Guid.NewGuid().ToString("N"),
                appId = appId,
                depotId = depotId,
                manifestId = manifestId,
                sha256 = valResult.Sha256,
                stardustCredited = stardustCredited,
                contributor = contributorPseudonym,
                canonicalPath = canonicalPath,
                isDuplicate = isDuplicate,
                timestamp = DateTimeOffset.UtcNow
            };
            Console.WriteLine(HubJsonSerializer.Serialize(receipt));
            return 0;
        }

        // Check for --generate-demand mode
        bool isGenerateDemand = args.Any(a => a.Equals("--generate-demand", StringComparison.OrdinalIgnoreCase));
        if (isGenerateDemand)
        {
            var demandAppIds = new List<uint>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--app" && i + 1 < args.Length && uint.TryParse(args[i + 1], out uint aId))
                {
                    demandAppIds.Add(aId);
                    i++;
                }
            }

            Console.WriteLine("[DEMAND] Calculating manifest demand across tracked applications...");
            var demandIndex = await demandEngine.GenerateDemandAsync(demandAppIds.Count > 0 ? demandAppIds : null);

            Console.WriteLine($"[DEMAND] Generated demand index with {demandIndex.TotalMissing} missing manifests across {demandIndex.Apps.Count} applications.");
            Console.WriteLine($"[DEMAND] Index stored at {storageManager.GetDemandIndexPath()}");
            return 0;
        }

        var rateGovernor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            RequestsPerMinute = RateGovernorOptions.DefaultRequestsPerMinute,
            StateFilePath = Path.Combine(Path.GetTempPath(), "bluestar_ratelimit_state.json")
        });

        string? apiKey = Environment.GetEnvironmentVariable("DEPOTBOX_API_KEY");
        var adapters = new List<IManifestSourceAdapter>();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            adapters.Add(new DepotBoxSourceAdapter(rateGovernor, new DepotBoxOptions { ApiKey = apiKey, AllowBulkIngestion = false, RequireApiKey = true }));
        }
        else
        {
            Console.WriteLine("[WARN] DEPOTBOX_API_KEY is not configured or empty. Cleanly skipping DepotBox source provider.");
        }
        adapters.Add(new ManifestHub2SourceAdapter(rateGovernor));
        adapters.Add(new K25SourceAdapter(rateGovernor));

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

        // Invoke batch snapshot generation across all processed applications
        Console.WriteLine("[SNAPSHOT] Generating batch snapshots for tracked applications...");
        int batchSnapshotsCount = await storageManager.GenerateBatchSnapshotsAsync(appIdsToIngest);
        Console.WriteLine($"[SNAPSHOT] Generated / synchronized snapshots for {batchSnapshotsCount} applications.");

        // Automatically update demand index at the end of synchronization
        Console.WriteLine("[DEMAND] Updating demand index (indices/demand.json)...");
        var syncDemandIndex = await demandEngine.GenerateDemandAsync(appIdsToIngest);
        Console.WriteLine($"[DEMAND] Demand index updated: {syncDemandIndex.TotalMissing} missing manifests cataloged across {syncDemandIndex.Apps.Count} applications.");

        Console.WriteLine("[SUCCESS] Ingestion completed successfully.");
        return 0;
    }
}
