using System.Threading.RateLimiting;
using PgKv.RateLimiting;

namespace PgKv.Tests;

[Collection(PgKvCollection.Name)]
public sealed class RateLimiterTests(PgKvFixture db)
{
    private static PgKvTokenBucketRateLimiterOptions Bucket(int tokens, TimeSpan period) => new()
    {
        TokenLimit = tokens,
        TokensPerPeriod = tokens,
        ReplenishmentPeriod = period,
    };

    [Fact]
    public async Task Allows_the_burst_then_rejects_with_retry_after()
    {
        using var limiter = new PgKvTokenBucketRateLimiter(db.NewClient(), "burst", Bucket(3, TimeSpan.FromMinutes(1)));
        for (var i = 0; i < 3; i++)
        {
            using var lease = await limiter.AcquireAsync();
            Assert.True(lease.IsAcquired);
            Assert.True(lease.TryGetMetadata(PgKvRateLimitMetadata.Remaining, out var remaining));
            Assert.Equal(2 - i, remaining);
        }
        using var rejected = await limiter.AcquireAsync();
        Assert.False(rejected.IsAcquired);
        Assert.True(rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(20));
        Assert.Equal(3, limiter.GetStatistics()!.TotalSuccessfulLeases);
        Assert.Equal(1, limiter.GetStatistics()!.TotalFailedLeases);
    }

    [Fact]
    public async Task Is_exact_under_concurrency_across_limiter_instances()
    {
        // Several instances on the same key stand in for several app servers.
        var client = db.NewClient();
        var limiters = Enumerable.Range(0, 4)
            .Select(_ => new PgKvTokenBucketRateLimiter(client, "shared", Bucket(50, TimeSpan.FromHours(1))))
            .ToArray();
        var leases = await Task.WhenAll(Enumerable.Range(0, 300).Select(i => limiters[i % 4].AcquireAsync().AsTask()));
        Assert.Equal(50, leases.Count(l => l.IsAcquired));
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    [Fact]
    public void Attempt_acquire_defers_to_the_async_path()
    {
        using var limiter = new PgKvTokenBucketRateLimiter(db.NewClient(), "sync", Bucket(3, TimeSpan.FromMinutes(1)));
        using var lease = limiter.AttemptAcquire();
        Assert.False(lease.IsAcquired);
        Assert.Empty(lease.MetadataNames);
    }

    [Fact]
    public async Task Partitions_have_separate_buckets()
    {
        var client = db.NewClient();
        using var limiter = PartitionedRateLimiter.Create<string, string>(key =>
            PgKvRateLimitPartition.GetTokenBucketLimiter(key, client, _ => Bucket(1, TimeSpan.FromHours(1))));

        Assert.True((await limiter.AcquireAsync("alice")).IsAcquired);
        Assert.False((await limiter.AcquireAsync("alice")).IsAcquired);
        Assert.True((await limiter.AcquireAsync("bob")).IsAcquired);
    }

    [Theory]
    [InlineData(PgKvBackendFailureMode.Allow, true)]
    [InlineData(PgKvBackendFailureMode.Reject, false)]
    public async Task Backend_failure_mode_decides_when_the_database_is_down(PgKvBackendFailureMode mode, bool acquired)
    {
        await using var unreachable = PgKvClient.Create("Host=127.0.0.1;Port=1;Timeout=1;Username=x;Password=x");
        var options = Bucket(3, TimeSpan.FromMinutes(1));
        options.OnBackendFailure = mode;
        using var limiter = new PgKvTokenBucketRateLimiter(unreachable, "down", options);

        using var lease = await limiter.AcquireAsync();
        Assert.Equal(acquired, lease.IsAcquired);
        Assert.Empty(lease.MetadataNames);
    }

    [Fact]
    public async Task Backend_failure_throws_by_default()
    {
        await using var unreachable = PgKvClient.Create("Host=127.0.0.1;Port=1;Timeout=1;Username=x;Password=x");
        using var limiter = new PgKvTokenBucketRateLimiter(unreachable, "down", Bucket(3, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAnyAsync<Exception>(async () => await limiter.AcquireAsync());
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var client = db.NewClient();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PgKvTokenBucketRateLimiter(client, "x", new PgKvTokenBucketRateLimiterOptions { TokenLimit = 0, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PgKvTokenBucketRateLimiter(client, "x", new PgKvTokenBucketRateLimiterOptions { TokenLimit = 1, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.Zero }));
    }
}
