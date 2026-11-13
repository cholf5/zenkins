using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
    private readonly string _artifactsPath;

    public AuthenticatedTenantApplicationFactory(TwoOrgsFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _connectionString = fixture.ConnectionString;
        _artifactsPath = fixture.ArtifactsPath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // F-016: the real Program asserts and creates the artifact store root at startup, so it is pointed at this
        // fixture's temp directory - the repository's App_Data must not gain an artifacts tree per test run.
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [Zenkins.Server.Artifacts.ArtifactOptions.RootPathConfigurationKey] = _artifactsPath,
            }));

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
