using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Diagnostics;
using Zenkins.Server.Components;
using Zenkins.Server.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddZenkinsData(builder.Configuration, builder.Environment.ContentRootPath);
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
