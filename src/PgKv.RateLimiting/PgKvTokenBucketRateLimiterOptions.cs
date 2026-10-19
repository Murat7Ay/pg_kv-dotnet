namespace PgKv.RateLimiting;

/// <summary>What a limiter does when the database cannot be reached.</summary>
public enum PgKvBackendFailureMode
{
    /// <summary>Let the exception reach the caller (the ASP.NET Core middleware turns it into a 500).</summary>
    Throw,

    /// <summary>Fail open: allow the request.</summary>
    Allow,

    /// <summary>Fail closed: reject the request.</summary>
    Reject,
}

/// <summary>
/// Options for <see cref="PgKvTokenBucketRateLimiter"/>, named like
/// <see cref="System.Threading.RateLimiting.TokenBucketRateLimiterOptions"/>. There is no queueing.
/// </summary>
public sealed class PgKvTokenBucketRateLimiterOptions
{
    /// <summary>Maximum number of tokens in the bucket (the burst size).</summary>
    public int TokenLimit { get; set; }

    /// <summary>Tokens added per <see cref="ReplenishmentPeriod"/>.</summary>
    public int TokensPerPeriod { get; set; }

    /// <summary>
    /// The refill period. pg_kv refills continuously, so tokens trickle in rather
    /// than arriving all at once at the end of each period.
    /// </summary>
    public TimeSpan ReplenishmentPeriod { get; set; }

    /// <summary>What to do when the database cannot be reached. Defaults to <see cref="PgKvBackendFailureMode.Throw"/>.</summary>
    public PgKvBackendFailureMode OnBackendFailure { get; set; } = PgKvBackendFailureMode.Throw;

    /// <summary>
    /// Converts to pg_kv's (capacity, per): a bucket of <see cref="TokenLimit"/> tokens that
    /// gains <see cref="TokensPerPeriod"/> every <see cref="ReplenishmentPeriod"/> refills
    /// <see cref="TokenLimit"/> tokens per <c>period × limit / tokensPerPeriod</c>.
    /// </summary>
    internal (int Capacity, TimeSpan Per) ToBucket()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(TokenLimit, 1, nameof(TokenLimit));
        ArgumentOutOfRangeException.ThrowIfLessThan(TokensPerPeriod, 1, nameof(TokensPerPeriod));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReplenishmentPeriod, TimeSpan.Zero, nameof(ReplenishmentPeriod));
        return (TokenLimit, ReplenishmentPeriod * ((double)TokenLimit / TokensPerPeriod));
    }
}
