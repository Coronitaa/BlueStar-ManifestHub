namespace BlueStar.ManifestHub.Core.Ingestion;

/// <summary>
/// Configuration options for the file-coordinated rate governor.
/// </summary>
public sealed class RateGovernorOptions
{
    /// <summary>
    /// Default rate budget for DepotBox ingestion: 45 req/min.
    /// </summary>
    public const int DefaultRequestsPerMinute = 45;

    /// <summary>
    /// Hard maximum allowable budget: 60 req/min.
    /// </summary>
    public const int AbsoluteMaxRequestsPerMinute = 60;

    private int _requestsPerMinute = DefaultRequestsPerMinute;

    /// <summary>
    /// Target requests per minute. Must be between 1 and 60.
    /// </summary>
    public int RequestsPerMinute
    {
        get => _requestsPerMinute;
        set
        {
            if (value <= 0 || value > AbsoluteMaxRequestsPerMinute)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"RequestsPerMinute must be between 1 and {AbsoluteMaxRequestsPerMinute}.");
            }
            _requestsPerMinute = value;
        }
    }

    /// <summary>
    /// Absolute file path used to persist the rate limiter state.
    /// </summary>
    public string StateFilePath { get; set; } = Path.Combine(Path.GetTempPath(), "bluestar_ratelimit_state.json");

    /// <summary>
    /// Maximum burst capacity in tokens (default 1 to enforce strict smoothing).
    /// </summary>
    public int MaxBurst { get; set; } = 1;

    /// <summary>
    /// Initial base backoff duration when a 429 signal is encountered.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum ceiling for exponential backoff.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Jitter ratio applied to exponential backoff (e.g. 0.25 = +/- 25%).
    /// </summary>
    public double JitterRatio { get; set; } = 0.25;

    /// <summary>
    /// Time provider for testability and clock abstraction.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
