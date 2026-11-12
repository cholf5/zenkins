namespace Zenkins.Server.Data.Tenancy;

/// <summary>
/// Explicit escape hatch for system-level operations that legitimately span tenants (migrations,
/// the scheduler, administrative repair jobs).
/// <para>
/// <b>Why this exists instead of <c>IgnoreQueryFilters()</c> at the call site:</b>
/// <list type="bullet">
/// <item><c>IgnoreQueryFilters()</c> only widens a single query. It does not disable the
/// <c>SaveChanges</c> guard, so a scheduler job still could not write, and it cannot be reached
/// from <c>Find</c>, <c>Include</c> or lazy-loaded navigations.</item>
/// <item>It is silent and unauditable: any query author can add it and no reviewer sees a
/// dependency change. Requiring this service makes the escalation visible in the constructor
/// (and therefore in the diff) and lets the data layer refuse cross-tenant reads unless a scope
/// is active.</item>
/// </list>
/// Obtain it only from the root provider (background services) and wrap the smallest possible
/// unit of work in <see cref="BeginScope"/>. It is intentionally <b>not</b> reachable through
/// <see cref="Zenkins.Shared.Tenancy.ITenantContext"/>.
/// </para>
/// </summary>
public interface ISystemDataScope
{
    /// <summary>Whether a system scope is currently entered on this instance.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Enters a system scope. Dispose the returned value to leave it; scopes nest.
    /// </summary>
    IDisposable BeginScope();
}
