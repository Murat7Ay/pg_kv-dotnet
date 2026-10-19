using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PgKv.AspNetCore;

namespace PgKv.Tests;

[Collection(PgKvCollection.Name)]
public sealed class AspNetCoreTests(PgKvFixture db)
{
    private async Task<WebApplication> StartAppAsync(string keyPrefix)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddPgKv(db.ConnectionString, o => o.KeyPrefix = keyPrefix);
        builder.Services.AddRateLimiter(o => o
            .AddPgKvTokenBucketLimiter("api",
                partitionKey: context => context.Request.Headers["X-Api-Key"].ToString(),
                configure: bucket =>
                {
                    bucket.TokenLimit = 3;
                    bucket.TokensPerPeriod = 3;
                    bucket.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
                })
            .UsePgKvRejectionResponse());

        var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/ping", () => "pong").RequireRateLimiting("api");
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> PingAsync(HttpClient client, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("X-Api-Key", apiKey);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Returns_429_with_retry_after_once_the_bucket_is_empty()
    {
        await using var app = await StartAppAsync($"test:{Guid.NewGuid():N}:");
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PingAsync(client, "key-1")).StatusCode);
        }
        var rejected = await PingAsync(client, "key-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("20", Assert.Single(rejected.Headers.GetValues("Retry-After")));
        Assert.Equal("0", Assert.Single(rejected.Headers.GetValues("RateLimit-Remaining")));

        Assert.Equal(HttpStatusCode.OK, (await PingAsync(client, "key-2")).StatusCode);
    }

    [Fact]
    public async Task Two_app_instances_share_one_bucket()
    {
        var prefix = $"test:{Guid.NewGuid():N}:";
        await using var first = await StartAppAsync(prefix);
        await using var second = await StartAppAsync(prefix);
        var a = first.GetTestClient();
        var b = second.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await PingAsync(a, "k")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PingAsync(b, "k")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PingAsync(a, "k")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PingAsync(b, "k")).StatusCode);
    }
}
