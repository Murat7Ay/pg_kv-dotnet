using Npgsql;

namespace PgKv.Tests;

[Collection(PgKvCollection.Name)]
public sealed class PgKvClientTests(PgKvFixture db)
{
    [Fact]
    public async Task Set_get_delete_round_trip()
    {
        var kv = db.NewClient();
        await kv.SetAsync("a", "hello");
        Assert.Equal("hello", await kv.GetAsync("a"));
        Assert.Null(await kv.GetAsync("missing"));
        Assert.True(await kv.DeleteAsync("a"));
        Assert.False(await kv.DeleteAsync("a"));
        Assert.Null(await kv.GetAsync("a"));
    }

    [Fact]
    public async Task Ttl_expire_and_exists()
    {
        var kv = db.NewClient();
        await kv.SetAsync("t", "x", TimeSpan.FromMinutes(5));
        var ttl = await kv.GetTimeToLiveAsync("t");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));

        await kv.SetAsync("p", "y");
        Assert.Null(await kv.GetTimeToLiveAsync("p"));
        Assert.True(await kv.ExistsAsync("p"));
        Assert.False(await kv.ExistsAsync("missing"));

        Assert.True(await kv.ExpireAsync("p", TimeSpan.FromMilliseconds(150)));
        await Task.Delay(400);
        Assert.Null(await kv.GetAsync("p"));
        Assert.False(await kv.ExistsAsync("p"));
        Assert.False(await kv.ExpireAsync("missing", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Increment_is_atomic_under_concurrency()
    {
        var kv = db.NewClient();
        Assert.Equal(1, await kv.IncrementAsync("n"));
        Assert.Equal(42, await kv.IncrementAsync("n", 41));
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => kv.IncrementAsync("c")));
        Assert.Equal("200", await kv.GetAsync("c"));
    }

    [Fact]
    public async Task Key_prefix_keeps_clients_apart()
    {
        var a = db.NewClient();
        var b = db.NewClient();
        await a.SetAsync("shared-name", "from a");
        Assert.Null(await b.GetAsync("shared-name"));

        await using var command = db.DataSource.CreateCommand("SELECT kv.get($1)");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = a.KeyPrefix + "shared-name" });
        Assert.Equal("from a", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Sorted_set_leaderboard()
    {
        var kv = db.NewClient();
        Assert.True(await kv.SortedSetAddAsync("lb", "alice", 10));
        Assert.True(await kv.SortedSetAddAsync("lb", "bob", 20));
        Assert.True(await kv.SortedSetAddAsync("lb", "carol", 15));
        Assert.False(await kv.SortedSetAddAsync("lb", "alice", 30));
        Assert.Equal(3, await kv.SortedSetLengthAsync("lb"));
        Assert.Equal(30, await kv.SortedSetScoreAsync("lb", "alice"));
        Assert.Null(await kv.SortedSetScoreAsync("lb", "nobody"));
        Assert.Equal(0, await kv.SortedSetRankAsync("lb", "carol"));
        Assert.Equal(0, await kv.SortedSetRankAsync("lb", "alice", descending: true));
        Assert.Null(await kv.SortedSetRankAsync("lb", "nobody"));
        Assert.Equal(115, await kv.SortedSetIncrementAsync("lb", "carol", 100));

        var top = await kv.SortedSetRangeAsync("lb", 0, 1, descending: true);
        Assert.Equal([new SortedSetEntry(0, "carol", 115), new SortedSetEntry(1, "alice", 30)], top);
        Assert.Empty(await kv.SortedSetRangeAsync("missing", 0, -1));

        Assert.True(await kv.SortedSetRemoveAsync("lb", "bob"));
        Assert.False(await kv.SortedSetRemoveAsync("lb", "bob"));
        Assert.Equal(2, await kv.SortedSetLengthAsync("lb"));
    }

    [Fact]
    public async Task Rate_limit_takes_tokens_and_reports_retry_after()
    {
        var kv = db.NewClient();
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await kv.RateLimitAsync("bucket", 3, TimeSpan.FromSeconds(30))).Allowed);
        }
        var denied = await kv.RateLimitAsync("bucket", 3, TimeSpan.FromSeconds(30));
        Assert.False(denied.Allowed);
        Assert.Equal(0, denied.Remaining);
        Assert.InRange(denied.RetryAfter, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Wrong_kind_raises_wrong_type()
    {
        var kv = db.NewClient();
        await kv.SortedSetAddAsync("z", "m", 1);
        var e = await Assert.ThrowsAsync<PgKvWrongTypeException>(() => kv.GetAsync("z"));
        Assert.Equal("42809", e.SqlState);
        Assert.Contains("WRONGTYPE", e.Message);
    }

    [Fact]
    public async Task Oversized_value_raises_limit_exceeded()
    {
        var kv = db.NewClient();
        var e = await Assert.ThrowsAsync<PgKvLimitExceededException>(() => kv.SetAsync("big", new string('x', 1 << 20)));
        Assert.Equal("54000", e.SqlState);
    }

    [Fact]
    public async Task Increment_on_text_raises_with_sqlstate()
    {
        var kv = db.NewClient();
        await kv.SetAsync("s", "abc");
        var e = await Assert.ThrowsAsync<PgKvException>(() => kv.IncrementAsync("s"));
        Assert.Equal("22P02", e.SqlState);
    }

    [Fact]
    public async Task Database_without_the_extension_raises_not_available()
    {
        await db.ExecuteAsync("DROP DATABASE IF EXISTS no_pgkv");
        await db.ExecuteAsync("CREATE DATABASE no_pgkv");
        var connectionString = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = "no_pgkv" }.ToString();
        await using var kv = PgKvClient.Create(connectionString);

        var e = await Assert.ThrowsAsync<PgKvNotAvailableException>(() => kv.GetAsync("a"));
        Assert.Contains("CREATE EXTENSION pg_kv", e.Message);
    }
}
