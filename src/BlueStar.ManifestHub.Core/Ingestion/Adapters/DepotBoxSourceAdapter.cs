using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BlueStar.ManifestHub.Core.Ingestion.Adapters;

/// <summary>
/// Circuit breaker state.
/// </summary>
public enum CircuitBreakerState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>
/// Configuration options for DepotBox API source adapter.
/// </summary>
public sealed class DepotBoxOptions
{
    public const string DefaultBaseUrl = "https://api.depotbox.org";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string? ApiKey { get; set; }
    public int Priority { get; set; } = 10;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; } = 3;
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public TimeSpan CircuitBreakerOpenDuration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Upstream source adapter for the DepotBox API service.
/// Coordinates throughput via IGlobalRateGovernor (target 45 req/min)
/// and features an integrated circuit breaker and transient retry policy.
/// </summary>
public sealed class DepotBoxSourceAdapter : IManifestSourceAdapter, IDisposable
{
    private readonly DepotBoxOptions _options;
    private readonly IGlobalRateGovernor _rateGovernor;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly object _cbLock = new();

    private CircuitBreakerState _circuitState = CircuitBreakerState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _lastStateChange = DateTimeOffset.UtcNow;
    private bool _disposed;

    public DepotBoxSourceAdapter(
        IGlobalRateGovernor rateGovernor,
        DepotBoxOptions? options = null,
        HttpClient? httpClient = null)
    {
        _rateGovernor = rateGovernor ?? throw new ArgumentNullException(nameof(rateGovernor));
        _options = options ?? new DepotBoxOptions();

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

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public string SourceId => "DepotBox";
    public string SourceType => "DepotBoxApi";
    public int Priority => _options.Priority;

    public CircuitBreakerState CircuitState
    {
        get
        {
            lock (_cbLock)
            {
                UpdateCircuitStateUnderLock();
                return _circuitState;
            }
        }
    }

    public bool IsAvailable
    {
        get
        {
            lock (_cbLock)
            {
                UpdateCircuitStateUnderLock();
                return _circuitState != CircuitBreakerState.Open;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsAsync(uint appId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string url = $"{_options.BaseUrl.TrimEnd('/')}/api/apps/{appId}/manifests";
        using var response = await ExecuteWithPolicyAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

        if (response == null || !response.IsSuccessStatusCode)
        {
            return Array.Empty<DiscoveredManifest>();
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseDepotBoxManifests(appId, 0, json);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredManifest>> DiscoverManifestsForDepotAsync(uint depotId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string url = $"{_options.BaseUrl.TrimEnd('/')}/api/depots/{depotId}/manifests";
        using var response = await ExecuteWithPolicyAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

        if (response == null || !response.IsSuccessStatusCode)
        {
            return Array.Empty<DiscoveredManifest>();
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseDepotBoxManifests(0, depotId, json);
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadManifestAsync(uint depotId, ulong manifestId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string url = $"{_options.BaseUrl.TrimEnd('/')}/api/depots/{depotId}/manifests/{manifestId}";
        using var response = await ExecuteWithPolicyAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

        if (response == null || !response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    #region Execution Policy (Rate Limiting + Retry + Circuit Breaker)

    private async Task<HttpResponseMessage?> ExecuteWithPolicyAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        // 1. Check circuit breaker
        lock (_cbLock)
        {
            UpdateCircuitStateUnderLock();
            if (_circuitState == CircuitBreakerState.Open)
            {
                return null;
            }
        }

        int attempts = 0;
        int maxAttempts = Math.Max(1, _options.MaxRetries);

        while (attempts < maxAttempts)
        {
            attempts++;
            cancellationToken.ThrowIfCancellationRequested();

            // 2. Throttle via global rate governor
            await _rateGovernor.WaitAsync(cancellationToken);

            try
            {
                using var request = requestFactory();
                var response = await _httpClient.SendAsync(request, cancellationToken);

                // Check for 429 Too Many Requests
                if (response.StatusCode == (HttpStatusCode)429)
                {
                    TimeSpan? retryAfter = ParseRetryAfter(response);
                    await _rateGovernor.ReportRateLimitExceededAsync(retryAfter, cancellationToken);
                    RecordFailure();

                    response.Dispose();

                    if (attempts < maxAttempts)
                    {
                        var delay = retryAfter ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempts));
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }

                    return null;
                }

                // Check for server errors (5xx)
                if ((int)response.StatusCode >= 500 && (int)response.StatusCode <= 599)
                {
                    RecordFailure();

                    response.Dispose();

                    if (attempts < maxAttempts)
                    {
                        var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempts));
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }

                    return null;
                }

                // Client error or Success
                if (response.IsSuccessStatusCode)
                {
                    RecordSuccess();
                    await _rateGovernor.ReportSuccessAsync(cancellationToken);
                }
                else
                {
                    RecordFailure();
                }

                return response;
            }
            catch (HttpRequestException)
            {
                RecordFailure();
                if (attempts >= maxAttempts)
                {
                    return null;
                }

                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempts));
                await Task.Delay(delay, cancellationToken);
            }
        }

        return null;
    }

    private void UpdateCircuitStateUnderLock()
    {
        if (_circuitState == CircuitBreakerState.Open)
        {
            if (DateTimeOffset.UtcNow - _lastStateChange >= _options.CircuitBreakerOpenDuration)
            {
                _circuitState = CircuitBreakerState.HalfOpen;
                _lastStateChange = DateTimeOffset.UtcNow;
            }
        }
    }

    private void RecordSuccess()
    {
        lock (_cbLock)
        {
            if (_circuitState == CircuitBreakerState.HalfOpen || _circuitState == CircuitBreakerState.Closed)
            {
                _circuitState = CircuitBreakerState.Closed;
                _consecutiveFailures = 0;
            }
        }
    }

    private void RecordFailure()
    {
        lock (_cbLock)
        {
            _consecutiveFailures++;
            if (_circuitState == CircuitBreakerState.HalfOpen || _consecutiveFailures >= _options.CircuitBreakerFailureThreshold)
            {
                _circuitState = CircuitBreakerState.Open;
                _lastStateChange = DateTimeOffset.UtcNow;
            }
        }
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

        return null;
    }

    #endregion

    #region Manifest Parsing

    private List<DiscoveredManifest> ParseDepotBoxManifests(uint defaultAppId, uint defaultDepotId, string json)
    {
        var list = new List<DiscoveredManifest>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Could be array of manifest records or { "manifests": [...] }
            JsonElement arrayElement = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("manifests", out var mArray))
            {
                arrayElement = mArray;
            }

            if (arrayElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arrayElement.EnumerateArray())
                {
                    if (TryParseDepotBoxItem(item, defaultAppId, defaultDepotId, out var discovered))
                    {
                        list.Add(discovered);
                    }
                }
            }
        }
        catch
        {
            // Graceful parse error handling
        }

        return list;
    }

