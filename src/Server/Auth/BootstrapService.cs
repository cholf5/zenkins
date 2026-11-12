using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Auth;

/// <summary>Outcome of an attempt to create the starter organization.</summary>
public enum BootstrapOutcome
{
    /// <summary>The bootstrap was not attempted because the first-run marker already exists.</summary>
    NotAttempted = 0,

    /// <summary>This call created the starter organization, the Owner membership and the marker.</summary>
    Created = 1,

    /// <summary>Another transaction already completed the bootstrap; nothing was written.</summary>
    AlreadyCompleted = 2,
}

/// <summary>
/// First-run bootstrap (PRD §3.1.1 / card F-001): the very first registration becomes Owner of a
/// starter organization.
/// <para>
/// <b>Race safety:</b> the guard is the primary key of <see cref="BootstrapMarker"/>, not a
/// <c>count == 0</c> check. <see cref="TryCreateOwnerOrganizationAsync"/> inserts the marker in the
/// same <c>SaveChanges</c> as the organization and the Owner membership; when two first
/// registrations race, the database accepts exactly one marker insert and the loser's
/// <c>SaveChanges</c> fails with a unique-constraint violation, which is reported as
/// <see cref="BootstrapOutcome.AlreadyCompleted"/>. That call does not create a second
/// organization.
/// </para>
/// <para>
/// The membership insert runs inside <see cref="ISystemDataScope"/> with an explicit
/// <c>OrganizationId</c>, because it happens before any tenant is selected (I-002 handoff).
/// </para>
/// </summary>
public sealed class BootstrapService(ZenkinsDbContext context, ISystemDataScope systemDataScope)
{
    /// <summary>Name of the organization created by the first-run bootstrap.</summary>
    public const string StarterOrganizationName = "Starter Organization";

    /// <summary>Whether the one-time bootstrap has already committed.</summary>
    public Task<bool> HasCompletedAsync(CancellationToken cancellationToken = default) =>
        context.BootstrapMarkers.AnyAsync(cancellationToken);

    /// <summary>
    /// Creates the starter organization, the Owner membership for <paramref name="user"/> and the
    /// one-row marker. The caller must run this inside the registration transaction so the user,
    /// organization, membership and marker commit together; the method itself only calls
    /// <c>SaveChanges</c>.
    /// </summary>
    public async Task<BootstrapOutcome> TryCreateOwnerOrganizationAsync(
        User user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        using var scope = systemDataScope.BeginScope();

        var organization = new Organization { Name = StarterOrganizationName };
        context.Organizations.Add(organization);
        context.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = organization.Id,
            UserId = user.Id,
            Role = OrganizationRole.Owner,
        });
        context.BootstrapMarkers.Add(new BootstrapMarker
        {
            Id = BootstrapMarker.SingletonId,
            OrganizationId = organization.Id,
            UserId = user.Id,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return BootstrapOutcome.Created;
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, nameof(ZenkinsDbContext.BootstrapMarkers)))
        {
            // The pending inserts belong to a transaction the caller is about to roll back. Clear
            // them so the same context can be reused for the fallback path ("account without an
            // organization") instead of reporting them on the next SaveChanges.
            context.ChangeTracker.Clear();
            return BootstrapOutcome.AlreadyCompleted;
        }
    }
}
