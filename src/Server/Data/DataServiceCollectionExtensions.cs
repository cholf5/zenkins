using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data;

/// <summary>Composition root for the data layer.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provider selected by <c>Database:Provider</c> / <c>ConnectionStrings:Default</c>,
    /// the scoped tenant context and the context itself.
    /// </summary>
    public static IServiceCollection AddZenkinsData(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        var options = ZenkinsDataOptions.FromConfiguration(configuration, contentRootPath);

        // Options are immutable and content-root dependent, so they are captured once at startup.
        services.AddSingleton(options);
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ISystemDataScope, SystemDataScope>();

        // AddDbContext registers ZenkinsDbContext scoped by default; it also resolves the tenant
        // context per request, so two concurrent requests never share an organization.
        services.AddDbContext<ZenkinsDbContext>((serviceProvider, builder) =>
            builder.UseZenkinsDatabase(serviceProvider.GetRequiredService<ZenkinsDataOptions>()));

        return services;
    }
}
