using Npgsql;

namespace PgKv;

/// <summary>Creates data sources suited to pg_kv.</summary>
public static class PgKvDataSource
{
    /// <summary>
    /// Builds a data source for pg_kv. Connections never enlist in an ambient
    /// <c>TransactionScope</c> (pg_kv writes are not transactional anyway), and
    /// statements are auto-prepared because the same few run over and over.
    /// </summary>
    public static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.Enlist = false;
        if (builder.ConnectionStringBuilder.MaxAutoPrepare == 0)
        {
            builder.ConnectionStringBuilder.MaxAutoPrepare = 32;
        }
        return builder.Build();
    }
}
