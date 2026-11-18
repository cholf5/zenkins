namespace Zenkins.Server.Snippets;

/// <summary>Composition root for card F-035's snippet library.</summary>
public static class SnippetServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped management service: it reads the request/circuit tenant and creates one context
    /// per operation through the factory, like the other request services.
    /// </summary>
    public static IServiceCollection AddZenkinsSnippets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<StepSnippetService>();
        return services;
    }
}
