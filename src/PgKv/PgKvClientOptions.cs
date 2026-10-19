namespace PgKv;

/// <summary>Options for <see cref="PgKvClient"/>.</summary>
public sealed class PgKvClientOptions
{
    /// <summary>
    /// Prepended to every key. The pg_kv keyspace is shared by all databases and
    /// applications on the server, so give each application its own prefix,
    /// e.g. <c>"orders-api:"</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "";
}
