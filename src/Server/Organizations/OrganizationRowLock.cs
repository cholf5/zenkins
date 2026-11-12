using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;

namespace Zenkins.Server.Organizations;

/// <summary>
/// Serializes organization mutations whose invariant spans rows - currently the last-owner guard for
/// role change and member removal - by taking a database lock on the organization row itself.
/// <para>
/// <b>Why a row lock and not a bigger isolation level:</b> two concurrent demotions against a
/// two-Owner organization both count two Owners at PostgreSQL's default READ COMMITTED and both commit,
/// leaving an organization nobody can administer. Locking the organization row first makes the count
/// and the write of every owner-affecting mutation mutually exclusive per organization; the cost is
/// only paid by mutations, never by reads.
/// </para>
/// <para>
/// <b>Provider behaviour:</b> PostgreSQL gets <c>SELECT "Id" ... FOR UPDATE</c> inside the caller's
/// transaction. SQLite (development and tests) has no row locks and serializes writers at the database
/// level, so the lock is a deliberate no-op there; the same code path and the same invariant still run.
/// </para>
/// </summary>
public interface IOrganizationRowLock
{
    /// <summary>
    /// Blocks until this transaction owns the organization row's write lock, or returns immediately
    /// on a provider that does not need one. Must be called inside the mutation's transaction.
    /// </summary>
    Task LockAsync(
        ZenkinsDbContext context,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class OrganizationRowLock : IOrganizationRowLock
{
    /// <inheritdoc />
    public async Task LockAsync(
        ZenkinsDbContext context,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Database.IsNpgsql())
        {
            // SQLite serializes writers, and the tests' provider is SQLite. Do not use raw SQL that the
            // provider cannot parse just to look symmetric.
            return;
        }

        var lockedIds = await BuildLockQuery(context, organizationId).ToListAsync(cancellationToken);
        if (lockedIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot lock organization '{organizationId}': the row does not exist.");
        }
    }

    /// <summary>
    /// The statement executed under PostgreSQL. Exposed internally so a test can assert its shape
    /// (<c>FOR UPDATE</c> on <c>Organizations</c>) without a live database; the same query object is
    /// what <see cref="LockAsync"/> executes.
    /// </summary>
    internal static IQueryable<Guid> BuildLockQuery(ZenkinsDbContext context, Guid organizationId) =>
        context.Database.SqlQuery<Guid>(
            $"SELECT \"Id\" AS \"Value\" FROM \"Organizations\" WHERE \"Id\" = {organizationId} FOR UPDATE");
}
