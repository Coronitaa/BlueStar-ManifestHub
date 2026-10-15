using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueStar.ManifestHub.Core.Ingestion;

/// <summary>
/// Persisted, file-coordinated rate governor.
/// Coordinates request throughput across concurrent workers and separate processes using atomic state locking.
/// Enforces rate budgets (default 45 req/min, max 60 req/min) with exponential backoff and jitter for 429 signals.
/// </summary>
public sealed class FileCoordinatedRateGovernor : IGlobalRateGovernor, IDisposable
{
    private readonly RateGovernorOptions _options;
    private readonly string _lockFilePath;
    private readonly SemaphoreSlim _inProcessLock = new(1, 1);
    private readonly Random _random = new();
    private bool _disposed;

    public FileCoordinatedRateGovernor(RateGovernorOptions? options = null)
    {
        _options = options ?? new RateGovernorOptions();
        _lockFilePath = _options.StateFilePath + ".lock";

        string? dir = Path.GetDirectoryName(_options.StateFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public int RequestsPerMinute => _options.RequestsPerMinute;
    public int MaxRequestsPerMinute => RateGovernorOptions.AbsoluteMaxRequestsPerMinute;

    /// <inheritdoc />
    public async Task<TimeSpan> WaitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        DateTimeOffset scheduledTime;
        DateTimeOffset now;

        using (await AcquireCrossProcessLockAsync(cancellationToken))
        {
            var state = await ReadStateInternalAsync(cancellationToken);
            now = _options.TimeProvider.GetUtcNow();
            double intervalMs = 60000.0 / _options.RequestsPerMinute;

            var backoffUntil = new DateTimeOffset(state.BackoffUntilTicksUtc, TimeSpan.Zero);
            var baseline = now > backoffUntil ? now : backoffUntil;

            var nextAvailable = state.NextAvailableTicksUtc == 0
                ? baseline
                : new DateTimeOffset(state.NextAvailableTicksUtc, TimeSpan.Zero);

            if (_options.MaxBurst > 1)
            {
                var earliest = baseline - TimeSpan.FromMilliseconds(intervalMs * _options.MaxBurst);
                if (nextAvailable < earliest)
                {
                    nextAvailable = earliest;
                }
            }
            else
            {
                if (nextAvailable < baseline)
                {
                    nextAvailable = baseline;
                }
            }

            scheduledTime = nextAvailable > baseline ? nextAvailable : baseline;
            state.NextAvailableTicksUtc = (scheduledTime + TimeSpan.FromMilliseconds(intervalMs)).UtcTicks;
            state.LastUpdatedTicksUtc = now.UtcTicks;

            await WriteStateInternalAsync(state, cancellationToken);
        }

        var waitTime = scheduledTime - now;
        if (waitTime > TimeSpan.Zero)
        {
            await Task.Delay(waitTime, _options.TimeProvider, cancellationToken);
            return waitTime;
        }

        return TimeSpan.Zero;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        DateTimeOffset scheduledTime;
        DateTimeOffset now;
        TimeSpan waitTime;

        using (await AcquireCrossProcessLockAsync(cancellationToken))
        {
            var state = await ReadStateInternalAsync(cancellationToken);
            now = _options.TimeProvider.GetUtcNow();
            double intervalMs = 60000.0 / _options.RequestsPerMinute;

            var backoffUntil = new DateTimeOffset(state.BackoffUntilTicksUtc, TimeSpan.Zero);
            var baseline = now > backoffUntil ? now : backoffUntil;

            var nextAvailable = state.NextAvailableTicksUtc == 0
                ? baseline
                : new DateTimeOffset(state.NextAvailableTicksUtc, TimeSpan.Zero);

            if (_options.MaxBurst > 1)
            {
                var earliest = baseline - TimeSpan.FromMilliseconds(intervalMs * _options.MaxBurst);
                if (nextAvailable < earliest)
                {
                    nextAvailable = earliest;
                }
            }
            else
            {
                if (nextAvailable < baseline)
                {
                    nextAvailable = baseline;
                }
            }

            scheduledTime = nextAvailable > baseline ? nextAvailable : baseline;
            waitTime = scheduledTime - now;

            if (waitTime > timeout)
            {
                // Slot cannot be served within timeout; do not reserve
                return false;
            }

            state.NextAvailableTicksUtc = (scheduledTime + TimeSpan.FromMilliseconds(intervalMs)).UtcTicks;
            state.LastUpdatedTicksUtc = now.UtcTicks;

            await WriteStateInternalAsync(state, cancellationToken);
        }

        if (waitTime > TimeSpan.Zero)
        {
            await Task.Delay(waitTime, _options.TimeProvider, cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task ReportRateLimitExceededAsync(TimeSpan? retryAfter = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using (await AcquireCrossProcessLockAsync(cancellationToken))
        {
            var state = await ReadStateInternalAsync(cancellationToken);
            var now = _options.TimeProvider.GetUtcNow();
            state.Consecutive429Count++;

            TimeSpan backoff;
            if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
            {
                backoff = retryAfter.Value;
            }
            else
            {
                double multiplier = Math.Pow(2, Math.Min(state.Consecutive429Count - 1, 6));
                double baseMs = _options.InitialBackoff.TotalMilliseconds * multiplier;
                baseMs = Math.Min(baseMs, _options.MaxBackoff.TotalMilliseconds);

                // Add jitter
                double jitter = (_random.NextDouble() * 2.0 - 1.0) * _options.JitterRatio * baseMs;
                double totalMs = Math.Clamp(baseMs + jitter, 100, _options.MaxBackoff.TotalMilliseconds);
                backoff = TimeSpan.FromMilliseconds(totalMs);
            }

            var newBackoffUntil = now + backoff;
            if (newBackoffUntil.UtcTicks > state.BackoffUntilTicksUtc)
            {
                state.BackoffUntilTicksUtc = newBackoffUntil.UtcTicks;
            }

            if (newBackoffUntil.UtcTicks > state.NextAvailableTicksUtc)
            {
                state.NextAvailableTicksUtc = newBackoffUntil.UtcTicks;
            }

            state.LastUpdatedTicksUtc = now.UtcTicks;
            await WriteStateInternalAsync(state, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task ReportSuccessAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using (await AcquireCrossProcessLockAsync(cancellationToken))
        {
            var state = await ReadStateInternalAsync(cancellationToken);
            if (state.Consecutive429Count > 0)
            {
                state.Consecutive429Count = 0;
                state.LastUpdatedTicksUtc = _options.TimeProvider.GetUtcNow().UtcTicks;
                await WriteStateInternalAsync(state, cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using (await AcquireCrossProcessLockAsync(cancellationToken))
        {
            var state = new RateGovernorDiskState();
            await WriteStateInternalAsync(state, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<RateGovernorStateSnapshot> GetStateSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using (await AcquireCrossProcessLockAsync(cancellationToken))
        {
            var state = await ReadStateInternalAsync(cancellationToken);
            return new RateGovernorStateSnapshot(
                new DateTimeOffset(state.NextAvailableTicksUtc, TimeSpan.Zero),
                new DateTimeOffset(state.BackoffUntilTicksUtc, TimeSpan.Zero),
                state.Consecutive429Count,
                new DateTimeOffset(state.LastUpdatedTicksUtc, TimeSpan.Zero));
        }
    }

    #region Internal File Locking & State I/O

    private async Task<IDisposable> AcquireCrossProcessLockAsync(CancellationToken cancellationToken)
    {
        await _inProcessLock.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(30);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var fileStream = new FileStream(
                        _lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);

                    return new LockReleaser(_inProcessLock, fileStream);
                }
                catch (IOException)
                {
                    if (stopwatch.Elapsed > timeout)
                    {
                        throw new TimeoutException($"Timed out waiting to acquire rate governor lock on '{_lockFilePath}'.");
                    }
                    await Task.Delay(10, cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            _inProcessLock.Release();
            throw;
        }
    }

    private async Task<RateGovernorDiskState> ReadStateInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.StateFilePath))
        {
            return new RateGovernorDiskState();
        }

        try
        {
            string json = await File.ReadAllTextAsync(_options.StateFilePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new RateGovernorDiskState();
            }

            return JsonSerializer.Deserialize<RateGovernorDiskState>(json) ?? new RateGovernorDiskState();
        }
        catch
        {
            return new RateGovernorDiskState();
        }
    }

    private async Task WriteStateInternalAsync(RateGovernorDiskState state, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_options.StateFilePath, json, cancellationToken);
    }

    private sealed class LockReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private FileStream? _fileStream;

        public LockReleaser(SemaphoreSlim semaphore, FileStream fileStream)
        {
            _semaphore = semaphore;
            _fileStream = fileStream;
        }

        public void Dispose()
        {
            if (_fileStream != null)
            {
                try { _fileStream.Dispose(); } catch { }
                _fileStream = null;
            }
            _semaphore.Release();
        }
    }

    private sealed class RateGovernorDiskState
    {
        [JsonPropertyName("nextAvailableTicksUtc")]
        public long NextAvailableTicksUtc { get; set; }

        [JsonPropertyName("backoffUntilTicksUtc")]
        public long BackoffUntilTicksUtc { get; set; }

        [JsonPropertyName("consecutive429Count")]
        public int Consecutive429Count { get; set; }

        [JsonPropertyName("lastUpdatedTicksUtc")]
        public long LastUpdatedTicksUtc { get; set; }
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
            _inProcessLock.Dispose();
        }
    }

    #endregion
}
