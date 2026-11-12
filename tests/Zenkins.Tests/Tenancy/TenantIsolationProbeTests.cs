using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-001's data-layer probes, driven entirely by <see cref="TwoOrgsFixture"/> and the generic
/// helpers in <see cref="TenantIsolationProbes"/>. One class fixture serves the whole class: every
/// probe here reads, or attempts a write that must be rejected, so no test mutates the seeded rows.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationProbeTests : IClassFixture<TwoOrgsFixture>
{
    private readonly TwoOrgsFixture _fixture;

    public TenantIsolationProbeTests(TwoOrgsFixture fixture) => _fixture = fixture;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public Task Organization_memberships_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<OrganizationMember>(_fixture, CancellationToken);

    [Fact]
    public Task Projects_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<Project>(_fixture, CancellationToken);

    [Fact]
    public Task Cross_tenant_membership_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<OrganizationMember>(
            _fixture,
            () => new OrganizationMember
            {
                // A user who belongs to neither organization: only the foreign OrganizationId can
                // make this insert interesting, and the guard must reject it before it is sent.
                UserId = "mallory",
                Role = OrganizationRole.Member,
            },
            CancellationToken);

    [Fact]
    public Task Cross_tenant_project_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<Project>(
            _fixture,
            () => new Project
            {
                Name = "smuggled-app",
                RepositoryUrl = "https://example.test/smuggled.git",
                DefaultBranch = "main",
            },
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_memberships() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<OrganizationMember>(
            _fixture,
            TwoOrgsFixture.OrgAOwnerMembershipId,
            TwoOrgsFixture.OrgBOwnerMembershipId,
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_projects() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<Project>(
            _fixture,
            TwoOrgsFixture.ProjectAId,
            TwoOrgsFixture.ProjectBId,
            CancellationToken);

    [Fact]
    public void Every_tenant_owned_entity_is_covered_by_the_harness()
    {
        // A later card that adds a tenant-owned entity without a probe would otherwise widen the
        // isolation surface with no test noticing. The registry is explicit so the diff shows it.
        var tenantOwnedEntityTypes = typeof(ZenkinsDbContext).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && typeof(ITenantOwned).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        var coveredEntityTypes = TenantIsolationProbes.CoveredEntityTypes
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(tenantOwnedEntityTypes, coveredEntityTypes);
    }

    [Fact]
    public async Task Fixture_seeds_both_organizations_their_people_and_their_projects()
    {
        // Pins the shape later cards read as prose: the probes above are only meaningful if this
        // seed is what the fixture claims it is.
        var memberships = await _fixture.ReadAcrossTenantsAsync<OrganizationMember>(
            _ => true, CancellationToken);
        Assert.Contains(memberships, membership =>
            membership.OrganizationId == TwoOrgsFixture.OrgAId
            && membership.UserId == TwoOrgsFixture.OrgAOwnerUserId
            && membership.Role == OrganizationRole.Owner);
        Assert.Contains(memberships, membership =>
            membership.OrganizationId == TwoOrgsFixture.OrgAId
            && membership.UserId == TwoOrgsFixture.OrgAMemberUserId
            && membership.Role == OrganizationRole.Member);
        Assert.Contains(memberships, membership =>
            membership.OrganizationId == TwoOrgsFixture.OrgBId
            && membership.UserId == TwoOrgsFixture.OrgBOwnerUserId
            && membership.Role == OrganizationRole.Owner);

        var projects = await _fixture.ReadAcrossTenantsAsync<Project>(_ => true, CancellationToken);
        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, project =>
            project.Id == TwoOrgsFixture.ProjectAId
            && project.OrganizationId == TwoOrgsFixture.OrgAId
            && project.Name == TwoOrgsFixture.ProjectAName);
        Assert.Contains(projects, project =>
            project.Id == TwoOrgsFixture.ProjectBId
            && project.OrganizationId == TwoOrgsFixture.OrgBId
            && project.Name == TwoOrgsFixture.ProjectBName);
    }
}
