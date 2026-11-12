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
    /// the scoped tenant context and system scope, the scoped context, and a scoped factory for
    /// per-operation contexts.
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

        // The context is scoped: one per HTTP request / DI scope, never per process. Interactive
        // Blazor circuits resolve the scope once per circuit, so components must NOT inject the
        // context; they take IDbContextFactory<ZenkinsDbContext> and create one context per operation.
        services.AddDbContext<ZenkinsDbContext>((serviceProvider, builder) =>
            builder.UseZenkinsDatabase(serviceProvider.GetRequiredService<ZenkinsDataOptions>()));

        services.AddDbContextFactory<ZenkinsDbContext, ZenkinsDbContextFactory>(
            (serviceProvider, builder) =>
                builder.UseZenkinsDatabase(serviceProvider.GetRequiredService<ZenkinsDataOptions>()),
            ServiceLifetime.Scoped);

        return services;
    }
}
