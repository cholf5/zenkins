using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.LogStreaming;

/// <summary>Composition root for card F-008's log streaming services.</summary>
public static class LogsServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsLogs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Immutable once read: the flush sizes and ticket lifetime do not change at runtime.
        services.AddSingleton(RunLogOptions.FromConfiguration(configuration));

        // Ticket mint/validate + join authorization: scoped like the other request services (it
        // re-selects the scoped tenant for its join check).
        services.AddScoped<RunLogAccess>();

        // The ingest is process-wide: one bounded per-run buffer set, one statistics counter set.
        services.AddSingleton<RunLogIngest>();

        // SignalR itself (JSON hub protocol, default transports). No new packages on the server:
        // the hub side lives in the ASP.NET Core shared framework.
        services.AddSignalR();

        // The two hub credential schemes. Both are deliberate alternatives to the cookie: the
        // worker hub accepts only a worker token, the browser hub only a subscription ticket.
        services.AddAuthentication()
            .AddScheme<WorkerTokenAuthenticationOptions, WorkerTokenAuthenticationHandler>(
                WorkerTokenAuthenticationDefaults.AuthenticationScheme, null)
            .AddScheme<RunLogTicketAuthenticationOptions, RunLogTicketAuthenticationHandler>(
                RunLogTicketAuthenticationDefaults.AuthenticationScheme, null);

        return services;
    }
}
