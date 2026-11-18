using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's probe for card F-039's six notification forms. Each one resolves the project <b>and</b> the endpoint
/// through the tenant filter before it looks at any role, so another organization's project or endpoint is the same
/// bodyless 404 a missing id is - on create as well as on every per-endpoint action.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationNotificationProbeTests : IClassFixture<TenantProbeHostFixture>
{
    private readonly TenantProbeHostFixture _host;

    public TenantIsolationNotificationProbeTests(TenantProbeHostFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Notification_endpoints_cannot_reach_another_tenants_project()
    {
        var betaEndpoint = await SeedBetaEndpointAsync();
        using var bob = await SignInAsync("bob@example.test");
        using var alice = await SignInAsync("alice@example.test");

        // B's project in the route: refused for a Member and for an Owner of A alike, and B gains no row.
        using (var create = await OrganizationTestHost.PostFormAsync(
            bob,
            $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectBId}/notifications/create",
            CreateFields("https://receiver.example.test/hook")))
        {
            Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        }

        // ...while A's own project under a Member's hand is a 403, not a 404: the role gate is real and the probe is
        // not passing by refusing everything.
        using (var memberCreate = await OrganizationTestHost.PostFormAsync(
            bob,
            $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectAId}/notifications/create",
            CreateFields("https://receiver.example.test/member")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, memberCreate.StatusCode);
        }

        // Every per-endpoint action, posted by A's Owner under A's own project with B's endpoint id: the endpoint is
        // resolved through the tenant filter, so each is the same bodyless 404 a missing id answers.
        foreach (var (route, extra) in new[]
                 {
                     ("delete", new Dictionary<string, string>(StringComparer.Ordinal)),
                     ("enable", new Dictionary<string, string>(StringComparer.Ordinal)),
                     ("disable", new Dictionary<string, string>(StringComparer.Ordinal)),
                     ("rotate", new Dictionary<string, string>(StringComparer.Ordinal) { ["Secret"] = "hijacked-secret" }),
                     ("test", new Dictionary<string, string>(StringComparer.Ordinal)),
                 })
        {
            using var response = await OrganizationTestHost.PostFormAsync(
                alice,
                $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
                $"/projects/{TwoOrgsFixture.ProjectAId}/notifications/{betaEndpoint}/{route}",
                extra);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var beta = await _host.Data.ReadAcrossTenantsAsync<NotificationEndpoint>(
            endpoint => endpoint.OrganizationId == TwoOrgsFixture.OrgBId,
            CancellationToken);
        var endpoint = Assert.Single(beta);
        Assert.Equal(betaEndpoint, endpoint.Id);
        Assert.True(endpoint.Enabled);

        // And B's receiver URL never appears on A's settings page.
        var settings = await AuthTestHost.GetStringAsync(bob, $"/projects/{TwoOrgsFixture.ProjectAId}/settings");
        Assert.DoesNotContain("receiver.example.test", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(betaEndpoint.ToString(), settings, StringComparison.OrdinalIgnoreCase);

        // Positive control: the same request shape works for A's own project.
        using var ownCreated = await OrganizationTestHost.PostFormAsync(
            alice,
            $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectAId}/notifications/create",
            CreateFields("https://receiver.example.test/alpha"));
        Assert.True(
            (int)ownCreated.StatusCode is 302 or 303,
            $"creating A's own endpoint answered {(int)ownCreated.StatusCode}.");

        var alpha = await _host.Data.ReadAcrossTenantsAsync<NotificationEndpoint>(
            endpoint => endpoint.OrganizationId == TwoOrgsFixture.OrgAId,
            CancellationToken);
        Assert.Single(alpha);
    }

    private static Dictionary<string, string> CreateFields(string url) => new(StringComparer.Ordinal)
    {
        ["Url"] = url,
        ["Secret"] = "fixture-secret-1",
        ["Events"] = "all",
        ["Enabled"] = "true",
        ["AllowInsecureHttp"] = "false",
    };

    /// <summary>
    /// One endpoint on B's project, written through the system scope: B's project exists before this probe runs, and
    /// the row is what the foreign actions must fail to touch.
    /// </summary>
    private async Task<Guid> SeedBetaEndpointAsync()
    {
        using var scope = _host.Data.SystemDataScope.BeginScope();
        await using var context = _host.Data.CreateContext();
        var endpoint = new NotificationEndpoint
        {
            OrganizationId = TwoOrgsFixture.OrgBId,
            ProjectId = TwoOrgsFixture.ProjectBId,
            Url = "https://receiver.example.test/beta",
            SecretCiphertext = "not-a-real-ciphertext",
            Events = "all",
            Enabled = true,
            CreatedByUserId = TwoOrgsFixture.OrgBOwnerUserId,
        };
        context.Set<NotificationEndpoint>().Add(endpoint);
        await context.SaveChangesAsync(CancellationToken);
        return endpoint.Id;
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _host.CreateClient();
        var response = await AuthTestHost.LoginAsync(
            client,
            email,
            TwoOrgsFixture.UserPassword,
            returnUrl: "/projects");
        if ((int)response.StatusCode is not (302 or 303))
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"Signing in as '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }
}
