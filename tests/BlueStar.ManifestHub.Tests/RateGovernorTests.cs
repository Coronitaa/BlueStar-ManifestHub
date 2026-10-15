using BlueStar.ManifestHub.Core.Ingestion;

namespace BlueStar.ManifestHub.Tests;

public sealed class RateGovernorTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _stateFilePath;

    public RateGovernorTests()
    {
        _testDir = TestManifestHelper.CreateTestDirectory("ratelimit_test_");
        _stateFilePath = Path.Combine(_testDir, "governor_state.json");
    }

    public void Dispose()
    {
        TestManifestHelper.CleanupTestDirectory(_testDir);
    }

    [Fact]
    public void BudgetConfiguration_EnforcesMax60AndPositiveBudget()
    {
        var options = new RateGovernorOptions();
        Assert.Equal(45, options.RequestsPerMinute);

        // Allowed values
        options.RequestsPerMinute = 60;
        Assert.Equal(60, options.RequestsPerMinute);

        options.RequestsPerMinute = 1;
        Assert.Equal(1, options.RequestsPerMinute);

        // Reject above 60
        Assert.Throws<ArgumentOutOfRangeException>(() => options.RequestsPerMinute = 61);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.RequestsPerMinute = 100);

        // Reject zero or negative
        Assert.Throws<ArgumentOutOfRangeException>(() => options.RequestsPerMinute = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.RequestsPerMinute = -5);
    }

    [Fact]
    public async Task SymmetricConcurrency_MultipleWorkersCoordinateCorrectly()
    {
        // 45 req/min => 1333.33ms interval per request
        int workerCount = 5;
        var workers = new List<FileCoordinatedRateGovernor>();

        for (int i = 0; i < workerCount; i++)
        {
            workers.Add(new FileCoordinatedRateGovernor(new RateGovernorOptions
            {
                StateFilePath = _stateFilePath,
                RequestsPerMinute = 45,
                MaxBurst = 1
            }));
        }

        try
        {
            // Run all workers concurrently to acquire slots with generous timeout
            var tasks = workers.Select(w => w.TryAcquireAsync(TimeSpan.FromMinutes(2))).ToArray();
            bool[] results = await Task.WhenAll(tasks);

            // All workers must have succeeded
            Assert.All(results, Assert.True);

            // Verify final state snapshot has advanced by 5 intervals
            var snapshot = await workers[0].GetStateSnapshotAsync();
            var expectedInterval = TimeSpan.FromMinutes(1.0 / 45);
            var totalAdvanced = snapshot.NextAvailableUtc - snapshot.LastUpdatedUtc;

            // NextAvailable must be advanced by at least workerCount * interval
            Assert.True(snapshot.NextAvailableUtc > DateTimeOffset.UtcNow);
        }
        finally
        {
            foreach (var w in workers)
            {
                w.Dispose();
            }
        }
    }

    [Fact]
    public async Task ReportRateLimitExceeded_AppliesExponentialBackoffWithJitter()
    {
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = _stateFilePath,
            RequestsPerMinute = 45,
            InitialBackoff = TimeSpan.FromSeconds(2),
            MaxBackoff = TimeSpan.FromSeconds(30),
            JitterRatio = 0.2
        });

        var before = DateTimeOffset.UtcNow;
        await governor.ReportRateLimitExceededAsync();

        var snapshot1 = await governor.GetStateSnapshotAsync();
        Assert.Equal(1, snapshot1.Consecutive429Count);
        // Backoff must be around 2 seconds (+/- 20%)
        Assert.True(snapshot1.BackoffUntilUtc >= before.AddSeconds(1.5));
        Assert.True(snapshot1.BackoffUntilUtc <= before.AddSeconds(2.6));

        // Report second 429: exponential step (around 4s)
        await governor.ReportRateLimitExceededAsync();
        var snapshot2 = await governor.GetStateSnapshotAsync();
        Assert.Equal(2, snapshot2.Consecutive429Count);
        Assert.True(snapshot2.BackoffUntilUtc >= before.AddSeconds(3.0));

        // Report success resets consecutive counter
        await governor.ReportSuccessAsync();
        var snapshot3 = await governor.GetStateSnapshotAsync();
        Assert.Equal(0, snapshot3.Consecutive429Count);
    }

    [Fact]
    public async Task ReportRateLimitExceeded_RespectsRetryAfterHeader()
    {
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = _stateFilePath,
            RequestsPerMinute = 45
        });

        var explicitRetryAfter = TimeSpan.FromSeconds(15);
        var before = DateTimeOffset.UtcNow;

        await governor.ReportRateLimitExceededAsync(explicitRetryAfter);

        var snapshot = await governor.GetStateSnapshotAsync();
        Assert.True(snapshot.BackoffUntilUtc >= before.AddSeconds(14.5));
        Assert.True(snapshot.BackoffUntilUtc <= before.AddSeconds(16.5));
    }

    [Fact]
    public async Task TryAcquireAsync_ReturnsFalseWhenTimeoutIsInsufficient()
    {
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = _stateFilePath,
            RequestsPerMinute = 45
        });

        // Trigger long backoff (e.g. 10s)
        await governor.ReportRateLimitExceededAsync(TimeSpan.FromSeconds(10));

        // Attempting to acquire with a 100ms timeout must fail
        bool acquired = await governor.TryAcquireAsync(TimeSpan.FromMilliseconds(100));
        Assert.False(acquired);
    }

    [Fact]
    public async Task ResetAsync_ClearsStateCompletely()
    {
        using var governor = new FileCoordinatedRateGovernor(new RateGovernorOptions
        {
            StateFilePath = _stateFilePath,
            RequestsPerMinute = 45
        });

        await governor.ReportRateLimitExceededAsync(TimeSpan.FromSeconds(20));
        var snapBefore = await governor.GetStateSnapshotAsync();
        Assert.Equal(1, snapBefore.Consecutive429Count);

        await governor.ResetAsync();

        var snapAfter = await governor.GetStateSnapshotAsync();
        Assert.Equal(0, snapAfter.Consecutive429Count);
        Assert.Equal(DateTimeOffset.MinValue, snapAfter.BackoffUntilUtc);
        Assert.Equal(DateTimeOffset.MinValue, snapAfter.NextAvailableUtc);
    }
}
