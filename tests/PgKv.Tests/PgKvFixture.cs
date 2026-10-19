using Npgsql;
using Testcontainers.PostgreSql;

namespace PgKv.Tests;

/// <summary>
/// One pg_kv server for the whole test run, from the published image (override with
/// PGKV_TEST_IMAGE). PostgreSqlBuilder replaces the image command with its own
/// "-c" flags; the image preloads pg_kv through postgresql.conf, so that still works.
/// </summary>
public sealed class PgKvFixture : IAsyncLifetime
{
    public static string Image { get; } =
        Environment.GetEnvironmentVariable("PGKV_TEST_IMAGE") ?? "ghcr.io/murat7ay/pg_kv:0.1.0-pg18";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    // The concurrency tests would otherwise grow one pool to the server's
    // max_connections (100) and starve every other client of connections.
    public string ConnectionString =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { MaxPoolSize = 20 }.ToString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = PgKvDataSource.Create(ConnectionString);
        await ExecuteAsync("CREATE EXTENSION IF NOT EXISTS pg_kv");
    }

    /// <summary>A client whose keys cannot collide with other tests.</summary>
    public PgKvClient NewClient() => new(DataSource, new PgKvClientOptions { KeyPrefix = $"test:{Guid.NewGuid():N}:" });

    public async Task ExecuteAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PgKvCollection : ICollectionFixture<PgKvFixture>
{
    public const string Name = "pg_kv";
}
