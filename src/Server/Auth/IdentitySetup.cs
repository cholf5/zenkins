using Microsoft.AspNetCore.Identity;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Auth;

/// <summary>
/// Composition root for authentication (F-001): ASP.NET Identity (user store on
/// <see cref="ZenkinsDbContext"/>), the application cookie, and the services that resolve the
/// signed-in user's organization.
/// <para>
/// <b>Why not Identity roles:</b> PRD §3.1.2 defines Owner/Member as <em>organization</em> roles, so
/// the model intentionally has no Identity role tables. Authorization for app pages is
/// "authenticated or not"; per-organization permissions are a later card (F-002).
/// </para>
/// <para>
/// <b>Cookies:</b> <c>HttpOnly</c> (no script access), <c>SameSite=Lax</c> (the login POST is
/// same-site; cross-site POSTs do not carry the cookie), and <c>Secure</c> whenever the request is
/// HTTPS - which includes <c>X-Forwarded-Proto: https</c> because <c>UseForwardedHeaders</c> runs
/// before authentication (see <c>Program.cs</c>).
/// </para>
/// </summary>
public static class IdentitySetup
{
    /// <summary>Name of the authentication cookie (not secret; it makes logs and DevTools readable).</summary>
    public const string AuthenticationCookieName = "Zenkins.Auth";

    /// <summary>Login route; also the redirect target for anonymous app-page requests.</summary>
    public const string LoginPath = "/account/login";

    /// <summary>Logout route (a static-SSR page with a POST form).</summary>
    public const string LogoutPath = "/account/logout";

    /// <summary>
    /// Registers Identity, the application cookie, authorization and the per-request/per-circuit
    /// session services. Data must already be registered (<c>AddZenkinsData</c>) because the
    /// Identity stores resolve <see cref="ZenkinsDbContext"/> from this container.
    /// </summary>
    public static IServiceCollection AddZenkinsIdentity(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        services
            .AddIdentityCore<User>(options =>
            {
                // Explicit policy, not Identity's defaults (which are weaker than what this product
                // wants for an internet-facing CI server).
                options.Password.RequiredLength = 12;
                options.Password.RequiredUniqueChars = 4;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;

                // Lockout is on for new accounts: five bad passwords for fifteen minutes. The
                // counters live in AspNetUsers (AccessFailedCount / LockoutEnd), so they survive a
                // process restart rather than living in memory.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                // One account per email address; email confirmation is deliberately not required in
                // v0.1 (no mail infrastructure exists, and self-service password reset is out of scope).
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<ZenkinsDbContext>()
            .AddSignInManager();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = AuthenticationCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;

            // SameAsRequest is exactly "Secure when the request is HTTPS": Request.IsHttps reflects
            // X-Forwarded-Proto once UseForwardedHeaders has run, so a TLS-terminating proxy is
            // covered without forcing Secure on plain-HTTP local development (where the browser
            // would drop the cookie).
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.IsEssential = true;

            options.LoginPath = LoginPath;
            options.LogoutPath = LogoutPath;
            options.AccessDeniedPath = LoginPath;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });

        services.AddAuthorization();

        // Supplies the AuthenticationState cascading value consumed by AuthorizeRouteView/cascading auth.
        services.AddCascadingAuthenticationState();

        services.AddScoped<UserOrganizationDirectory>();
        services.AddScoped<UserSession>();
        services.AddScoped<BootstrapService>();
        services.AddScoped<IBootstrapService>(serviceProvider =>
            serviceProvider.GetRequiredService<BootstrapService>());
        services.AddScoped<RegistrationService>();

        return services;
    }
}
