using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Secrets;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// TC-003 (card F-012) at the storage layer: the value is encrypted at rest, the entity stores no
/// plaintext and no value hash, rotation replaces the ciphertext, and the only read path is
/// tenant-scoped.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretStorageTests
{
    private const string Name = "STORAGE_TOKEN";
    private const string Value = "storage-secret-value-a1c9d3";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_plaintext_is_never_written_to_the_database()
    {
        const string Marker = "plaintext-marker-0b6e1f7a";
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        var secret = await host.CreateSecretAsync(owner, organizationId, Name, Marker);

        Assert.DoesNotContain(Marker, secret.Ciphertext, StringComparison.Ordinal);
        Assert.Equal(Marker, await host.ReadDecryptedValueAsync(organizationId, Name));

        // The strongest form of "encrypted at rest": the value is not in any database file, including
        // the write-ahead log. A future change that stores the plaintext (or a reversible encoding of
        // it) fails here even if the row's Ciphertext column still looks encrypted.
        foreach (var path in DatabaseFiles(host.Host.DatabasePath))
        {
            var bytes = await File.ReadAllBytesAsync(path, CancellationToken);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain(Marker, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_entity_stores_no_plaintext_and_no_value_hash()
    {
        // F-012's AC is a property of the model: only ciphertext plus timestamps plus audit ids - a later card
        // cannot add a convenience Plaintext/ValueHash column without failing this. D-006 added one audit id
        // (who last rotated the secret), which is an identity, never a value: the forbidden-name guard below
        // still covers it.
        var forbidden = new[] { "Value", "Plaintext", "Secret", "PlaintextHash", "ValueHash", "Hash" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var properties = typeof(Secret).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(properties, forbidden.Contains);
        Assert.Equal(
            new[]
            {
                "Ciphertext", "CreatedAt", "CreatedByUserId", "Id", "Name", "OrganizationId",
                "RotatedAt", "RotatedByUserId", "UpdatedAt",
            },
            properties.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Rotation_replaces_the_ciphertext_and_the_previous_blob_is_gone()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        var created = await host.CreateSecretAsync(owner, organizationId, Name, Value);
        var before = created.Ciphertext;

        using (var rotate = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{created.Id}/rotate",
            new Dictionary<string, string> { ["Value"] = "rotation-replacement-6dd2" }))
        {
            Assert.Equal(302, (int)rotate.StatusCode);
        }

        var rotated = Assert.IsType<Secret>(await host.ReadSecretByIdAsync(created.Id));
        Assert.NotEqual(before, rotated.Ciphertext);
        Assert.NotNull(rotated.RotatedAt);

        // D-006: the rotation is attributed to the member who performed it (an id, never the value).
        Assert.Equal(await host.ReadUserIdAsync(SecretsTestHost.OwnerEmail), rotated.RotatedByUserId);
        Assert.Equal("rotation-replacement-6dd2", await host.ReadDecryptedValueAsync(organizationId, Name));

        // The pre-rotation blob is gone from the store: no row carries it any more, so "decrypt the old
        // ciphertext" is not a reachable operation rather than a rejected one. (SQLite does not zero
        // freed pages, so the raw file is not the right oracle for "no longer stored".)
        var rows = await host.ReadSecretsAsync(organizationId);
        Assert.DoesNotContain(rows, row => string.Equals(row.Ciphertext, before, StringComparison.Ordinal));
        Assert.Single(rows);
    }

    [Fact]
    public async Task The_internal_read_path_is_scoped_to_the_active_tenant()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        var foreignOrganizationId = await host.CreateForeignOrganizationAsync();
        using var owner = await host.SignInOwnerAsync();

        await host.CreateSecretAsync(owner, organizationId, Name, Value);

        // The name exists in A and nowhere else: the internal seam must not resolve it in B.
        Assert.Equal(Value, await host.ReadDecryptedValueAsync(organizationId, Name));
        Assert.Null(await host.ReadDecryptedValueAsync(foreignOrganizationId, Name));
    }

    [Fact]
    public async Task The_audit_columns_are_recorded_and_the_name_is_immutable()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        var ownerUserId = (await host.Organization.ReadUserAsync(SecretsTestHost.OwnerEmail)).Id;
        using var owner = await host.SignInOwnerAsync();

        var created = await host.CreateSecretAsync(owner, organizationId, Name, Value);

        Assert.Equal(ownerUserId, created.CreatedByUserId);
        Assert.Null(created.RotatedAt);
        Assert.NotEqual(default, created.CreatedAt);
        Assert.NotEqual(default, created.UpdatedAt);

        // A rotate cannot rename: the form carries no name, the route id picks the row.
        using (var rotate = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{created.Id}/rotate",
            new Dictionary<string, string> { ["Name"] = "RENAMED_TOKEN", ["Value"] = "renamed-value" }))
        {
            Assert.Equal(302, (int)rotate.StatusCode);
        }

        var rotated = Assert.IsType<Secret>(await host.ReadSecretByIdAsync(created.Id));
        Assert.Equal(Name, rotated.Name);
        Assert.Null(await host.ReadSecretByNameAsync(organizationId, "RENAMED_TOKEN"));
    }

    [Fact]
    public async Task The_service_read_path_is_owner_only_and_case_sensitive()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();
        using var member = await host.CreateMemberAsync();

        await host.CreateSecretAsync(owner, organizationId, "TOKEN", "upper-value");
        await host.CreateSecretAsync(owner, organizationId, "token", "lower-value");

        // A name is unique per organization and case-sensitive: both rows coexist.
        Assert.Equal(2, (await host.ReadSecretsAsync(organizationId)).Count);

        // The Owner reads the list; the Member is refused at the service, not only at the page.
        var ownerRead = await host.ReadSecretsAsAsync(
            organizationId,
            await host.ReadUserIdAsync(SecretsTestHost.OwnerEmail));
        Assert.Equal(SecretMutationStatus.Success, ownerRead.Status);
        Assert.Equal(2, ownerRead.Secrets.Count);

        var memberRead = await host.ReadSecretsAsAsync(
            organizationId,
            await host.ReadUserIdAsync(SecretsTestHost.MemberEmail));
        Assert.Equal(SecretMutationStatus.Forbidden, memberRead.Status);
        Assert.Empty(memberRead.Secrets);
    }

    /// <summary>The SQLite database file plus its journal/WAL siblings, when present.</summary>
    private static IEnumerable<string> DatabaseFiles(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            yield return databasePath;
        }

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }
}
