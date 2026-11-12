using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Isolation tests for card I-002 (TC-001..TC-003). Every query goes through the real
/// <see cref="ZenkinsDbContext"/> filter rather than a hand-written <c>Where</c>, and every test
/// uses its own temporary SQLite file so nothing touches <c>App_Data</c>.
/// </summary>
public sealed class TenantIsolationTests : IDisposable
{
    private const string RepositoryUrl = "https://example.test/repository.git";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"zenkins-i002-{Guid.NewGuid():N}.db");

    private readonly TenantContext _tenantContext = new();
    private readonly SystemDataScope _systemDataScope = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Query_filter_returns_only_the_active_tenant_and_follows_a_tenant_switch()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();

        _tenantContext.OrganizationId = organizationA;
        using var context = CreateContext();

        // No Where clause on purpose: the isolation must come from the query filter.
        var projectsOfA = context.Projects.ToList();
        var projectOfA = Assert.Single(projectsOfA);
        Assert.Equal(organizationA, projectOfA.OrganizationId);

        // Re-selecting the same organization is not a tenant switch and must not bump the revision.
        var revisionBeforeReselect = _tenantContext.TenantRevision;
        _tenantContext.OrganizationId = organizationA;
        Assert.Equal(revisionBeforeReselect, _tenantContext.TenantRevision);

        // Re-point the active tenant without rebuilding the context or the model. A filter that
        // captured ITenantContext at model-build time would still return organization A's project.
        _tenantContext.OrganizationId = organizationB;

        var projectsOfB = context.Projects.ToList();
        var projectOfB = Assert.Single(projectsOfB);
        Assert.Equal(organizationB, projectOfB.OrganizationId);
        Assert.NotEqual(projectOfA.Id, projectOfB.Id);
    }

    [Fact]
    public void Cross_tenant_insert_throws_and_persists_nothing()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        using (var context = CreateContext())
        {
            context.Projects.Add(new Project
            {
                OrganizationId = organizationB,
                Name = "smuggled",
                RepositoryUrl = RepositoryUrl,
                DefaultBranch = "main",
            });

            Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
        }

        // Verified through the tenant's own filtered view, not through the system escape hatch.
        using var verification = CreateContext();
        var project = Assert.Single(verification.Projects.ToList());
        Assert.Equal("alpha-app", project.Name);
        Assert.Equal(organizationA, project.OrganizationId);
    }

    [Fact]
    public void Cross_tenant_update_throws_even_when_the_caller_fakes_the_original_value()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();

        Guid projectOfAId;
        using (var seed = CreateContext())
        {
            _tenantContext.OrganizationId = organizationA;
            projectOfAId = seed.Projects.Single().Id;
        }

        // Organization B's session attaches organization A's row and claims the row is its own.
        _tenantContext.OrganizationId = organizationB;
        using (var context = CreateContext())
        {
            var impostor = new Project
            {
                Id = projectOfAId,
                OrganizationId = organizationB,
                Name = "hijacked",
                RepositoryUrl = RepositoryUrl,
                DefaultBranch = "main",
            };

            context.Attach(impostor);
            context.Entry(impostor).State = EntityState.Modified;
            context.Entry(impostor).Property(project => project.OrganizationId).OriginalValue = organizationB;

            Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
        }

        using var verification = CreateContext();
        _tenantContext.OrganizationId = organizationA;
        var untouched = verification.Projects.Single();
        Assert.Equal("alpha-app", untouched.Name);
        Assert.Equal(organizationA, untouched.OrganizationId);
    }

    [Fact]
    public async Task Cross_tenant_organization_delete_and_update_are_rejected()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        using (var context = CreateContext())
        {
            // Organizations stay readable without a tenant (needed by the switcher), so the write
            // guard is the only thing standing between tenant A and the cascade delete of tenant B.
            context.Organizations.Remove(context.Organizations.Single(o => o.Id == organizationB));
            Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
        }

        using (var context = CreateContext())
        {
            context.Organizations.Single(o => o.Id == organizationB).Name = "renamed across tenants";
            Assert.Throws<TenantIsolationException>(() => context.SaveChanges());
        }

        using var verification = CreateContext();
        Assert.Equal("Beta", verification.Organizations.Single(o => o.Id == organizationB).Name);
        using (_systemDataScope.BeginScope())
        {
            var survivingProjects = await verification.SystemQueryAsync<Project>(
                project => project.OrganizationId == organizationB, CancellationToken);
            Assert.Single(survivingProjects);
        }
    }

    [Fact]
    public void Duplicate_membership_is_rejected_by_the_unique_index_sqlite()
    {
        var (organizationA, _) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        using (var context = CreateContext())
        {
            // The guard stamps OrganizationId from the active tenant; the caller does not set it.
            context.OrganizationMembers.Add(new OrganizationMember
            {
                UserId = "alice",
                Role = OrganizationRole.Owner,
            });
            context.SaveChanges();

            context.OrganizationMembers.Add(new OrganizationMember
            {
                UserId = "alice",
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

        using var verification = CreateContext();
        var membership = Assert.Single(verification.OrganizationMembers.ToList());
        Assert.Equal("alice", membership.UserId);
        Assert.Equal(OrganizationRole.Owner, membership.Role);
    }

    [Fact]
    public void Insert_stamps_the_active_organization_and_the_timestamps()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        var stale = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var context = CreateContext();
        var project = new Project
        {
            Name = "gamma-app",
            RepositoryUrl = RepositoryUrl,
            DefaultBranch = "main",
            CreatedAt = stale,
            UpdatedAt = stale,
        };

        var insertStarted = DateTimeOffset.UtcNow;
        context.Projects.Add(project);
        context.SaveChanges();

        Assert.Equal(organizationA, project.OrganizationId);
        Assert.NotEqual(organizationB, project.OrganizationId);
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
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        using var context = CreateContext();
        var projectOfA = context.Projects.Single();

        // Loaded and tracked under organization A.
        Assert.NotNull(await context.Projects.FindAsync([projectOfA.Id], CancellationToken));

        _tenantContext.OrganizationId = organizationB;

        // Find short-circuits in the change tracker, so only dropping the tracker makes this null.
        Assert.Null(await context.Projects.FindAsync([projectOfA.Id], CancellationToken));

        var projectOfB = Assert.Single(context.Projects.ToList());
        Assert.Equal(organizationB, projectOfB.OrganizationId);
    }

    [Fact]
    public async Task Find_after_a_tenant_switch_does_not_leak_through_a_decorated_tenant_context()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        // F-002 will replace/wrap ITenantContext; the protection must not depend on the concrete type.
        using var context = CreateContext(new ForwardingTenantContext(_tenantContext));
        var projectOfA = context.Projects.Single();
        Assert.NotNull(await context.Projects.FindAsync([projectOfA.Id], CancellationToken));

        _tenantContext.OrganizationId = organizationB;

        // The non-generic overloads bypass Set<T>(), so they pin the sampler themselves. They must be
        // asserted before any Set<T>() access, otherwise the tracker would already be cleared.
        Assert.Null(context.Find(typeof(Project), projectOfA.Id));
        Assert.Null(await context.FindAsync(typeof(Project), [projectOfA.Id], CancellationToken));
        Assert.Null(context.Find<Project>(projectOfA.Id));
        Assert.Null(await context.Projects.FindAsync([projectOfA.Id], CancellationToken));
        Assert.Equal(organizationB, Assert.Single(context.Projects.ToList()).OrganizationId);
    }

    [Fact]
    public void Generic_find_is_the_first_tracker_access_after_a_switch_and_still_returns_null()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        using var context = CreateContext();
        var projectOfA = context.Projects.Single();

        _tenantContext.OrganizationId = organizationB;

        // Deliberately the first access on this context after the switch: no Set<T>() call has run,
        // so the tracker still holds tenant A's project. Find<T> must not return it. This pins the
        // EF Core 10 behaviour that the generic overload goes through Set<T>(); if a future EF
        // version changes that, this test fails instead of silently losing coverage.
        Assert.Null(context.Find<Project>(projectOfA.Id));
        Assert.Equal(organizationB, Assert.Single(context.Projects.ToList()).OrganizationId);
    }

    [Fact]
    public void Switching_tenant_with_unsaved_changes_throws_instead_of_discarding_them()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        using var context = CreateContext();
        context.Projects.Single().Name = "pending rename";

        var exception = Assert.Throws<TenantIsolationException>(() =>
        {
            _tenantContext.OrganizationId = organizationB;
            _ = context.Projects.ToList();
        });

        Assert.StartsWith("Cannot switch the active organization with unsaved changes.", exception.Message);
        Assert.Equal(organizationB, _tenantContext.OrganizationId);
    }

    [Fact]
    public async Task System_query_requires_an_active_scope_and_returns_all_tenants_inside_it()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;
        using var context = CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SystemQueryAsync<Project>(_ => true, CancellationToken));

        using (_systemDataScope.BeginScope())
        {
            var all = await context.SystemQueryAsync<Project>(_ => true, CancellationToken);
            Assert.Equal(2, all.Count);
            Assert.Contains(all, project => project.OrganizationId == organizationA);
            Assert.Contains(all, project => project.OrganizationId == organizationB);
        }
    }

    [Fact]
    public void Data_layer_registration_is_scoped_and_exposes_a_scoped_context_factory()
    {
        var configuration = CreateConfiguration();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZenkinsData(configuration, Path.GetTempPath());

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
        var (organizationA, organizationB) = SeedTwoOrganizations();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZenkinsData(CreateConfiguration(), Path.GetTempPath());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Resolve through the interface, switch through the concrete registration (F-001 does the same).
        Assert.IsType<TenantContext>(scope.ServiceProvider.GetRequiredService<ITenantContext>());
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>();

        tenantContext.OrganizationId = organizationA;
        using (var context = factory.CreateDbContext())
        {
            Assert.Equal(organizationA, Assert.Single(context.Projects.ToList()).OrganizationId);
        }

        // A new per-operation context must see the new tenant, not a cached model or stale tracker.
        tenantContext.OrganizationId = organizationB;
        using (var context = factory.CreateDbContext())
        {
            Assert.Equal(organizationB, Assert.Single(context.Projects.ToList()).OrganizationId);
        }
    }

    private IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ZenkinsDataOptions.ProviderConfigurationKey] = "Sqlite",
                [ZenkinsDataOptions.ConnectionStringConfigurationKey] = $"Data Source={_databasePath}",
            })
            .Build();

    private (Guid OrganizationA, Guid OrganizationB) SeedTwoOrganizations()
    {
        var organizationA = new Organization { Name = "Alpha" };
        var organizationB = new Organization { Name = "Beta" };

        using var context = CreateContext();
        using (_systemDataScope.BeginScope())
        {
            context.Organizations.AddRange(organizationA, organizationB);
            context.Projects.AddRange(
                new Project
                {
                    OrganizationId = organizationA.Id,
                    Name = "alpha-app",
                    RepositoryUrl = RepositoryUrl,
                    DefaultBranch = "main",
                },
                new Project
                {
                    OrganizationId = organizationB.Id,
                    Name = "beta-app",
                    RepositoryUrl = RepositoryUrl,
                    DefaultBranch = "develop",
                });
            context.SaveChanges();
        }

        return (organizationA.Id, organizationB.Id);
    }

    private ZenkinsDbContext CreateContext() => CreateContext(_tenantContext);

    private ZenkinsDbContext CreateContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(
                DatabaseProvider.Sqlite,
                $"Data Source={_databasePath}"))
            .Options;

        var context = new ZenkinsDbContext(options, tenantContext, _systemDataScope);
        context.Database.Migrate();
        return context;
    }

    /// <summary>Stands in for F-002's decorator: a cast-free wrapper around the real tenant context.</summary>
    private sealed class ForwardingTenantContext(ITenantContext inner) : ITenantContext
    {
        public Guid? OrganizationId => inner.OrganizationId;

        public int TenantRevision => inner.TenantRevision;
    }

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
