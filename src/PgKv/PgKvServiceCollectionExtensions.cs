using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace PgKv;

/// <summary>Registers <see cref="PgKvClient"/> as a singleton.</summary>
public static class PgKvServiceCollectionExtensions
{
    /// <summary>Registers a client with its own data source for <paramref name="connectionString"/>.</summary>
    public static IServiceCollection AddPgKv(
        this IServiceCollection services, string connectionString, Action<PgKvClientOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return services.AddPgKv(_ => connectionString, configure);
    }

    /// <summary>
    /// Registers a client with its own data source; the connection string is resolved
    /// when the client is first needed (e.g. from configuration).
    /// </summary>
    public static IServiceCollection AddPgKv(
        this IServiceCollection services, Func<IServiceProvider, string> connectionString, Action<PgKvClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        services.TryAddSingleton(sp => PgKvClient.Create(connectionString(sp), Options(configure)));
        return services;
    }

    /// <summary>Registers a client on a data source owned by the application.</summary>
    public static IServiceCollection AddPgKv(
        this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> dataSource, Action<PgKvClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        services.TryAddSingleton(sp => new PgKvClient(dataSource(sp), Options(configure)));
        return services;
    }

    private static PgKvClientOptions Options(Action<PgKvClientOptions>? configure)
    {
        var options = new PgKvClientOptions();
        configure?.Invoke(options);
        return options;
    }
}
