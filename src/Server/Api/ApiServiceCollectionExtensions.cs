using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Api;

/// <summary>
/// Composition root for card F-027: the API token scheme, one authorization policy per scope, the token
/// service, the per-token limiter and its clock.
/// </summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>The policy a route requires for a scope (see <see cref="ApiTokenScopes"/>).</summary>
    public static string PolicyFor(string scope) => ApiTokenAuthenticationDefaults.PolicyFor(scope);

    public static IServiceCollection AddZenkinsApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The knobs come from the fully loaded configuration at first resolution (the other option
        // types' discipline: a value read at registration would freeze before the later sources land).
        services.AddSingleton(serviceProvider => ApiOptions.FromConfiguration(
            serviceProvider.GetRequiredService<IConfiguration>()));

        // TryAdd so a test can substitute a fake and drive the window without waiting a minute.
        services.TryAddSingleton(TimeProvider.System);

        // Process-wide: one budget per token, not per request/DbContext.
        services.AddSingleton<ApiRateLimiter>();

        services.AddScoped<ApiTokenService>();

        // The bearer scheme is additive: the Identity cookie stays the default and the pages' scheme.
        services.AddAuthentication()
            .AddScheme<ApiTokenAuthenticationOptions, ApiTokenAuthenticationHandler>(
                ApiTokenAuthenticationDefaults.AuthenticationScheme,
                configureOptions: null);

        // One policy per scope: the API scheme only (never the cookie), an authenticated token, and the
        // scope claim. A valid token without the scope is a 403 - before any handler runs.
        services.AddAuthorization(options =>
        {
            foreach (var scope in ApiTokenScopes.All)
            {
                options.AddPolicy(PolicyFor(scope), policy => policy
                    .AddAuthenticationSchemes(ApiTokenAuthenticationDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .RequireClaim(ApiTokenAuthenticationDefaults.ScopeClaimType, scope));
            }
        });

        return services;
    }
}
