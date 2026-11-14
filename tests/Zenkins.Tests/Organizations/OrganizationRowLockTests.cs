using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Organizations;
using Zenkins.Tests.Tenancy;

namespace Zenkins.Tests.Organizations;

/// <summary>
/// F-002 review B1: the last-owner guard must be atomic on PostgreSQL. These tests pin the two halves
/// of the fix without a live PostgreSQL server:
/// <list type="number">
/// <item>the statement executed by <see cref="OrganizationRowLock"/> under Npgsql is
/// <c>SELECT ... FOR UPDATE</c> on the organization row;</item>
/// <item>both owner-affecting mutations (role change and removal) actually request that lock.</item>
/// </list>
/// The PostgreSQL race itself remains fixed-by-construction, not end-to-end verified; the card says so.
/// </summary>
[Trait("Category", "Organizations")]
public sealed class OrganizationRowLockTests : IDisposable
{
    private readonly TwoOrgsFixture _fixture = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Lock_query_is_a_for_update_on_the_organization_row_under_npgsql()
    {
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins")
            .Options;

        using var context = new ZenkinsDbContext(options, new TenantContext(), new SystemDataScope());
        var organizationId = TwoOrgsFixture.OrgAId;

        var sql = OrganizationRowLock.BuildLockQuery(context, organizationId).ToQueryString();

        Assert.Contains("FOR UPDATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Organizations", sql, StringComparison.Ordinal);
        Assert.Contains(organizationId.ToString(), sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Role_change_and_removal_both_take_the_organization_lock()
    {
        var recorder = new RecordingOrganizationRowLock();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZenkinsData(_fixture.Configuration);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton<IOrganizationRowLock>(recorder);
        services.AddScoped<OrganizationManagementService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantContext.OrganizationId = TwoOrgsFixture.OrgAId;
        var management = scope.ServiceProvider.GetRequiredService<OrganizationManagementService>();

        using (_fixture.SystemDataScope.BeginScope())
        {
            await using var context = _fixture.CreateContext();
            var bob = await context.SystemQueryAsync<OrganizationMember>(
                member => member.UserId == TwoOrgsFixture.OrgAMemberUserId,
                CancellationToken);
            Assert.Single(bob);

            var promotion = await management.ChangeRoleAsync(
                TwoOrgsFixture.OrgAOwnerUserId,
                bob[0].Id.ToString(),
                nameof(OrganizationRole.Owner),
                CancellationToken);
            Assert.True(promotion.Succeeded, promotion.ErrorCode);

            // Two Owners now, so removing bob is allowed and exercises the removal path.
            var removal = await management.RemoveMemberAsync(
                TwoOrgsFixture.OrgAOwnerUserId,
                bob[0].Id.ToString(),
                CancellationToken);
            Assert.True(removal.Succeeded, removal.ErrorCode);
        }

        Assert.Equal(
            [TwoOrgsFixture.OrgAId, TwoOrgsFixture.OrgAId],
            recorder.LockedOrganizationIds);
    }

    private sealed class RecordingOrganizationRowLock : IOrganizationRowLock
    {
        public List<Guid> LockedOrganizationIds { get; } = [];

        public Task LockAsync(
            ZenkinsDbContext context,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            LockedOrganizationIds.Add(organizationId);
            return Task.CompletedTask;
        }
    }

    public void Dispose() => _fixture.Dispose();
}
