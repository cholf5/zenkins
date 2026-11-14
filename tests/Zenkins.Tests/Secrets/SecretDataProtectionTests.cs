using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Secrets;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// M2 (F-012 review patch): pins the three reachable branches of
/// <see cref="SecretDataProtection.ResolveKeysDirectory"/>. The end-to-end key-ring tests only exercise
/// the explicit-configuration branch, so without this the documented default (<c>App_Data/keys</c> for
/// the default deployment) would be an untested claim.
/// <para>
/// Every branch is data-driven and must actually be produced by the resolver; an unknown branch name
/// fails instead of skipping, so a branch that stops being reachable turns this red.
/// </para>
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretDataProtectionTests
{
    [Theory]
    [InlineData("explicit-relative-directory")]
    [InlineData("explicit-absolute-directory")]
    [InlineData("relative-data-source-keeps-the-ring-beside-the-working-directory")]
    [InlineData("sqlite-keeps-the-ring-next-to-the-database")]
    [InlineData("postgres-falls-back-to-the-application-base-directory")]
    public void Each_resolution_branch_lands_where_the_documentation_says(string branch)
    {
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-f012-resolve-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            ZenkinsDataOptions? dataOptions = null;
            var configuration = new ConfigurationBuilder();
            var expected = string.Empty;

            switch (branch)
            {
                case "explicit-relative-directory":
                    // The configured value wins over the database location and is relative to the
                    // application base directory (the publish/install directory) - the I-003 path
                    // discipline, never the working directory or the content root.
                    dataOptions = new ZenkinsDataOptions(
                        DatabaseProvider.Sqlite,
                        $"Data Source={Path.Combine(dataDirectory, "zenkins.db")}");
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [SecretDataProtection.KeysDirectoryConfigurationKey] = "custom/keys",
                    });
                    expected = Path.Combine(AppContext.BaseDirectory, "custom", "keys");
                    break;

                case "explicit-absolute-directory":
                    // An absolute configured value is used as-is, not re-rooted under the content root.
                    dataOptions = new ZenkinsDataOptions(
                        DatabaseProvider.Sqlite,
                        $"Data Source={Path.Combine(dataDirectory, "zenkins.db")}");
                    var absoluteKeys = Path.Combine(root, "mounted-keys");
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [SecretDataProtection.KeysDirectoryConfigurationKey] = absoluteKeys,
                    });
                    expected = absoluteKeys;
                    break;

                case "relative-data-source-keeps-the-ring-beside-the-working-directory":
                    // Card Q-003 (debt handed over by F-012): the connection string's Data Source can be
                    // relative, and the resolver documents that Microsoft.Data.Sqlite's own resolution
                    // applies - relative to the process working directory, NOT the content root. The ring
                    // therefore lands next to the database file the worker data layer would open.
                    dataOptions = new ZenkinsDataOptions(DatabaseProvider.Sqlite, "Data Source=App_Data/zenkins.db");
                    expected = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "keys");
                    break;

                case "sqlite-keeps-the-ring-next-to-the-database":
                    // No configuration: the ring lives beside the SQLite file, which is App_Data/keys
                    // for the default deployment and a temporary ring for a test/dev database elsewhere.
                    var databasePath = Path.Combine(dataDirectory, "zenkins.db");
                    dataOptions = new ZenkinsDataOptions(DatabaseProvider.Sqlite, $"Data Source={databasePath}");
                    expected = Path.Combine(dataDirectory, "keys");
                    break;

                case "postgres-falls-back-to-the-application-base-directory":
                    // A server database has no local file to sit next to, so the documented default is
                    // App_Data/keys under the application base directory (the publish/install
                    // directory - card I-003).
                    dataOptions = new ZenkinsDataOptions(
                        DatabaseProvider.Postgres,
                        "Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins");
                    expected = Path.Combine(AppContext.BaseDirectory, "App_Data", "keys");
                    break;

                default:
                    Assert.Fail($"Unknown branch '{branch}': every branch must be exercised, not skipped.");
                    return;
            }

            var resolved = SecretDataProtection.ResolveKeysDirectory(
                configuration.Build(),
                dataOptions);

            Assert.Equal(Path.GetFullPath(expected), resolved);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
