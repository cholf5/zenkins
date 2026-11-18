using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's probe for card F-026's two chain endpoints (<c>POST /projects/{id}/links</c> and
/// <c>POST /projects/{id}/links/{linkId}/delete</c>).
/// <para>
/// The chain is tenant-owned data on both ends: the route names one project and the form names the other, so a
/// cross-tenant attempt can put a foreign id in the route, in the body, or in the link id - all three are tried
/// here. The refusal contract is the one every other project mutation keeps: a foreign or unknown id is a
/// bodyless 404 and no row is written or removed. The positive control at the end proves the same request shape
/// works for the caller's own organization, so the probe cannot pass by refusing everything.
/// </para>
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationLinkProbeTests : IClassFixture<TenantProbeHostFixture>
{
    private readonly TenantProbeHostFixture _host;

    public TenantIsolationLinkProbeTests(TenantProbeHostFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Link_endpoints_cannot_reach_another_tenants_project()
    {
        // Each organization gets a second project and (for B) one edge of its own, so the probe has a real row
        // to protect and the positive control has something to link to.
        var seeded = await SeedChainsAsync();
        var betaLink = seeded.BetaLinkId;

        using var bob = await SignInAsync("bob@example.test");

        // 1. B's project in the route: the edge is never created, and nothing about B is learned.
        using var foreignRoute = await OrganizationTestHost.PostFormAsync(
            bob,
            $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectBId}/links",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TargetProjectId"] = TwoOrgsFixture.ProjectAId.ToString(),
                ["Direction"] = "downstream",
                ["OnlyOnSuccess"] = "true",
                ["BranchPolicy"] = ProjectLinkBranchPolicies.DefaultBranch,
            });
        Assert.Equal(HttpStatusCode.NotFound, foreignRoute.StatusCode);

        // 2. B's project in the body: the route is A's own project, so the only leak left is the target.
        using var foreignTarget = await OrganizationTestHost.PostFormAsync(
            bob,
            $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectAId}/links",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TargetProjectId"] = TwoOrgsFixture.ProjectBId.ToString(),
                ["Direction"] = "downstream",
                ["OnlyOnSuccess"] = "true",
                ["BranchPolicy"] = ProjectLinkBranchPolicies.DefaultBranch,
            });
        Assert.Equal(HttpStatusCode.NotFound, foreignTarget.StatusCode);

        // 3. B's link id, under A's own route: a foreign link is the same 404 as a missing one.
        using var foreignLink = await OrganizationTestHost.PostFormAsync(
            bob,
            $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectAId}/links/{betaLink}/delete",
            new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, foreignLink.StatusCode);

        // Nothing was written in either tenant, and B's own edge is untouched.
        Assert.Empty(await _host.Data.ReadAcrossTenantsAsync<ProjectLink>(
            link => link.OrganizationId == TwoOrgsFixture.OrgAId, CancellationToken));
        var betaLinks = await _host.Data.ReadAcrossTenantsAsync<ProjectLink>(
            link => link.OrganizationId == TwoOrgsFixture.OrgBId, CancellationToken);
        Assert.Equal(betaLink, Assert.Single(betaLinks).Id);

        // And A's settings page never renders B's projects or edges.
        var settings = await AuthTestHost.GetStringAsync(bob, $"/projects/{TwoOrgsFixture.ProjectAId}/settings");
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, settings, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-worker", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(betaLink.ToString(), settings, StringComparison.OrdinalIgnoreCase);

        // Positive control: the same two requests succeed for A's own organization.
        using var alice = await SignInAsync("alice@example.test");

        using var saved = await OrganizationTestHost.PostFormAsync(
            alice,
            $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectAId}/links",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TargetProjectId"] = seeded.AlphaSecond.ToString(),
                ["Direction"] = "downstream",
                ["OnlyOnSuccess"] = "true",
                ["BranchPolicy"] = ProjectLinkBranchPolicies.SameBranch,
            });
        Assert.True(
            (int)saved.StatusCode is 302 or 303
                && saved.Headers.Location!.OriginalString.Contains("links=saved", StringComparison.Ordinal),
            $"saving A's own edge answered {(int)saved.StatusCode}: {saved.Headers.Location}.");

        var alphaLink = Assert.Single(await _host.Data.ReadAcrossTenantsAsync<ProjectLink>(
            link => link.OrganizationId == TwoOrgsFixture.OrgAId, CancellationToken));
        Assert.Equal(TwoOrgsFixture.ProjectAId, alphaLink.UpstreamProjectId);
        Assert.Equal(seeded.AlphaSecond, alphaLink.DownstreamProjectId);

        // ...and its delete, for the link that really does belong to the route's project.
        using var removed = await OrganizationTestHost.PostFormAsync(
            alice,
            $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectAId}/links/{alphaLink.Id}/delete",
            new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.True(
            (int)removed.StatusCode is 302 or 303
                && removed.Headers.Location!.OriginalString.Contains("links=deleted", StringComparison.Ordinal),
            $"removing A's own edge answered {(int)removed.StatusCode}: {removed.Headers.Location}.");
        Assert.Empty(await _host.Data.ReadAcrossTenantsAsync<ProjectLink>(
            link => link.OrganizationId == TwoOrgsFixture.OrgAId, CancellationToken));
    }

    /// <summary>
    /// Two projects per organization plus one B-internal edge, written through the system scope because both
    /// organizations' rows exist before either tenant is selected - the same shape the fixture's own seed uses.
    /// </summary>
    private async Task<(Guid AlphaSecond, Guid BetaSecond, Guid BetaLinkId)> SeedChainsAsync()
    {
        using var scope = _host.Data.SystemDataScope.BeginScope();
        await using var context = _host.Data.CreateContext();

        var alphaSecond = await AddProjectAsync(context, TwoOrgsFixture.OrgAId, "alpha-worker");
        var betaSecond = await AddProjectAsync(context, TwoOrgsFixture.OrgBId, "beta-worker");
        var betaLink = await AddLinkAsync(context, TwoOrgsFixture.OrgBId, TwoOrgsFixture.ProjectBId, betaSecond);
        return (alphaSecond, betaSecond, betaLink);
    }

    private static async Task<Guid> AddProjectAsync(
        Zenkins.Server.Data.ZenkinsDbContext context,
        Guid organizationId,
        string name)
    {
        var project = new Project
        {
            OrganizationId = organizationId,
            Name = name,
            RepositoryUrl = $"https://example.test/{name}.git",
            DefaultBranch = "main",
            PipelinePath = "pipeline.lua",
        };
        context.Projects.Add(project);
        await context.SaveChangesAsync(CancellationToken);
        return project.Id;
    }

    private static async Task<Guid> AddLinkAsync(
        Zenkins.Server.Data.ZenkinsDbContext context,
        Guid organizationId,
        Guid upstreamId,
        Guid downstreamId)
    {
        var link = new ProjectLink
        {
            OrganizationId = organizationId,
            UpstreamProjectId = upstreamId,
            DownstreamProjectId = downstreamId,
            OnlyOnSuccess = true,
            BranchPolicy = ProjectLinkBranchPolicies.DefaultBranch,
        };
        context.ProjectLinks.Add(link);
        await context.SaveChangesAsync(CancellationToken);
        return link.Id;
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
