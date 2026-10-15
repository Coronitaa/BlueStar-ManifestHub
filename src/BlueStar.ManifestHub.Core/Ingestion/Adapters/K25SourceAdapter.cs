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
    public string ManifestUrlTemplate { get; set; } = "{baseUrl}/manifests/{depotId}/{manifestId}.manifest";
    public string AppMetadataUrlTemplate { get; set; } = "{baseUrl}/apps/{appId}.json";
    public string DepotMetadataUrlTemplate { get; set; } = "{baseUrl}/depots/{depotId}.json";
}

/// <summary>
/// Adapter for qwe213312/k25FCdfEOoEJ42S6 manifest archive mirror.
/// Discovers and downloads manifests hosted on the K25 mirror repository.
/// </summary>
public sealed class K25SourceAdapter : IManifestSourceAdapter, IDisposable
{
    private readonly K25Options _options;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private int _consecutiveFailures;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private readonly object _stateLock = new();
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
            return ParseK25Json(appId, 0, json);
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

        if (!IsAvailable)
            return Array.Empty<DiscoveredManifest>();

        string url = _options.DepotMetadataUrlTemplate
            .Replace("{baseUrl}", _options.BaseUrl.TrimEnd('/'))
            .Replace("{depotId}", depotId.ToString());

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
            return ParseK25Json(0, depotId, json);
        }
        catch (HttpRequestException)
        {
            RecordFailure();
            return Array.Empty<DiscoveredManifest>();
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadManifestAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

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

    private List<DiscoveredManifest> ParseK25Json(uint defaultAppId, uint defaultDepotId, string json)
    {
        var list = new List<DiscoveredManifest>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Handle object format with "manifests" array or depot mapping
            if (root.TryGetProperty("manifests", out var manifestsElement) && manifestsElement.ValueKind == JsonValueKind.Array)
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
