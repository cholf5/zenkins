namespace Zenkins.Server.Organizations;

/// <summary>Composition root for card F-002's organization services.</summary>
public static class OrganizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Owner-only management service. The active-organization directory is registered
    /// with the auth services (<c>AddZenkinsIdentity</c>) because the request middleware and the
    /// interactive circuit share it; the management service reads the scoped request tenant and is
    /// therefore scoped as well.
    /// </summary>
    public static IServiceCollection AddZenkinsOrganizations(this IServiceCollection services)
    {
        services.AddSingleton<IOrganizationRowLock, OrganizationRowLock>();
        services.AddScoped<OrganizationManagementService>();
        return services;
    }
}
