namespace Zenkins.Server.Projects;

/// <summary>Composition root for card F-003's project services.</summary>
public static class ProjectServiceCollectionExtensions
{
    /// <summary>
    /// Registers the project service scoped: it reads the scoped request/circuit tenant and creates
    /// one <c>ZenkinsDbContext</c> per operation through the factory. The draft stash is a singleton,
    /// like the definition-validation registry (F-022): it is the in-memory bridge between the refused
    /// POST and the PRG GET that follows it, which are two different scopes (card B-015).
    /// </summary>
    public static IServiceCollection AddZenkinsProjects(this IServiceCollection services)
    {
        services.AddScoped<ProjectService>();
        services.AddSingleton<FreestyleDraftStash>();
        return services;
    }
}
