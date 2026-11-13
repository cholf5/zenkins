namespace Zenkins.Server.Secrets;

/// <summary>Composition root for card F-012's secret services.</summary>
public static class SecretServiceCollectionExtensions
{
    /// <summary>
    /// Registers the encryptor and the scoped management service. Scoped like the other request
    /// services: both read the scoped request/circuit tenant and create one <c>ZenkinsDbContext</c>
    /// per operation through the factory.
    /// <para>
    /// <c>IDataProtectionProvider</c> comes from the framework's Data Protection registration, whose
    /// persisted key ring is configured in <c>Program</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddZenkinsSecrets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<SecretProtector>();
        services.AddScoped<SecretService>();

        // F-015: the run-scoped, worker-authenticated fetch secret() is delivered through.
        services.AddScoped<RunSecretService>();
        return services;
    }
}
