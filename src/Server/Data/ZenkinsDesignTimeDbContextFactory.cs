using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Data;

/// <summary>
/// Lets <c>dotnet ef</c> (migrations, script generation) construct the context without booting the
/// web host. It mirrors the runtime configuration sources so design-time and runtime cannot silently
/// target different databases: <c>appsettings.json</c>, <c>appsettings.{Environment}.json</c>, user
/// secrets, then environment variables (<c>Database__Provider</c>, <c>ConnectionStrings__Default</c>).
/// <para>
/// Content root comes from <c>ZENKINS_CONTENT_ROOT</c> or, failing that, the directory containing the
/// server <c>.csproj</c> found by walking up from the design-time assembly. That keeps the default
/// SQLite file at <c>src/Server/App_Data/zenkins.db</c> instead of somewhere under <c>bin/</c>.
/// </para>
/// </summary>
public sealed class ZenkinsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ZenkinsDbContext>
{
    public ZenkinsDbContext CreateDbContext(string[] args)
    {
        var contentRoot = ResolveContentRoot();
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddUserSecrets(typeof(ZenkinsDesignTimeDbContextFactory).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = ZenkinsDataOptions.FromConfiguration(configuration, contentRoot);
        var builder = new DbContextOptionsBuilder<ZenkinsDbContext>().UseZenkinsDatabase(options);

        // No tenant is selected at design time; filters are never evaluated while only the model is read.
        return new ZenkinsDbContext(builder.Options, new TenantContext(), new SystemDataScope());
    }

    private static string ResolveContentRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ZENKINS_CONTENT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.csproj").Any())
            {
                return directory.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
