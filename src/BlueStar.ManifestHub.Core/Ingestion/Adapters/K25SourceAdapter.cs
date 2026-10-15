using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace BlueStar.ManifestHub.Core.Ingestion.Adapters;

/// <summary>
/// Configuration options for K25 source adapter.
/// </summary>
public sealed class K25Options
{
    public const string DefaultBaseUrl = "https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public int Priority { get; set; } = 30;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public string ManifestUrlTemplate { get; set; } = "{baseUrl}/{depotId}_{manifestId}.manifest";
    public string? AppMetadataUrlTemplate { get; set; } = "{baseUrl}/apps/{appId}.json";
    public string? DepotMetadataUrlTemplate { get; set; } = "{baseUrl}/depots/{depotId}.json";
    public string? TreeApiUrlTemplate { get; set; } = "https://api.github.com/repos/qwe213312/k25FCdfEOoEJ42S6/git/trees/main";
}

/// <summary>
/// Adapter for qwe213312/k25FCdfEOoEJ42S6 manifest archive mirror.
/// Discovers and downloads manifests stored at repository root: {depotId}_{manifestId}.manifest.
/// </summary>
public sealed class K25SourceAdapter : IManifestSourceAdapter, IDisposable
{
    private readonly K25Options _options;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private int _consecutiveFailures;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<uint, List<DiscoveredManifest>> _knownDepotManifests = new();
    private readonly ConcurrentDictionary<(uint DepotId, ulong ManifestId), DiscoveredManifest> _allKnownManifests = new();
    private bool _disposed;

    public K25SourceAdapter(K25Options? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? new K25Options();
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

    public string SourceId => "K25";
    public string SourceType => "K25Mirror";
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
    /// Registers a known depot manifest for targeted query matching.
    /// </summary>
    public void RegisterManifest(uint depotId, ulong manifestId, string? sha256 = null, uint appId = 0, DateTimeOffset? creationTime = null)
    {
        var discovered = new DiscoveredManifest
        {
            AppId = appId,
            DepotId = depotId,
            ManifestId = manifestId,
            ExpectedSha256 = sha256,
            CreationTime = creationTime,
            SourceId = SourceId
        };
        _allKnownManifests[(depotId, manifestId)] = discovered;
        var list = _knownDepotManifests.GetOrAdd(depotId, _ => new List<DiscoveredManifest>());
        lock (list)
        {
            if (!list.Any(x => x.ManifestId == manifestId))
            {
                list.Add(discovered);
            }
        }
    }

    private void RecordSuccess()
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _cooldownUntil = DateTimeOffset.MinValue;
        }
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsAsync(uint appId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (appId == 0)
            return Array.Empty<DiscoveredManifest>();

        if (!IsAvailable)
            return Array.Empty<DiscoveredManifest>();

        if (string.IsNullOrWhiteSpace(_options.AppMetadataUrlTemplate))
        {
            return _allKnownManifests.Values.Where(m => m.AppId == appId).ToList();
        }

        string url = _options.AppMetadataUrlTemplate
            .Replace("{baseUrl}", _options.BaseUrl.TrimEnd('/'))
            .Replace("{appId}", appId.ToString());

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RecordSuccess();
                var matching = _allKnownManifests.Values.Where(m => m.AppId == appId).ToList();
                return matching;
            }

