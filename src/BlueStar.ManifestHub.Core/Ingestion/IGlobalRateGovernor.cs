namespace BlueStar.ManifestHub.Core.Ingestion;

/// <summary>
/// Contract for a globally coordinated rate governor.
/// Regulates request throughput across concurrent threads and processes.
/// </summary>
public interface IGlobalRateGovernor
{
    /// <summary>
    /// Configured target requests per minute.
    /// </summary>
    int RequestsPerMinute { get; }

    /// <summary>
    /// Hard maximum budget allowed per minute (e.g. 60 req/min).
    /// </summary>
    int MaxRequestsPerMinute { get; }

    /// <summary>
    /// Waits until a request slot is available according to the rate schedule.
    /// Returns the duration waited.
    /// </summary>
    Task<TimeSpan> WaitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire a request slot within the specified timeout.
    /// </summary>
    Task<bool> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports an HTTP 429 or rate limit signal, triggering exponential backoff with jitter
    /// or applying the explicit retry-after interval if provided.
    /// </summary>
    Task ReportRateLimitExceededAsync(TimeSpan? retryAfter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports a successful request, resetting consecutive 429 failure counters.
    /// </summary>
    Task ReportSuccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the governor state, clearing past reservations and backoffs.
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a snapshot of the current rate governor state.
    /// </summary>
    Task<RateGovernorStateSnapshot> GetStateSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Snapshot of the rate governor's internal state.
/// </summary>
public sealed record RateGovernorStateSnapshot(
    DateTimeOffset NextAvailableUtc,
    DateTimeOffset BackoffUntilUtc,
    int Consecutive429Count,
    DateTimeOffset LastUpdatedUtc);
