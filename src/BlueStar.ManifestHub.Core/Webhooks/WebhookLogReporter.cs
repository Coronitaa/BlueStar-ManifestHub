using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueStar.ManifestHub.Core.Webhooks;

/// <summary>
/// Utility for sanitizing sensitive credentials, tokens, personal data, and SteamIDs.
/// </summary>
public static class DataSanitizer
{
    // SteamIDs
    private static readonly Regex SteamId64Regex = new(@"\b7656119\d{10}\b", RegexOptions.Compiled);
    private static readonly Regex SteamId3Regex = new(@"\[U:1:\d+\]|U:1:\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SteamId2Regex = new(@"STEAM_[0-5]:[0-1]:\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Authentication and Secret Tokens
    private static readonly Regex BearerTokenRegex = new(@"(?i)bearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.Compiled);
    private static readonly Regex JwtRegex = new(@"\bey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled);
    private static readonly Regex KeyValueSecretRegex = new(
        @"(?i)(token|access_token|refresh_token|api_?key|auth|secret|password|passwd|pwd|private_?key|client_?secret|credential|session_?id)\s*([=:]|\s+)\s*['""]?([^\s,""';&]+)['""]?",
        RegexOptions.Compiled);

    // Personal Data / PII
    private static readonly Regex EmailRegex = new(@"\b[a-zA-Z0-9_.+-]+@[a-zA-Z0-9-]+\.[a-zA-Z0-9-.]+\b", RegexOptions.Compiled);
    private static readonly Regex Ipv4Regex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex Ipv6Regex = new(@"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b", RegexOptions.Compiled);

    private static readonly HashSet<string> SensitiveKeyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "apikey", "api_key", "secret", "password", "passwd", "pwd",
        "auth", "authorization", "bearer", "cookie", "session", "key", "credential",
        "privatekey", "private_key", "client_secret"
    };

    /// <summary>
    /// Sanitizes an arbitrary text string, removing any tokens, passwords, SteamIDs, or personal information.
    /// </summary>
    public static string SanitizeText(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        string result = input;

        // 1. Sanitize SteamIDs
        result = SteamId64Regex.Replace(result, "[REDACTED_STEAMID]");
        result = SteamId3Regex.Replace(result, "[REDACTED_STEAMID]");
        result = SteamId2Regex.Replace(result, "[REDACTED_STEAMID]");

        // 2. Sanitize Bearer tokens and JWTs
        result = BearerTokenRegex.Replace(result, "Bearer [REDACTED]");
        result = JwtRegex.Replace(result, "[REDACTED_JWT]");

        // 3. Sanitize key=value or key:value secrets
        result = KeyValueSecretRegex.Replace(result, m =>
        {
            string key = m.Groups[1].Value;
            string separator = m.Groups[2].Value;
            return $"{key}{separator}[REDACTED]";
        });

        // 4. Sanitize Personal Data (Emails and IP addresses)
        result = EmailRegex.Replace(result, "[REDACTED_EMAIL]");
        result = Ipv4Regex.Replace(result, "[REDACTED_IP]");
        result = Ipv6Regex.Replace(result, "[REDACTED_IP]");

        return result;
    }

    /// <summary>
    /// Sanitizes an API endpoint or URL by redacting sensitive query parameters.
    /// </summary>
    public static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        int queryIndex = url.IndexOf('?');
        if (queryIndex < 0)
        {
            return SanitizeText(url);
        }

        string basePath = SanitizeText(url[..queryIndex]);
        string queryString = url[(queryIndex + 1)..];
        string[] pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var sanitizedPairs = new List<string>();

        foreach (var pair in pairs)
        {
            int eqIdx = pair.IndexOf('=');
            if (eqIdx > 0)
            {
                string key = pair[..eqIdx];
                string val = pair[(eqIdx + 1)..];

                if (IsSensitiveKey(key))
                {
                    sanitizedPairs.Add($"{key}=[REDACTED]");
                }
                else
                {
                    sanitizedPairs.Add($"{key}={SanitizeText(val)}");
                }
            }
            else
            {
                sanitizedPairs.Add(SanitizeText(pair));
            }
        }

        return $"{basePath}?{string.Join('&', sanitizedPairs)}";
    }

    /// <summary>
    /// Sanitizes a dictionary of metadata, redacting values for sensitive keys and scanning values for secrets.
    /// </summary>
    public static Dictionary<string, string> SanitizeDictionary(IReadOnlyDictionary<string, string>? dict)
    {
        var sanitized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dict == null)
        {
            return sanitized;
        }

        foreach (var (k, v) in dict)
        {
            if (IsSensitiveKey(k))
            {
                sanitized[k] = "[REDACTED]";
            }
            else
            {
                sanitized[k] = SanitizeText(v);
            }
        }

        return sanitized;
    }

    private static bool IsSensitiveKey(string key)
    {
        foreach (var kw in SensitiveKeyWords)
        {
            if (key.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Creates a strictly sanitized copy of an ApiUsageLogPayload.
    /// </summary>
    public static ApiUsageLogPayload SanitizeLogPayload(ApiUsageLogPayload raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return new ApiUsageLogPayload
        {
            LogId = raw.LogId,
            Timestamp = raw.Timestamp,
            Endpoint = SanitizeUrl(raw.Endpoint),
            HttpMethod = raw.HttpMethod,
            StatusCode = raw.StatusCode,
            DurationMs = raw.DurationMs,
            ClientIp = string.IsNullOrWhiteSpace(raw.ClientIp) ? null : SanitizeText(raw.ClientIp),
            UserAgent = string.IsNullOrWhiteSpace(raw.UserAgent) ? null : SanitizeText(raw.UserAgent),
            Details = SanitizeDictionary(raw.Details)
        };
    }
}

/// <summary>
/// Configuration for the WebhookLogReporter.
/// </summary>
public sealed class WebhookLogReporterOptions
{
    public const string DefaultBaseUrl = "https://api.depotbox.org";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string LogsEndpointPath { get; set; } = "/api/webhooks/depotbox/logs";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Reports usage logs to /api/webhooks/depotbox/logs with strict sanitization,
/// eliminating any tokens, credentials, API keys, passwords, SteamIDs, or personal information.
/// </summary>
public sealed class WebhookLogReporter : IDisposable
{
    private readonly WebhookLogReporterOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private bool _disposed;

    public WebhookLogReporter(WebhookLogReporterOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? new WebhookLogReporterOptions();
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = _options.Timeout };
            _disposeClient = true;
        }
    }

    /// <summary>
    /// Sanitizes and dispatches an API usage log to DepotBox.
    /// </summary>
    public async Task<bool> ReportLogAsync(ApiUsageLogPayload rawLog, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(rawLog);

        // Strict sanitization
        var sanitized = DataSanitizer.SanitizeLogPayload(rawLog);

        string url = $"{_options.BaseUrl.TrimEnd('/')}{_options.LogsEndpointPath}";
        string json = JsonSerializer.Serialize(sanitized);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // Log reporting failure is non-fatal
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_disposeClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