    private bool TryParseDepotBoxItem(JsonElement item, uint fallbackAppId, uint fallbackDepotId, out DiscoveredManifest manifest)
    {
        manifest = null!;
        ulong manifestId = 0;
        uint depotId = fallbackDepotId;
        uint appId = fallbackAppId;
        string? sha256 = null;
        string? sha1 = null;
        DateTimeOffset? creationTime = null;
        long? sizeOriginal = null;
        long? sizeCompressed = null;

        if (item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty("manifestId", out var mProp))
            {
                if (mProp.ValueKind == JsonValueKind.Number) mProp.TryGetUInt64(out manifestId);
                else if (mProp.ValueKind == JsonValueKind.String) ulong.TryParse(mProp.GetString(), out manifestId);
            }

            if (item.TryGetProperty("depotId", out var dProp))
            {
                if (dProp.ValueKind == JsonValueKind.Number) dProp.TryGetUInt32(out depotId);
                else if (dProp.ValueKind == JsonValueKind.String) uint.TryParse(dProp.GetString(), out depotId);
            }

            if (item.TryGetProperty("appId", out var aProp))
            {
                if (aProp.ValueKind == JsonValueKind.Number) aProp.TryGetUInt32(out appId);
                else if (aProp.ValueKind == JsonValueKind.String) uint.TryParse(aProp.GetString(), out appId);
            }

            if (item.TryGetProperty("sha256", out var sha256Prop)) sha256 = sha256Prop.GetString();
            if (item.TryGetProperty("sha1", out var sha1Prop)) sha1 = sha1Prop.GetString();
            if (item.TryGetProperty("sizeOriginal", out var soProp) && soProp.TryGetInt64(out long so)) sizeOriginal = so;
            if (item.TryGetProperty("sizeCompressed", out var scProp) && scProp.TryGetInt64(out long sc)) sizeCompressed = sc;
            if (item.TryGetProperty("creationTime", out var ctProp) && ctProp.TryGetDateTimeOffset(out var ct)) creationTime = ct;
        }

        if (manifestId > 0 && depotId > 0)
        {
            manifest = new DiscoveredManifest
            {
                AppId = appId,
                DepotId = depotId,
                ManifestId = manifestId,
                ExpectedSha256 = sha256,
                ExpectedSha1 = sha1,
                CreationTime = creationTime,
                SizeOriginal = sizeOriginal,
                SizeCompressed = sizeCompressed,
                SourceId = SourceId
            };
            return true;
        }

        return false;
    }

    #endregion

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
