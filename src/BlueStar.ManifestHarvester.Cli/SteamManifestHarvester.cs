using System.Diagnostics;
using System.Security.Cryptography;
using BlueStar.ManifestHub.Core.Serialization;
using BlueStar.ManifestHub.Core.Validation;

namespace BlueStar.ManifestHarvester.Cli;

public sealed record HarvestResult(
    bool Success,
    uint AppId,
    uint DepotId,
    ulong ManifestId,
    string? FilePath,
    string? Sha256Hash,
    long FileSizeBytes,
    string? ErrorMessage,
    bool FromCache
);

public static class SteamManifestHarvester
{
    private static readonly byte[] SteamProtobufMagic = [0xD0, 0x17, 0xF6, 0x71];

    public static async Task<HarvestResult> HarvestAsync(
        string steamPath,
        uint appId,
        uint depotId,
        ulong manifestId,
        string destinationDir,
        TimeSpan timeout,
        bool overwrite = true,
        CancellationToken ct = default)
    {
        var depotCacheDir = Path.Combine(steamPath, "depotcache");
        Directory.CreateDirectory(depotCacheDir);

        var manifestFileName = $"{depotId}_{manifestId}.manifest";
        var cacheFilePath = Path.Combine(depotCacheDir, manifestFileName);

        var destDepotDir = Path.Combine(destinationDir, depotId.ToString());
        Directory.CreateDirectory(destDepotDir);
        var finalDestPath = Path.Combine(destDepotDir, $"{manifestId}.manifest");

        // 1. Check if already present and valid in depotcache
        if (File.Exists(cacheFilePath) && ValidateMagicHeader(cacheFilePath))
        {
            var res = CopyAndVerify(cacheFilePath, finalDestPath, appId, depotId, manifestId, overwrite, fromCache: true);
            if (res.Success)
            {
                return res;
            }
        }

        // 2. Prepare FileSystemWatcher
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        var fileTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new FileSystemWatcher(depotCacheDir, manifestFileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler onCreated = (s, e) =>
        {
            if (File.Exists(cacheFilePath))
            {
                fileTcs.TrySetResult(true);
            }
        };

        watcher.Created += onCreated;
        watcher.Changed += onCreated;

        // 3. Trigger download_manifest in Steam client
        var manifestCommand = $"download_manifest {appId} {depotId} {manifestId}";
        var commandArgs = $"-console +download_manifest {appId} {depotId} {manifestId}";
        var steamExe = Path.Combine(steamPath, "steam.exe");

        // Open Steam console tab directly
        try
        {
            Process.Start(new ProcessStartInfo("steam://open/console") { UseShellExecute = true });
        }
        catch { }

        // Copy command to clipboard for instant pasting
        bool copied = TryCopyToClipboard(manifestCommand);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n=========================================================================");
        Console.WriteLine(" [STEAM CONSOLE ACTIVADO]");
        if (copied)
        {
            Console.WriteLine(" El comando se ha COPIADO automáticamente a tu portapapeles (Ctrl+V):");
        }
        else
        {
            Console.WriteLine(" Comando para descargar el manifest:");
        }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"   >> {manifestCommand} <<");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("\n Instrucciones:");
        Console.WriteLine("   1. En la ventana de Steam (pestaña CONSOLE ya abierta):");
        Console.WriteLine("   2. Haz clic en la caja de texto inferior, presiona Ctrl+V y pulsa Enter.");
        Console.WriteLine("=========================================================================");
        Console.ResetColor();

        // Also launch steam.exe with args in case Steam was not running or cold-booted
        try
        {
            if (File.Exists(steamExe))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = steamExe,
                    Arguments = commandArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
            }
        }
        catch { }

