using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Components;
using Zenkins.Server.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddZenkinsData(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddZenkinsIdentity();

// EF logs every SQL command at Information; keep that out of the production log so a parameter
// value can never be one config change away from being written down. Identity/registration logs
// stay at Information but only ever carry a user id.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

// Behind a TLS-terminating reverse proxy, X-Forwarded-Proto is what makes Request.IsHttps true and
// therefore what makes the auth cookie Secure in production. Loopback proxies are trusted by
// default; ForwardedHeaders:KnownProxies / KnownNetworks in configuration add real ones.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
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
