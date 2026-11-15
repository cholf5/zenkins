using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Zenkins.Server.Data;

namespace Zenkins.Server.Secrets;

/// <summary>
/// The Data Protection wiring the secret store depends on (card F-012). Constants, not literals
/// scattered across <c>Program</c> and the tests, so the configuration key and the default location
/// cannot drift apart.
/// </summary>
public static class SecretDataProtection
{
    /// <summary>
    /// Application name for the key ring. One name per deployment: changing it derives different keys
    /// and orphans every existing ciphertext.
    /// </summary>
    public const string ApplicationName = "Zenkins";

    /// <summary>
    /// Configuration key for the persisted key-ring directory (<c>DataProtection:KeysDirectory</c>).
    /// Absolute, or relative to <see cref="AppContext.BaseDirectory"/> (the publish/install directory —
    /// the I-003 path discipline, never the working directory).
    /// </summary>
    public const string KeysDirectoryConfigurationKey = "DataProtection:KeysDirectory";

    /// <summary>
    /// Default key-ring directory, relative to <see cref="AppContext.BaseDirectory"/>: next to the
    /// published database. Must be backed up with the database - losing it means every secret is
    /// re-entered, not restored.
    /// </summary>
    public const string DefaultKeysDirectoryRelativePath = "App_Data/keys";

    /// <summary>
    /// Resolves the persisted key-ring directory.
    /// <para>
    /// Precedence: the configured value (absolute, or relative to <see cref="AppContext.BaseDirectory"/>),
    /// then <c>keys/</c> next to the SQLite database file (so a backup of <c>App_Data</c> carries both
    /// the ciphertexts and the ring that reads them - and a test host with a temporary database
    /// automatically gets a temporary ring), then <see cref="DefaultKeysDirectoryRelativePath"/> under
    /// the application base directory for a server database, which has no local file to sit next to.
    /// Relative values never resolve from the working directory or the content root: a packaged server
    /// runs with an arbitrary working directory, and its key ring follows its binaries (I-003).
    /// </para>
    /// </summary>
    public static string ResolveKeysDirectory(
        IConfiguration configuration,
        ZenkinsDataOptions? dataOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[KeysDirectoryConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured, AppContext.BaseDirectory);
        }

        if (dataOptions is { Provider: DatabaseProvider.Sqlite } sqlite)
        {
            // Same resolution Microsoft.Data.Sqlite applies: a relative Data Source is relative to the
            // process working directory, not the content root.
            var dataSource = new SqliteConnectionStringBuilder(sqlite.ConnectionString).DataSource;
            if (!string.IsNullOrWhiteSpace(dataSource)
                && !string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
                if (!string.IsNullOrEmpty(directory))
                {
                    return Path.Combine(directory, "keys");
                }
            }
        }

        // The relative default is written with forward slashes so every document quotes one portable
        // path, but the runtime directory must be joined segment-wise: Path.Combine embeds a forward
        // slash verbatim on Windows ("App_Data/keys" inside a backslash path), which is not the shape
        // any other surface reports the directory in.
        return Path.Combine([AppContext.BaseDirectory, .. DefaultKeysDirectoryRelativePath.Split('/')]);
    }
}
