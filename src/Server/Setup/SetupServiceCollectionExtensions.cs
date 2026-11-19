namespace Zenkins.Server.Setup;

/// <summary>
/// Registration for the first-run setup wizard (card F-044). One scoped service reads the facts the
/// welcome page shows; the gate itself is middleware because it has to answer before a page renders.
/// </summary>
public static class SetupServiceCollectionExtensions
{
    /// <summary>Registers the setup wizard's services. Data and artifacts must already be registered.</summary>
    public static IServiceCollection AddZenkinsSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<SetupReadiness>();
        return services;
    }
}
