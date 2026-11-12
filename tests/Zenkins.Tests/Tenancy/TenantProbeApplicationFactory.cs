using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-001's HTTP id-swap probe host: the real <c>Program</c> pipeline over the fixture's
/// temporary SQLite file, plus a <b>test-only</b> probe endpoint.
/// <para>
/// <b>Why the probe is test-only:</b> no product endpoint reads tenant data yet (F-002/F-003 own
/// those). The probe therefore maps <c>/__probe/projects/{id}</c> inside the factory, and a
/// test-only middleware resolves the tenant from the <c>X-Test-Organization</c> header, standing in
/// for F-001's authentication pipeline. F-002/F-003 replace the probe with the real authenticated
/// endpoint; the auth-dependent half of the probe (a signed-in user can only activate an
/// organization they are a member of) moves there with F-003. Nothing here is skipped: the probe
/// runs on every <c>dotnet test</c>.
/// </para>
/// <para>
/// The probe is a terminal middleware rather than a <c>MapGet</c> route because the app has already
/// been built (and <c>Program</c> already ran <c>app.Run()</c>) by the time a
/// <c>WebApplicationFactory</c> can hook in, and adding a route to the running endpoint data source
/// would need a production hook the card does not allow. Registering the probe through an
/// <see cref="IStartupFilter"/> still exercises the whole host: request features, the scoped DI
/// request, the real <see cref="ZenkinsDbContext"/> with its global query filters, serialization and
/// status codes.
/// </para>
/// </summary>
public sealed class TenantProbeApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Header the probe reads instead of an authenticated principal (F-001 replaces it).</summary>
    public const string OrganizationHeaderName = "X-Test-Organization";

    /// <summary>Path the probe answers on; deliberately namespaced so it can never collide with a product route.</summary>
    public const string ProbePath = "/__probe/projects";

    private readonly string _connectionString;

    public TenantProbeApplicationFactory(TwoOrgsFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _connectionString = fixture.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Program resolves Database:Provider / ConnectionStrings:Default at startup and registers
            // the result as a singleton; the data-layer options actions resolve it from DI, so
            // replacing the singleton points every context at the fixture's temp file. The app must
            // never open the repository's App_Data database during tests.
            services.RemoveAll<ZenkinsDataOptions>();
            services.AddSingleton(new ZenkinsDataOptions(DatabaseProvider.Sqlite, _connectionString));

            services.AddSingleton<IStartupFilter, TestOnlyProbeStartupFilter>();
        });
    }
}

/// <summary>Wraps the host pipeline with the test-only probe, so it runs before the app's own middleware.</summary>
internal sealed class TestOnlyProbeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<TestOnlyProjectProbeMiddleware>();
        next(app);
    };
}

/// <summary>
/// Test-only tenant selection + id-swap probe. It accepts the project id from the route, the query
/// string or the form body, and reads the project through the same tenant-filtered context a real
/// endpoint would use. A foreign id must therefore look absent (404), never like the other tenant's
/// data; a legitimate id must return that tenant's row (the positive control).
/// </summary>
internal sealed class TestOnlyProjectProbeMiddleware(RequestDelegate next)
{
    private const string ProjectIdQueryKey = "id";
    private const string ProjectIdFormKey = "id";

    public async Task InvokeAsync(
        HttpContext context,
        TenantContext tenantContext,
        IDbContextFactory<ZenkinsDbContext> contextFactory)
    {
        if (!IsProbeRequest(context.Request.Path))
        {
            await next(context);
            return;
        }

        var cancellationToken = context.RequestAborted;

        // Stands in for F-001's authentication middleware: the tenant comes from the request, never
        // from the id being looked up.
        var organizationHeader = context.Request.Headers[TenantProbeApplicationFactory.OrganizationHeaderName];
        if (Guid.TryParse(organizationHeader.ToString(), out var organizationId))
        {
            tenantContext.OrganizationId = organizationId;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var rawProjectId = await ResolveProjectIdAsync(context, cancellationToken);
        if (!Guid.TryParse(rawProjectId, out var projectId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The only data access in the probe. The global query filter is what makes a foreign id
        // indistinguishable from a nonexistent one.
        var project = await db.Projects.FindAsync([projectId], cancellationToken);
        if (project is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(
            new
            {
                id = project.Id,
                organizationId = project.OrganizationId,
                name = project.Name,
            },
            cancellationToken);
    }

    private static bool IsProbeRequest(PathString path)
    {
        if (!path.StartsWithSegments(TenantProbeApplicationFactory.ProbePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Exact path or one more segment; a sibling such as /__probe/projectsX is not the probe.
        var value = path.Value ?? string.Empty;
        return value.Length == TenantProbeApplicationFactory.ProbePath.Length
            || value[TenantProbeApplicationFactory.ProbePath.Length] == '/';
    }

    private static async Task<string?> ResolveProjectIdAsync(HttpContext context, CancellationToken cancellationToken)
    {
        // Route shape: /__probe/projects/{id}
        var remainder = context.Request.Path.Value![TenantProbeApplicationFactory.ProbePath.Length..];
        if (remainder.Length > 1 && remainder[0] == '/')
        {
            return Uri.UnescapeDataString(remainder[1..]);
        }

        // Query shape: /__probe/projects?id={id}
        if (context.Request.Query.TryGetValue(ProjectIdQueryKey, out var queryValue))
        {
            return queryValue.ToString();
        }

        // Form shape: POST /__probe/projects with id={id}
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            if (form.TryGetValue(ProjectIdFormKey, out var formValue))
            {
                return formValue.ToString();
            }
        }

        return null;
    }
}
