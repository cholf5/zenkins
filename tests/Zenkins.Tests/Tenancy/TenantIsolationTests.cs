using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

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

        // Re-point the active tenant without rebuilding the context or the model. A filter that
        // captured ITenantContext at model-build time would still return organization A's project.
        _tenantContext.OrganizationId = organizationB;

        var projectsOfB = context.Projects.ToList();
        var projectOfB = Assert.Single(projectsOfB);
        Assert.Equal(organizationB, projectOfB.OrganizationId);
        Assert.NotEqual(projectsOfA[0].Id, projectOfB.Id);
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

        using var verification = CreateContext();
        using (_systemDataScope.BeginScope())
        {
            Assert.Equal(2, verification.SystemQuery<Project>().Count());
            Assert.DoesNotContain(
                verification.SystemQuery<Project>().ToList(),
                project => project.Name == "smuggled");
        }
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
    public void Duplicate_membership_for_the_same_organization_and_user_is_rejected()
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

            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        }

        using var verification = CreateContext();
        using (_systemDataScope.BeginScope())
        {
            var membership = Assert.Single(verification.SystemQuery<OrganizationMember>().ToList());
            Assert.Equal("alice", membership.UserId);
            Assert.Equal(OrganizationRole.Owner, membership.Role);
        }
    }

    [Fact]
    public void Insert_without_tenant_stamps_the_active_organization()
    {
        var (organizationA, organizationB) = SeedTwoOrganizations();
        _tenantContext.OrganizationId = organizationA;

        using var context = CreateContext();
        var project = new Project
        {
            Name = "gamma-app",
            RepositoryUrl = RepositoryUrl,
            DefaultBranch = "main",
        };

        context.Projects.Add(project);
        context.SaveChanges();

        Assert.Equal(organizationA, project.OrganizationId);
        Assert.NotEqual(organizationB, project.OrganizationId);
        Assert.Equal("pipeline.lua", project.PipelinePath);
    }

    [Fact]
    public void Data_layer_registration_is_scoped_and_reads_the_provider_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ZenkinsDataOptions.ProviderConfigurationKey] = "Sqlite",
                [ZenkinsDataOptions.ConnectionStringConfigurationKey] = $"Data Source={_databasePath}",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZenkinsData(configuration, Path.GetTempPath());

        using var provider = services.BuildServiceProvider();
        using var firstRequest = provider.CreateScope();
        using var secondRequest = provider.CreateScope();

        var firstContext = firstRequest.ServiceProvider.GetRequiredService<ZenkinsDbContext>();
        var secondContext = secondRequest.ServiceProvider.GetRequiredService<ZenkinsDbContext>();

        Assert.Same(firstContext, firstRequest.ServiceProvider.GetRequiredService<ZenkinsDbContext>());
        Assert.NotSame(firstContext, secondContext);
        Assert.Equal(DatabaseProvider.Sqlite, provider.GetRequiredService<ZenkinsDataOptions>().Provider);
    }

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

    private ZenkinsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(
                DatabaseProvider.Sqlite,
                $"Data Source={_databasePath}"))
            .Options;

        var context = new ZenkinsDbContext(options, _tenantContext, _systemDataScope);
        context.Database.Migrate();
        return context;
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
