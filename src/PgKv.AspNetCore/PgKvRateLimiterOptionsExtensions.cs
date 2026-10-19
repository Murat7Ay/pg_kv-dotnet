using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using PgKv.RateLimiting;

namespace PgKv.AspNetCore;

/// <summary>pg_kv-backed policies for the ASP.NET Core rate limiting middleware.</summary>
public static class PgKvRateLimiterOptionsExtensions
{
    /// <summary>
    /// Adds policy <paramref name="policyName"/>: one token bucket per partition key, stored
    /// in pg_kv under <c>rl:&lt;policy&gt;:&lt;partition&gt;</c> and shared by every instance of
    /// the application. Needs a <see cref="PgKvClient"/> registered with <c>AddPgKv</c>.
    /// </summary>
    /// <param name="options">The middleware options.</param>
    /// <param name="policyName">Name used with <c>RequireRateLimiting</c> or <c>[EnableRateLimiting]</c>.</param>
    /// <param name="partitionKey">Picks the bucket, e.g. the API key, user id or client IP.</param>
    /// <param name="configure">Sets the bucket size and refill rate.</param>
    public static RateLimiterOptions AddPgKvTokenBucketLimiter(
        this RateLimiterOptions options,
        string policyName,
        Func<HttpContext, string> partitionKey,
        Action<PgKvTokenBucketRateLimiterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(configure);

        var limiterOptions = new PgKvTokenBucketRateLimiterOptions();
        configure(limiterOptions);
        limiterOptions.ToBucket(); // validate at startup rather than on the first request

        var keyPrefix = $"rl:{policyName}:";
        return options.AddPolicy(policyName, context => PgKvRateLimitPartition.GetTokenBucketLimiter(
            partitionKey(context),
            context.RequestServices.GetRequiredService<PgKvClient>(),
            _ => limiterOptions,
            keyPrefix));
    }

    /// <summary>
    /// Answers rejected requests with 429 and the <c>Retry-After</c>, <c>RateLimit-Remaining</c>
    /// and <c>RateLimit-Reset</c> headers taken from the pg_kv lease.
    /// </summary>
    public static RateLimiterOptions UsePgKvRejectionResponse(this RateLimiterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, _) =>
        {
            WriteRateLimitHeaders(context.HttpContext.Response, context.Lease);
            return ValueTask.CompletedTask;
        };
        return options;
    }

    /// <summary>Writes Retry-After and RateLimit-* headers from a lease's metadata, when present.</summary>
    public static void WriteRateLimitHeaders(HttpResponse response, RateLimitLease lease)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter = Seconds(retryAfter);
        }
        if (lease.TryGetMetadata(PgKvRateLimitMetadata.Remaining, out var remaining))
        {
            response.Headers["RateLimit-Remaining"] = remaining.ToString(CultureInfo.InvariantCulture);
        }
        if (lease.TryGetMetadata(PgKvRateLimitMetadata.ResetAfter, out var resetAfter))
        {
            response.Headers["RateLimit-Reset"] = Seconds(resetAfter);
        }
    }

    private static string Seconds(TimeSpan value)
        => ((long)Math.Ceiling(value.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
