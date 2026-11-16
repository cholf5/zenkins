using System.Net;
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zenkins.Server.Auth;
using Zenkins.Server.Api;
using Zenkins.Server.Artifacts;
using Zenkins.Server.Components;
using Zenkins.Server.Data;
using Zenkins.Server.Dashboard;
using Zenkins.Server.Git;
using Zenkins.Server.LogStreaming;
using Zenkins.Server.Organizations;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Server.Schedules;
using Zenkins.Server.Secrets;
using Zenkins.Server.Webhooks;
using Zenkins.Server.Workers;
using Zenkins.Shared;

// P-001: the operator's version surface, before anything else runs - no configuration, database or
// key ring is needed to answer it, and the process exits 0. The stamp comes from this assembly's
// AssemblyInformationalVersion, written at build time from the one Version property in
// Directory.Build.props (Zenkins.Shared.ProductVersion strips the source revision). The same stamp
// is the shell footer's version line, so an operator who only has a browser can read the version of
// a running deployment; a deliberately new endpoint was weighed and rejected - it would have to
// enter Q-006's machine-enforced surface inventory and gain a probe row for a read-only string.
if (args.Contains("--version"))
{
    Console.WriteLine("Zenkins Server " + ProductVersion.Current);
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

// I-003: service hosting through the hosting layer, never hand-written wrappers. Each extension is a
// guarded no-op off its platform and service context (on macOS neither applies: launchd supervises the
// published apphost directly via deploy/macos). Where they do apply, the framework swaps in the
// service lifetime and owns the shutdown story. The data-path guarantee is NOT the content root:
// it is this codebase's own AppContext.BaseDirectory resolvers below (a packaged server runs with an
// arbitrary working directory), and appsettings.json discovery stays a working-directory question -
// pin it in the unit/plist (WorkingDirectory) or configure entirely through environment variables.
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

builder.Services.AddHttpContextAccessor();
builder.Services.AddZenkinsData(builder.Configuration);
builder.Services.AddZenkinsIdentity();
builder.Services.AddZenkinsOrganizations();
builder.Services.AddZenkinsProjects();
builder.Services.AddZenkinsWorkers();
builder.Services.AddZenkinsRuns();

// F-024: per-project cron schedules - the settings card's service, the due-time ledger and the sweeper that
// queues a run at each due time (through the same insert path, and the same F-028/F-029 gate, as every trigger).
builder.Services.AddZenkinsSchedules();
builder.Services.AddZenkinsDashboard();
builder.Services.AddZenkinsLogs(builder.Configuration);
builder.Services.AddZenkinsSecrets();
builder.Services.AddZenkinsGit();

// F-016: the local artifact store (App_Data/artifacts), the upload/download service and the sweeper that
// closes abandoned uploads and orphaned directories.
builder.Services.AddZenkinsArtifacts();

// F-017: webhook management, the signature-verified delivery pipeline and the replay-record sweeper.
builder.Services.AddZenkinsWebhooks();

// F-027: the organization API (bearer tokens with scopes), its per-token budget, and the Owner-only
// token-management forms.
builder.Services.AddZenkinsApi();

// F-012: the Data Protection key ring is data, not a machine default. It encrypts every stored
// secret and mints the F-008 log-subscription tickets, so it is persisted next to the database and
// backed up with it (docs/development.md, "Secrets key ring"): losing the ring means every secret has
// to be re-entered, not restored. DataProtection:KeysDirectory pins another directory.
//
// The path is resolved when the options are materialised, not here: deployment/test configuration
// sources are applied later than this line, and a path read too early would silently fall back to
// the default. Relative values resolve from AppContext.BaseDirectory (the publish/install directory —
// the I-003 path discipline); the startup assertion below then fails fast when the directory is not
// writable.
builder.Services
    .AddDataProtection()
    // One application name keeps the keys stable across deployments; changing it orphans the ring.
    .SetApplicationName(SecretDataProtection.ApplicationName);
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<IConfiguration, ILoggerFactory, ZenkinsDataOptions>((options, configuration, loggerFactory, dataOptions) =>
    {
        var keysDirectory = SecretDataProtection.ResolveKeysDirectory(configuration, dataOptions);

        // PersistKeysToFileSystem's own effect, spelled out because the directory has to be resolved
        // after every configuration source has been applied (see above).
        options.XmlRepository = new FileSystemXmlRepository(
            new DirectoryInfo(keysDirectory),
            loggerFactory);
    });

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
    // Dark mode is the only dimension this flag governs: the accent, base gray scale and radius
    // picked in the theme switcher (D-007) live in the same `bb-theme` entry and are replayed onto
    // <html> before the first paint by the package's theme-init.js, loaded from App.razor.
    theme.DetectSystemPreference = false;
});

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// F-012: fail fast if the key ring cannot be persisted. Starting with an unwritable key directory
// would only surface later, when the first secret cannot be decrypted - at that point the data is
// already unrecoverable.
var dataProtectionKeysDirectory = SecretDataProtection.ResolveKeysDirectory(
    app.Configuration,
    app.Services.GetRequiredService<ZenkinsDataOptions>());
