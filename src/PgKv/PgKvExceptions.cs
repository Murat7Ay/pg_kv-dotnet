using Npgsql;

namespace PgKv;

/// <summary>
/// An error raised by the pg_kv extension. <see cref="SqlState"/> carries the
/// PostgreSQL error code; the subclasses cover the cases callers usually handle.
/// </summary>
public class PgKvException : Exception
{
    /// <summary>Creates the exception from the server error it translates.</summary>
    public PgKvException(string message, PostgresException inner)
        : base(message, inner) => SqlState = inner.SqlState;

    /// <summary>The PostgreSQL SQLSTATE, e.g. <c>42809</c> for WRONGTYPE.</summary>
    public string SqlState { get; }

    /// <summary>The server error this exception was translated from.</summary>
    public PostgresException PostgresException => (PostgresException)InnerException!;

    /// <summary>Maps a pg_kv server error to its exception type; <see langword="null"/> for other errors.</summary>
    internal static PgKvException? Translate(PostgresException e) => e.SqlState switch
    {
        PostgresErrorCodes.WrongObjectType => new PgKvWrongTypeException(e),
        PostgresErrorCodes.ProgramLimitExceeded => new PgKvLimitExceededException(e),
        PostgresErrorCodes.ObjectNotInPrerequisiteState => new PgKvNotAvailableException(e.MessageText, e),
        PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.UndefinedFunction
            when e.MessageText.Contains("kv", StringComparison.Ordinal) => new PgKvNotAvailableException(
                "The pg_kv extension is not installed in this database. Run CREATE EXTENSION pg_kv "
                + "on a server that has pg_kv in shared_preload_libraries.", e),
        PostgresErrorCodes.InsufficientPrivilege => new PgKvException(
            e.MessageText + ". The role needs USAGE on schema kv (GRANT USAGE ON SCHEMA kv TO <role>).", e),
        PostgresErrorCodes.InvalidParameterValue
            or PostgresErrorCodes.InvalidTextRepresentation
            or PostgresErrorCodes.NumericValueOutOfRange => new PgKvException(e.MessageText, e),
        _ => null,
    };
}

/// <summary>
/// The key holds another kind of value, e.g. reading a sorted set as a string
/// (SQLSTATE <c>42809</c>, Redis' WRONGTYPE).
/// </summary>
public sealed class PgKvWrongTypeException(PostgresException inner) : PgKvException(inner.MessageText, inner);

/// <summary>
/// A configured limit was hit: key, value or member too large, or the sorted-set
/// member pool is full (SQLSTATE <c>54000</c>).
/// </summary>
public sealed class PgKvLimitExceededException(PostgresException inner) : PgKvException(inner.MessageText, inner);

/// <summary>
/// pg_kv cannot be used on this server or database: the extension is not installed,
/// not preloaded, or sorted sets are disabled (SQLSTATE <c>55000</c>, <c>3F000</c> or <c>42883</c>).
/// </summary>
public sealed class PgKvNotAvailableException(string message, PostgresException inner) : PgKvException(message, inner);
