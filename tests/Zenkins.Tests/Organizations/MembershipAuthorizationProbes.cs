using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Organizations;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Organizations;

/// <summary>
/// Card F-002's membership-based authorization probes, extending Q-001's harness. They are written
/// against a real authenticated HTTP host (cookies, antiforgery, the real endpoints), so a guard that
/// only lives in the UI fails here.
/// </summary>
public static class MembershipAuthorizationProbes
{
    /// <summary>
    /// A Member cannot perform any management action, even by POSTing the endpoints directly with a
    /// valid antiforgery token; the members page shows the list but no management controls.
    /// </summary>
    public static async Task AssertMemberCannotManageAsync(OrganizationTestHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var ownerEmail = OrganizationTestHost.OwnerEmail;
        const string memberEmail = "carol@example.test";

        var starterOrganizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        var memberClient = await host.RegisterUserAsync(memberEmail);
        using var _ = memberClient;

        var ownerClient = await host.SignInAsync(ownerEmail);
        using var __ = ownerClient;

        // Owner invites carol, who becomes an ordinary Member.
        var invite = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = memberEmail });
        AssertRedirect(invite, "/organizations/members?notice=invited");

        // The list itself is visible to the Member...
        var membersHtml = await AuthTestHost.GetStringAsync(memberClient, OrganizationEndpoints.MembersPath);
        Assert.Contains(ownerEmail, membersHtml, StringComparison.Ordinal);
        Assert.Contains(memberEmail, membersHtml, StringComparison.Ordinal);
        Assert.Contains("Your role: Member", membersHtml, StringComparison.Ordinal);

        // ...but no management control is rendered for them.
        Assert.DoesNotContain("/organizations/members/invite", membersHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/organizations/members/remove", membersHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/organizations/members/role", membersHtml, StringComparison.Ordinal);

        var settingsHtml = await AuthTestHost.GetStringAsync(memberClient, OrganizationEndpoints.SettingsPath);
        Assert.Contains("Owner access required", settingsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/organizations/settings\"", settingsHtml, StringComparison.Ordinal);

        var membersBefore = await host.ReadMembersAsync(starterOrganizationId);
        var ownerMembership = membersBefore.Single(member => member.Email == ownerEmail);
        var memberMembership = membersBefore.Single(member => member.Email == memberEmail);

        // Rename: 403 and the name is untouched.
        var rename = await OrganizationTestHost.PostFormAsync(
            memberClient,
            OrganizationEndpoints.SettingsPath,
            "/organizations/settings",
            new Dictionary<string, string> { ["Name"] = "Hijacked by a Member" });
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        Assert.Equal(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName,
            (await host.ReadOrganizationsAsync()).Single(organization => organization.Id == starterOrganizationId).Name);

        // Role change of the Owner: 403 and the role is untouched.
        var roleChange = await OrganizationTestHost.PostFormAsync(
            memberClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/role",
            new Dictionary<string, string>
            {
                ["MembershipId"] = ownerMembership.MembershipId.ToString(),
                ["Role"] = nameof(OrganizationRole.Member),
            });
        Assert.Equal(HttpStatusCode.Forbidden, roleChange.StatusCode);
        Assert.Equal(
            OrganizationRole.Owner,
            (await host.ReadMembersAsync(starterOrganizationId))
                .Single(member => member.Email == ownerEmail).Role);

        // Removing the Owner: 403 and the membership survives.
        var remove = await OrganizationTestHost.PostFormAsync(
            memberClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/remove",
            new Dictionary<string, string> { ["MembershipId"] = ownerMembership.MembershipId.ToString() });
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        Assert.Equal(2, (await host.ReadMembersAsync(starterOrganizationId)).Count);

        // Self-promotion: 403 as well; a Member cannot make themselves an Owner.
        var selfPromotion = await OrganizationTestHost.PostFormAsync(
            memberClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/role",
            new Dictionary<string, string>
            {
                ["MembershipId"] = memberMembership.MembershipId.ToString(),
                ["Role"] = nameof(OrganizationRole.Owner),
            });
        Assert.Equal(HttpStatusCode.Forbidden, selfPromotion.StatusCode);
        Assert.Equal(
            OrganizationRole.Member,
            (await host.ReadMembersAsync(starterOrganizationId))
                .Single(member => member.Email == memberEmail).Role);
    }

    /// <summary>
    /// A user who is not a member of the target organization cannot switch to it, even with a valid
    /// antiforgery token: 403, and the persisted selection stays on the organization they belong to.
    /// </summary>
    public static async Task AssertNonMemberCannotSwitchAsync(OrganizationTestHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var ownerEmail = OrganizationTestHost.OwnerEmail;
        const string nonMemberEmail = "carol@example.test";
        const string otherOrganizationName = "Bravo";

        var starterOrganizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        var ownerClient = await host.SignInAsync(ownerEmail);
        using var _ = ownerClient;

        var memberClient = await host.RegisterUserAsync(nonMemberEmail);
        using var __ = memberClient;

        // carol belongs to Starter only.
        var invite = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = nonMemberEmail });
        AssertRedirect(invite, "/organizations/members?notice=invited");

        // bravo belongs to alice only; carol is not a member.
        var bravoId = await host.AddOrganizationAsync(
            otherOrganizationName,
            (ownerEmail, OrganizationRole.Owner));

        // Resolve once so carol's selection is persisted as Starter (the deterministic fallback).
        await AuthTestHost.GetStringAsync(memberClient, "/");

        var activeBefore = await host.ReadActiveOrganizationIdAsync(nonMemberEmail);
        Assert.Equal(starterOrganizationId, activeBefore);

        // The switcher is membership-only: alice may see both organizations, carol only Starter.
        var aliceHome = await AuthTestHost.GetStringAsync(ownerClient, "/");
        Assert.Contains(otherOrganizationName, aliceHome, StringComparison.Ordinal);
        Assert.Contains("Starter Organization", aliceHome, StringComparison.Ordinal);

        var carolHome = await AuthTestHost.GetStringAsync(memberClient, "/");
        Assert.Contains("Starter Organization", carolHome, StringComparison.Ordinal);
        Assert.DoesNotContain(otherOrganizationName, carolHome, StringComparison.Ordinal);

        var forgedSwitch = await OrganizationTestHost.PostFormAsync(
            memberClient,
            "/",
            "/organizations/switch",
            new Dictionary<string, string> { ["OrganizationId"] = bravoId.ToString() });
        Assert.Equal(HttpStatusCode.Forbidden, forgedSwitch.StatusCode);

        // Rejected server-side: the selection never moved.
        Assert.Equal(activeBefore, await host.ReadActiveOrganizationIdAsync(nonMemberEmail));
    }

    /// <summary>
    /// TC-004: switching between two organizations the user does belong to changes the project data
    /// rendered by the read-only Projects page.
    /// </summary>
    public static async Task AssertSwitchChangesVisibleDataAsync(OrganizationTestHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        const string starterProject = "starter-app";
        const string bravoProject = "bravo-app";
        const string bravoName = "Bravo";

        var starterOrganizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        var ownerClient = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = ownerClient;

        var bravoId = await host.AddOrganizationAsync(
            bravoName,
            (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));

        await host.AddProjectAsync(starterOrganizationId, starterProject);
        await host.AddProjectAsync(bravoId, bravoProject);

        // Start on Starter (the deterministic default), then switch to Bravo via the real POST.
        var bravoPage = await AuthTestHost.GetStringAsync(ownerClient, "/projects");
        Assert.Contains(starterProject, bravoPage, StringComparison.Ordinal);
        Assert.DoesNotContain(bravoProject, bravoPage, StringComparison.Ordinal);

        var switchToBravo = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            "/",
            "/organizations/switch",
            new Dictionary<string, string> { ["OrganizationId"] = bravoId.ToString() });
        AssertRedirect(switchToBravo, "/");
        Assert.Equal(bravoId, await host.ReadActiveOrganizationIdAsync(OrganizationTestHost.OwnerEmail));

        var bravoProjects = await AuthTestHost.GetStringAsync(ownerClient, "/projects");
        Assert.Contains(bravoProject, bravoProjects, StringComparison.Ordinal);
        Assert.DoesNotContain(starterProject, bravoProjects, StringComparison.Ordinal);
        Assert.Contains(bravoName, bravoProjects, StringComparison.Ordinal);

        // And back: the previous organization's data reappears.
        var switchBack = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            "/",
            "/organizations/switch",
            new Dictionary<string, string> { ["OrganizationId"] = starterOrganizationId.ToString() });
        AssertRedirect(switchBack, "/");

        var starterProjects = await AuthTestHost.GetStringAsync(ownerClient, "/projects");
        Assert.Contains(starterProject, starterProjects, StringComparison.Ordinal);
        Assert.DoesNotContain(bravoProject, starterProjects, StringComparison.Ordinal);
    }

    /// <summary>Asserts a 302 to <paramref name="expectedLocation"/>.</summary>
    public static void AssertRedirect(HttpResponseMessage response, string expectedLocation)
    {
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Expected a redirect, got {(int)response.StatusCode}.");
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
    }

    /// <summary>Whether the HTML contains a form that posts to <paramref name="action"/>.</summary>
    public static bool HasFormAction(string html, string action) =>
        Regex.IsMatch(html, $"action=\"{Regex.Escape(action)}\"", RegexOptions.CultureInvariant);
}
