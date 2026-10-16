using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace BlueStar.ManifestHub.Core.Ingestion.Adapters;

/// <summary>
/// Configuration options for ManifestHub2 source adapter.
/// </summary>
public sealed class ManifestHub2Options
{
    public const string DefaultBaseUrl = "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public int Priority { get; set; } = 20;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public string ManifestUrlTemplate { get; set; } = "{baseUrl}/{appId}/{depotId}_{manifestId}.manifest";
    public string AppMetadataUrlTemplate { get; set; } = "{baseUrl}/{appId}/{appId}.json";
}

/// <summary>
/// Adapter for SSMGAlt/ManifestHub2 manifest source.
/// Discovers manifests from branch-per-AppId repository structure ({appId}/{appId}.json)
/// and retrieves manifest binaries ({appId}/{depotId}_{manifestId}.manifest).
/// </summary>
public sealed class ManifestHub2SourceAdapter : IManifestSourceAdapter, IDisposable
{
    private readonly ManifestHub2Options _options;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private int _consecutiveFailures;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<(uint DepotId, ulong ManifestId), uint> _manifestToAppMap = new();
    private readonly ConcurrentDictionary<uint, uint> _depotToAppMap = new();
    private readonly ConcurrentDictionary<uint, List<DiscoveredManifest>> _discoveredByDepot = new();
    private readonly IGlobalRateGovernor? _rateGovernor;
    private bool _disposed;

    public ManifestHub2SourceAdapter(ManifestHub2Options? options = null, HttpClient? httpClient = null, IGlobalRateGovernor? rateGovernor = null)
    {
        _options = options ?? new ManifestHub2Options();
        _rateGovernor = rateGovernor;
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

        // Pre-seed known depot-to-app mappings for standard tracked applications
        RegisterAppDepotMapping(730, 731);
        RegisterAppDepotMapping(730, 732);
        RegisterAppDepotMapping(440, 441);
        RegisterAppDepotMapping(105600, 105601);
        RegisterAppDepotMapping(570, 570);
        foreach (uint dId in new uint[] { 11, 12, 13, 14, 15, 17, 18, 19, 95, 142 })
        {
            RegisterAppDepotMapping(10, dId);
        }
    }

    public ManifestHub2SourceAdapter(IGlobalRateGovernor rateGovernor, ManifestHub2Options? options = null, HttpClient? httpClient = null)
        : this(options, httpClient, rateGovernor)
    {
    }

    public string SourceId => "ManifestHub2";
    public string SourceType => "GitHubRaw";
    public int Priority => _options.Priority;

    public bool IsAvailable
    {
        get
        {
            lock (_stateLock)
            {
                return DateTimeOffset.UtcNow >= _cooldownUntil;
            }
        }
    }

    /// <summary>
    /// Explicitly registers a depot-to-appId mapping for URL routing.
    /// </summary>
    public void RegisterAppDepotMapping(uint appId, uint depotId)
    {
        if (appId > 0 && depotId > 0)
        {
            _depotToAppMap[depotId] = appId;
        }
    }

    private static string CleanBaseUrl(string baseUrl)
    {
        string cleaned = baseUrl.TrimEnd('/');
        if (cleaned.EndsWith("/main", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^5];
        }
        return cleaned;
    }

