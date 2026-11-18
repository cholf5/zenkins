namespace Zenkins.Server.Folders;

/// <summary>Composition root for card F-042's folder services.</summary>
public static class FolderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the folder service scoped: it reads the scoped request/circuit tenant and creates one
    /// <c>ZenkinsDbContext</c> per operation through the factory.
    /// </summary>
    public static IServiceCollection AddZenkinsFolders(this IServiceCollection services)
    {
        services.AddScoped<FolderService>();
        return services;
    }
}
