using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zenkins.Server.Data;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-001's HTTP host, re-pointed by F-003 at the <b>real authenticated</b> project pages and
/// endpoints. Q-001 originally mapped a test-only <c>/__probe/projects/{id}</c> route and selected a
/// tenant from an <c>X-Test-Organization</c> header, because no product endpoint read tenant data
/// yet. That seam is gone: the factory now only points the app at the fixture's SQLite file, and the
/// probes sign in through the real login form (the fixture's users have password hashes) so the
/// tenant comes from the same membership-derived resolution every request uses.
/// </summary>
public sealed class AuthenticatedTenantApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public AuthenticatedTenantApplicationFactory(TwoOrgsFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _connectionString = fixture.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            // Program resolves Database:Provider / ConnectionStrings:Default at startup; replacing
            // the singleton points every context (and the startup migration) at the fixture's temp
            // file instead of the repository's App_Data database.
            services.RemoveAll<ZenkinsDataOptions>();
            services.AddSingleton(new ZenkinsDataOptions(DatabaseProvider.Sqlite, _connectionString));
        });
    }
}
