using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zenkins.Server.Artifacts;
using Zenkins.Server.Data;
using Zenkins.Server.Secrets;
using Zenkins.Shared;

namespace Zenkins.Server.Setup;

/// <summary>Whether the database is usable by the setup that is about to write to it.</summary>
public enum SetupSchemaState
{
    /// <summary>Connected, and every migration is applied.</summary>
    Ready = 0,

    /// <summary>Connected, but the schema is behind the binaries (Production never migrates on startup).</summary>
    MigrationsPending = 1,

    /// <summary>The connection or the configuration failed; the message is in <see cref="SetupFacts.Problem"/>.</summary>
    Unreachable = 2,
}

/// <summary>
/// What the setup wizard shows about this deployment (card F-044). The facts are read from the same
/// options the rest of the server uses, and the schema state is a real query - not a restatement of
/// the startup assertions, which would always be green because the process would not be running
/// otherwise.
/// </summary>
/// <param name="Version">The running build's product version.</param>
/// <param name="Provider">SQLite or PostgreSQL, as configured.</param>
/// <param name="DatabaseTarget">
/// The SQLite file, or <c>host / database</c> for PostgreSQL. Never the connection string: it can
/// carry a password, and this page is rendered before anyone has signed in.
/// </param>
/// <param name="KeyRingDirectory">The Data Protection directory that must be backed up with the database.</param>
/// <param name="ArtifactRoot">The artifact store that must be backed up with the database.</param>
/// <param name="SchemaState">The real schema answer.</param>
/// <param name="PendingMigrations">How many migrations the database is behind; <c>0</c> unless the state says otherwise.</param>
/// <param name="Problem">Why the database could not be used, when it could not be.</param>
public sealed record SetupFacts(
    string Version,
    DatabaseProvider Provider,
    string DatabaseTarget,
    string KeyRingDirectory,
    string ArtifactRoot,
    SetupSchemaState SchemaState,
    int PendingMigrations,
    string? Problem);

/// <summary>
/// Reads the facts and the schema state the welcome page shows (F-044).
/// <para>
/// A failure is a <em>result</em>, not an exception: on a fresh production database the
/// <c>BootstrapMarkers</c> table may not exist yet, and the operator needs the page that explains how
/// to create it instead of an exception page. Only the two exception families a database surface can
/// raise for "unreachable or misconfigured" are caught (<see cref="DbException"/> and the
/// configuration errors <see cref="InvalidOperationException"/>); a bug still surfaces.
/// </para>
/// </summary>
public sealed class SetupReadiness(
    IConfiguration configuration,
    ZenkinsDataOptions dataOptions,
    ArtifactOptions artifactOptions,
    IDbContextFactory<ZenkinsDbContext> contextFactory)
{
    /// <summary>The facts and the live schema state.</summary>
    public async Task<SetupFacts> GetAsync(CancellationToken cancellationToken = default)
    {
        var baseFacts = new SetupFacts(
            ProductVersion.Current,
            dataOptions.Provider,
            DescribeTarget(dataOptions),
            SecretDataProtection.ResolveKeysDirectory(configuration, dataOptions),
            artifactOptions.RootPath,
            SetupSchemaState.Ready,
            PendingMigrations: 0,
            Problem: null);

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await context.Database.CanConnectAsync(cancellationToken))
            {
                return baseFacts with
                {
                    SchemaState = SetupSchemaState.Unreachable,
                    Problem = "The database refused the connection check.",
                };
            }

            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            return pending.Count == 0
                ? baseFacts
                : baseFacts with
                {
                    SchemaState = SetupSchemaState.MigrationsPending,
                    PendingMigrations = pending.Count,
                };
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return baseFacts with
            {
                SchemaState = SetupSchemaState.Unreachable,
                Problem = exception.Message,
            };
        }
    }

    /// <summary>
    /// The database target as an operator recognizes it, with no credentials. A relative SQLite
    /// <c>Data Source</c> is resolved the way the connection itself resolves it (from the working
    /// directory), so the path the page prints is the file that is actually open.
    /// </summary>
    private static string DescribeTarget(ZenkinsDataOptions options)
    {
        if (options.Provider == DatabaseProvider.Sqlite)
        {
            var dataSource = new SqliteConnectionStringBuilder(options.ConnectionString).DataSource;
            if (string.IsNullOrWhiteSpace(dataSource)
                || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return "(in-memory SQLite)";
            }

            return Path.GetFullPath(dataSource);
        }

        var postgres = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        var database = string.IsNullOrWhiteSpace(postgres.Database) ? "(default)" : postgres.Database;
        return $"{postgres.Host} / {database}";
    }
}
