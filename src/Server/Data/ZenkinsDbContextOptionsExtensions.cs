using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Zenkins.Server.Data;

/// <summary>Applies <see cref="ZenkinsDataOptions"/> to a context options builder.</summary>
public static class ZenkinsDbContextOptionsExtensions
{
    /// <summary>Configures the provider named by <paramref name="options"/>.</summary>
    public static DbContextOptionsBuilder UseZenkinsDatabase(
        this DbContextOptionsBuilder builder,
        ZenkinsDataOptions options)
    {
        Configure(builder, options);
        return builder;
    }

    /// <summary>Configures the provider named by <paramref name="options"/> for a typed builder.</summary>
    public static DbContextOptionsBuilder<TContext> UseZenkinsDatabase<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        ZenkinsDataOptions options)
        where TContext : DbContext
    {
        Configure(builder, options);
        return builder;
    }

    private static void Configure(DbContextOptionsBuilder builder, ZenkinsDataOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProvider.Sqlite:
                EnsureSqliteDirectoryExists(options.ConnectionString);

                // SQLite only enforces foreign keys when PRAGMA foreign_keys is on for the
                // connection (its default is off). Without this the OrganizationMember -> AspNetUsers
                // foreign key F-001 added would be decorative in development while PostgreSQL
                // enforces it in production. Existing connection-string keywords are preserved.
                var sqliteConnectionString = new SqliteConnectionStringBuilder(options.ConnectionString)
                {
                    ForeignKeys = true,
                }.ConnectionString;
                builder.UseSqlite(sqliteConnectionString);

                // The single migration set is generated against PostgreSQL (uuid, timestamptz,
                // varchar), because a SQLite-generated set would create TEXT columns that Npgsql
                // cannot bind Guid/DateTimeOffset to. SQLite is dynamically typed, so those declared
                // type names are harmless there - but its model then differs from the snapshot in
                // store type names alone, which EF reports as "pending model changes". The check is
                // only ignored for SQLite; for PostgreSQL it stays active and is also run explicitly
                // in the gate `dotnet ef migrations has-pending-model-changes`.
                builder.ConfigureWarnings(warnings =>
                    warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
                break;

            case DatabaseProvider.Postgres:
                builder.UseNpgsql(options.ConnectionString);
                break;

            default:
                throw new InvalidOperationException($"Unsupported database provider '{options.Provider}'.");
        }
    }

    /// <summary>
    /// SQLite creates the file but not its directory, so the default <c>App_Data</c> location has
    /// to be prepared before the first connection.
    /// </summary>
    private static void EnsureSqliteDirectoryExists(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrEmpty(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
