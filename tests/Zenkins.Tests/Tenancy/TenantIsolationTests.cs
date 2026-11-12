using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Isolation tests for card I-002 (TC-001..TC-003), refactored onto card Q-001's
/// <see cref="TwoOrgsFixture"/>: one seed routine, one temporary SQLite file per test, and no local
/// copy of the two-organization setup. Every query goes through the real
/// <see cref="ZenkinsDbContext"/> filter rather than a hand-written <c>Where</c>.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationTests : IDisposable
{
    // A fixture per test, not per class: these tests mutate rows (inserts, renames), and a shared
    // file would make them order-dependent. The read-only probes use IClassFixture instead.
    private readonly TwoOrgsFixture _fixture = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Query_filter_returns_only_the_active_tenant_and_follows_a_tenant_switch()
    {
        _fixture.UseOrganizationA();
        await using var context = _fixture.CreateContext();

        // No Where clause on purpose: the isolation must come from the query filter.
        var projectsOfA = await context.Projects.ToListAsync(CancellationToken);
        var projectOfA = Assert.Single(projectsOfA);
        Assert.Equal(TwoOrgsFixture.OrgAId, projectOfA.OrganizationId);
        Assert.Equal(TwoOrgsFixture.ProjectAName, projectOfA.Name);

        // Re-selecting the same organization is not a tenant switch and must not bump the revision.
        var revisionBeforeReselect = _fixture.TenantContext.TenantRevision;
        _fixture.UseOrganizationA();
        Assert.Equal(revisionBeforeReselect, _fixture.TenantContext.TenantRevision);

        // Re-point the active tenant without rebuilding the context or the model. A filter that
        // captured ITenantContext at model-build time would still return organization A's project.
        _fixture.UseOrganizationB();

        var projectsOfB = await context.Projects.ToListAsync(CancellationToken);
        var projectOfB = Assert.Single(projectsOfB);
        Assert.Equal(TwoOrgsFixture.OrgBId, projectOfB.OrganizationId);
        Assert.Equal(TwoOrgsFixture.ProjectBName, projectOfB.Name);
        Assert.NotEqual(projectOfA.Id, projectOfB.Id);
    }

    [Fact]
    public void Cross_tenant_insert_throws_and_persists_nothing()
    {
        _fixture.UseOrganizationA();

        using (var context = _fixture.CreateContext())
        {
            context.Projects.Add(new Project
            {
                OrganizationId = TwoOrgsFixture.OrgBId,
                Name = "smuggled",
                RepositoryUrl = TwoOrgsFixture.ProjectARepositoryUrl,
                DefaultBranch = "main",
            });

            Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
        }

        // Verified through the tenant's own filtered view, not through the system escape hatch.
        using var verification = _fixture.CreateContext();
        var project = Assert.Single(verification.Projects.ToList());
        Assert.Equal(TwoOrgsFixture.ProjectAName, project.Name);
        Assert.Equal(TwoOrgsFixture.OrgAId, project.OrganizationId);
    }

    [Fact]
    public void Cross_tenant_update_throws_even_when_the_caller_fakes_the_original_value()
    {
        _fixture.UseOrganizationA();

        Guid projectOfAId;
        using (var seed = _fixture.CreateContext())
        {
            projectOfAId = seed.Projects.Single().Id;
        }

        // Organization B's session attaches organization A's row and claims the row is its own.
        _fixture.UseOrganizationB();
        using (var context = _fixture.CreateContext())
        {
            var impostor = new Project
            {
                Id = projectOfAId,
                OrganizationId = TwoOrgsFixture.OrgBId,
                Name = "hijacked",
                RepositoryUrl = TwoOrgsFixture.ProjectARepositoryUrl,
                DefaultBranch = "main",
            };

            context.Attach(impostor);
            context.Entry(impostor).State = EntityState.Modified;
            context.Entry(impostor).Property(project => project.OrganizationId).OriginalValue =
                TwoOrgsFixture.OrgBId;

            Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
        }

        _fixture.UseOrganizationA();
        using var verification = _fixture.CreateContext();
        var untouched = verification.Projects.Single();
        Assert.Equal(TwoOrgsFixture.ProjectAName, untouched.Name);
        Assert.Equal(TwoOrgsFixture.OrgAId, untouched.OrganizationId);
    }

    [Fact]
    public async Task Cross_tenant_organization_delete_and_update_are_rejected()
    {
        _fixture.UseOrganizationA();

        using (var context = _fixture.CreateContext())
        {
            // Organizations stay readable without a tenant (needed by the switcher), so the write
            // guard is the only thing standing between tenant A and the cascade delete of tenant B.
            context.Organizations.Remove(
                context.Organizations.Single(organization => organization.Id == TwoOrgsFixture.OrgBId));
            Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
        }

        using (var context = _fixture.CreateContext())
        {
            context.Organizations.Single(organization => organization.Id == TwoOrgsFixture.OrgBId).Name =
                "renamed across tenants";
            Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
        }

        using var verification = _fixture.CreateContext();
        Assert.Equal(
            TwoOrgsFixture.OrgBName,
            verification.Organizations.Single(organization => organization.Id == TwoOrgsFixture.OrgBId).Name);

        using (_fixture.SystemDataScope.BeginScope())
        {
            var survivingProjects = await verification.SystemQueryAsync<Project>(
                project => project.OrganizationId == TwoOrgsFixture.OrgBId, CancellationToken);
            Assert.Single(survivingProjects);
        }
    }

    [Fact]
    public void Duplicate_membership_is_rejected_by_the_unique_index_sqlite()
    {
        _fixture.UseOrganizationA();

        using (var context = _fixture.CreateContext())
        {
            // alice already owns organization A (seeded by the fixture), so a second row for the
            // same (OrganizationId, UserId) pair must be refused by the database.
            context.OrganizationMembers.Add(new OrganizationMember
            {
                UserId = TwoOrgsFixture.OrgAOwnerUserId,
                Role = OrganizationRole.Member,
            });

            var exception = Assert.Throws<DbUpdateException>(() => context.SaveChanges());

            // Assert the specific unique index, not just "some DbUpdateException": once F-001 adds
            // the User foreign key, a generic assertion would keep passing for the wrong reason.
            var sqlite = Assert.IsType<SqliteException>(exception.InnerException);
            Assert.Equal(19, sqlite.SqliteErrorCode); // SQLITE_CONSTRAINT
            Assert.Contains(
                "UNIQUE constraint failed: OrganizationMembers.OrganizationId, OrganizationMembers.UserId",
                sqlite.Message,
                StringComparison.Ordinal);
        }

        using var verification = _fixture.CreateContext();
        var membersOfA = verification.OrganizationMembers.Where(member => member.UserId == TwoOrgsFixture.OrgAOwnerUserId)
            .ToList();
        var owner = Assert.Single(membersOfA);
        Assert.Equal(TwoOrgsFixture.OrgAId, owner.OrganizationId);
        Assert.Equal(OrganizationRole.Owner, owner.Role);
    }

    [Fact]
    public void Insert_stamps_the_active_organization_and_the_timestamps()
    {
        _fixture.UseOrganizationA();

        var stale = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var context = _fixture.CreateContext();
        var project = new Project
        {
            Name = "gamma-app",
            RepositoryUrl = TwoOrgsFixture.ProjectARepositoryUrl,
            DefaultBranch = "main",
            CreatedAt = stale,
            UpdatedAt = stale,
        };

        var insertStarted = DateTimeOffset.UtcNow;
        context.Projects.Add(project);
        context.SaveChanges();

        Assert.Equal(TwoOrgsFixture.OrgAId, project.OrganizationId);
        Assert.NotEqual(TwoOrgsFixture.OrgBId, project.OrganizationId);
        Assert.Equal("pipeline.lua", project.PipelinePath);
        Assert.InRange(project.CreatedAt, insertStarted, DateTimeOffset.UtcNow);
        Assert.InRange(project.UpdatedAt, insertStarted, DateTimeOffset.UtcNow);

        var createdAtAfterInsert = project.CreatedAt;
        var updateStarted = DateTimeOffset.UtcNow;
        project.Name = "gamma-app-renamed";
        context.SaveChanges();

        Assert.Equal(createdAtAfterInsert, project.CreatedAt);
        Assert.InRange(project.UpdatedAt, updateStarted, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Find_after_a_tenant_switch_does_not_return_the_previous_tenants_row()
    {
        _fixture.UseOrganizationA();

        await using var context = _fixture.CreateContext();
        var projectOfA = await context.Projects.SingleAsync(CancellationToken);

        // Loaded and tracked under organization A.
        Assert.NotNull(await context.Projects.FindAsync([projectOfA.Id], CancellationToken));

        _fixture.UseOrganizationB();

        // Find short-circuits in the change tracker, so only dropping the tracker makes this null.
        Assert.Null(await context.Projects.FindAsync([projectOfA.Id], CancellationToken));

        var projectOfB = Assert.Single(await context.Projects.ToListAsync(CancellationToken));
        Assert.Equal(TwoOrgsFixture.OrgBId, projectOfB.OrganizationId);
    }

    [Fact]
    public async Task Find_after_a_tenant_switch_does_not_leak_through_a_decorated_tenant_context()
    {
        _fixture.UseOrganizationA();

        // F-002 will replace/wrap ITenantContext; the protection must not depend on the concrete type.
        await using var context = _fixture.CreateContext(new ForwardingTenantContext(_fixture.TenantContext));
        var projectOfA = await context.Projects.SingleAsync(CancellationToken);
        Assert.NotNull(await context.Projects.FindAsync([projectOfA.Id], CancellationToken));

        _fixture.UseOrganizationB();

        // The non-generic overloads bypass Set<T>(), so they pin the sampler themselves. They must be
        // asserted before any Set<T>() access, otherwise the tracker would already be cleared.
        Assert.Null(context.Find(typeof(Project), projectOfA.Id));
        Assert.Null(await context.FindAsync(typeof(Project), [projectOfA.Id], CancellationToken));
        Assert.Null(context.Find<Project>(projectOfA.Id));
        Assert.Null(await context.Projects.FindAsync([projectOfA.Id], CancellationToken));
        Assert.Equal(
            TwoOrgsFixture.OrgBId,
            Assert.Single(await context.Projects.ToListAsync(CancellationToken)).OrganizationId);
    }

    [Fact]
    public void Generic_find_is_the_first_tracker_access_after_a_switch_and_still_returns_null()
    {
        _fixture.UseOrganizationA();

        using var context = _fixture.CreateContext();
        var projectOfA = context.Projects.Single();

        _fixture.UseOrganizationB();

        // Deliberately the first access on this context after the switch: no Set<T>() call has run,
        // so the tracker still holds tenant A's project. Find<T> must not return it. This pins the
        // EF Core 10 behaviour that the generic overload goes through Set<T>(); if a future EF
        // version changes that, this test fails instead of silently losing coverage.
        Assert.Null(context.Find<Project>(projectOfA.Id));
        Assert.Equal(
            TwoOrgsFixture.OrgBId,
            Assert.Single(context.Projects.ToList()).OrganizationId);
    }

    [Fact]
    public void Switching_tenant_with_unsaved_changes_throws_instead_of_discarding_them()
    {
        _fixture.UseOrganizationA();

        using var context = _fixture.CreateContext();
        context.Projects.Single().Name = "pending rename";

        var exception = Assert.Throws<TenantIsolationException>(() =>
        {
            _fixture.UseOrganizationB();
            _ = context.Projects.ToList();
        });

        Assert.StartsWith("Cannot switch the active organization with unsaved changes.", exception.Message);
        Assert.Equal(TwoOrgsFixture.OrgBId, _fixture.ActiveOrganizationId);
    }

    [Fact]
    public async Task System_query_requires_an_active_scope_and_returns_all_tenants_inside_it()
    {
        _fixture.UseOrganizationA();
        await using var context = _fixture.CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SystemQueryAsync<Project>(_ => true, CancellationToken));

        using (_fixture.SystemDataScope.BeginScope())
        {
            var all = await context.SystemQueryAsync<Project>(_ => true, CancellationToken);
            Assert.Equal(2, all.Count);
            Assert.Contains(all, project => project.OrganizationId == TwoOrgsFixture.OrgAId);
            Assert.Contains(all, project => project.OrganizationId == TwoOrgsFixture.OrgBId);
        }
    }

    [Fact]
    public void Data_layer_registration_is_scoped_and_exposes_a_scoped_context_factory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZenkinsData(_fixture.Configuration, Path.GetTempPath());

        using var provider = services.BuildServiceProvider();
        using var firstRequest = provider.CreateScope();
        using var secondRequest = provider.CreateScope();

        var firstContext = firstRequest.ServiceProvider.GetRequiredService<ZenkinsDbContext>();
        Assert.Same(firstContext, firstRequest.ServiceProvider.GetRequiredService<ZenkinsDbContext>());
        Assert.NotSame(firstContext, secondRequest.ServiceProvider.GetRequiredService<ZenkinsDbContext>());

        var factory = firstRequest.ServiceProvider.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>();
        using var operationContext = factory.CreateDbContext();
        using var secondOperationContext = factory.CreateDbContext();
        Assert.NotSame(operationContext, secondOperationContext);

        // The concrete type and the interface must be the same scoped instance, otherwise switching
        // through the concrete type would not affect anything that consumes ITenantContext.
        var concreteTenant = firstRequest.ServiceProvider.GetRequiredService<TenantContext>();
        Assert.Same(concreteTenant, firstRequest.ServiceProvider.GetRequiredService<ITenantContext>());

        // A singleton tenant context would leak one request's organization into every other request.
        Assert.NotSame(
            firstRequest.ServiceProvider.GetRequiredService<ITenantContext>(),
            secondRequest.ServiceProvider.GetRequiredService<ITenantContext>());

        Assert.Equal(DatabaseProvider.Sqlite, provider.GetRequiredService<ZenkinsDataOptions>().Provider);
    }

    [Fact]
    public void Factory_created_contexts_follow_the_current_tenant()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZenkinsData(_fixture.Configuration, Path.GetTempPath());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Resolve through the interface, switch through the concrete registration (F-001 does the same).
        Assert.IsType<TenantContext>(scope.ServiceProvider.GetRequiredService<ITenantContext>());
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>();

        tenantContext.OrganizationId = TwoOrgsFixture.OrgAId;
        using (var context = factory.CreateDbContext())
        {
            Assert.Equal(
                TwoOrgsFixture.OrgAId,
                Assert.Single(context.Projects.ToList()).OrganizationId);
        }

        // A new per-operation context must see the new tenant, not a cached model or stale tracker.
        tenantContext.OrganizationId = TwoOrgsFixture.OrgBId;
        using (var context = factory.CreateDbContext())
        {
            Assert.Equal(
                TwoOrgsFixture.OrgBId,
                Assert.Single(context.Projects.ToList()).OrganizationId);
        }
    }

    /// <summary>Stands in for F-002's decorator: a cast-free wrapper around the real tenant context.</summary>
    private sealed class ForwardingTenantContext(ITenantContext inner) : ITenantContext
    {
        public Guid? OrganizationId => inner.OrganizationId;

        public int TenantRevision => inner.TenantRevision;
    }

    public void Dispose() => _fixture.Dispose();
}
