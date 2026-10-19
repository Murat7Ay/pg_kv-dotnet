namespace PgKv;

/// <summary>The outcome of one token-bucket check (<c>kv.rate_limit_info</c>).</summary>
/// <param name="Allowed">Whether the requested tokens were taken.</param>
/// <param name="Remaining">Whole tokens left in the bucket.</param>
/// <param name="RetryAfter">How long until the request could succeed; zero when allowed.</param>
/// <param name="ResetAfter">How long until the bucket is full again.</param>
public readonly record struct RateLimitResult(bool Allowed, int Remaining, TimeSpan RetryAfter, TimeSpan ResetAfter);

/// <summary>A sorted-set member with its score and its rank in the requested order.</summary>
/// <param name="Rank">0-based position in the requested order.</param>
/// <param name="Member">The member.</param>
/// <param name="Score">The member's score.</param>
public readonly record struct SortedSetEntry(long Rank, string Member, double Score);
