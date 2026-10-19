# Changelog

## 0.1.0-preview.1

First preview, for pg_kv 0.1.x on PostgreSQL 15–18 and .NET 10.

- **PgKv:** `PgKvClient` covers:
  - strings, counters and TTLs (`SetAsync`, `GetAsync`, `IncrementAsync`, `ExpireAsync`, ...);
  - token buckets (`RateLimitAsync`);
  - sorted sets (`SortedSetAddAsync`, `SortedSetRankAsync`, `SortedSetRangeAsync`, ...).

  Also in this package:
  - **Errors:** pg_kv's SQLSTATEs map to `PgKvWrongTypeException`, `PgKvLimitExceededException` and `PgKvNotAvailableException`.
  - **Setup:** `AddPgKv` registers the client; `KeyPrefix` keeps applications apart.
- **PgKv.RateLimiting:** `PgKvTokenBucketRateLimiter` and `PgKvRateLimitPartition`, a `System.Threading.RateLimiting` token bucket shared by every instance through pg_kv. `OnBackendFailure` decides between throwing, failing open and failing closed.
- **PgKv.AspNetCore:** `AddPgKvTokenBucketLimiter` for the rate limiting middleware, and `UsePgKvRejectionResponse` for 429 with `Retry-After`, `RateLimit-Remaining` and `RateLimit-Reset`.
