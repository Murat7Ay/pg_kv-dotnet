using System.Globalization;
using System.Threading.RateLimiting;

namespace PgKv.RateLimiting;

/// <summary>Partitions backed by pg_kv token buckets, for <see cref="PartitionedRateLimiter"/>.</summary>
public static class PgKvRateLimitPartition
{
    /// <summary>
    /// A token bucket per partition key, stored under <c>keyPrefix + partitionKey</c>
    /// (after the client's own prefix).
    /// </summary>
    public static RateLimitPartition<TKey> GetTokenBucketLimiter<TKey>(
        TKey partitionKey,
        PgKvClient client,
        Func<TKey, PgKvTokenBucketRateLimiterOptions> factory,
        string keyPrefix = "rl:")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(factory);
        return RateLimitPartition.Get(partitionKey, key => new PgKvTokenBucketRateLimiter(
            client, keyPrefix + Convert.ToString(key, CultureInfo.InvariantCulture), factory(key)));
    }
}
