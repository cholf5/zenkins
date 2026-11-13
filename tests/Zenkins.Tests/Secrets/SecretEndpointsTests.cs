using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Secrets;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// TC-001 (card F-012) through the real authenticated HTTP pipeline: counters, antiforgery and the
/// form endpoints. Every rejection is additionally verified against the database, and every response
/// is checked for the plaintext - a value must never come back out of the server.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretEndpointsTests
{
    private const string Name = "MY_API_TOKEN";
    private const string FirstValue = "first-secret-value-9f2c";
    private const string RotatedValue = "rotated-secret-value-4b7e";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Owner_create_rotate_and_delete_never_return_the_value()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        var ownerUserId = (await host.Organization.ReadUserAsync(SecretsTestHost.OwnerEmail)).Id;

        // --- create ---------------------------------------------------------------------------
        using (var create = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string> { ["Name"] = Name, ["Value"] = FirstValue }))
        {
            MembershipAuthorizationProbes.AssertRedirect(create, "/secrets?notice=created");
            await AssertCarriesNoPlaintextAsync(create, FirstValue);
        }

        var created = Assert.IsType<Secret>(
            await host.ReadSecretByNameAsync(organizationId, Name));
        Assert.Equal(organizationId, created.OrganizationId);
        Assert.Equal(ownerUserId, created.CreatedByUserId);
        Assert.NotEqual(FirstValue, created.Ciphertext);
        Assert.DoesNotContain(FirstValue, created.Ciphertext, StringComparison.Ordinal);
        Assert.Null(created.RotatedAt);
        Assert.NotEqual(default, created.CreatedAt);
        Assert.NotEqual(default, created.UpdatedAt);

        // --- the list page: names and timestamps only ------------------------------------------
        var list = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);
        Assert.Contains(Name, list, StringComparison.Ordinal);
        Assert.Contains("Never", list, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstValue, list, StringComparison.Ordinal);

        // The only read path in the product decrypts what was stored.
        Assert.Equal(FirstValue, await host.ReadDecryptedValueAsync(organizationId, Name));

        // --- rotate ---------------------------------------------------------------------------
        using (var rotate = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{created.Id}/rotate",
            new Dictionary<string, string> { ["Value"] = RotatedValue }))
        {
            MembershipAuthorizationProbes.AssertRedirect(rotate, "/secrets?notice=rotated");
            await AssertCarriesNoPlaintextAsync(rotate, RotatedValue, FirstValue);
        }

        var rotated = Assert.IsType<Secret>(
            await host.ReadSecretByNameAsync(organizationId, Name));
        // The ciphertext really changed: the column was replaced, not merely re-stamped.
        Assert.NotEqual(created.Ciphertext, rotated.Ciphertext);
        Assert.NotNull(rotated.RotatedAt);
        Assert.Equal(created.Name, rotated.Name);
        Assert.Equal(created.CreatedAt, rotated.CreatedAt);
        Assert.Equal(RotatedValue, await host.ReadDecryptedValueAsync(organizationId, Name));

        // --- delete ---------------------------------------------------------------------------
        using (var delete = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{created.Id}/delete",
            new Dictionary<string, string>()))
        {
            MembershipAuthorizationProbes.AssertRedirect(delete, "/secrets?notice=deleted");
        }

        Assert.Null(await host.ReadSecretByIdAsync(created.Id));
    }

    [Fact]
    public async Task A_member_gets_403_on_writes_and_nothing_changes()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();

        using var owner = await host.SignInOwnerAsync();
        var secret = await host.CreateSecretAsync(owner, organizationId, Name, FirstValue);
        using var member = await host.CreateMemberAsync();

        // The Member loads the shell (its token) but not /secrets, which is a 403 for them.
        using (var create = await host.PostSecretFormAsync(
            member,
            "/secrets/create",
            new Dictionary<string, string> { ["Name"] = "MEMBER_TOKEN", ["Value"] = "member-value" },
            tokenPagePath: "/"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            await AssertCarriesNoPlaintextAsync(create, "member-value");
        }

        using (var rotate = await host.PostSecretFormAsync(
            member,
            $"/secrets/{secret.Id}/rotate",
            new Dictionary<string, string> { ["Value"] = "member-rotation" },
            tokenPagePath: "/"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rotate.StatusCode);
            await AssertCarriesNoPlaintextAsync(rotate, "member-rotation");
        }

        using (var delete = await host.PostSecretFormAsync(
            member,
            $"/secrets/{secret.Id}/delete",
            new Dictionary<string, string>(),
            tokenPagePath: "/"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }

        // Nothing was created, rotated or deleted.
        Assert.Null(await host.ReadSecretByNameAsync(organizationId, "MEMBER_TOKEN"));
        var unchanged = Assert.IsType<Secret>(
            await host.ReadSecretByNameAsync(organizationId, Name));
        Assert.Equal(secret.Ciphertext, unchanged.Ciphertext);
        Assert.Null(unchanged.RotatedAt);
    }

    [Fact]
    public async Task Another_organizations_owner_gets_404_with_an_empty_body()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();

        using var owner = await host.SignInOwnerAsync();
        var secret = await host.CreateSecretAsync(owner, organizationId, Name, FirstValue);

        var foreignOrganizationId = await host.CreateForeignOrganizationAsync();
        using var foreign = await host.SignInAsync(SecretsTestHost.ForeignOwnerEmail);

        // carol is an Owner - of a different organization. A's id must be indistinguishable from a
        // nonexistent one: 404 with an empty body, in both directions of the id.
        using (var rotate = await host.PostSecretFormAsync(
            foreign,
            $"/secrets/{secret.Id}/rotate",
            new Dictionary<string, string> { ["Value"] = "stolen-value" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);
            Assert.Equal(string.Empty, await rotate.Content.ReadAsStringAsync(CancellationToken));
            await AssertCarriesNoPlaintextAsync(rotate, "stolen-value");
        }

        using (var delete = await host.PostSecretFormAsync(
            foreign,
            $"/secrets/{secret.Id}/delete",
            new Dictionary<string, string>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
            Assert.Equal(string.Empty, await delete.Content.ReadAsStringAsync(CancellationToken));
        }

        var unchanged = Assert.IsType<Secret>(
            await host.ReadSecretByNameAsync(organizationId, Name));
        Assert.Equal(secret.Ciphertext, unchanged.Ciphertext);
        Assert.Null(unchanged.RotatedAt);

        // The foreign owner's own organization is untouched by the attempt.
        Assert.Empty(await host.ReadSecretsAsync(foreignOrganizationId));
    }

    [Fact]
    public async Task Rotating_or_deleting_a_missing_secret_is_404()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var missingId = Guid.NewGuid();

        using (var rotate = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{missingId}/rotate",
            new Dictionary<string, string> { ["Value"] = "whatever" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);
        }

        using (var delete = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{missingId}/delete",
            new Dictionary<string, string>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }
    }

    [Fact]
    public async Task A_duplicate_name_is_a_field_error_and_creates_no_second_row()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();

        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, organizationId, Name, FirstValue);

        using var duplicate = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string> { ["Name"] = Name, ["Value"] = "another-value" });

        MembershipAuthorizationProbes.AssertRedirect(
            duplicate,
            $"/secrets?error={SecretErrorCodes.NameDuplicate}&name={Name}");
        await AssertCarriesNoPlaintextAsync(duplicate, "another-value");

        var secrets = await host.ReadSecretsAsync(organizationId);
        Assert.Single(secrets);
        Assert.Equal(FirstValue, await host.ReadDecryptedValueAsync(organizationId, Name));
    }

    [Theory]
    [InlineData("", SecretErrorCodes.NameRequired)]
    [InlineData("a b", SecretErrorCodes.NameInvalid)]
    [InlineData("1TOKEN", SecretErrorCodes.NameInvalid)]
    [InlineData("A-VAR", SecretErrorCodes.NameInvalid)]
    [InlineData("$(x)", SecretErrorCodes.NameInvalid)]
    [InlineData("A=B", SecretErrorCodes.NameInvalid)]
    public async Task An_invalid_name_redirects_with_the_rule_and_writes_no_row(string name, string expectedCode)
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string> { ["Name"] = name, ["Value"] = "some-value" });

        Assert.StartsWith(
            $"/secrets?error={expectedCode}",
            response.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
        await AssertCarriesNoPlaintextAsync(response, "some-value");

        Assert.Empty(await host.ReadSecretsAsync(organizationId));
    }

    [Fact]
    public async Task A_missing_value_redirects_with_the_rule_and_writes_no_row()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string> { ["Name"] = Name, ["Value"] = string.Empty });

        Assert.StartsWith(
            $"/secrets?error={SecretErrorCodes.ValueRequired}",
            response.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
        Assert.Empty(await host.ReadSecretsAsync(organizationId));
    }

    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_rejected_and_writes_nothing()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();

        // No token: UseAntiforgery rejects the POST before the endpoint runs.
        using var response = await owner.PostAsync(
            "/secrets/create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Name"] = Name,
                ["Value"] = FirstValue,
            }),
            CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await host.ReadSecretsAsync(organizationId));
    }

    /// <summary>
    /// The value must not appear in a body, a header or a redirect target: a create/rotate response is
    /// a redirect, and echoing the value into the query would put it into every access log.
    /// </summary>
    private static async Task AssertCarriesNoPlaintextAsync(HttpResponseMessage response, params string[] values)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        var headers = string.Join('\n', response.Headers.SelectMany(header => header.Value));
        var location = response.Headers.Location?.OriginalString ?? string.Empty;

        foreach (var value in values)
        {
            Assert.DoesNotContain(value, body, StringComparison.Ordinal);
            Assert.DoesNotContain(value, headers, StringComparison.Ordinal);
            Assert.DoesNotContain(value, location, StringComparison.Ordinal);
        }
    }
}
