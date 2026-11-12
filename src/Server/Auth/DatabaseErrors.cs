using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zenkins.Server.Auth;

/// <summary>
/// Classifies database constraint failures without leaking provider details into the callers that
/// need a friendly outcome. Only the "unique violation" case is recognised; every other
/// <see cref="DbUpdateException"/> stays an error.
/// </summary>
internal static class DatabaseErrors
{
    /// <summary>
    /// Whether <paramref name="exception"/> is a unique-constraint violation, optionally restricted
    /// to one table/index name (so "the bootstrap marker won the race" cannot be confused with an
    /// unrelated duplicate).
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException exception, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.InnerException?.Message ?? exception.Message;
        var isUnique = exception.InnerException switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == 19, // SQLITE_CONSTRAINT
            PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
            _ => message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase),
        };

        return isUnique
            && (tableName is null || message.Contains(tableName, StringComparison.OrdinalIgnoreCase));
    }
}
