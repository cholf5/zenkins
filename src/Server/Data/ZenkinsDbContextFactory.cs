using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data;

/// <summary>
/// Creates one short-lived context per operation. Register as scoped: the factory and the factory's
/// <see cref="ITenantContext"/> then come from the same request/circuit scope, while each
/// <see cref="CreateDbContext"/> call gets a clean change tracker.
/// <para>
/// Use this from UI components and page handlers (Blazor Server keeps a circuit alive for hours, so
/// an injected <see cref="ZenkinsDbContext"/> would be one long-lived context) and anywhere a unit of
/// work should not share tracking state with its caller.
/// </para>
/// </summary>
public sealed class ZenkinsDbContextFactory : IDbContextFactory<ZenkinsDbContext>
{
    private readonly DbContextOptions<ZenkinsDbContext> _options;
    private readonly ITenantContext _tenantContext;
    private readonly ISystemDataScope _systemDataScope;

    public ZenkinsDbContextFactory(
        DbContextOptions<ZenkinsDbContext> options,
        ITenantContext tenantContext,
        ISystemDataScope systemDataScope)
    {
        _options = options;
        _tenantContext = tenantContext;
        _systemDataScope = systemDataScope;
    }

    /// <summary>
    /// The caller owns the returned context and must dispose it (wrap it in <c>using</c>): it holds a
    /// change tracker for its lifetime, and the tenant-revision check only runs when it is used.
    /// </summary>
    public ZenkinsDbContext CreateDbContext() => new(_options, _tenantContext, _systemDataScope);
}
