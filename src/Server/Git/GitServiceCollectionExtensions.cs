namespace Zenkins.Server.Git;

/// <summary>Composition root for card F-013's Git services.</summary>
public static class GitServiceCollectionExtensions
{
    /// <summary>
    /// Registers the purpose-bound credential protector and the scoped management/fetch service.
    /// Scoped like the other request services: both read the scoped request/circuit tenant and create
    /// one <c>ZenkinsDbContext</c> per operation through the factory.
    /// <para>
    /// <c>IDataProtectionProvider</c> comes from the framework's Data Protection registration, whose
    /// persisted key ring is configured in <c>Program</c> - the same ring the secrets protector uses,
    /// with a different purpose (see <see cref="GitCredentialProtector"/>).
    /// </para>
    /// </summary>
    public static IServiceCollection AddZenkinsGit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<GitCredentialProtector>();
        services.AddScoped<GitCredentialService>();
        return services;
    }
}
