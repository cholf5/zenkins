using System.Net;
using Xunit;
using Zenkins.Server.Secrets;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// The Owner-only page (card F-012 AC 6): names and timestamps, create/rotate/delete with a
/// confirmation, no value and no reveal control. A Member's page request is a real 403 - the page is
/// not merely missing a button.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretPageTests
{
    private const string Name = "PAGE_TOKEN";
    private const string Value = "page-page-secret-value-71d";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_owners_page_lists_names_and_timestamps_without_the_value()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, organizationId, Name, Value);

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        Assert.Contains(Name, page, StringComparison.Ordinal);
        Assert.Contains("Created", page, StringComparison.Ordinal);
        Assert.Contains("Rotated", page, StringComparison.Ordinal);
        Assert.Contains("Never", page, StringComparison.Ordinal);
        Assert.DoesNotContain(Value, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_gets_403_and_the_page_does_not_list_the_secrets()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, organizationId, Name, Value);

        using var member = await host.CreateMemberAsync();

        using var response = await member.GetAsync(SecretEndpoints.ListPath, CancellationToken);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);

        // bob is a Member of the same organization: not a list, not a 200.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(Name, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Value, body, StringComparison.Ordinal);
        Assert.Contains("Owner access required", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_renders_create_rotate_and_delete_with_a_confirmation()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();
        var secret = await host.CreateSecretAsync(owner, organizationId, Name, Value);

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        Assert.Contains("action=\"/secrets/create\"", page, StringComparison.Ordinal);
        Assert.Contains($"action=\"/secrets/{secret.Id}/rotate\"", page, StringComparison.Ordinal);
        Assert.Contains($"action=\"/secrets/{secret.Id}/delete\"", page, StringComparison.Ordinal);

        // The delete control is a two-step affordance: a disclosure that reveals the confirming form
        // with its own button, so one click cannot destroy a secret.
        Assert.Contains("Confirm delete", page, StringComparison.Ordinal);
        Assert.Contains("Delete…", page, StringComparison.Ordinal);

        // The value inputs are write-only password fields and are empty, so a page render can never
        // hand back a stored value through the form.
        Assert.DoesNotContain($"value=\"{Value}\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"secret-value\"", page, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_no_reveal_control_and_no_route_backed_by_the_page()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, organizationId, Name, Value);

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        // Not a disabled button - no affordance and no link at all. The route-shape test pins the
        // server side; this pins the page so a "show value" control cannot be added alone.
        foreach (var marker in new[] { "Reveal", "Show value", "View value", "/value", "/reveal" })
        {
            Assert.DoesNotContain(marker, page, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_navigation_entry_still_points_at_the_real_page()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var home = await AuthTestHost.GetStringAsync(owner, "/");

        Assert.Contains("href=\"/secrets\"", home, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_shows_the_notice_for_a_successful_mutation()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var page = await AuthTestHost.GetStringAsync(owner, $"{SecretEndpoints.ListPath}?notice=created");

        Assert.Contains("The secret was created.", page, StringComparison.Ordinal);
    }
}
