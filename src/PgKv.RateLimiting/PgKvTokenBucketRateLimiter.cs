using System.Diagnostics;
using System.Threading.RateLimiting;

namespace PgKv.RateLimiting;

/// <summary>Lease metadata beyond <see cref="MetadataName.RetryAfter"/>, for RateLimit-* response headers.</summary>
public static class PgKvRateLimitMetadata
{
    /// <summary>Whole tokens left in the bucket after this request.</summary>
    public static MetadataName<int> Remaining { get; } = MetadataName.Create<int>("PGKV_REMAINING");

    /// <summary>How long until the bucket is full again.</summary>
    public static MetadataName<TimeSpan> ResetAfter { get; } = MetadataName.Create<TimeSpan>("PGKV_RESET_AFTER");
}

/// <summary>
/// A token bucket kept in pg_kv: every instance of the application that uses the same
/// key shares one bucket, and each acquisition is a single atomic statement.
/// </summary>
/// <remarks>
/// <see cref="RateLimiter.AttemptAcquire(int)"/> never blocks on the database: it reports
/// "not acquired" without metadata, so callers such as the ASP.NET Core middleware move on
/// to <see cref="RateLimiter.AcquireAsync(int, CancellationToken)"/>, which does the work.
/// </remarks>
public sealed class PgKvTokenBucketRateLimiter : RateLimiter
{
    private static readonly RateLimitLease SyncPathLease = new Lease(false, null);

    private readonly PgKvClient _client;
    private readonly string _key;
    private readonly int _capacity;
    private readonly TimeSpan _per;
    private readonly PgKvBackendFailureMode _onFailure;

    private int _active;
    private long _idleSince = Stopwatch.GetTimestamp();
    private long _successful;
    private long _failed;
    private int _lastRemaining;

    /// <summary>Creates a limiter for the bucket stored under <paramref name="key"/> (after the client's prefix).</summary>
    public PgKvTokenBucketRateLimiter(PgKvClient client, string key, PgKvTokenBucketRateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _key = key;
        (_capacity, _per) = options.ToBucket();
        _onFailure = options.OnBackendFailure;
        _lastRemaining = _capacity;
    }

    /// <inheritdoc />
    public override TimeSpan? IdleDuration
        => Volatile.Read(ref _active) > 0 ? null : Stopwatch.GetElapsedTime(Interlocked.Read(ref _idleSince));

    /// <summary>
    /// Statistics as seen by this instance. <see cref="RateLimiterStatistics.CurrentAvailablePermits"/>
    /// is the last value read from the database; other instances take tokens too.
    /// </summary>
    public override RateLimiterStatistics GetStatistics() => new()
    {
        CurrentAvailablePermits = Volatile.Read(ref _lastRemaining),
        CurrentQueuedCount = 0,
        TotalSuccessfulLeases = Interlocked.Read(ref _successful),
        TotalFailedLeases = Interlocked.Read(ref _failed),
    };

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => SyncPathLease;

    /// <inheritdoc />
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        if (permitCount > _capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount, $"permitCount cannot exceed TokenLimit ({_capacity}).");
        }
        Interlocked.Increment(ref _active);
        try
        {
            RateLimitResult result;
            try
            {
                result = await _client.RateLimitAsync(_key, _capacity, _per, permitCount, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (_onFailure != PgKvBackendFailureMode.Throw && e is not OperationCanceledException)
            {
                return new Lease(_onFailure == PgKvBackendFailureMode.Allow, null);
            }
            Volatile.Write(ref _lastRemaining, result.Remaining);
            Interlocked.Increment(ref result.Allowed ? ref _successful : ref _failed);
            return new Lease(result.Allowed, result);
        }
        finally
        {
            Interlocked.Exchange(ref _idleSince, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _active);
        }
    }

    private sealed class Lease(bool acquired, RateLimitResult? result) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => result is null
            ? []
            : acquired
                ? [PgKvRateLimitMetadata.Remaining.Name, PgKvRateLimitMetadata.ResetAfter.Name]
                : [MetadataName.RetryAfter.Name, PgKvRateLimitMetadata.Remaining.Name, PgKvRateLimitMetadata.ResetAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            if (result is not { } r)
            {
                return false;
            }
            if (metadataName == MetadataName.RetryAfter.Name && !acquired)
            {
                metadata = r.RetryAfter;
            }
            else if (metadataName == PgKvRateLimitMetadata.Remaining.Name)
            {
                metadata = r.Remaining;
            }
            else if (metadataName == PgKvRateLimitMetadata.ResetAfter.Name)
            {
                metadata = r.ResetAfter;
            }
            return metadata is not null;
        }
    }
}
