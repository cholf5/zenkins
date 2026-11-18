using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Runs;
using Zenkins.Shared.Projects;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card F-041's parameter-file delivery is the one route this card added that a worker can read material through,
/// so it needs the same tenancy fact the secret delivery has: a worker token of one organization must not reach
/// another's uploaded file.
/// <para>
/// Both organizations' runs carry REAL sealed bytes in this fixture. Without that, every refusal below would pass
/// against a row that holds nothing - the probe would be asserting "no bytes where there are no bytes". The
/// positive controls (each worker fetching its own file, and the bytes matching) are what make the refusals mean
/// something.
/// </para>
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationParameterFileDeliveryProbeTests
{
    private const string FileParameter = "TENANT_KEY";

    private static readonly byte[] AlphaBytes = Encoding.UTF8.GetBytes("alpha-file-bytes-1d3f7a");
    private static readonly byte[] BetaBytes = Encoding.UTF8.GetBytes("beta-file-bytes-8c42e1");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_parameter_file_delivery_never_crosses_the_tenant_boundary()
    {
        using var host = new TenantProbeHostFixture();
        await SealFileAsync(host, TwoOrgsFixture.BuildRunAId, AlphaBytes);
        await SealFileAsync(host, TwoOrgsFixture.BuildRunBId, BetaBytes);

        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var (_, betaToken) = await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host);

        using var alphaClient = host.CreateClient();
        alphaClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);
        using var betaClient = host.CreateClient();
        betaClient.DefaultRequestHeaders.Authorization = new("Bearer", betaToken);

        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(alphaClient, TwoOrgsFixture.BuildRunAId);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(betaClient, TwoOrgsFixture.BuildRunBId);

        // Positive control: org B's worker fetches org B's file while executing org B's run.
        var (betaStatus, betaBytes) = await TenantIsolationWorkerApiProbes.FetchRunParameterFileAsync(
            betaClient,
            TwoOrgsFixture.BuildRunBId,
            FileParameter);
        Assert.Equal(HttpStatusCode.OK, betaStatus);
        Assert.Equal(BetaBytes, betaBytes);

        // Org A's token, org B's run, org B's parameter: one bodyless 404 - not B's bytes, not an error code.
        var (foreignStatus, foreignBytes) = await TenantIsolationWorkerApiProbes.FetchRunParameterFileAsync(
            alphaClient,
            TwoOrgsFixture.BuildRunBId,
            FileParameter);
        Assert.Equal(HttpStatusCode.NotFound, foreignStatus);
        Assert.Empty(foreignBytes);

        // The reverse direction: org B's token against org A's run.
        var (reverseStatus, reverseBytes) = await TenantIsolationWorkerApiProbes.FetchRunParameterFileAsync(
            betaClient,
            TwoOrgsFixture.BuildRunAId,
            FileParameter);
        Assert.Equal(HttpStatusCode.NotFound, reverseStatus);
        Assert.Empty(reverseBytes);

        // Org A's own fetch still works (the delivery is a feature, not a bug).
        var (alphaStatus, alphaBytes) = await TenantIsolationWorkerApiProbes.FetchRunParameterFileAsync(
            alphaClient,
            TwoOrgsFixture.BuildRunAId,
            FileParameter);
        Assert.Equal(HttpStatusCode.OK, alphaStatus);
        Assert.Equal(AlphaBytes, alphaBytes);
    }

    /// <summary>
    /// Seals real bytes onto one fixture run, the way a trigger would: the type record says the parameter is a
    /// file, and the bytes sit behind the run's own purpose-bound cipher.
    /// </summary>
    private static async Task SealFileAsync(TenantProbeHostFixture host, Guid runId, byte[] bytes)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var systemDataScope = services.GetRequiredService<ISystemDataScope>();
        var protector = services.GetRequiredService<RunParameterProtector>();

        await using var context = await services
            .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
            .CreateDbContextAsync(CancellationToken);
        using var systemScope = systemDataScope.BeginScope();

        var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId)).Single();
        run.ParameterDetailsJson = RunParameterDocuments.SerializeDetails(
            new Dictionary<string, RunParameterDetail>(StringComparer.Ordinal)
            {
                [FileParameter] = new RunParameterDetail(ProjectParameterTypes.File),
            });
        run.ParameterFilesJson = RunParameterFiles.Serialize(
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { [FileParameter] = bytes },
            protector);
        await context.SaveChangesAsync(CancellationToken);
    }
}
