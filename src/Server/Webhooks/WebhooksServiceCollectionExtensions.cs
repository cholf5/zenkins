using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Zenkins.Server.Webhooks;

/// <summary>Composition root for card F-017's webhook management, delivery pipeline and sweeper.</summary>
public static class WebhooksServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsWebhooks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Read from the fully loaded application configuration at first resolution (the other option
        // types' discipline: a value read during registration would freeze before the test/deployment
        // sources land).
        services.AddSingleton(sp => WebhookOptions.FromConfiguration(
            sp.GetRequiredService<IConfiguration>()));

        // The limiter's clock. TryAdd so a test can substitute a fake and drive the window without waiting.
        services.TryAddSingleton(TimeProvider.System);

        // Process-wide: one budget per webhook, not per request/DbContext.
        services.AddSingleton<WebhookRateLimiter>();

        // The endpoint's buffer/refusal counters (review m1): process-wide so a test can read what the
        // handler actually held instead of trusting the code path.
        services.AddSingleton<WebhookDeliveryTelemetry>();

        // Scoped like the other request services: they read the scoped tenant, the scoped context factory
        // and the Data Protection provider.
        services.AddScoped<WebhookSecretProtector>();
        services.AddScoped<WebhookService>();
        services.AddScoped<WebhookDeliveryService>();

        services.AddHostedService<WebhookDeliverySweeper>();
        return services;
    }
}
