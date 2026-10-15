using System.Net;
using System.Text.Json;

namespace BlueStar.ManifestHub.Core.Ingestion.Adapters;

/// <summary>
/// Configuration options for ManifestHub2 source adapter.
/// </summary>
public sealed class ManifestHub2Options
{
    public const string DefaultBaseUrl = "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public int Priority { get; set; } = 20;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public string ManifestUrlTemplate { get; set; } = "{baseUrl}/manifests/{depotId}/{manifestId}.manifest";
    public string AppMetadataUrlTemplate { get; set; } = "{baseUrl}/{appId}.json";
}

/// <summary>
/// Adapter for SSMGAlt/ManifestHub2 manifest source.
/// Discovers manifests from {appId}.json files and retrieves manifest binaries.
/// </summary>
public sealed class ManifestHub2SourceAdapter : IManifestSourceAdapter, IDisposable
{
    private readonly ManifestHub2Options _options;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private int _consecutiveFailures;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();
    private bool _disposed;

    public ManifestHub2SourceAdapter(ManifestHub2Options? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? new ManifestHub2Options();
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

        string url = _options.AppMetadataUrlTemplate
            .Replace("{baseUrl}", _options.BaseUrl.TrimEnd('/'))
            .Replace("{appId}", appId.ToString());

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RecordSuccess();
                return Array.Empty<DiscoveredManifest>();
            }

            response.EnsureSuccessStatusCode();
            RecordSuccess();

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseAppManifests(appId, json);
        }
        catch (HttpRequestException)
        {
            RecordFailure();
            return Array.Empty<DiscoveredManifest>();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsForDepotAsync(uint depotId, CancellationToken cancellationToken = default)
    {
        // ManifestHub2 structures catalog by appId.json; depot-only discovery returns empty list
        return Task.FromResult<IReadOnlyList<DiscoveredManifest>>(Array.Empty<DiscoveredManifest>());
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

    private List<DiscoveredManifest> ParseAppManifests(uint appId, string json)
    {
        var list = new List<DiscoveredManifest>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("depots", out var depotsElement))
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
                        list.Add(new DiscoveredManifest
                        {
                            AppId = appId,
                            DepotId = depotId,
                            ManifestId = mId,
                            SourceId = SourceId
                        });
                    }
                }
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("manifests", out var manifestsProp) && manifestsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in manifestsProp.EnumerateArray())
                    {
                        if (TryParseManifestId(item, out ulong mId))
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

                if (element.TryGetProperty("latestManifestId", out var latestProp) ||
                    element.TryGetProperty("latest", out latestProp) ||
                    element.TryGetProperty("manifestId", out latestProp))
                {
                    if (TryParseManifestId(latestProp, out ulong mId))
                    {
                        if (!list.Any(x => x.DepotId == depotId && x.ManifestId == mId))
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
                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
                if (TryParseManifestId(element, out ulong singleId))
                {
                    list.Add(new DiscoveredManifest
                    {
                        AppId = appId,
                        DepotId = depotId,
                        ManifestId = singleId,
                        SourceId = SourceId
                    });
                }
                break;
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