    private void RecordSuccess(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _cooldownUntil = DateTimeOffset.MinValue;
        }
        _rateGovernor?.ReportSuccessAsync(cancellationToken);
    }

    private void RecordFailure()
    {
        lock (_stateLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3)
            {
                _cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            }
        }
    }

    private void RecordRateLimitExceeded(TimeSpan? retryAfter, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _consecutiveFailures++;
            var cooldown = retryAfter ?? TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(_consecutiveFailures, 4))));
            _cooldownUntil = DateTimeOffset.UtcNow + cooldown;
        }
        _rateGovernor?.ReportRateLimitExceededAsync(retryAfter, cancellationToken);
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                return response.Headers.RetryAfter.Delta.Value;
            }

            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var delay = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
            }
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
        {
            var val = resetValues.FirstOrDefault();
            if (long.TryParse(val, out long epochSeconds))
            {
                var resetTime = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
                var diff = resetTime - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    return diff;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsAsync(uint appId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (appId == 0)
            return Array.Empty<DiscoveredManifest>();

        if (!IsAvailable)
            return Array.Empty<DiscoveredManifest>();

        if (_rateGovernor != null)
        {
            await _rateGovernor.WaitAsync(cancellationToken);
        }

        string baseUrl = CleanBaseUrl(_options.BaseUrl);
        string url = _options.AppMetadataUrlTemplate
            .Replace("{baseUrl}", baseUrl)
            .Replace("{appId}", appId.ToString());

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_options.Timeout);

        try
        {
            using var response = await _httpClient.GetAsync(url, linkedCts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RecordSuccess(cancellationToken);
                return Array.Empty<DiscoveredManifest>();
            }

            if (response.StatusCode == (HttpStatusCode)429 ||
                (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0"))
            {
                var retryAfter = ParseRetryAfter(response);
                RecordRateLimitExceeded(retryAfter, cancellationToken);
                return Array.Empty<DiscoveredManifest>();
            }

            response.EnsureSuccessStatusCode();
            RecordSuccess(cancellationToken);

            string json = await response.Content.ReadAsStringAsync(linkedCts.Token);
            var manifests = ParseAppManifests(appId, json);

            foreach (var m in manifests)
            {
                _manifestToAppMap[(m.DepotId, m.ManifestId)] = appId;
                _depotToAppMap[m.DepotId] = appId;
                var list = _discoveredByDepot.GetOrAdd(m.DepotId, _ => new List<DiscoveredManifest>());
                lock (list)
                {
                    if (!list.Any(x => x.ManifestId == m.ManifestId))
                    {
                        list.Add(m);
                    }
                }
            }

            return manifests;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordFailure();
            return Array.Empty<DiscoveredManifest>();
        }
        catch (HttpRequestException)
        {
            RecordFailure();
            return Array.Empty<DiscoveredManifest>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsForDepotAsync(uint depotId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (depotId == 0)
            return Array.Empty<DiscoveredManifest>();

        if (!IsAvailable)
            return Array.Empty<DiscoveredManifest>();

        if (_discoveredByDepot.TryGetValue(depotId, out var cached) && cached.Count > 0)
        {
            lock (cached)
            {
                return cached.ToList();
            }
        }

        if (_depotToAppMap.TryGetValue(depotId, out uint mappedAppId) && mappedAppId > 0)
        {
            var manifests = await DiscoverManifestsAsync(mappedAppId, cancellationToken);
            return manifests.Where(m => m.DepotId == depotId).ToList();
        }

        // Common Steam convention: base depot ID often equals app ID
        var direct = await DiscoverManifestsAsync(depotId, cancellationToken);
        var matching = direct.Where(m => m.DepotId == depotId).ToList();
        return matching.Count > 0 ? matching : direct;
    }

    /// <inheritdoc />
    public Task<byte[]?> DownloadManifestAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default)
    {
        uint appId = 0;
        if (_manifestToAppMap.TryGetValue((depotId, manifestId), out uint mAppId) && mAppId > 0)
        {
            appId = mAppId;
        }
        else if (_depotToAppMap.TryGetValue(depotId, out uint dAppId) && dAppId > 0)
        {
            appId = dAppId;
        }
        else
        {
            appId = depotId;
        }

        return DownloadManifestAsync(appId, depotId, manifestId, cancellationToken);
    }

    /// <summary>
    /// Downloads manifest binary using explicit branch routing for {appId}.
    /// </summary>
    public async Task<byte[]?> DownloadManifestAsync(uint appId, uint depotId, ulong manifestId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (depotId == 0 || manifestId == 0)
            return null;

        if (!IsAvailable)
            return null;

        uint effectiveAppId = appId > 0 ? appId : depotId;
        string baseUrl = CleanBaseUrl(_options.BaseUrl);
        string url = _options.ManifestUrlTemplate
            .Replace("{baseUrl}", baseUrl)
            .Replace("{appId}", effectiveAppId.ToString())
            .Replace("{depotId}", depotId.ToString())
            .Replace("{manifestId}", manifestId.ToString());

        if (_rateGovernor != null)
        {
            await _rateGovernor.WaitAsync(cancellationToken);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_options.Timeout);

        try
        {
            using var response = await _httpClient.GetAsync(url, linkedCts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RecordSuccess(cancellationToken);
                return null;
            }

            if (response.StatusCode == (HttpStatusCode)429 ||
                (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0"))
            {
                var retryAfter = ParseRetryAfter(response);
                RecordRateLimitExceeded(retryAfter, cancellationToken);
                return null;
            }

            response.EnsureSuccessStatusCode();
            RecordSuccess(cancellationToken);

            return await response.Content.ReadAsByteArrayAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordFailure();
            return null;
        }
        catch (HttpRequestException)
        {
            RecordFailure();
            return null;
        }
    }

    private List<DiscoveredManifest> ParseAppManifests(uint appId, string json)
    {
        var list = new List<DiscoveredManifest>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("depot", out var depotsElement) ||
                root.TryGetProperty("depots", out depotsElement))
            {
                if (depotsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in depotsElement.EnumerateObject())
                    {
                        if (uint.TryParse(prop.Name, out uint depotId))
                        {
                            ExtractManifestsFromElement(list, appId, depotId, prop.Value);
                        }
                    }
                }
                else if (depotsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in depotsElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("depotId", out var dIdElem) && dIdElem.TryGetUInt32(out uint depotId))
                        {
                            ExtractManifestsFromElement(list, appId, depotId, item);
                        }
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Root is a direct depot map e.g. { "731": { ... } } or { "731": [ ... ] }
                foreach (var prop in root.EnumerateObject())
                {
                    if (uint.TryParse(prop.Name, out uint depotId))
                    {
                        ExtractManifestsFromElement(list, appId, depotId, prop.Value);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                // Root is an array of depots
                foreach (var item in root.EnumerateArray())
                {
                    if (item.TryGetProperty("depotId", out var dIdElem) && dIdElem.TryGetUInt32(out uint depotId))
                    {
                        ExtractManifestsFromElement(list, appId, depotId, item);
                    }
                }
            }
        }
        catch
        {
            // Return any successfully extracted manifests
        }

        return list;
    }

    private void ExtractManifestsFromElement(List<DiscoveredManifest> list, uint appId, uint depotId, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryParseManifestId(item, out ulong mId))
                    {
                        AddManifest(list, appId, depotId, mId, null);
                    }
                }
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("manifests", out var manifestsProp))
                {
                    if (manifestsProp.ValueKind == JsonValueKind.Object)
                    {
                        // Authentic GitHub format: "manifests": { "public": { "gid": "5289214263727115270", "size": "7" }, ... }
                        foreach (var branchProp in manifestsProp.EnumerateObject())
                        {
                            string branch = branchProp.Name;
                            var val = branchProp.Value;

                            if (val.ValueKind == JsonValueKind.Object)
                            {
                                if (val.TryGetProperty("gid", out var gidElem) && TryParseManifestId(gidElem, out ulong gid))
                                {
                                    AddManifest(list, appId, depotId, gid, branch);
                                }
                                else if (val.TryGetProperty("manifestId", out var mElem) && TryParseManifestId(mElem, out ulong mId))
                                {
                                    AddManifest(list, appId, depotId, mId, branch);
                                }
                            }
                            else if (TryParseManifestId(val, out ulong singleId))
                            {
                                AddManifest(list, appId, depotId, singleId, branch);
                            }
                        }
                    }
                    else if (manifestsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in manifestsProp.EnumerateArray())
                        {
                            if (TryParseManifestId(item, out ulong mId))
                            {
                                AddManifest(list, appId, depotId, mId, null);
                            }
                        }
                    }
                }

                if (element.TryGetProperty("latestManifestId", out var latestProp) ||
                    element.TryGetProperty("latest", out latestProp) ||
                    element.TryGetProperty("manifestId", out latestProp))
                {
                    if (TryParseManifestId(latestProp, out ulong mId))
                    {
                        AddManifest(list, appId, depotId, mId, null);
                    }
                }
                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
                if (TryParseManifestId(element, out ulong directId))
                {
                    AddManifest(list, appId, depotId, directId, null);
                }
                break;
        }
    }

    private void AddManifest(List<DiscoveredManifest> list, uint appId, uint depotId, ulong manifestId, string? branch)
    {
        if (manifestId == 0) return;
        if (!list.Any(x => x.DepotId == depotId && x.ManifestId == manifestId))
        {
            list.Add(new DiscoveredManifest
            {
                AppId = appId,
                DepotId = depotId,
                ManifestId = manifestId,
                Branch = branch,
                SourceId = SourceId
            });
        }
    }

    private static bool TryParseManifestId(JsonElement element, out ulong manifestId)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetUInt64(out manifestId);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return ulong.TryParse(element.GetString(), out manifestId);
        }

        manifestId = 0;
        return false;
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
