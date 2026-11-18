using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// The reusable probes of the isolation harness (card Q-001). Each probe is generic over an
/// <see cref="ITenantOwned"/> entity and derives its expectations from the seeded fixture through
/// the system scope instead of hard-coded row counts, so a new entity joins the harness with one
/// line per probe:
/// <code>
/// [Fact] public Task Projects_are_isolated() =>
///     TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync&lt;Project&gt(_fixture);
/// </code>
/// Add the entity to <see cref="CoveredEntityTypes"/> too; a reflection guard fails until every
/// tenant-owned entity is covered, so a later card cannot silently widen the isolation surface
/// without a probe.
/// </summary>
public static class TenantIsolationProbes
{
    /// <summary>
    /// Every tenant-owned entity the harness probes. Kept explicit (no reflection here) because the
    /// probe bodies are generic and a new entity needs a reviewed line, not an invisible discovery.
    /// </summary>
    public static IReadOnlyList<Type> CoveredEntityTypes { get; } =
        [
            typeof(OrganizationMember), typeof(Project), typeof(BuildRun), typeof(BuildLog),
            typeof(BuildStep), typeof(WorkerEntity), typeof(WorkerToken), typeof(Secret), typeof(GitCredential),
            typeof(Artifact), typeof(Webhook), typeof(WebhookDelivery),

            // F-027: the organization API's credential and its idempotency record. Probed by
            // TenantIsolationApiProbeTests (a foreign token is refused; a foreign project/run id is one
            // bare 404 on every route; a key binds only inside the token that sent it).
            typeof(ApiToken), typeof(ApiIdempotencyRecord),

            // F-024: the per-project cron schedules and their due-time ledger. Probed by
            // TenantIsolationProbeTests (each tenant sees only its own; a cross-tenant write is refused;
            // a Find after a tenant switch finds nothing).
            typeof(ProjectSchedule), typeof(ScheduleOccurrence),

            // F-023: the branch jobs of a multibranch project - the same three probes, plus the branch
            // list's own project+tenant scope (the same branch name in another project never appears).
            typeof(BranchJob),

            // F-025: the polled branch heads and the change ledger shared by polling and webhooks. The
            // same three probes as every other tenant entity, plus the one that matters for this card: the
            // ledger's unique key is (ProjectId, Branch, Commit), so two tenants observing the same commit
            // must both be allowed to row it and neither may read the other's.
            typeof(BranchHead), typeof(ChangeTrigger),

            // F-034: the organization environment variables - the same three probes, plus two the other
            // entities do not need: the snapshot reader the trigger uses returns only the active tenant's
            // layer, and the page/mutation probes live in TenantIsolationHttpSurfaceProbeTests.
            typeof(OrganizationEnvironmentVariable),

            // F-035: the organization's reusable step snippets. Probed by TenantIsolationProbeTests (the
            // generic read/write/find trio) and TenantIsolationHttpSurfaceProbeTests (a foreign id in the
            // mutation routes is one bare 404 and nothing is written; the page renders the active org's own
            // snippets only). Nothing references a snippet at run time, so the row is the whole surface.
            typeof(StepSnippet),

            // F-026: the chain edges and the outbox obligation behind them. The HTTP half is probed by
            // TenantIsolationLinkProbeTests (a foreign project id in the route, in the body or in the link id is
            // one bare 404 and nothing is written, and the settings page renders no foreign edge); the rows
            // themselves get the generic write probe in TenantIsolationProbeTests, like every other entity here.
            // F-042: grouping folders, probed the same way and through TenantIsolationHttpSurfaceProbeTests for
            // the route (a foreign folder page is a bare 404 and no mutation can reach another tenant's folder).
            typeof(ProjectLink), typeof(OutboxMessage), typeof(Folder),

            // F-039: the outbound notification endpoints. The HTTP half is probed by
            // Notification_endpoints_cannot_reach_another_tenants_project (a foreign project or endpoint id is one bare
            // 404 on every form, and the settings card renders no foreign receiver); the row itself gets the generic
            // write probe below, like every other entity here.
            typeof(NotificationEndpoint),
        ];

    /// <summary>
    /// The read probe: an unfiltered query returns exactly the active tenant's rows, for A and then
    /// for B, against the same fixture and with no <c>Where</c> clause anywhere.
    /// </summary>
    public static async Task AssertActiveTenantSeesOnlyOwnRowsAsync<TEntity>(
        TwoOrgsFixture fixture,
        CancellationToken cancellationToken = default)
        where TEntity : class, ITenantOwned
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var expectations = await TenantRowCountsAsync<TEntity>(fixture, cancellationToken);
        if (expectations.Count == 0)
        {
            throw new InvalidOperationException(
                $"The harness seeds no '{typeof(TEntity).Name}' rows, so the read probe would pass "
                + "vacuously. Seed one row per organization in TwoOrgsFixture.Seed and expect a row "
                + "count above zero here.");
        }

