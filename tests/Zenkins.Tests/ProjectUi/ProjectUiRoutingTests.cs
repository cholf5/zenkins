using System.Net;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// TC-001 (card D-003): every project tab is a real page. Each of the four routes renders its own
/// content with no cross-tab bleed, a reload (a second GET of the deep link) keeps the tab, an
/// unknown project id is a 404 on all four, and a foreign organization's id is the same 404 - the
/// tenant filter decides, the id is never an oracle. A Member sees every tab, but the settings tab
/// carries no credential write affordance for them, and their forged POSTs are 403 and change
/// nothing (asserted against the server, never assumed from a hidden button).
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectUiRoutingTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private const string MemberEmail = "bob@example.test";

    [Fact]
    public async Task Every_tab_is_a_page_reloads_keep_the_tab_and_unknown_ids_are_404()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
        var projectId = await host.AddProjectAsync(organizationId, "tab-app");

        // The one marker each tab renders and the other three do not: content must not bleed.
        var tabs = new (string Path, string Marker, string[] ForeignMarkers)[]
        {
            ($"/projects/{projectId}", "Latest run", ["Build runs", "pipelines-path", "webhook-name"]),
            ($"/projects/{projectId}/pipelines", "pipelines-path", ["Latest run", "Build runs", "webhook-name"]),
            ($"/projects/{projectId}/runs", "Build runs", ["Latest run", "pipelines-path", "webhook-name"]),
            ($"/projects/{projectId}/settings", "webhook-name", ["Latest run", "pipelines-path", "Build runs"]),
        };

        foreach (var (path, marker, foreignMarkers) in tabs)
        {
            var page = await AuthTestHost.GetStringAsync(owner, path);
            Assert.Contains(marker, page, StringComparison.Ordinal);
            Assert.All(foreignMarkers, foreign => Assert.DoesNotContain(foreign, page, StringComparison.Ordinal));

            // A reload is the same deep link again: the tab is a route, so nothing is lost.
            var reload = await AuthTestHost.GetStringAsync(owner, path);
            Assert.Contains(marker, reload, StringComparison.Ordinal);
            Assert.All(foreignMarkers, foreign => Assert.DoesNotContain(foreign, reload, StringComparison.Ordinal));
        }

        // The overview still carries the start-run form (it kept what the single page had) and the
        // settings tab carries both webhook and credential management.
        var overview = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");
        Assert.Contains("Start run", overview, StringComparison.Ordinal);
        Assert.Contains("Repository URL", overview, StringComparison.Ordinal);

        var settings = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        Assert.Contains("Project basics", settings, StringComparison.Ordinal);
        Assert.Contains("Git credentials", settings, StringComparison.Ordinal);

        // A random id is a 404 on all four routes - the same bodyless answer everywhere.
        var unknown = Guid.NewGuid();
        foreach (var path in new[]
                 {
                     $"/projects/{unknown}",
                     $"/projects/{unknown}/pipelines",
                     $"/projects/{unknown}/runs",
                     $"/projects/{unknown}/settings",
                 })
        {
            using var response = await owner.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_foreign_organizations_project_id_is_404_on_all_four_tabs()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var starterId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
        await host.AddProjectAsync(starterId, "starter-app");

        // alice owns Bravo too, so this is not a role failure: the active organization is Starter
        // and Bravo's project simply is not there - 404, never a 403 existence oracle.
        var bravoId = await host.AddOrganizationAsync(
            "Bravo",
            (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));
        var bravoProjectId = await host.AddProjectAsync(
            bravoId,
            "beta-app",
            "https://example.test/beta.git",
            "develop");

        foreach (var path in new[]
                 {
                     $"/projects/{bravoProjectId}",
                     $"/projects/{bravoProjectId}/pipelines",
                     $"/projects/{bravoProjectId}/runs",
                     $"/projects/{bravoProjectId}/settings",
                     $"/projects/{bravoProjectId}/git-credentials",
                 })
        {
            using var response = await owner.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.DoesNotContain("beta-app", body, StringComparison.Ordinal);
            Assert.DoesNotContain("https://example.test/beta.git", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_member_sees_every_tab_but_their_forged_credential_writes_are_403()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
        var projectId = await host.AddProjectAsync(organizationId, "member-tabs");

        using var member = await host.RegisterUserAsync(MemberEmail);
        var invite = await OrganizationTestHost.PostFormAsync(
            owner,
            "/organizations/members",
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = MemberEmail });
        Assert.True((int)invite.StatusCode is 302 or 303, "Inviting the Member failed.");

        // The persisted selection resolves on the member's first request.
        _ = await AuthTestHost.GetStringAsync(member, "/");

        // Every tab renders for a Member (200, own content) ...
        foreach (var path in new[]
                 {
                     $"/projects/{projectId}",
                     $"/projects/{projectId}/pipelines",
                     $"/projects/{projectId}/runs",
                     $"/projects/{projectId}/settings",
                 })
        {
            using var response = await member.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ... but the settings tab shows no credential form and no credential names (the asymmetry
        // stance: credentials are managed like secrets).
        var settings = await AuthTestHost.GetStringAsync(member, $"/projects/{projectId}/settings");
        Assert.Contains("managed by organization Owners", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-name", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("/git-credentials/create", settings, StringComparison.Ordinal);

        // Forged writes as the Member: the server is the enforcement, every one is a 403.
        var memberCreate = await OrganizationTestHost.PostFormAsync(
            member,
            $"/projects/{projectId}/settings",
            "/git-credentials/create",
            new Dictionary<string, string>
            {
                ["Name"] = "MEMBER_TOKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = "zkpat_member_must_not_store",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.Equal(HttpStatusCode.Forbidden, memberCreate.StatusCode);

        // The Owner's store is usable - one credential exists for the rotate/delete probes.
        var ownerCreate = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            "/git-credentials/create",
            new Dictionary<string, string>
            {
                ["Name"] = "DEPLOY_TOKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = "zkpat_0123456789abcdef0123456789abcdef",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)ownerCreate.StatusCode is 302 or 303, "The Owner's create failed.");

        var credentialId = await ReadSingleCredentialIdAsync(owner, projectId);

        var memberRotate = await OrganizationTestHost.PostFormAsync(
            member,
            $"/projects/{projectId}/settings",
            $"/git-credentials/{credentialId}/rotate",
            new Dictionary<string, string>
            {
                ["Kind"] = "HttpsToken",
                ["Value"] = "zkpat_member_must_not_rotate1",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.Equal(HttpStatusCode.Forbidden, memberRotate.StatusCode);

        var memberDelete = await OrganizationTestHost.PostFormAsync(
            member,
            $"/projects/{projectId}/settings",
            $"/git-credentials/{credentialId}/delete",
            new Dictionary<string, string> { ["ProjectId"] = projectId.ToString() });
        Assert.Equal(HttpStatusCode.Forbidden, memberDelete.StatusCode);

        // A Member may not even read the list (they must not learn which names exist).
        using var memberList = await member.GetAsync($"/projects/{projectId}/git-credentials", CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, memberList.StatusCode);

        // Nothing changed: still exactly one credential, never rotated, same name and kind.
        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        Assert.Contains("DEPLOY_TOKEN", list, StringComparison.Ordinal);
        Assert.Contains("\"rotatedAt\":null", list, StringComparison.Ordinal);
        using var document = System.Text.Json.JsonDocument.Parse(list);
        Assert.Single(document.RootElement.EnumerateArray());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<Guid> ReadSingleCredentialIdAsync(HttpClient owner, Guid projectId)
    {
        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        using var document = System.Text.Json.JsonDocument.Parse(list);
        var item = Assert.Single(document.RootElement.EnumerateArray());
        return item.GetProperty("id").GetGuid();
    }
}
