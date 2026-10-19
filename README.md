# PgKv for .NET

.NET packages for [pg_kv](https://github.com/Murat7Ay/pg_kv), the PostgreSQL extension that keeps a Redis-style cache, token-bucket rate limiter and sorted sets in shared memory.

pg_kv uses no WAL, no MVCC row versions and needs no VACUUM. The main use case is **ASP.NET Core rate limiting shared by every instance of your app, without Redis**.

> **0.1.0-preview.1:** an early preview that tracks pg_kv 0.1.x. The API may still change.

| Package | What it gives you |
|---|---|
| `PgKv` | `PgKvClient`: strings and counters with TTL, token buckets, sorted sets; SQLSTATE-aware exceptions; `AddPgKv` |
| `PgKv.RateLimiting` | `PgKvTokenBucketRateLimiter`, a `System.Threading.RateLimiting` limiter whose bucket lives in Postgres |
| `PgKv.AspNetCore` | `AddPgKvTokenBucketLimiter` for the rate limiting middleware, and 429 responses with `Retry-After` / `RateLimit-*` headers |

## Quick start: ASP.NET Core rate limiting

You need a PostgreSQL server with pg_kv:

```bash
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres ghcr.io/murat7ay/pg_kv:pg18
psql -h localhost -U postgres -c "CREATE EXTENSION pg_kv"
```

```bash
dotnet add package PgKv.AspNetCore --prerelease
```

```csharp
using PgKv;
using PgKv.AspNetCore;

builder.Services.AddPgKv(builder.Configuration.GetConnectionString("pgkv")!,
    options => options.KeyPrefix = "orders-api:");

builder.Services.AddRateLimiter(options => options
    .AddPgKvTokenBucketLimiter("api",
        partitionKey: context => context.Request.Headers["X-Api-Key"].ToString(),
        configure: bucket =>
        {
            bucket.TokenLimit = 100;                           // burst
            bucket.TokensPerPeriod = 100;                      // refill...
            bucket.ReplenishmentPeriod = TimeSpan.FromMinutes(1); // ...per minute, smoothly
        })
    .UsePgKvRejectionResponse());                          // 429 + Retry-After + RateLimit-*

app.UseRateLimiter();
app.MapGet("/api/orders", ...).RequireRateLimiting("api");
```

How the limiter behaves:

- **One bucket for all instances.** Each request takes a token with one atomic SQL statement, so every instance of the app draws from the same bucket. In the tests, 300 concurrent requests on four limiter instances got exactly the bucket's 50 tokens.
- **No blocking.** `AttemptAcquire` never waits on the database. The middleware moves to `AcquireAsync`, which makes the call.
- **Database outages:** `OnBackendFailure` decides what happens when the database cannot be reached: `Throw` (default), `Allow` (fail open) or `Reject` (fail closed).

## The client

```csharp
await using var kv = PgKvClient.Create(connectionString, new() { KeyPrefix = "app:" });

await kv.SetAsync("session:42", json, TimeSpan.FromMinutes(30));
string? session = await kv.GetAsync("session:42");
long views = await kv.IncrementAsync("views:post:7");

RateLimitResult r = await kv.RateLimitAsync("login:" + ip, capacity: 5, per: TimeSpan.FromMinutes(15));

await kv.SortedSetIncrementAsync("leaderboard", "player:9", 30);
long? rank = await kv.SortedSetRankAsync("leaderboard", "player:9", descending: true); // 0 = top
var top10 = await kv.SortedSetRangeAsync("leaderboard", 0, 9, descending: true);
```

Method names follow StackExchange.Redis where it makes sense: `SortedSetAddAsync`, `SortedSetRangeAsync` and so on.

### Errors

pg_kv reports errors with SQLSTATE codes. The client turns them into exceptions you can catch:

| Exception | SQLSTATE | When |
|---|---|---|
| `PgKvWrongTypeException` | `42809` | the key holds another kind of value (WRONGTYPE) |
| `PgKvLimitExceededException` | `54000` | key, value or member too large; sorted-set pool full |
| `PgKvNotAvailableException` | `55000`, `3F000`, `42883` | extension not installed or not preloaded |
| `PgKvException` | `22023`, `22P02`, `22003`, `42501` | bad argument, `incr` on a non-integer, overflow, missing `USAGE` on schema `kv` |

Other database errors, such as connection failures, stay `NpgsqlException`s.

## Things to know

- **pg_kv is a cache:**
  - not transactional: a write stays even when your transaction rolls back;
  - not durable: the cache is empty after a restart;
  - not replicated: standbys have their own cache.
- **Server requirements:**
  - pg_kv must be in `shared_preload_libraries`, so it is not available on managed Postgres services yet.
  - The [pg_kv images](https://github.com/Murat7Ay/pg_kv#installation) already set it.
- **Shared keyspace:** all databases on a server share one keyspace. `KeyPrefix` keeps applications apart.
- **Permissions:** a role needs `GRANT USAGE ON SCHEMA kv TO <role>`.

## Compatibility

| PgKv | pg_kv | PostgreSQL | .NET |
|---|---|---|---|
| 0.1.x | 0.1.x | 15, 16, 17, 18 | 10 |

## Development

```bash
dotnet test PgKv.slnx   # starts ghcr.io/murat7ay/pg_kv:0.1.0-pg18 with Testcontainers (needs Docker)
PGKV_TEST_IMAGE=ghcr.io/murat7ay/pg_kv:0.1.0-pg15 dotnet test PgKv.slnx
```

A sample API (rate limiting, cache-aside, leaderboard) is in `samples/RateLimitApi`.

To release, set the version in `Directory.Build.props` and push a matching tag (`v0.1.0-preview.1`). The tag runs `.github/workflows/release.yml`, which tests, packs and publishes to nuget.org with [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing). No API key is stored.

## License

MIT
