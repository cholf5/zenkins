using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Data;

/// <summary>
/// Lets <c>dotnet ef</c> (migrations, script generation) construct the context without booting the
/// web host. Configuration comes from the Server's <c>appsettings.json</c> next to the design-time
/// assembly plus environment variables, so gates pass
/// <c>Database__Provider=Postgres ConnectionStrings__Default="..."</c> without editing files.
/// No tenant is selected at design time; filters are never evaluated while only the model is read.
/// </summary>
public sealed class ZenkinsDbContextFactory : IDesignTimeDbContextFactory<ZenkinsDbContext>
{
    public ZenkinsDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = ZenkinsDataOptions.FromConfiguration(configuration, AppContext.BaseDirectory);
        var builder = new DbContextOptionsBuilder<ZenkinsDbContext>().UseZenkinsDatabase(options);

        return new ZenkinsDbContext(builder.Options, new TenantContext(), new SystemDataScope());
    }
}