            response.EnsureSuccessStatusCode();
            RecordSuccess();

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseK25Json(appId, 0, json);
        }
        catch (HttpRequestException)
        {
            RecordFailure();
            var matching = _allKnownManifests.Values.Where(m => m.AppId == appId).ToList();
            return matching;
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

        // 1. Check known / registered
        if (_knownDepotManifests.TryGetValue(depotId, out var cached) && cached.Count > 0)
        {
            lock (cached)
            {
                return cached.ToList();
            }
        }

        // 2. Query Depot metadata url if configured
        if (!string.IsNullOrWhiteSpace(_options.DepotMetadataUrlTemplate))
        {
            string url = _options.DepotMetadataUrlTemplate
                .Replace("{baseUrl}", _options.BaseUrl.TrimEnd('/'))
                .Replace("{depotId}", depotId.ToString());

            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    RecordSuccess();
                    string json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var parsed = ParseK25Json(0, depotId, json);
                    if (parsed.Count > 0)
                    {
                        foreach (var m in parsed)
                        {
                            _allKnownManifests[(m.DepotId, m.ManifestId)] = m;
                            var list = _knownDepotManifests.GetOrAdd(m.DepotId, _ => new List<DiscoveredManifest>());
                            lock (list)
                            {
                                if (!list.Any(x => x.ManifestId == m.ManifestId))
                                {
                                    list.Add(m);
                                }
                            }
                        }
                        return parsed;
                    }
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    RecordSuccess();
                }
            }
            catch (HttpRequestException)
            {
                RecordFailure();
            }
        }

        // 3. Query Tree API / index if configured
        if (!string.IsNullOrWhiteSpace(_options.TreeApiUrlTemplate))
        {
            try
            {
                using var response = await _httpClient.GetAsync(_options.TreeApiUrlTemplate, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    RecordSuccess();
                    string json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var parsed = ParseTreeJson(json, depotId);
                    if (parsed.Count > 0)
                    {
                        return parsed;
                    }
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    RecordSuccess();
                }
            }
            catch (HttpRequestException)
            {
                RecordFailure();
            }
        }

        if (_knownDepotManifests.TryGetValue(depotId, out var fallback))
        {
            lock (fallback)
            {
                return fallback.ToList();
            }
        }

        return Array.Empty<DiscoveredManifest>();
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadManifestAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (depotId == 0 || manifestId == 0)
            return null;

        if (!IsAvailable)
            return null;

        string url = _options.ManifestUrlTemplate
            .Replace("{baseUrl}", _options.BaseUrl.TrimEnd('/'))
            .Replace("{depotId}", depotId.ToString())
            .Replace("{manifestId}", manifestId.ToString());

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RecordSuccess();
                return null;
            }

            response.EnsureSuccessStatusCode();
            RecordSuccess();

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            RecordFailure();
            return null;
        }
    }

    /// <summary>
    /// Parses a git tree or directory index JSON containing {depotId}_{manifestId}.manifest entries.
    /// </summary>
    public IReadOnlyList<DiscoveredManifest> ParseTreeJson(string json, uint targetDepotId = 0)
    {
        var list = new List<DiscoveredManifest>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement arrayElem = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tree", out var treeProp))
            {
                arrayElem = treeProp;
            }

            if (arrayElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arrayElem.EnumerateArray())
                {
                    string? path = null;
                    string? sha = null;

                    if (item.ValueKind == JsonValueKind.String)
                    {
                        path = item.GetString();
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("path", out var pProp) || item.TryGetProperty("name", out pProp))
                        {
                            path = pProp.GetString();
                        }
                        if (item.TryGetProperty("sha", out var sProp))
                        {
                            sha = sProp.GetString();
                        }
                    }

                    if (!string.IsNullOrEmpty(path) && TryParseManifestFileName(path, out uint depotId, out ulong manifestId))
                    {
                        if (targetDepotId == 0 || depotId == targetDepotId)
                        {
                            var discovered = new DiscoveredManifest
                            {
                                DepotId = depotId,
                                ManifestId = manifestId,
                                ExpectedSha1 = sha?.Length == 40 ? sha : null,
                                ExpectedSha256 = sha?.Length == 64 ? sha : null,
                                SourceId = SourceId
                            };
                            list.Add(discovered);
                            _allKnownManifests[(depotId, manifestId)] = discovered;
                            var depotList = _knownDepotManifests.GetOrAdd(depotId, _ => new List<DiscoveredManifest>());
                            lock (depotList)
                            {
                                if (!depotList.Any(x => x.ManifestId == manifestId))
                                {
                                    depotList.Add(discovered);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // fallback
        }

        return list;
    }

    private static bool TryParseManifestFileName(string fileName, out uint depotId, out ulong manifestId)
    {
        depotId = 0;
        manifestId = 0;

        string name = Path.GetFileName(fileName);
        if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^9];
        }

        int underscoreIdx = name.IndexOf('_');
        if (underscoreIdx > 0 && underscoreIdx < name.Length - 1)
        {
            string depotStr = name[..underscoreIdx];
            string manifestStr = name[(underscoreIdx + 1)..];
            if (uint.TryParse(depotStr, out depotId) && ulong.TryParse(manifestStr, out manifestId))
            {
                return true;
            }
        }

        return false;
    }

    private List<DiscoveredManifest> ParseK25Json(uint defaultAppId, uint defaultDepotId, string json)
    {
        var list = new List<DiscoveredManifest>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tree", out _))
            {
                return ParseTreeJson(json, defaultDepotId).ToList();
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Check if array of filename strings
                var first = root.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.String && first.GetString()?.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return ParseTreeJson(json, defaultDepotId).ToList();
                }

                foreach (var item in root.EnumerateArray())
                {
                    if (TryParseManifestEntry(item, out ulong mId, out uint? itemDepotId, out string? sha256, out DateTimeOffset? timestamp))
                    {
                        uint effectiveDepotId = itemDepotId.HasValue && itemDepotId.Value > 0 ? itemDepotId.Value : defaultDepotId;
                        list.Add(new DiscoveredManifest
                        {
                            AppId = defaultAppId,
                            DepotId = effectiveDepotId,
                            ManifestId = mId,
                            ExpectedSha256 = sha256,
                            CreationTime = timestamp,
                            SourceId = SourceId
                        });
                    }
                }
            }
            // Handle object format with "manifests" array or depot mapping
            else if (root.TryGetProperty("manifests", out var manifestsElement) && manifestsElement.ValueKind == JsonValueKind.Array)
            {
                uint appId = root.TryGetProperty("appId", out var aProp) && aProp.TryGetUInt32(out uint aId) ? aId : defaultAppId;
                uint depotId = root.TryGetProperty("depotId", out var dProp) && dProp.TryGetUInt32(out uint dId) ? dId : defaultDepotId;

                foreach (var item in manifestsElement.EnumerateArray())
                {
                    if (TryParseManifestEntry(item, out ulong mId, out uint? itemDepotId, out string? sha256, out DateTimeOffset? timestamp))
                    {
                        uint effectiveDepotId = itemDepotId.HasValue && itemDepotId.Value > 0 ? itemDepotId.Value : depotId;
                        list.Add(new DiscoveredManifest
                        {
                            AppId = appId,
                            DepotId = effectiveDepotId,
                            ManifestId = mId,
                            ExpectedSha256 = sha256,
                            CreationTime = timestamp,
                            SourceId = SourceId
                        });
                    }
                }
            }
            else if (root.TryGetProperty("depots", out var depotsElement) && depotsElement.ValueKind == JsonValueKind.Object)
            {
                uint appId = root.TryGetProperty("appId", out var aProp) && aProp.TryGetUInt32(out uint aId) ? aId : defaultAppId;
                foreach (var prop in depotsElement.EnumerateObject())
                {
                    if (uint.TryParse(prop.Name, out uint depotId))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                if (TryParseManifestEntry(item, out ulong mId, out _, out _, out _))
                                {
                                    list.Add(new DiscoveredManifest
                                    {
                                        AppId = appId,
                                        DepotId = depotId,
                                        ManifestId = mId,
                                        SourceId = SourceId
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Graceful fallback
        }

        return list;
    }

    private static bool TryParseManifestEntry(
        JsonElement element,
        out ulong manifestId,
        out uint? depotId,
        out string? sha256,
        out DateTimeOffset? timestamp)
    {
        manifestId = 0;
        depotId = null;
        sha256 = null;
        timestamp = null;

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetUInt64(out manifestId);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return ulong.TryParse(element.GetString(), out manifestId);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("depotId", out var dProp))
            {
                if (dProp.ValueKind == JsonValueKind.Number && dProp.TryGetUInt32(out uint dId)) depotId = dId;
                else if (dProp.ValueKind == JsonValueKind.String && uint.TryParse(dProp.GetString(), out uint dIdStr)) depotId = dIdStr;
            }

            if (element.TryGetProperty("manifestId", out var mProp) || element.TryGetProperty("id", out mProp))
            {
                if (mProp.ValueKind == JsonValueKind.Number && mProp.TryGetUInt64(out manifestId) ||
                    mProp.ValueKind == JsonValueKind.String && ulong.TryParse(mProp.GetString(), out manifestId))
                {
                    if (element.TryGetProperty("sha256", out var shaProp))
                    {
                        sha256 = shaProp.GetString();
                    }

                    if (element.TryGetProperty("creationTime", out var timeProp) && timeProp.TryGetDateTimeOffset(out var dto))
                    {
                        timestamp = dto;
                    }

                    return true;
                }
            }
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
