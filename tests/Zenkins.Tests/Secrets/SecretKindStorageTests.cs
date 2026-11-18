using System.Net;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Secrets;
using Zenkins.Shared.Secrets;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// Card F-040's kinds, end to end through the product's own create/rotate endpoints: what the ciphertext holds,
/// which fields a kind requires, and the one rule that keeps a stored secret's meaning stable - a rotation
/// replaces the value of the <b>existing</b> kind and can never switch it.
/// <para>
/// Every assertion reads the decrypted payload (<see cref="SecretsTestHost.ReadPayloadAsync"/>), not the
/// ciphertext: "it is a pair" is a claim about the shape inside the envelope, so a test that only checked the
/// <c>Kind</c> column would pass for a service that stored the text form and mislabelled it.
/// </para>
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretKindStorageTests
{
    private const string PairName = "REGISTRY";
    private const string PairUser = "deploy-bot";
    private const string PairPassword = "registry-pw-4c1f9a";
    private const string NewPairPassword = "registry-pw-rotated-77b2";

    private const string FileName = "DEPLOY_KEY";
    private const string FileContent = "-----BEGIN KEY-----\nZmlsZS1ieXRlcy0zZjhh\n-----END KEY-----\n";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pair_secret_stores_both_halves_and_neither_is_rendered_or_stored_in_the_clear()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = PairName,
                ["Kind"] = SecretKinds.UsernamePassword,
                ["Username"] = PairUser,
                ["Password"] = PairPassword,
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var row = await host.ReadSecretByNameAsync(organizationId, PairName);
        Assert.NotNull(row);
        Assert.Equal(SecretKinds.UsernamePassword, row!.Kind);

        // The envelope holds one pair document: both halves decrypt out of the same ciphertext.
        var payload = await host.ReadPayloadAsync(organizationId, PairName);
        Assert.Equal(SecretKinds.UsernamePassword, payload.Kind);
        Assert.Equal(PairUser, payload.Username);
        Assert.Equal(PairPassword, payload.Password);
        Assert.Null(payload.Value);

        // Write-only above the storage layer, unchanged by the kinds: no page and no ciphertext echoes either
        // half, and the value-bearing field of the create form is not the one a pair uses.
        Assert.DoesNotContain(PairPassword, row.Ciphertext, StringComparison.Ordinal);
        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);
        Assert.Contains(PairName, page, StringComparison.Ordinal);
        Assert.DoesNotContain(PairPassword, page, StringComparison.Ordinal);
        Assert.DoesNotContain(PairUser, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_secret_keeps_its_contents_byte_for_byte()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = FileName,
                ["Kind"] = SecretKinds.File,
                ["Value"] = FileContent,
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var payload = await host.ReadPayloadAsync(organizationId, FileName);

        Assert.Equal(SecretKinds.File, payload.Kind);

        // Untrimmed and unchanged: a key file's leading and trailing newlines are part of the material, so a
        // value that was normalised on the way in would be a different key on the way out.
        Assert.Equal(FileContent, payload.Content);
        Assert.Null(payload.Value);
    }

    [Fact]
    public async Task Rotating_a_pair_replaces_both_halves_and_keeps_its_kind()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        // An unrelated text secret, to pin that nothing here reaches across rows.
        await host.CreateSecretAsync(owner, organizationId, "UNRELATED_TEXT", "text-value-first");

        // The row is a pair before the rotation, so the rotation is judged against the pair's fields.
        var pair = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "PAIR_ROW",
                ["Kind"] = SecretKinds.UsernamePassword,
                ["Username"] = PairUser,
                ["Password"] = PairPassword,
            });
        Assert.Equal(HttpStatusCode.Redirect, pair.StatusCode);

        var row = await host.ReadSecretByNameAsync(organizationId, "PAIR_ROW");
        Assert.NotNull(row);

        // The posted kind is deliberately the wrong one: the stored kind governs, so this is a pair rotation.
        using var rotated = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{row!.Id}/rotate",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Kind"] = SecretKinds.Text,
                ["Username"] = "second-bot",
                ["Password"] = NewPairPassword,
            });
        Assert.Equal(HttpStatusCode.Redirect, rotated.StatusCode);

        var payload = await host.ReadPayloadAsync(organizationId, "PAIR_ROW");
        Assert.Equal(SecretKinds.UsernamePassword, payload.Kind);
        Assert.Equal("second-bot", payload.Username);
        Assert.Equal(NewPairPassword, payload.Password);

        // The earlier value is gone, not merely superseded: rotation replaces the ciphertext.
        var afterRotation = await host.ReadSecretByIdAsync(row.Id);
        Assert.NotNull(afterRotation);
        Assert.NotNull(afterRotation!.RotatedAt);

        // The unrelated text secret was not touched by any of this.
        var textPayload = await host.ReadPayloadAsync(organizationId, "UNRELATED_TEXT");
        Assert.Equal(SecretKinds.Text, textPayload.Kind);
        Assert.Equal("text-value-first", textPayload.Value);
    }

    [Fact]
    public async Task An_unknown_kind_is_a_field_error_and_nothing_is_stored()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "MISLABELLED",
                ["Kind"] = "keyValue",
                ["Value"] = "whatever",
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "error=" + SecretErrorCodes.KindInvalid,
            response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Null(await host.ReadSecretByNameAsync(organizationId, "MISLABELLED"));
    }

    [Fact]
    public async Task A_pair_without_its_password_is_a_field_error_and_nothing_is_stored()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "HALF_PAIR",
                ["Kind"] = SecretKinds.UsernamePassword,
                ["Username"] = PairUser,
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "error=" + SecretErrorCodes.PasswordRequired,
            response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Null(await host.ReadSecretByNameAsync(organizationId, "HALF_PAIR"));
    }

    [Fact]
    public async Task A_file_secret_over_the_bound_is_refused_at_save_time()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        // One byte over the bound in UTF-8: the worker would refuse to materialise it, so the server must not
        // accept it - a stored secret that cannot be delivered is a failure deferred to the worst moment.
        var oversized = new string('k', SecretFileLimits.MaxBytes + 1);

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "TOO_BIG_KEY",
                ["Kind"] = SecretKinds.File,
                ["Value"] = oversized,
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "error=" + SecretErrorCodes.FileTooLarge,
            response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Null(await host.ReadSecretByNameAsync(organizationId, "TOO_BIG_KEY"));
    }

    [Fact]
    public async Task A_secret_created_without_a_kind_is_a_text_secret()
    {
        // AC-1's "text stays today's behaviour": the shape every caller written before the kinds submits is a
        // name and a value with <b>no kind at all</b>, and the fallback belongs to the service rather than to the
        // page - a form always posts a kind, so a fact that only drove the form would never exercise this path.
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        var ownerUserId = await host.ReadUserIdAsync(SecretsTestHost.OwnerEmail);

        var created = await host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = organizationId;
            return await services.GetRequiredService<SecretService>()
                .CreateAsync(ownerUserId, new SecretInput("PLAIN_ROW", "plain-value-7a2"));
        });
        Assert.Equal(SecretMutationStatus.Success, created.Status);

        // Stored as a text secret, and it decrypts to the bare string an F-015-era reader also wrote.
        var row = await host.ReadSecretByNameAsync(organizationId, "PLAIN_ROW");
        Assert.NotNull(row);
        Assert.Equal(SecretKinds.Text, row!.Kind);

        var payload = await host.ReadPayloadAsync(organizationId, "PLAIN_ROW");
        Assert.Equal(SecretKinds.Text, payload.Kind);
        Assert.Equal("plain-value-7a2", payload.Value);
    }

    [Fact]
    public async Task An_old_row_reads_as_a_text_secret()
    {
        // Card F-040's backfill, asserted where it is observable without a migration: the entity's own default
        // and the column's database default are both `text`, so a row that never met the kind column - and a
        // row inserted by an older build - is a text secret rather than nothing. The migration half of the same
        // claim is SecretKindMigrationTests.
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, organizationId, "LEGACY_SHAPE", "legacy-plain-value");

        var payload = await host.ReadPayloadAsync(organizationId, "LEGACY_SHAPE");

        Assert.Equal(SecretKinds.Text, payload.Kind);
        Assert.Equal("legacy-plain-value", payload.Value);

        // The kinds' arrival did not silently re-label it: the stored row says text, and the payload decodes as
        // the bare string an F-015-era reader also wrote.
        var row = await host.ReadSecretByNameAsync(organizationId, "LEGACY_SHAPE");
        Assert.Equal(SecretKinds.Text, row!.Kind);
    }
}
