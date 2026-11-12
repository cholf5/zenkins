using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Organizations;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Organizations;

/// <summary>
/// Card F-002 endpoint and invariant tests. Each test builds its own host (a temporary SQLite file
/// plus the real pipeline), because these tests mutate organizations and memberships.
/// </summary>
[Trait("Category", "Organizations")]
public sealed class OrganizationEndpointTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Member_cannot_manage_and_sees_no_management_controls()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        await MembershipAuthorizationProbes.AssertMemberCannotManageAsync(host);
    }

    [Fact]
    public async Task Non_member_cannot_switch()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        await MembershipAuthorizationProbes.AssertNonMemberCannotSwitchAsync(host);
    }

    [Fact]
    public async Task Switching_between_memberships_changes_the_visible_project_data()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        await MembershipAuthorizationProbes.AssertSwitchChangesVisibleDataAsync(host);
    }

    [Fact]
    public async Task Owner_can_rename_the_organization()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var ownerClient = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        var organizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.SettingsPath,
            "/organizations/settings",
            new Dictionary<string, string> { ["Name"] = "Research and Development" });
        MembershipAuthorizationProbes.AssertRedirect(
            response,
            "/organizations/settings?notice=renamed");

        var renamed = (await host.ReadOrganizationsAsync()).Single(organization => organization.Id == organizationId);
        Assert.Equal("Research and Development", renamed.Name);

        // The shell and the switcher read the new name from the server-side organization row.
        var home = await AuthTestHost.GetStringAsync(ownerClient, "/");
        Assert.Contains("Research and Development", home, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_can_invite_an_existing_account_and_sees_it_in_the_members_list()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var ownerClient = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var invitedClient = await host.RegisterUserAsync("bob@example.test");

        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = "bob@example.test" });
        MembershipAuthorizationProbes.AssertRedirect(
            response,
            "/organizations/members?notice=invited");

        var membersHtml = await AuthTestHost.GetStringAsync(ownerClient, OrganizationEndpoints.MembersPath);
        Assert.Contains("bob@example.test", membersHtml, StringComparison.Ordinal);
        Assert.Contains("alice@example.test", membersHtml, StringComparison.Ordinal);
        Assert.Contains("Member", membersHtml, StringComparison.Ordinal);

        // Joined date is rendered (yyyy-MM-dd); the members list shows name, email, role and date.
        Assert.Matches(@"\d{4}-\d{2}-\d{2}", membersHtml);

        var starterId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);
        var members = await host.ReadMembersAsync(starterId);
        Assert.Contains(members, member => member.Email == "bob@example.test" && member.Role == OrganizationRole.Member);
        Assert.Equal(2, members.Count);
    }

    [Fact]
    public async Task Invite_of_an_unknown_email_is_refused_and_creates_nothing()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var ownerClient = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var unrelated = await host.RegisterUserAsync("bob@example.test");

        var starterId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);
        var before = await host.ReadMembersAsync(starterId);

        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = "nobody@example.test" });

        MembershipAuthorizationProbes.AssertRedirect(
            response,
            "/organizations/members?error=unknown-email");
        Assert.Equal(before, await host.ReadMembersAsync(starterId));
    }

    [Fact]
    public async Task Last_owner_cannot_be_demoted_or_removed_but_the_guard_is_conditional()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var ownerClient = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var secondClient = await host.RegisterUserAsync("bob@example.test");

        var starterId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);
        var ownerMembership = (await host.ReadMembersAsync(starterId)).Single();

        // Demote the only Owner: refused with a clear message, role unchanged.
        var demotion = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/role",
            new Dictionary<string, string>
            {
                ["MembershipId"] = ownerMembership.MembershipId.ToString(),
                ["Role"] = nameof(OrganizationRole.Member),
            });
        MembershipAuthorizationProbes.AssertRedirect(
            demotion,
            "/organizations/members?error=last-owner");
        Assert.Equal(
            OrganizationRole.Owner,
            (await host.ReadMembersAsync(starterId)).Single().Role);

        var page = await AuthTestHost.GetStringAsync(ownerClient, OrganizationEndpoints.MembersPath + "?error=last-owner");
        Assert.Contains(
            "The last Owner cannot be demoted or removed",
            page,
            StringComparison.Ordinal);

        // Remove the only Owner: refused, membership unchanged.
        var removal = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/remove",
            new Dictionary<string, string> { ["MembershipId"] = ownerMembership.MembershipId.ToString() });
        MembershipAuthorizationProbes.AssertRedirect(
            removal,
            "/organizations/members?error=last-owner");
        Assert.Single(await host.ReadMembersAsync(starterId));

        // With a second Owner the guard must allow the change - it protects the last Owner, not every
        // Owner.
        var invite = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = "bob@example.test" });
        MembershipAuthorizationProbes.AssertRedirect(invite, "/organizations/members?notice=invited");

        var bobMembership = (await host.ReadMembersAsync(starterId))
            .Single(member => member.Email == "bob@example.test");
        var promotion = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/role",
            new Dictionary<string, string>
            {
                ["MembershipId"] = bobMembership.MembershipId.ToString(),
                ["Role"] = nameof(OrganizationRole.Owner),
            });
        MembershipAuthorizationProbes.AssertRedirect(promotion, "/organizations/members?notice=role-changed");

        var nowDemotable = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/role",
            new Dictionary<string, string>
            {
                ["MembershipId"] = ownerMembership.MembershipId.ToString(),
                ["Role"] = nameof(OrganizationRole.Member),
            });
        MembershipAuthorizationProbes.AssertRedirect(
            nowDemotable,
            "/organizations/members?notice=role-changed");
        Assert.Equal(
            OrganizationRole.Member,
            (await host.ReadMembersAsync(starterId))
                .Single(member => member.Email == OrganizationTestHost.OwnerEmail).Role);
    }

    [Fact]
    public async Task Stale_persisted_selection_falls_back_and_a_revoked_membership_loses_the_tenant()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var ownerClient = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        var starterId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);
        var foreignId = await host.AddOrganizationAsync("Charlie");

        // A stale/forged persisted selection: alice is not a member of Charlie.
        await host.SetPersistedSelectionAsync(OrganizationTestHost.OwnerEmail, foreignId);

        var home = await AuthTestHost.GetStringAsync(ownerClient, "/");
        Assert.Contains(Zenkins.Server.Auth.BootstrapService.StarterOrganizationName, home, StringComparison.Ordinal);
        Assert.Equal(starterId, await host.ReadActiveOrganizationIdAsync(OrganizationTestHost.OwnerEmail));

        // Membership revoked by someone else (or the organization deleted): no tenant, not a stale one.
        var ownerMembership = (await host.ReadMembersAsync(starterId)).Single();
        await host.RemoveMembershipAsync(ownerMembership.MembershipId);

        var noOrganization = await AuthTestHost.GetStringAsync(ownerClient, "/");
        Assert.Contains("No organization yet", noOrganization, StringComparison.Ordinal);
        Assert.Null(await host.ReadActiveOrganizationIdAsync(OrganizationTestHost.OwnerEmail));
    }

    [Fact]
    public async Task Switch_without_an_antiforgery_token_is_rejected_and_changes_nothing()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var ownerClient = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        var starterId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);
        var bravoId = await host.AddOrganizationAsync(
            "Bravo",
            (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));

        // Sanity: the form-post helper with a token works.
        var withToken = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            "/",
            "/organizations/switch",
            new Dictionary<string, string> { ["OrganizationId"] = bravoId.ToString() });
        MembershipAuthorizationProbes.AssertRedirect(withToken, "/");
        Assert.Equal(bravoId, await host.ReadActiveOrganizationIdAsync(OrganizationTestHost.OwnerEmail));

        // A POST without the antiforgery token is a 400 and the selection stays on Bravo.
        using var response = await ownerClient.PostAsync(
            "/organizations/switch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["OrganizationId"] = starterId.ToString(),
            }),
            CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(bravoId, await host.ReadActiveOrganizationIdAsync(OrganizationTestHost.OwnerEmail));
    }
}
