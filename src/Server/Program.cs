using System.Net;
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Components;
using Zenkins.Server.Data;
using Zenkins.Server.Organizations;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Server.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddZenkinsData(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddZenkinsIdentity();
builder.Services.AddZenkinsOrganizations();
builder.Services.AddZenkinsProjects();
builder.Services.AddZenkinsWorkers();
builder.Services.AddZenkinsRuns(builder.Configuration);

// EF logs every SQL command at Information; keep that out of the production log so a parameter
// value can never be one config change away from being written down. Identity/registration logs
// stay at Information but only ever carry a user id.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

// Behind a TLS-terminating reverse proxy, X-Forwarded-Proto is what makes Request.IsHttps true and
// therefore what makes the auth cookie Secure in production. Loopback proxies are trusted by
// default; a proxy on another host or network must be listed, otherwise its headers are ignored
// (a remote client can never make this app trust its own X-Forwarded-Proto). Configuration:
//   ForwardedHeaders:KnownProxies:0   = 203.0.113.7
//   ForwardedHeaders:KnownNetworks:0  = 10.0.0.0/8
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    var forwardedHeaders = builder.Configuration.GetSection("ForwardedHeaders");
    foreach (var proxy in forwardedHeaders.GetSection("KnownProxies").Get<string[]>() ?? [])
    {
        if (IPAddress.TryParse(proxy, out var address))
        {
            options.KnownProxies.Add(address);
        }
    }

    foreach (var network in forwardedHeaders.GetSection("KnownNetworks").Get<string[]>() ?? [])
    {
        var parts = network.Split('/', 2);
        if (parts.Length == 2
            && IPAddress.TryParse(parts[0], out var prefix)
            && int.TryParse(parts[1], out var prefixLength))
        {
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
        }
    }
});

builder.Services.AddBlazorBlueprintComponents(configureTheme: theme =>
{
    // The pre-paint bridge in wwwroot/js/theme-persistence.js resolves the theme from the cookie,
    // the legacy `bb-theme` entry or the OS preference and seeds `bb-theme`, so ThemeService always
    // has a value to read. Letting it also consult the OS preference would let it disagree with the
    // server-rendered class when localStorage is unavailable (and then rewrite the cookie).
    theme.DetectSystemPreference = false;
});

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Startup migration hook (I-002 deferred it to F-001). Off in production by default so a multi-node
// deployment runs `dotnet ef database update` once; development gets a ready database on first run.
if (app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", app.Environment.IsDevelopment()))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var database = migrationScope.ServiceProvider.GetRequiredService<ZenkinsDbContext>();
    await database.Database.MigrateAsync();
}

// Must run before authentication: it rewrites Request.Scheme/IsHttps from X-Forwarded-Proto.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found");
app.Use(async (context, next) =>
{
    await next();

    // The /not-found re-execute replaces whatever body the response had, so scope it to the only
    // case it describes: a plain 404 on an HTML navigation. Without this every 4xx/5xx would answer
    // with "page not found" copy, and a mistyped asset URL would answer text/html instead of 404.
    if (context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages &&
        (context.Response.StatusCode != StatusCodes.Status404NotFound || !IsHtmlNavigation(context)))
    {
        statusCodePages.Enabled = false;
    }
});

app.UseAuthentication();
app.UseAuthorization();

// Selects the request tenant from the signed-in user's memberships (never from the URL).
app.UseMiddleware<TenantResolutionMiddleware>();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Real POST endpoints for switching and Owner-only management (F-002). Form-bound, so the existing
// UseAntiforgery validates every call.
app.MapOrganizationEndpoints();

// Project create/edit/delete (F-003). Same form-bound, antiforgery-validated shape; the service
// enforces membership (create/edit) and Owner-only deletion.
app.MapProjectEndpoints();

// Worker registration/heartbeat API (token-authenticated, exempt from cookie auth) and the
// Owner-only registration-token form endpoints (F-004). See WorkerEndpoints for why the two
// groups deliberately differ in authentication.
app.MapWorkerEndpoints();

// Run trigger form (F-003's authenticated form shape) and the token-authenticated run claim and
// reporting API (F-006); the background lease sweeper is registered by AddZenkinsRuns.
app.MapRunEndpoints();

app.Run();

// Only a navigation can be re-executed into a page: browsers ask for text/html, the path carries no
// file extension and is not a package asset, and the method has no body worth replaying.
static bool IsHtmlNavigation(HttpContext context)
{
    var request = context.Request;

    if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
    {
        return false;
    }

    if (request.Path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (Path.HasExtension(request.Path.Value))
    {
        return false;
    }

    return request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
}

// Q-001: WebApplicationFactory<Program> needs a reachable entry-point type. Top-level statements
// generate an internal Program class, so this declaration is the only production change the HTTP
// probe needs; it must stay at the end of the file, after every statement and local function.
public partial class Program { }
