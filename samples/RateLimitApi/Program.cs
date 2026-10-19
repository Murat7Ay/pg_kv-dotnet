// A minimal API whose rate limits, cache and leaderboard live in Postgres (pg_kv),
// shared by every instance of the app. Start a server first:
//   docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres ghcr.io/murat7ay/pg_kv:pg18
//   psql -h localhost -U postgres -c "CREATE EXTENSION pg_kv"
using PgKv;
using PgKv.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPgKv(
    sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("pgkv")
          ?? "Host=localhost;Username=postgres;Password=postgres",
    options => options.KeyPrefix = "sample:");

builder.Services.AddRateLimiter(options => options
    // 10 requests per minute per API key, refilled smoothly, across all instances.
    .AddPgKvTokenBucketLimiter("api",
        partitionKey: context => context.Request.Headers["X-Api-Key"].FirstOrDefault()
                                 ?? context.Connection.RemoteIpAddress?.ToString()
                                 ?? "anonymous",
        configure: bucket =>
        {
            bucket.TokenLimit = 10;
            bucket.TokensPerPeriod = 10;
            bucket.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
        })
    .UsePgKvRejectionResponse());

var app = builder.Build();
app.UseRateLimiter();

var api = app.MapGroup("/api").RequireRateLimiting("api");

// Cache-aside with a 30-second TTL.
api.MapGet("/quote/{symbol}", async (string symbol, PgKvClient kv) =>
{
    if (await kv.GetAsync($"quote:{symbol}") is { } cached)
    {
        return Results.Ok(new { symbol, price = cached, cached = true });
    }
    var price = Random.Shared.Next(100, 200).ToString(); // stand-in for a slow upstream call
    await kv.SetAsync($"quote:{symbol}", price, TimeSpan.FromSeconds(30));
    return Results.Ok(new { symbol, price, cached = false });
});

// Leaderboard on a sorted set.
api.MapPost("/scores/{player}/{points:int}", async (string player, int points, PgKvClient kv) =>
    Results.Ok(new { player, score = await kv.SortedSetIncrementAsync("leaderboard", player, points) }));

api.MapGet("/leaderboard", async (PgKvClient kv) =>
    Results.Ok(await kv.SortedSetRangeAsync("leaderboard", 0, 9, descending: true)));

app.Run();