        // 4. Await file appearance via watcher + polling with live spinner
        try
        {
            using (linkedCts.Token.Register(() => fileTcs.TrySetCanceled()))
            {
                var pollInterval = TimeSpan.FromMilliseconds(250);
                var startTime = Stopwatch.GetTimestamp();
                char[] spinner = ['|', '/', '-', '\\'];
                int spinIdx = 0;

                while (!fileTcs.Task.IsCompleted && !linkedCts.Token.IsCancellationRequested)
                {
                    if (File.Exists(cacheFilePath))
                    {
                        fileTcs.TrySetResult(true);
                        break;
                    }

                    var elapsed = Stopwatch.GetElapsedTime(startTime);
                    Console.Write($"\r[Esperando en depotcache] {spinner[spinIdx++ % spinner.Length]} {elapsed.TotalSeconds:0}s / {timeout.TotalSeconds:0}s...   ");
                    await Task.Delay(pollInterval, linkedCts.Token).ConfigureAwait(false);
                }

                Console.WriteLine();
                await fileTcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            // Final fallback check
            if (File.Exists(cacheFilePath) && ValidateMagicHeader(cacheFilePath))
            {
                return CopyAndVerify(cacheFilePath, finalDestPath, appId, depotId, manifestId, overwrite, fromCache: false);
            }

            return new HarvestResult(
                Success: false,
                AppId: appId,
                DepotId: depotId,
                ManifestId: manifestId,
                FilePath: null,
                Sha256Hash: null,
                FileSizeBytes: 0,
                ErrorMessage: $"Timed out after {(int)timeout.TotalSeconds}s waiting for Steam to download {manifestFileName}. Make sure you own this game and Steam is running.",
                FromCache: false
            );
        }

        // 5. Allow file write to flush completely
        await WaitForFileReadyAsync(cacheFilePath, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

        // 6. Copy and verify
        return CopyAndVerify(cacheFilePath, finalDestPath, appId, depotId, manifestId, overwrite, fromCache: false);
    }

    public static List<HarvestResult> ImportExistingFromCache(
        string steamPath,
        string destinationDir,
        Func<uint, ulong, bool>? filter = null,
        bool overwrite = false)
    {
        var results = new List<HarvestResult>();
        var depotCacheDir = Path.Combine(steamPath, "depotcache");
        if (!Directory.Exists(depotCacheDir)) return results;

        var files = Directory.GetFiles(depotCacheDir, "*.manifest");
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var parts = fileName.Split('_');
            if (parts.Length != 2 || !uint.TryParse(parts[0], out var depotId) || !ulong.TryParse(parts[1], out var manifestId))
            {
                continue;
            }

            if (filter != null && !filter(depotId, manifestId))
            {
                continue;
            }

            if (!ValidateMagicHeader(file))
            {
                continue;
            }

            var destDepotDir = Path.Combine(destinationDir, depotId.ToString());
            Directory.CreateDirectory(destDepotDir);
            var finalDestPath = Path.Combine(destDepotDir, $"{manifestId}.manifest");

            var res = CopyAndVerify(file, finalDestPath, appId: 0, depotId, manifestId, overwrite, fromCache: true);
            results.Add(res);
        }

        return results;
    }

    private static HarvestResult CopyAndVerify(
        string sourceFile,
        string destinationFile,
        uint appId,
        uint depotId,
        ulong manifestId,
        bool overwrite,
        bool fromCache)
    {
        try
        {
            var bytes = File.ReadAllBytes(sourceFile);
            if (!ValidateMagicHeaderBytes(bytes))
            {
                return new HarvestResult(
                    Success: false,
                    AppId: appId,
                    DepotId: depotId,
                    ManifestId: manifestId,
                    FilePath: null,
                    Sha256Hash: null,
                    FileSizeBytes: bytes.Length,
                    ErrorMessage: "Manifest failed protobuf magic header check (expected 0x71F617D0).",
                    FromCache: fromCache
                );
            }

            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            if (File.Exists(destinationFile) && !overwrite)
            {
                return new HarvestResult(
                    Success: true,
                    AppId: appId,
                    DepotId: depotId,
                    ManifestId: manifestId,
                    FilePath: destinationFile,
                    Sha256Hash: sha256,
                    FileSizeBytes: bytes.Length,
                    ErrorMessage: null,
                    FromCache: fromCache
                );
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);

            return new HarvestResult(
                Success: true,
                AppId: appId,
                DepotId: depotId,
                ManifestId: manifestId,
                FilePath: destinationFile,
                Sha256Hash: sha256,
                FileSizeBytes: bytes.Length,
                ErrorMessage: null,
                FromCache: fromCache
            );
        }
        catch (Exception ex)
        {
            return new HarvestResult(
                Success: false,
                AppId: appId,
                DepotId: depotId,
                ManifestId: manifestId,
                FilePath: null,
                Sha256Hash: null,
                FileSizeBytes: 0,
                ErrorMessage: $"Error copying/verifying manifest: {ex.Message}",
                FromCache: fromCache
            );
        }
    }

    public static bool ValidateMagicHeader(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            var fi = new FileInfo(filePath);
            if (fi.Length < 4) return false;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[4];
            int read = fs.Read(header, 0, 4);
            return read == 4 && ValidateMagicHeaderBytes(header);
        }
        catch
        {
            return false;
        }
    }

    public static bool ValidateMagicHeaderBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return false;
        return bytes[0] == SteamProtobufMagic[0] &&
               bytes[1] == SteamProtobufMagic[1] &&
               bytes[2] == SteamProtobufMagic[2] &&
               bytes[3] == SteamProtobufMagic[3];
    }

    private static async Task WaitForFileReadyAsync(string path, TimeSpan maxWait, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < maxWait && !ct.IsCancellationRequested)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 4)
                {
                    return;
                }
            }
            catch (IOException)
            {
                // File still being written by Steam
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public static bool TryCopyToClipboard(string text)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Set-Clipboard -Value '{text.Replace("'", "''")}'\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
