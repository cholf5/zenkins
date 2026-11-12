namespace Zenkins.Server.Projects;

/// <summary>Composition root for card F-003's project services.</summary>
public static class ProjectServiceCollectionExtensions
{
    /// <summary>
    /// Registers the project service scoped: it reads the scoped request/circuit tenant and creates
    /// one <c>ZenkinsDbContext</c> per operation through the factory.
    /// </summary>
    public static IServiceCollection AddZenkinsProjects(this IServiceCollection services)
    {
        services.AddScoped<ProjectService>();
        return services;
    }
}