EnsureWritableDirectory(
    dataProtectionKeysDirectory,
    "Data Protection key ring",
    "Point DataProtection:KeysDirectory at a writable directory and back it up with the database - "
    + "without this key ring, stored secrets cannot be decrypted.");
app.Logger.LogInformation("Data Protection key ring: {KeysDirectory}", dataProtectionKeysDirectory);

// F-016: the artifact store is data like the key ring - fail fast when its root cannot be written, instead of
// surfacing it on the first upload of the first run.
var artifactRoot = app.Services.GetRequiredService<ArtifactOptions>().RootPath;
EnsureWritableDirectory(
    artifactRoot,
    "artifact store",
    "Point Artifacts:RootPath at a writable directory with room for the run artifacts, and back it up "
    + "with the database - the store holds the bytes every downloaded artifact comes from.");
app.Logger.LogInformation("Artifact store: {ArtifactRoot}", artifactRoot);

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

// F-024: the settings tab's schedule forms (create/preview, toggle, delete).
app.MapScheduleEndpoints();

// Secret create/rotate/delete (F-012). Owner-only, form-bound; there is deliberately no
// value-returning route at all - SecretRouteShapeTests pins the route table.
app.MapSecretEndpoints();
app.MapGitCredentialEndpoints();

// Worker registration/heartbeat API (token-authenticated, exempt from cookie auth) and the
// Owner-only registration-token form endpoints (F-004). See WorkerEndpoints for why the two
// groups deliberately differ in authentication.
app.MapWorkerEndpoints();

// Run trigger form (F-003's authenticated form shape) and the token-authenticated run claim and
// reporting API (F-006); the background lease sweeper is registered by AddZenkinsRuns.
app.MapRunEndpoints();

// F-016: the worker artifact API (create/files/finalize) and the per-artifact download.
app.MapArtifactEndpoints();

// F-017: the anonymous, signature-authenticated delivery endpoint (POST /webhooks/{webhookId}) and the
// cookie+antiforgery settings forms. The delivery route is deliberately outside the cookie-auth fallback,
// like the worker API: a git host has no session, and the shared secret is the whole credential.
app.MapWebhookEndpoints();

// F-027: the organization API (/api/v1) - bearer token + scope policies, bearer only, never a cookie -
// and the Owner-only token-management forms (/api-tokens).
app.MapApiEndpoints();

// Log streaming hubs (F-008): the worker upstream (worker-token authenticated) and the browser
// subscription hub (subscription-ticket authenticated). Both authenticate before any hub method
// runs - an unauthenticated negotiate is a bare 401.
app.MapHub<WorkerLogHub>("/hubs/worker-logs");
app.MapHub<RunLogHub>("/hubs/run-logs");

app.Run();

// P-001: the early --version path returned above; reaching this line means the host ran and shut
// down gracefully, which has always been exit code 0 - now it is spelled out because the early
// return makes the top-level statements return an int.
return 0;

// F-012/F-016: a one-line startup assertion instead of a late failure (an unreadable key ring means
// undecryptable secrets; an unwritable store means artifacts that cannot be published). Writing and
// deleting a probe file is the only portable check (permissions, read-only mounts, a path that is a file).
// The remedy is per purpose: a key-ring failure is about backups of the key material, a store failure
// about the build products and disk space.
static void EnsureWritableDirectory(string path, string description, string remedy)
{
    try
    {
        Directory.CreateDirectory(path);
        var probePath = Path.Combine(path, $".write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        File.WriteAllText(probePath, "zenkins");
        File.Delete(probePath);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
        or NotSupportedException or ArgumentException or System.Security.SecurityException)
    {
        throw new InvalidOperationException(
            $"The {description} directory '{path}' is not writable: {exception.Message} {remedy}",
            exception);
    }
}

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