        foreach (var (organizationId, expectedCount) in expectations)
        {
            fixture.UseOrganization(organizationId);

            await using var context = fixture.CreateContext();

            // No Where clause on purpose: isolation must come from the global query filter.
            var rows = await context.Set<TEntity>().ToListAsync(cancellationToken);

            Assert.Equal(expectedCount, rows.Count);
            Assert.All(rows, row => Assert.Equal(organizationId, row.OrganizationId));
        }
    }

    /// <summary>
    /// The write probe: an insert that names the *other* organization while the active tenant is the
    /// current one is rejected, and neither tenant's row count changes. Probed in both directions, so
    /// the harness cannot pass by having the guard only cover one organization.
    /// </summary>
    public static async Task AssertCrossTenantWriteRejectedAsync<TEntity>(
        TwoOrgsFixture fixture,
        Func<TEntity> createRow,
        CancellationToken cancellationToken = default)
        where TEntity : class, ITenantOwned
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(createRow);

        foreach (var (activeOrganizationId, foreignOrganizationId) in TenantPairs())
        {
            var activeBefore = await fixture.CountAcrossTenantsAsync<TEntity>(activeOrganizationId, cancellationToken);
            var foreignBefore = await fixture.CountAcrossTenantsAsync<TEntity>(foreignOrganizationId, cancellationToken);

            fixture.UseOrganization(activeOrganizationId);
            await using (var context = fixture.CreateContext())
            {
                var row = createRow();
                row.OrganizationId = foreignOrganizationId;
                context.Set<TEntity>().Add(row);

                Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
            }

            // Nothing persisted: asserted through the active tenant's own filtered view *and*
            // through the unfiltered system read of both organizations.
            fixture.UseOrganization(activeOrganizationId);
            await using (var context = fixture.CreateContext())
            {
                Assert.Equal(activeBefore, await context.Set<TEntity>().CountAsync(cancellationToken));
            }

            Assert.Equal(
                activeBefore,
                await fixture.CountAcrossTenantsAsync<TEntity>(activeOrganizationId, cancellationToken));
            Assert.Equal(
                foreignBefore,
                await fixture.CountAcrossTenantsAsync<TEntity>(foreignOrganizationId, cancellationToken));
        }
    }

    /// <summary>
    /// The tracked-row probe (I-002's guarantee): a row loaded under A must not come back from
    /// <c>Find</c>/<c>FindAsync</c> after the tenant is switched to B, in either direction, on the
    /// same context instance.
    /// </summary>
    public static async Task AssertFindAfterTenantSwitchReturnsNullAsync<TEntity>(
        TwoOrgsFixture fixture,
        Guid rowOfA,
        Guid rowOfB,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(fixture);

        fixture.UseOrganizationA();
        await using var context = fixture.CreateContext();

        Assert.NotNull(await context.FindAsync<TEntity>([rowOfA], cancellationToken));
        Assert.Null(await context.FindAsync<TEntity>([rowOfB], cancellationToken));

        fixture.UseOrganizationB();

        // Find short-circuits in the change tracker, so only dropping the tracker makes these null.
        Assert.Null(await context.FindAsync<TEntity>([rowOfA], cancellationToken));
        Assert.Null(context.Find<TEntity>(rowOfA));
        Assert.NotNull(await context.FindAsync<TEntity>([rowOfB], cancellationToken));

        fixture.UseOrganizationA();

        Assert.Null(await context.FindAsync<TEntity>([rowOfB], cancellationToken));
        Assert.Null(context.Find<TEntity>(rowOfB));
        Assert.NotNull(await context.FindAsync<TEntity>([rowOfA], cancellationToken));
    }

    /// <summary>
    /// Reads every tenant's row count for <typeparamref name="TEntity"/>, keyed by organization.
    /// Organizations with no rows are dropped so callers only assert on tenants the fixture seeds.
    /// </summary>
    private static async Task<List<(Guid OrganizationId, int Count)>> TenantRowCountsAsync<TEntity>(
        TwoOrgsFixture fixture,
        CancellationToken cancellationToken)
        where TEntity : class, ITenantOwned
    {
        var rows = await fixture.ReadAcrossTenantsAsync<TEntity>(_ => true, cancellationToken);
        return rows
            .GroupBy(row => row.OrganizationId)
            .Select(group => (OrganizationId: group.Key, Count: group.Count()))
            .ToList();
    }

    private static IEnumerable<(Guid Active, Guid Foreign)> TenantPairs()
    {
        yield return (TwoOrgsFixture.OrgAId, TwoOrgsFixture.OrgBId);
        yield return (TwoOrgsFixture.OrgBId, TwoOrgsFixture.OrgAId);
    }
}
