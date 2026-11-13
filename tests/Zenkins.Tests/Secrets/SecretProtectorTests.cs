using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Secrets;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// TC-003 (card F-012): encryption is purpose-bound and the key ring is persisted, so a stored
/// ciphertext survives a restart and cannot be decrypted under another purpose or another ring.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretProtectorTests
{
    [Fact]
    public void A_value_round_trips_through_the_purpose_bound_protector()
    {
        var keysDirectory = CreateKeysDirectory();
        try
        {
            using var provider = CreateDataProtectionProvider(keysDirectory);
            var protector = new SecretProtector(DataProtection(provider));

            var ciphertext = protector.Protect("hunter2-correct-horse");

            Assert.NotEqual("hunter2-correct-horse", ciphertext);
            Assert.DoesNotContain("hunter2-correct-horse", ciphertext, StringComparison.Ordinal);
            Assert.Equal("hunter2-correct-horse", protector.Unprotect(ciphertext));
        }
        finally
        {
            DeleteKeysDirectory(keysDirectory);
        }
    }

    [Fact]
    public void A_ciphertext_from_another_purpose_cannot_be_decrypted()
    {
        var keysDirectory = CreateKeysDirectory();
        try
        {
            using var provider = CreateDataProtectionProvider(keysDirectory);
            var secrets = new SecretProtector(DataProtection(provider));

            // The other purpose in this application is F-008's log-subscription ticket. A blob from it
            // must not be accepted as a secret value, and vice versa.
            var otherPurpose = DataProtection(provider).CreateProtector("Zenkins.RunLogSubscription.v1");
            var ticketLikeBlob = otherPurpose.Protect("secret-value");

            Assert.Throws<CryptographicException>(() => secrets.Unprotect(ticketLikeBlob));

            var secretBlob = secrets.Protect("secret-value");
            Assert.Throws<CryptographicException>(() => otherPurpose.Unprotect(secretBlob));
        }
        finally
        {
            DeleteKeysDirectory(keysDirectory);
        }
    }

    [Fact]
    public void The_key_ring_persists_across_a_restart()
    {
        var keysDirectory = CreateKeysDirectory();
        try
        {
            string ciphertext;
            using (var firstRun = CreateDataProtectionProvider(keysDirectory))
            {
                ciphertext = new SecretProtector(DataProtection(firstRun)).Protect("survives-a-restart");
            }

            // A new provider over the same directory is what a process restart looks like: the keys
            // were persisted, so the ciphertext is still readable.
            using var secondRun = CreateDataProtectionProvider(keysDirectory);
            Assert.Equal("survives-a-restart", new SecretProtector(DataProtection(secondRun)).Unprotect(ciphertext));
        }
        finally
        {
            DeleteKeysDirectory(keysDirectory);
        }
    }

    [Fact]
    public void A_different_key_ring_cannot_decrypt()
    {
        var firstKeys = CreateKeysDirectory();
        var secondKeys = CreateKeysDirectory();
        try
        {
            string ciphertext;
            using (var firstRun = CreateDataProtectionProvider(firstKeys))
            {
                ciphertext = new SecretProtector(DataProtection(firstRun)).Protect("ring-bound");
            }

            using var foreignRing = CreateDataProtectionProvider(secondKeys);
            Assert.Throws<CryptographicException>(
                () => new SecretProtector(DataProtection(foreignRing)).Unprotect(ciphertext));
        }
        finally
        {
            DeleteKeysDirectory(firstKeys);
            DeleteKeysDirectory(secondKeys);
        }
    }

    private static IDataProtectionProvider DataProtection(IServiceProvider services) =>
        services.GetRequiredService<IDataProtectionProvider>();

    private static ServiceProvider CreateDataProtectionProvider(string keysDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddDataProtection()
            .SetApplicationName(SecretDataProtection.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
        return services.BuildServiceProvider();
    }

    private static string CreateKeysDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zenkins-f012-protector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteKeysDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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
