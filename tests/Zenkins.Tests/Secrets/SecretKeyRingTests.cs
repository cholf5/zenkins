using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Secrets;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// TC-003 (card F-012) end to end: the key ring is pinned to the configured directory, asserted
/// writable at startup, and it is what makes a stored secret readable after a restart. A restart with
/// a different ring must fail to decrypt - losing the ring means re-entering every secret.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretKeyRingTests
{
    private const string OwnerEmail = "alice@example.test";
    private const string Name = "RESTART_TOKEN";
    private const string Value = "restart-secret-value-5c1e";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_key_ring_is_persisted_under_the_configured_directory()
    {
        using var workspace = new Workspace();
        using var host = CreateHost(workspace.DatabasePath, workspace.KeysDirectory);

        await BootstrapOwnerAsync(host);
        using var owner = await SignInOwnerAsync(host);
        await CreateSecretAsync(owner, Name, Value);

        Assert.True(Directory.Exists(workspace.KeysDirectory));
        Assert.NotEmpty(KeyFiles(workspace.KeysDirectory));
    }

    [Fact]
    public async Task A_restart_with_the_same_data_and_key_ring_reads_the_secret()
    {
        using var workspace = new Workspace();

        using (var firstRun = CreateHost(workspace.DatabasePath, workspace.KeysDirectory))
        {
            await BootstrapOwnerAsync(firstRun);
            using var owner = await SignInOwnerAsync(firstRun);
            await CreateSecretAsync(owner, Name, Value);
        }

        // Same database file, same key-ring directory: this is a process restart.
        using var secondRun = CreateHost(workspace.DatabasePath, workspace.KeysDirectory);
        using var restarted = await SignInOwnerAsync(secondRun);

        var page = await AuthTestHost.GetStringAsync(restarted, SecretEndpoints.ListPath);
        Assert.Contains(Name, page, StringComparison.Ordinal);
        Assert.DoesNotContain(Value, page, StringComparison.Ordinal);

        // The stored ciphertext decrypts with the restarted host's own protector: the keys persisted.
        var ciphertext = await ReadCiphertextAsync(secondRun, Name);
        Assert.Equal(Value, UnprotectWithHost(secondRun, ciphertext));
    }

    [Fact]
    public async Task A_restart_with_a_different_key_ring_cannot_decrypt_the_secret()
    {
        using var workspace = new Workspace();
        using var foreignRing = new Workspace();

        using (var firstRun = CreateHost(workspace.DatabasePath, workspace.KeysDirectory))
        {
            await BootstrapOwnerAsync(firstRun);
            using var owner = await SignInOwnerAsync(firstRun);
            await CreateSecretAsync(owner, Name, Value);
        }

        // Same database, a different ring: the page still lists the name (it never decrypts), but the
        // value is unrecoverable - the failure is a cryptographic exception, not a wrong plaintext.
        using var secondRun = CreateHost(workspace.DatabasePath, foreignRing.KeysDirectory);
        using var restarted = await SignInOwnerAsync(secondRun);
        var page = await AuthTestHost.GetStringAsync(restarted, SecretEndpoints.ListPath);
        Assert.Contains(Name, page, StringComparison.Ordinal);

        var ciphertext = await ReadCiphertextAsync(secondRun, Name);
        Assert.Throws<CryptographicException>(() => UnprotectWithHost(secondRun, ciphertext));
    }

    [Fact]
    public async Task An_unwritable_key_ring_directory_fails_startup_with_a_clear_message()
    {
        using var workspace = new Workspace();
        var blockingFile = Path.Combine(workspace.Root, "keys-is-a-file");
        await File.WriteAllTextAsync(blockingFile, "not a directory", CancellationToken);

        // The startup assertion runs before the host serves any request; a host over a file path must
        // fail with the key-ring message instead of starting and failing at the first decryption.
        var exception = Record.Exception(() =>
        {
            using var host = CreateHost(workspace.DatabasePath, blockingFile);
            using var client = host.CreateClient();
            client.GetAsync("/", CancellationToken).GetAwaiter().GetResult();
        });

        Assert.NotNull(exception);
        var text = exception.ToString();
        Assert.Contains("key ring", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(blockingFile, text, StringComparison.Ordinal);
    }

    private static AuthTestHost CreateHost(string databasePath, string keysDirectory) =>
        new(
            configureTestServices: services =>
            {
                services.RemoveAll<ZenkinsDataOptions>();
                services.AddSingleton(new ZenkinsDataOptions(
                    DatabaseProvider.Sqlite,
                    $"Data Source={databasePath}"));
            },
            configureAppConfiguration: configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [SecretDataProtection.KeysDirectoryConfigurationKey] = keysDirectory,
                }));

    private static async Task BootstrapOwnerAsync(AuthTestHost host)
    {
        using var client = host.CreateClient();
        var response = await AuthTestHost.RegisterAsync(client, OwnerEmail);
        Assert.Equal(302, (int)response.StatusCode);
    }

    private static async Task<HttpClient> SignInOwnerAsync(AuthTestHost host)
    {
        var client = host.CreateClient();
        try
        {
            var response = await AuthTestHost.LoginAsync(client, OwnerEmail);
            Assert.Equal(302, (int)response.StatusCode);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task CreateSecretAsync(HttpClient owner, string name, string value)
    {
        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);
        var token = OrganizationTestHost.ExtractAntiforgeryMetaToken(page);

        using var response = await owner.PostAsync(
            "/secrets/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Value"] = value,
                ["__RequestVerificationToken"] = token,
            }),
            CancellationToken);

        Assert.Equal(302, (int)response.StatusCode);
    }

    private static async Task<string> ReadCiphertextAsync(AuthTestHost host, string name) =>
        await host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            using var scope = systemDataScope.BeginScope();
            var secret = await context.SystemQueryAsync<Secret>(
                candidate => candidate.Name == name,
                CancellationToken);
            return secret.Single().Ciphertext;
        });

    /// <summary>Decrypts with the host's own configured protector (its persisted ring).</summary>
    private static string UnprotectWithHost(AuthTestHost host, string ciphertext)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SecretProtector>().Unprotect(ciphertext);
    }

    private static string[] KeyFiles(string keysDirectory) =>
        Directory.Exists(keysDirectory) ? Directory.GetFiles(keysDirectory, "key-*.xml") : [];

    /// <summary>A temporary database + key-ring root; removing it removes both.</summary>
    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"zenkins-f012-ring-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "zenkins.db");
            KeysDirectory = Path.Combine(Root, "keys");
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public string KeysDirectory { get; }

        public void Dispose()
        {
            // SQLite pools connections; the hosts are disposed before this workspace (using order).
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
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
