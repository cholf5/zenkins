using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data;

/// <summary>
/// The only EF Core context in the product. Tenant isolation is enforced here, not by callers:
/// query filters hide other organizations' rows and <c>SaveChanges</c> refuses cross-tenant writes.
/// <para>
/// <b>Bypasses this context cannot guard:</b> <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> never call
/// <c>SaveChanges</c> (the query filter only constrains their <c>WHERE</c>, not their <c>SET</c>), and
/// raw SQL does not go through the model at all. Do not use either on tenant-owned entities outside
/// an <see cref="ISystemDataScope"/> scope; add a guarded method here if one is ever needed.
/// </para>
/// <para>
/// <b>Lifetime:</b> register the context per operation, never as a long-lived object. UI components
/// must not inject <see cref="ZenkinsDbContext"/> (an interactive Blazor circuit lives for hours);
/// they use <see cref="IDbContextFactory{TContext}"/> and create one context per operation.
/// </para>
/// </summary>
public sealed class ZenkinsDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;
    private readonly ISystemDataScope _systemDataScope;
    private TenantContext? _mutableTenantContext;

    public ZenkinsDbContext(
        DbContextOptions<ZenkinsDbContext> options,
        ITenantContext tenantContext,
        ISystemDataScope systemDataScope)
        : base(options)
    {
        _tenantContext = tenantContext;
        _systemDataScope = systemDataScope;

        // A tracked entity from the previous tenant would survive a tenant switch and could be
        // returned by Find/Include without ever touching the query filter. Dropping the tracker on
        // every switch makes "one context, one tenant" impossible to violate by accident.
        if (tenantContext is TenantContext mutableTenantContext)
        {
            _mutableTenantContext = mutableTenantContext;
            mutableTenantContext.OrganizationIdChanged += OnOrganizationIdChanged;
        }
    }

    /// <summary>
    /// Active organization exposed as a plain property so query filters reference <em>this context
    /// instance</em> rather than a captured <see cref="ITenantContext"/>. Falls back to
    /// <see cref="Guid.Empty"/> (which matches nothing) when no tenant is selected, so a
    /// filter can never accidentally mean "all tenants".
    /// </summary>
    public Guid CurrentOrganizationId => _tenantContext.OrganizationId ?? Guid.Empty;

    /// <summary>
    /// Deliberately unfiltered: an organization must be readable while the tenant is still being
    /// selected (the organization switcher runs before a tenant exists). Writes are still guarded
    /// in <c>SaveChanges</c>: only the active organization may be modified or deleted.
    /// </summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    public DbSet<Project> Projects => Set<Project>();

    /// <summary>
    /// Cross-tenant read path for system operations. The scope is checked when the query is
    /// <em>executed</em> and the rows are materialised before this method returns, because an
    /// <c>IQueryable</c> could be captured inside the scope and enumerated after it ended.
    /// </summary>
    public Task<List<TEntity>> SystemQueryAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (!_systemDataScope.IsActive)
        {
            throw new InvalidOperationException(
                $"Cross-tenant access to '{typeof(TEntity).Name}' requires an active {nameof(ISystemDataScope)} scope.");
        }

        return Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(predicate)
            .ToListAsync(cancellationToken);
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

            // Belt and braces for the string conversion above: a row written by raw SQL with an
            // unknown role value would otherwise make every member listing throw.
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_OrganizationMembers_Role",
                $"\"Role\" IN ('{nameof(OrganizationRole.Owner)}', '{nameof(OrganizationRole.Member)}')"));

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

    public override void Dispose()
    {
        UnsubscribeFromTenantChanges();
        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        UnsubscribeFromTenantChanges();
        return base.DisposeAsync();
    }

    private void OnOrganizationIdChanged(object? sender, EventArgs e)
    {
        // Rows tracked under the previous tenant must never be reachable through Find/Include.
        ChangeTracker.Clear();
    }

    private void UnsubscribeFromTenantChanges()
    {
        if (_mutableTenantContext is not null)
        {
            _mutableTenantContext.OrganizationIdChanged -= OnOrganizationIdChanged;
            _mutableTenantContext = null;
        }
    }

    private void EnforceTenantBoundary()
    {
        var activeOrganizationId = _tenantContext.OrganizationId;
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            StampTimestamps(entry, now);

            if (entry.Entity is Organization organization)
            {
                EnsureOrganizationWriteStaysInScope(entry, organization, activeOrganizationId);
                continue;
            }

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
    /// The tenant root is outside the query filters (it must be readable before a tenant exists), so
    /// its writes are guarded by identity instead: touching another organization is a cross-tenant
    /// write even though the row itself does not carry an <c>OrganizationId</c>.
    /// </summary>
    private void EnsureOrganizationWriteStaysInScope(
        EntityEntry entry,
        Organization organization,
        Guid? activeOrganizationId)
    {
        if (entry.State is not (EntityState.Modified or EntityState.Deleted))
        {
            return;
        }

        if (_systemDataScope.IsActive)
        {
            return;
        }

        if (activeOrganizationId is null || organization.Id != activeOrganizationId.Value)
        {
            throw new TenantIsolationException(
                $"Cannot change or delete organization '{organization.Id}': the active organization is "
                + $"'{activeOrganizationId?.ToString() ?? "<none>"}'.");
        }
    }

    /// <summary>
    /// Single owner of the audit timestamps. F-003 (project management) reads these values and must
    /// not stamp them again.
    /// </summary>
    private static void StampTimestamps(EntityEntry entry, DateTimeOffset now)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified))
        {
            return;
        }

        if (entry.State == EntityState.Added && entry.Metadata.FindProperty("CreatedAt") is not null)
        {
            entry.Property("CreatedAt").CurrentValue = now;
        }

        if (entry.Metadata.FindProperty("UpdatedAt") is not null)
        {
            entry.Property("UpdatedAt").CurrentValue = now;
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
