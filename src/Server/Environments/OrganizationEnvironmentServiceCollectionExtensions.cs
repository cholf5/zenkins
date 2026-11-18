namespace Zenkins.Server.Environments;

/// <summary>Composition root for card F-034's organization environment service.</summary>
public static class OrganizationEnvironmentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped management service. Scoped like the other request services: it reads the
    /// scoped request tenant and creates one <see cref="Zenkins.Server.Data.ZenkinsDbContext"/> per
    /// operation through the factory.
    /// </summary>
    public static IServiceCollection AddZenkinsOrganizationEnvironment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<OrganizationEnvironmentService>();
        return services;
    }
}
