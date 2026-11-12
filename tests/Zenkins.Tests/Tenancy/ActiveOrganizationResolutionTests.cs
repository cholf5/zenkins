using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// F-002's resolution order, exercised against Q-001's two-organization fixture: the persisted
/// selection is honoured only while the membership exists, a stale/forged value falls back and is
/// repaired, and a switch to an organization the user does not belong to is rejected without moving
/// the selection. These are data-layer probes; <c>OrganizationEndpointTests</c> covers the same
/// behaviour through the authenticated HTTP pipeline.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class ActiveOrganizationResolutionTests : IDisposable
{
    private readonly TwoOrgsFixture _fixture = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Persisted_selection_is_honoured_when_the_membership_exists()
    {
        await SetPersistedSelectionAsync(TwoOrgsFixture.OrgAOwnerUserId, TwoOrgsFixture.OrgAId);

        var resolved = await CreateDirectory().ResolveAsync(TwoOrgsFixture.OrgAOwnerUserId, CancellationToken);

        Assert.Equal(TwoOrgsFixture.OrgAId, resolved.Active?.OrganizationId);
        Assert.Equal(TwoOrgsFixture.OrgAId, await ReadPersistedSelectionAsync(TwoOrgsFixture.OrgAOwnerUserId));
        Assert.Equal(
            new[] { TwoOrgsFixture.OrgAId },
            resolved.Memberships.Select(membership => membership.OrganizationId).ToArray());
    }

    [Fact]
    public async Task A_stale_selection_falls_back_to_a_valid_membership_and_is_repaired()
    {
        // bob is a Member of A; point him at B (not a member) as a forged/stale value would.
        await SetPersistedSelectionAsync(TwoOrgsFixture.OrgAMemberUserId, TwoOrgsFixture.OrgBId);

        var resolved = await CreateDirectory().ResolveAsync(TwoOrgsFixture.OrgAMemberUserId, CancellationToken);

        Assert.Equal(TwoOrgsFixture.OrgAId, resolved.Active?.OrganizationId);
        Assert.Equal(OrganizationRole.Member, resolved.Active?.Role);
        Assert.Equal(TwoOrgsFixture.OrgAId, await ReadPersistedSelectionAsync(TwoOrgsFixture.OrgAMemberUserId));
    }

    [Fact]
    public async Task Switching_to_an_organization_the_user_does_not_belong_to_is_rejected()
    {
        await SetPersistedSelectionAsync(TwoOrgsFixture.OrgAMemberUserId, TwoOrgsFixture.OrgAId);

        var directory = CreateDirectory();
        var selected = await directory.SwitchAsync(
            TwoOrgsFixture.OrgAMemberUserId,
            TwoOrgsFixture.OrgBId,
            CancellationToken);

        Assert.Null(selected);
        Assert.Equal(
            TwoOrgsFixture.OrgAId,
            await ReadPersistedSelectionAsync(TwoOrgsFixture.OrgAMemberUserId));
    }

    [Fact]
    public async Task Switching_to_a_valid_membership_persists_the_selection()
    {
        var directory = CreateDirectory();

        // carol owns B, so switching her is a legitimate move; resolve it and read it back.
        await SetPersistedSelectionAsync(TwoOrgsFixture.OrgBOwnerUserId, TwoOrgsFixture.OrgAId);
        var carolSwitch = await directory.SwitchAsync(
            TwoOrgsFixture.OrgBOwnerUserId,
            TwoOrgsFixture.OrgBId,
            CancellationToken);

        Assert.Equal(TwoOrgsFixture.OrgBId, carolSwitch?.OrganizationId);
        Assert.Equal(
            TwoOrgsFixture.OrgBId,
            await ReadPersistedSelectionAsync(TwoOrgsFixture.OrgBOwnerUserId));
    }

    [Fact]
    public async Task A_revoked_membership_leaves_no_active_organization()
    {
        await SetPersistedSelectionAsync(TwoOrgsFixture.OrgAMemberUserId, TwoOrgsFixture.OrgAId);

        await RevokeMembershipAsync(TwoOrgsFixture.OrgAMemberUserId);

        var resolved = await CreateDirectory().ResolveAsync(TwoOrgsFixture.OrgAMemberUserId, CancellationToken);

        Assert.Null(resolved.Active);
        Assert.Empty(resolved.Memberships);
        Assert.Null(await ReadPersistedSelectionAsync(TwoOrgsFixture.OrgAMemberUserId));
    }

    [Fact]
    public async Task UserSession_re_resolves_so_a_revoked_member_loses_read_access_without_a_reload()
    {
        var tenantContext = new TenantContext();
        var session = new UserSession(
            new FixedAuthenticationStateProvider(TwoOrgsFixture.OrgAMemberUserId, "bob@example.test"),
            CreateDirectory(),
            tenantContext);

        var first = await session.GetAsync();
        Assert.Equal(TwoOrgsFixture.OrgAId, first?.Organization?.OrganizationId);
        Assert.Equal(TwoOrgsFixture.OrgAId, tenantContext.OrganizationId);
        var revisionAfterFirstResolution = tenantContext.TenantRevision;

        // Another Owner removes bob while this "circuit" stays open.
        await RevokeMembershipAsync(TwoOrgsFixture.OrgAMemberUserId);

        var second = await session.GetAsync();

        Assert.Null(second?.Organization);
        Assert.False(second?.HasOrganization ?? true);
        Assert.Null(tenantContext.OrganizationId);
        Assert.True(tenantContext.TenantRevision > revisionAfterFirstResolution);
    }

    [Fact]
    public async Task The_switcher_list_contains_only_the_users_own_memberships()
    {
        var organizations = await CreateDirectory().ListForUserAsync(
            TwoOrgsFixture.OrgAOwnerUserId,
            CancellationToken);

        var organization = Assert.Single(organizations);
        Assert.Equal(TwoOrgsFixture.OrgAId, organization.OrganizationId);
        Assert.Equal(TwoOrgsFixture.OrgAName, organization.Name);
        Assert.Equal(OrganizationRole.Owner, organization.Role);
    }

    private UserOrganizationDirectory CreateDirectory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZenkinsData(_fixture.Configuration, Path.GetTempPath());
        services.AddScoped<UserOrganizationDirectory>();

        // A scope is what a request/circuit would get; keep the provider alive for the test.
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var scope = provider.CreateScope();
        _scopes.Add(scope);

        return scope.ServiceProvider.GetRequiredService<UserOrganizationDirectory>();
    }

    private readonly List<ServiceProvider> _providers = [];

    private readonly List<IServiceScope> _scopes = [];

    private async Task RevokeMembershipAsync(string userId)
    {
        using var scope = _fixture.SystemDataScope.BeginScope();
        await using var context = _fixture.CreateContext();
        var membership = await context.SystemQueryAsync<OrganizationMember>(
            row => row.UserId == userId,
            CancellationToken);
        context.OrganizationMembers.Remove(membership.Single());
        await context.SaveChangesAsync(CancellationToken);
    }

    private async Task SetPersistedSelectionAsync(string userId, Guid organizationId)
    {
        using var scope = _fixture.SystemDataScope.BeginScope();
        await using var context = _fixture.CreateContext();
        var user = await context.Users.SingleAsync(candidate => candidate.Id == userId, CancellationToken);
        user.ActiveOrganizationId = organizationId;
        await context.SaveChangesAsync(CancellationToken);
    }

    private async Task<Guid?> ReadPersistedSelectionAsync(string userId)
    {
        await using var context = _fixture.CreateContext();
        return (await context.Users.SingleAsync(candidate => candidate.Id == userId, CancellationToken))
            .ActiveOrganizationId;
    }

    private sealed class FixedAuthenticationStateProvider(string userId, string? email)
        : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
            if (email is not null)
            {
                claims.Add(new Claim(ClaimTypes.Email, email));
            }

            var identity = new ClaimsIdentity(claims, authenticationType: "Zenkins.Tests");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _fixture.Dispose();
    }
}
