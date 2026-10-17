using System.Diagnostics;
using System.Text.Json;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;

namespace BlueStar.ManifestHarvester.Cli;

public sealed record SteamAuthToken(
    string AccountName,
    string RefreshToken,
    ulong SteamId,
    DateTimeOffset AuthenticatedAt
);

public static class SteamQrAuthenticator
{
    public static async Task<SteamAuthToken?> AuthenticateViaQrAsync(string repoRoot, CancellationToken ct = default)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            BlueStar Steam Client - Autenticación con Código QR           ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.Write("Conectando con la red oficial de Steam (Steam3)... ");

        var config = SteamConfiguration.Create(c => c.WithHttpClientFactory(() => new HttpClient()));
        var client = new SteamClient(config);
        var callbacks = new CallbackManager(client);

        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        callbacks.Subscribe<SteamClient.ConnectedCallback>(cb => connectedTcs.TrySetResult(true));
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            if (!connectedTcs.Task.IsCompleted)
            {
                connectedTcs.TrySetResult(false);
            }
        });

        client.Connect();

        // Run callbacks in background
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            while (!loopCts.Token.IsCancellationRequested)
            {
                callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
                await Task.Delay(20, loopCts.Token).ConfigureAwait(false);
            }
        }, loopCts.Token);

        var connected = await connectedTcs.Task.ConfigureAwait(false);
        if (!connected)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error al conectar con los servidores de Steam.");
            Console.ResetColor();
            return null;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("¡Conectado!");
        Console.ResetColor();

        Console.WriteLine("Solicitando desafío de inicio de sesión con Código QR...");

        QrAuthSession? authSession;
        try
        {
            authSession = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                DeviceFriendlyName = "BlueStar Manifest Harvester",
                IsPersistentSession = true
            });
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error al iniciar la sesión de autenticación: {ex.Message}");
            Console.ResetColor();
            return null;
        }

        string challengeUrl = authSession.ChallengeURL;
        string htmlPath = Path.Combine(Path.GetTempPath(), "bluestar_steam_qr.html");

        // Generate and open HTML interface
        GenerateHtmlInterface(challengeUrl, htmlPath);

        // Also display in terminal
        DisplayTerminalQr(challengeUrl);

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("──────────────────────────────────────────────────────────────────────────");
        Console.WriteLine("  1. Abre la aplicación de Steam en tu teléfono móvil.");
        Console.WriteLine("  2. Toca el ícono de escáner en la barra superior (o menú -> Steam Guard).");
        Console.WriteLine("  3. Escanea el código QR que se abrió en tu navegador o el de la terminal.");
        Console.WriteLine("  4. Pulsa 'Iniciar sesión' en tu celular para confirmar.");
        Console.WriteLine("──────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        // Handle QR challenge rotation if Steam changes it
        authSession.ChallengeURLChanged = () =>
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n[Steam] El código QR ha sido renovado. Actualizando interfaz...");
            Console.ResetColor();
            GenerateHtmlInterface(authSession.ChallengeURL, htmlPath);
            DisplayTerminalQr(authSession.ChallengeURL);
        };

        // Poll for authorization result
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Esperando tu confirmación en la app de Steam...");
        Console.ResetColor();

        char[] spinner = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
        int sIdx = 0;
        var startWait = Stopwatch.GetTimestamp();

        var pollTask = authSession.PollingWaitForResultAsync();

        while (!pollTask.IsCompleted && !ct.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(startWait);
            Console.Write($"\r [{spinner[sIdx++ % spinner.Length]}] Esperando escaneo ({elapsed.TotalSeconds:0}s)... ");
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        Console.WriteLine();

        try
        {
            var result = await pollTask.ConfigureAwait(false);

            ulong steamId = 0;
            if (client.SteamID != null)
            {
                steamId = client.SteamID.ConvertToUInt64();
            }

            var token = new SteamAuthToken(
                AccountName: result.AccountName,
                RefreshToken: result.RefreshToken,
                SteamId: steamId,
                AuthenticatedAt: DateTimeOffset.UtcNow
            );

            // Save token to config/steam_auth.json
            var configDir = Path.Combine(repoRoot, "config");
            Directory.CreateDirectory(configDir);
            var tokenFile = Path.Combine(configDir, "steam_auth.json");
            File.WriteAllText(tokenFile, JsonSerializer.Serialize(token, new JsonSerializerOptions { WriteIndented = true }));

            // Update HTML with success state
            GenerateSuccessHtml(result.AccountName, htmlPath);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                    ¡AUTENTICACIÓN EXITOSA CON STEAM!                     ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine($"  Usuario:        {result.AccountName}");
            Console.WriteLine($"  Token Guardado: {tokenFile}");
            Console.WriteLine("  A partir de ahora, todas las descargas se autenticarán automáticamente.\n");

            loopCts.Cancel();
            client.Disconnect();

            return token;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[Error] La autenticación falló o expiró: {ex.Message}");
            Console.ResetColor();
            loopCts.Cancel();
            client.Disconnect();
            return null;
        }
    }

    private static void DisplayTerminalQr(string challengeUrl)
    {
        try
        {
            using var qrGen = new QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrData);
            var lines = qrCode.GetLineByLineGraphic(1, drawQuietZones: true);

            Console.WriteLine();
            foreach (var line in lines)
            {
                Console.WriteLine("   " + line);
            }
            Console.WriteLine();
        }
        catch
        {
            Console.WriteLine($"[URL de desafío Steam]: {challengeUrl}");
        }
    }

    private static void GenerateHtmlInterface(string challengeUrl, string htmlPath)
    {
        try
        {
            using var qrGen = new QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.M);
            using var svgCode = new SvgQRCode(qrData);
            string svg = svgCode.GetGraphic(8);

            string html = $$"""
<!DOCTYPE html>
<html lang="es">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>BlueStar - Inicio de Sesión Steam QR</title>
    <style>
        :root {
            --bg-color: #0b141a;
            --card-bg: rgba(23, 26, 33, 0.95);
            --steam-blue: #1a9fff;
            --steam-navy: #171a21;
            --steam-gradient: linear-gradient(135deg, #101822 0%, #172230 100%);
            --accent-glow: #66c0f4;
            --text-main: #f0f4f8;
            --text-dim: #8f98a0;
            --success: #5c7e10;
            --success-glow: #a4d007;
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
        }

        body {
            background-color: var(--bg-color);
            background-image: 
                radial-gradient(circle at 50% 0%, rgba(26, 159, 255, 0.15) 0%, transparent 60%),
                radial-gradient(circle at 100% 100%, rgba(102, 192, 244, 0.08) 0%, transparent 50%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            color: var(--text-main);
            padding: 24px;
        }

        .card {
            background: var(--card-bg);
            border: 1px solid rgba(102, 192, 244, 0.2);
            border-radius: 20px;
            box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.6), 0 0 40px rgba(26, 159, 255, 0.15);
            width: 100%;
            max-width: 480px;
            overflow: hidden;
            backdrop-filter: blur(16px);
            animation: fadeIn 0.6s cubic-bezier(0.16, 1, 0.3, 1);
        }

        @keyframes fadeIn {
            from { opacity: 0; transform: translateY(20px) scale(0.98); }
            to { opacity: 1; transform: translateY(0) scale(1); }
        }

        .header {
            background: var(--steam-gradient);
            padding: 28px 24px 20px;
            text-align: center;
            border-bottom: 1px solid rgba(255, 255, 255, 0.06);
            position: relative;
        }

        .badge {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            background: rgba(102, 192, 244, 0.12);
            color: var(--accent-glow);
            padding: 6px 14px;
            border-radius: 9999px;
            font-size: 13px;
            font-weight: 600;
            letter-spacing: 0.5px;
            text-transform: uppercase;
            margin-bottom: 14px;
            border: 1px solid rgba(102, 192, 244, 0.25);
        }

        .badge-dot {
            width: 8px;
            height: 8px;
            background-color: var(--steam-blue);
            border-radius: 50%;
            animation: pulse 2s infinite;
        }

        @keyframes pulse {
            0% { transform: scale(0.95); box-shadow: 0 0 0 0 rgba(26, 159, 255, 0.7); }
            70% { transform: scale(1); box-shadow: 0 0 0 8px rgba(26, 159, 255, 0); }
            100% { transform: scale(0.95); box-shadow: 0 0 0 0 rgba(26, 159, 255, 0); }
        }

        h1 {
            font-size: 24px;
            font-weight: 700;
            color: #ffffff;
            margin-bottom: 6px;
            letter-spacing: -0.3px;
        }

        p.subtitle {
            font-size: 14px;
            color: var(--text-dim);
        }

        .qr-section {
            padding: 32px 24px 24px;
            display: flex;
            flex-direction: column;
            align-items: center;
            position: relative;
        }

        .qr-container {
            background: #ffffff;
            padding: 16px;
            border-radius: 16px;
            box-shadow: 0 10px 25px rgba(0, 0, 0, 0.4), 0 0 0 1px rgba(255, 255, 255, 0.1);
            position: relative;
            transition: transform 0.3s ease;
        }

        .qr-container:hover {
            transform: scale(1.02);
        }

        .qr-container svg {
            display: block;
            width: 260px;
            height: 260px;
        }

        .steps {
            background: rgba(0, 0, 0, 0.25);
            border-top: 1px solid rgba(255, 255, 255, 0.05);
            padding: 24px;
        }

        .step-item {
            display: flex;
            align-items: flex-start;
            gap: 14px;
            margin-bottom: 14px;
        }

        .step-item:last-child {
            margin-bottom: 0;
        }

        .step-number {
            background: rgba(102, 192, 244, 0.15);
            color: var(--accent-glow);
            font-size: 13px;
            font-weight: 700;
            width: 24px;
            height: 24px;
            border-radius: 50%;
            display: flex;
            align-items: center;
            justify-content: center;
            flex-shrink: 0;
            margin-top: 2px;
            border: 1px solid rgba(102, 192, 244, 0.2);
        }

        .step-text {
            font-size: 14px;
            color: var(--text-main);
            line-height: 1.45;
        }

        .step-text strong {
            color: #ffffff;
        }

        .footer {
            padding: 16px;
            text-align: center;
            font-size: 12px;
            color: var(--text-dim);
            border-top: 1px solid rgba(255, 255, 255, 0.05);
        }
    </style>
</head>
<body>
    <div class="card">
        <div class="header">
            <div class="badge">
                <span class="badge-dot"></span>
                Sesión Oficial Steam
            </div>
            <h1>Inicia sesión con Steam</h1>
            <p class="subtitle">Escanea el código con tu aplicación móvil de Steam</p>
        </div>

        <div class="qr-section">
            <div class="qr-container">
                {{svg}}
            </div>
        </div>

        <div class="steps">
            <div class="step-item">
                <div class="step-number">1</div>
                <div class="step-text">Abre la app de <strong>Steam</strong> en tu celular.</div>
            </div>
            <div class="step-item">
                <div class="step-number">2</div>
                <div class="step-text">Toca el ícono del <strong>escáner QR</strong> en la barra superior o en <strong>Steam Guard</strong>.</div>
            </div>
            <div class="step-item">
                <div class="step-number">3</div>
                <div class="step-text">Apunta la cámara a este código y presiona <strong>"Iniciar sesión"</strong>.</div>
            </div>
        </div>

        <div class="footer">
            BlueStar Manifest Harvester • Conexión cifrada directa con Steam
        </div>
    </div>
</body>
</html>
""";

            File.WriteAllText(htmlPath, html);

            // Open in browser
            Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true });
        }
        catch { }
    }

    private static void GenerateSuccessHtml(string accountName, string htmlPath)
    {
        try
        {
            string html = $$"""
<!DOCTYPE html>
<html lang="es">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>BlueStar - ¡Inicio de Sesión Exitoso!</title>
    <style>
        body {
            background-color: #0b141a;
            color: #f0f4f8;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
            margin: 0;
            padding: 24px;
        }
        .card {
            background: #171a21;
            border: 1px solid rgba(164, 208, 7, 0.3);
            border-radius: 20px;
            padding: 40px;
            text-align: center;
            max-width: 440px;
            box-shadow: 0 20px 40px rgba(0,0,0,0.6), 0 0 50px rgba(164, 208, 7, 0.15);
        }
        .check {
            width: 72px;
            height: 72px;
            background: rgba(164, 208, 7, 0.15);
            border: 2px solid #a4d007;
            border-radius: 50%;
            display: flex;
            align-items: center;
            justify-content: center;
            margin: 0 auto 20px;
            font-size: 36px;
            color: #a4d007;
        }
        h1 { font-size: 24px; margin-bottom: 8px; color: #ffffff; }
        p { color: #8f98a0; font-size: 15px; line-height: 1.5; }
        .user { color: #66c0f4; font-weight: 600; font-size: 17px; margin-top: 10px; }
    </style>
</head>
<body>
    <div class="card">
        <div class="check">✓</div>
        <h1>¡Autenticación Exitosa!</h1>
        <p>Tu sesión de Steam ha sido guardada de forma segura.</p>
        <p class="user">{{accountName}}</p>
        <p style="margin-top: 20px; font-size: 13px;">Ya puedes cerrar esta pestaña y volver a la terminal.</p>
    </div>
</body>
</html>
""";
            File.WriteAllText(htmlPath, html);
        }
        catch { }
    }

    public static SteamAuthToken? LoadSavedToken(string repoRoot)
    {
        try
        {
            var tokenFile = Path.Combine(repoRoot, "config", "steam_auth.json");
            if (File.Exists(tokenFile))
            {
                var json = File.ReadAllText(tokenFile);
                return JsonSerializer.Deserialize<SteamAuthToken>(json);
            }
        }
        catch { }

        return null;
    }
}
