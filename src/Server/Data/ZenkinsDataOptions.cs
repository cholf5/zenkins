namespace Zenkins.Server.Data;

/// <summary>Supported EF Core providers (PRD §7.1: SQLite for development, PostgreSQL for production).</summary>
public enum DatabaseProvider
{
    Sqlite = 0,
    Postgres = 1,
}

/// <summary>
/// Provider selection read from configuration instead of compile-time <c>#if</c>, so the same
/// binary serves both databases.
/// </summary>
/// <param name="Provider">Selected provider.</param>
/// <param name="ConnectionString">Provider-specific connection string.</param>
public sealed record ZenkinsDataOptions(DatabaseProvider Provider, string ConnectionString)
{
    /// <summary>Configuration key for the provider name (values: <c>Sqlite</c>, <c>Postgres</c>).</summary>
    public const string ProviderConfigurationKey = "Database:Provider";

    /// <summary>Configuration key for the connection string.</summary>
    public const string ConnectionStringConfigurationKey = "ConnectionStrings:Default";

    /// <summary>
    /// Reads the provider and connection string. When no connection string is configured and the
    /// provider is SQLite, the database defaults to <c>App_Data/zenkins.db</c>.
    /// <para>
    /// <b>I-003 path discipline:</b> a relative default resolves against
    /// <see cref="AppContext.BaseDirectory"/> — the publish/install directory — and never the process
    /// working directory or the content root: a service-packaged server (Windows Service, systemd,
    /// launchd) runs with an arbitrary working directory, and its data must land beside its own
    /// binaries regardless of who started it. <paramref name="relativePathBase"/> exists only for the
    /// design-time EF factory, which deliberately targets the development source tree; runtime callers
    /// pass nothing. <c>Assembly.Location</c> is banned everywhere (IL3000 under single-file, which
    /// I-004 inherits).
    /// </para>
    /// </summary>
    public static ZenkinsDataOptions FromConfiguration(IConfiguration configuration, string? relativePathBase = null)
    {
        var providerText = configuration[ProviderConfigurationKey];
        var provider = string.IsNullOrWhiteSpace(providerText)
            ? DatabaseProvider.Sqlite
            : Enum.TryParse<DatabaseProvider>(providerText, ignoreCase: true, out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"Unsupported {ProviderConfigurationKey} value '{providerText}'. Supported values: "
                    + $"{nameof(DatabaseProvider.Sqlite)}, {nameof(DatabaseProvider.Postgres)}.");

        var connectionString = configuration[ConnectionStringConfigurationKey];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (provider != DatabaseProvider.Sqlite)
            {
                throw new InvalidOperationException(
                    $"{ConnectionStringConfigurationKey} is required when {ProviderConfigurationKey} is "
                    + $"'{nameof(DatabaseProvider.Postgres)}'.");
            }

            connectionString = $"Data Source={Path.Combine(
                relativePathBase ?? AppContext.BaseDirectory,
                "App_Data",
                "zenkins.db")}";
        }

        return new ZenkinsDataOptions(provider, connectionString);
    }
}
