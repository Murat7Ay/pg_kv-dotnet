using Npgsql;
using NpgsqlTypes;

namespace PgKv;

/// <summary>
/// Typed access to the pg_kv extension (the <c>kv.*</c> SQL functions): strings and
/// counters with TTL, token-bucket rate limiting and sorted sets.
/// </summary>
/// <remarks>
/// <para>
/// pg_kv is a cache: writes are not transactional, not durable and not replicated.
/// Every call is one statement on its own connection from the pool.
/// </para>
/// <para>
/// Errors raised by the extension surface as <see cref="PgKvException"/> subclasses.
/// Thread-safe; register it as a singleton.
/// </para>
/// </remarks>
public sealed class PgKvClient : IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly string _prefix;

    /// <summary>Uses an existing data source, which the caller keeps owning.</summary>
    public PgKvClient(NpgsqlDataSource dataSource, PgKvClientOptions? options = null)
        : this(dataSource, options, ownsDataSource: false)
    {
    }

    private PgKvClient(NpgsqlDataSource dataSource, PgKvClientOptions? options, bool ownsDataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _prefix = options?.KeyPrefix ?? "";
    }

    /// <summary>
    /// Creates a client with its own data source (see <see cref="PgKvDataSource.Create"/>),
    /// disposed together with the client.
    /// </summary>
    public static PgKvClient Create(string connectionString, PgKvClientOptions? options = null)
        => new(PgKvDataSource.Create(connectionString), options, ownsDataSource: true);

    /// <summary>The data source the client runs its statements on.</summary>
    public NpgsqlDataSource DataSource => _dataSource;

    /// <summary>The prefix prepended to every key.</summary>
    public string KeyPrefix => _prefix;

    // ------------------------------------------------------------------ strings

    /// <summary>Stores <paramref name="value"/>, replacing any previous value (of any kind) and TTL.</summary>
    public Task SetAsync(string key, string value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => ExecuteAsync("SELECT kv.set($1, $2, $3)", cancellationToken, Key(key), Text(value), Interval(ttl));

    /// <summary>Returns the value, or <see langword="null"/> when the key is missing or expired.</summary>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        => ScalarAsync<string?>("SELECT kv.get($1)", cancellationToken, Key(key));

    /// <summary>Deletes the key (of any kind). Returns whether a live entry was removed.</summary>
    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        => ScalarAsync<bool>("SELECT kv.del($1)", cancellationToken, Key(key));

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to an integer value and returns the result.
    /// A missing key starts at 0; an existing TTL is kept.
    /// </summary>
    public Task<long> IncrementAsync(string key, long delta = 1, CancellationToken cancellationToken = default)
        => ScalarAsync<long>("SELECT kv.incr($1, $2)", cancellationToken, Key(key), new NpgsqlParameter<long> { TypedValue = delta });

    /// <summary>
    /// Remaining time to live, or <see langword="null"/> when the key has no TTL or
    /// does not exist (use <see cref="ExistsAsync"/> to tell them apart).
    /// </summary>
    public async Task<TimeSpan?> GetTimeToLiveAsync(string key, CancellationToken cancellationToken = default)
    {
        var ms = await ScalarAsync<long>("SELECT kv.ttl($1)", cancellationToken, Key(key)).ConfigureAwait(false);
        return ms >= 0 ? TimeSpan.FromMilliseconds(ms) : null;
    }

    /// <summary>Whether a live entry of any kind exists under the key.</summary>
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        => await ScalarAsync<long>("SELECT kv.ttl($1)", cancellationToken, Key(key)).ConfigureAwait(false) != -2;

    /// <summary>
    /// Sets the TTL of an existing key, or removes it with <see langword="null"/>.
    /// Returns <see langword="false"/> when the key does not exist.
    /// </summary>
    public Task<bool> ExpireAsync(string key, TimeSpan? ttl, CancellationToken cancellationToken = default)
        => ScalarAsync<bool>("SELECT kv.expire($1, $2)", cancellationToken, Key(key), Interval(ttl));

    // ------------------------------------------------------------ rate limiting

    /// <summary>
    /// Takes <paramref name="cost"/> tokens from the bucket under <paramref name="key"/>,
    /// which holds up to <paramref name="capacity"/> tokens and refills smoothly at
    /// <paramref name="capacity"/> per <paramref name="per"/>. Atomic across all clients.
    /// </summary>
    public async Task<RateLimitResult> RateLimitAsync(
        string key, int capacity, TimeSpan per, int cost = 1, CancellationToken cancellationToken = default)
    {
        await using var command = Command(
            "SELECT allowed, remaining, retry_after_ms, reset_after_ms FROM kv.rate_limit_info($1, $2, $3, $4)",
            Key(key), new NpgsqlParameter<int> { TypedValue = capacity }, Interval(per), new NpgsqlParameter<int> { TypedValue = cost });
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new RateLimitResult(
                reader.GetBoolean(0),
                reader.GetInt32(1),
                TimeSpan.FromMilliseconds(reader.GetInt64(2)),
                TimeSpan.FromMilliseconds(reader.GetInt64(3)));
        }
        catch (PostgresException e) when (PgKvException.Translate(e) is { } translated)
        {
            throw translated;
        }
    }

    // -------------------------------------------------------------- sorted sets

    /// <summary>Adds <paramref name="member"/> with <paramref name="score"/>, or updates its score. Returns whether it was added.</summary>
    public Task<bool> SortedSetAddAsync(string key, string member, double score, CancellationToken cancellationToken = default)
        => ScalarAsync<bool>("SELECT kv.zadd($1, $2, $3)", cancellationToken, Key(key), Text(member), Float(score));

    /// <summary>Adds <paramref name="delta"/> to the member's score (a missing member starts at 0) and returns the new score.</summary>
    public Task<double> SortedSetIncrementAsync(string key, string member, double delta, CancellationToken cancellationToken = default)
        => ScalarAsync<double>("SELECT kv.zincrby($1, $2, $3)", cancellationToken, Key(key), Text(member), Float(delta));

    /// <summary>The member's score, or <see langword="null"/> when it is not in the set.</summary>
    public Task<double?> SortedSetScoreAsync(string key, string member, CancellationToken cancellationToken = default)
        => ScalarAsync<double?>("SELECT kv.zscore($1, $2)", cancellationToken, Key(key), Text(member));

    /// <summary>
    /// The member's 0-based rank by ascending score, or by descending score with
    /// <paramref name="descending"/> (0 = highest); <see langword="null"/> when it is not in the set. O(log n).
    /// </summary>
    public Task<long?> SortedSetRankAsync(string key, string member, bool descending = false, CancellationToken cancellationToken = default)
        => ScalarAsync<long?>(descending ? "SELECT kv.zrevrank($1, $2)" : "SELECT kv.zrank($1, $2)", cancellationToken, Key(key), Text(member));

    /// <summary>
    /// Members by rank, Redis ZRANGE style: <paramref name="start"/> and <paramref name="stop"/> are
    /// inclusive 0-based ranks, negative values count from the end. With <paramref name="descending"/>
    /// the highest scores come first, e.g. <c>(0, 9, descending: true)</c> is a top 10.
    /// </summary>
    public async Task<IReadOnlyList<SortedSetEntry>> SortedSetRangeAsync(
        string key, long start, long stop, bool descending = false, CancellationToken cancellationToken = default)
    {
        await using var command = Command(
            "SELECT rank, member, score FROM kv.zrange($1, $2, $3, $4)",
            Key(key), new NpgsqlParameter<long> { TypedValue = start }, new NpgsqlParameter<long> { TypedValue = stop },
            new NpgsqlParameter<bool> { TypedValue = descending });
        try
        {
            var entries = new List<SortedSetEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new SortedSetEntry(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2)));
            }
            return entries;
        }
        catch (PostgresException e) when (PgKvException.Translate(e) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>Removes <paramref name="member"/>; an emptied set is deleted. Returns whether it was there.</summary>
    public Task<bool> SortedSetRemoveAsync(string key, string member, CancellationToken cancellationToken = default)
        => ScalarAsync<bool>("SELECT kv.zrem($1, $2)", cancellationToken, Key(key), Text(member));

    /// <summary>Number of members (0 when the key does not exist).</summary>
    public Task<long> SortedSetLengthAsync(string key, CancellationToken cancellationToken = default)
        => ScalarAsync<long>("SELECT kv.zcard($1)", cancellationToken, Key(key));

    // ------------------------------------------------------------------ plumbing

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _ownsDataSource ? _dataSource.DisposeAsync() : ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    private NpgsqlParameter<string> Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new() { TypedValue = _prefix + key };
    }

    private static NpgsqlParameter<string> Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new() { TypedValue = value };
    }

    private static NpgsqlParameter<double> Float(double value) => new() { TypedValue = value };

    private static NpgsqlParameter Interval(TimeSpan? value)
        => new() { NpgsqlDbType = NpgsqlDbType.Interval, Value = value.HasValue ? value.Value : DBNull.Value };

    private NpgsqlCommand Command(string sql, params NpgsqlParameter[] parameters)
    {
        var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return command;
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = Command(sql, parameters);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException e) when (PgKvException.Translate(e) is { } translated)
        {
            throw translated;
        }
    }

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = Command(sql, parameters);
        try
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? default! : (T)value;
        }
        catch (PostgresException e) when (PgKvException.Translate(e) is { } translated)
        {
            throw translated;
        }
    }
}
