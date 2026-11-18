using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.Runs;

/// <summary>Composition root for card F-006's run services and F-009's deadline sweeper.</summary>
public static class RunsServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsRuns(this IServiceCollection services)
    {
        // Read from the fully loaded application configuration at first resolution. The eager
        // read of the WebApplicationBuilder's Configuration would freeze the values before the
        // host builder's extra configuration sources land (they are applied while the host
        // builds); a singleton resolved during startup reads the same merged configuration.
        services.AddSingleton(sp => RunOptions.FromConfiguration(
            sp.GetRequiredService<IConfiguration>()));

        // Scoped like the other request services: it reads the scoped request/circuit tenant,
        // sets the tenant for token-authenticated calls, and creates one context per operation.
        services.AddScoped<RunService>();

        // F-041: the purpose-bound cipher for password-typed parameter values. Scoped like the secrets'
        // protector; both wrap the application's one Data Protection key ring.
        services.AddScoped<RunParameterProtector>();

        // F-041: the run-scoped, worker-authenticated fetch a file parameter's bytes take.
        services.AddScoped<RunParameterFileService>();

        // F-026: the chain edges (and the save-time cycle check), the dispatcher's per-pass work, and the
        // background half that works the outbox off after every terminal run.
        services.AddScoped<ProjectLinkService>();
        services.AddScoped<DownstreamTriggerService>();
        services.AddHostedService<DownstreamTriggerDispatcher>();
        services.AddHostedService<LeaseSweeper>();
        return services;
    }
}
