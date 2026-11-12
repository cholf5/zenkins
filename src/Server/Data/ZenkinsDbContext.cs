using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data;

/// <summary>
/// The only EF Core context in the product. Tenant isolation is enforced here, not by callers:
/// query filters hide other organizations' rows and <c>SaveChanges</c> refuses cross-tenant writes.
/// </summary>
public sealed class ZenkinsDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;
    private readonly ISystemDataScope _systemDataScope;

    public ZenkinsDbContext(
        DbContextOptions<ZenkinsDbContext> options,
        ITenantContext tenantContext,
        ISystemDataScope systemDataScope)
        : base(options)
    {
        _tenantContext = tenantContext;
        _systemDataScope = systemDataScope;
    }

    /// <summary>
    /// Active organization exposed as a plain property so query filters reference <em>this context
    /// instance</em> rather than a captured <see cref="ITenantContext"/>. Falls back to
    /// <see cref="Guid.Empty"/> (which matches nothing) when no tenant is selected, so a
    /// filter can never accidentally mean "all tenants".
    /// </summary>
    public Guid CurrentOrganizationId => _tenantContext.OrganizationId ?? Guid.Empty;

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    public DbSet<Project> Projects => Set<Project>();

    /// <summary>
    /// Cross-tenant read path. Only usable while an <see cref="ISystemDataScope"/> scope is active;
    /// see <see cref="ISystemDataScope"/> for why an unguarded <c>IgnoreQueryFilters()</c> is not an
    /// acceptable escape hatch.
    /// </summary>
    public IQueryable<TEntity> SystemQuery<TEntity>()
        where TEntity : class
    {
        if (!_systemDataScope.IsActive)
        {
            throw new InvalidOperationException(
                $"Cross-tenant access to '{typeof(TEntity).Name}' requires an active {nameof(ISystemDataScope)} scope.");
        }

        return Set<TEntity>().IgnoreQueryFilters();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(entity =>
        {
            entity.HasKey(organization => organization.Id);
            entity.Property(organization => organization.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<OrganizationMember>(entity =>
        {
            entity.HasKey(member => member.Id);
            entity.Property(member => member.UserId).IsRequired().HasMaxLength(450);
            entity.Property(member => member.Role).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(member => new { member.OrganizationId, member.UserId }).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(member => member.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            // NOTE: no foreign key to a users table on purpose - I-002 does not model User.
            // F-001 adds the Identity table and must add that foreign key here.

            entity.HasQueryFilter(member => member.OrganizationId == CurrentOrganizationId);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Name).IsRequired().HasMaxLength(200);
            entity.Property(project => project.RepositoryUrl).IsRequired().HasMaxLength(2048);
            entity.Property(project => project.DefaultBranch).IsRequired().HasMaxLength(200);
            entity.Property(project => project.PipelinePath).IsRequired().HasMaxLength(512);
            entity.HasOne<Organization>().WithMany().HasForeignKey(project => project.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(project => project.OrganizationId == CurrentOrganizationId);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantBoundary();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    // The guard is shared with the synchronous overload so there is exactly one isolation code path
    // to review. It performs no I/O for inserts; only updates/deletes do one row lookup each, and
    // v0.1 has no async write path yet (F-001 adds the first one).
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnforceTenantBoundary();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceTenantBoundary()
    {
        var activeOrganizationId = _tenantContext.OrganizationId;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not ITenantOwned owned)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (_systemDataScope.IsActive)
                    {
                        // A system-scope caller must name the organization itself; stamping from a
                        // request tenant would be meaningless there.
                        if (owned.OrganizationId == Guid.Empty)
                        {
                            throw new TenantIsolationException(
                                $"Cannot insert '{entry.Metadata.ClrType.Name}' without an organization. "
                                + "System-scope writes must set OrganizationId explicitly.");
                        }

                        break;
                    }

                    if (activeOrganizationId is null)
                    {
                        throw new TenantIsolationException(
                            $"Cannot insert '{entry.Metadata.ClrType.Name}' without an active organization.");
                    }

                    if (owned.OrganizationId == Guid.Empty)
                    {
                        // Tenant-owned rows are stamped from the request, never from the caller.
                        owned.OrganizationId = activeOrganizationId.Value;
                    }
                    else if (owned.OrganizationId != activeOrganizationId.Value)
                    {
                        throw new TenantIsolationException(
                            $"Cannot insert '{entry.Metadata.ClrType.Name}' for organization '{owned.OrganizationId}': "
                            + $"the active organization is '{activeOrganizationId.Value}'.");
                    }

                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    if (_systemDataScope.IsActive)
                    {
                        break;
                    }

                    if (activeOrganizationId is null)
                    {
                        throw new TenantIsolationException(
                            $"Cannot change '{entry.Metadata.ClrType.Name}' without an active organization.");
                    }

                    EnsureRowBelongsToActiveOrganization(entry, owned, activeOrganizationId.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// Tracker values are not trustworthy on their own: <c>Attach</c> lets a caller supply any
    /// "original" value, so an id-swapping request could rewrite another tenant's row while
    /// claiming it is own. The persisted row is therefore checked as well.
    /// </summary>
    private static void EnsureRowBelongsToActiveOrganization(
        EntityEntry entry,
        ITenantOwned owned,
        Guid activeOrganizationId)
    {
        var originalOrganizationId =
            entry.Property(nameof(ITenantOwned.OrganizationId)).OriginalValue as Guid? ?? Guid.Empty;
        if (originalOrganizationId != activeOrganizationId)
        {
            throw new TenantIsolationException(
                $"Cannot change '{entry.Metadata.ClrType.Name}': its loaded organization '{originalOrganizationId}' "
                + $"is not the active organization '{activeOrganizationId}'.");
        }

        if (owned.OrganizationId != activeOrganizationId)
        {
            throw new TenantIsolationException(
                $"OrganizationId of '{entry.Metadata.ClrType.Name}' cannot be changed from "
                + $"'{activeOrganizationId}' to '{owned.OrganizationId}'.");
        }

        var databaseValues = entry.GetDatabaseValues();
        if (databaseValues is null)
        {
            throw new TenantIsolationException(
                $"Cannot change '{entry.Metadata.ClrType.Name}': the row does not exist in the active organization "
                + $"'{activeOrganizationId}'.");
        }

        var storedOrganizationId = databaseValues[nameof(ITenantOwned.OrganizationId)];
        if (storedOrganizationId is not Guid stored || stored != activeOrganizationId)
        {
            throw new TenantIsolationException(
                $"Cannot change '{entry.Metadata.ClrType.Name}': the stored row belongs to organization "
                + $"'{storedOrganizationId}', not the active organization '{activeOrganizationId}'.");
        }
    }
}
