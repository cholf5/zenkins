using System.Net;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-014 closes F-013's check-then-act window on the <c>Queued → Running</c> edge: the transition
/// is now the guarded compare-and-set <see cref="ZenkinsDbContext.TryAcceptBuildRunAsync"/>, so two
/// concurrent accepted reports for one run cannot both win. These tests pin the seam directly and
/// through two really concurrent HTTP requests.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineClaimCasTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_accept_compare_and_set_wins_exactly_once()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync("cas-project");
        var (workerToken, workerId) = await host.RegisterWorkerAsync(name: "cas-agent");
        var runId = await host.AddRunAsync(projectId);

        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        var now = DateTimeOffset.UtcNow;
        var organizationId = await host.OrganizationIdAsync();

        // The CAS is tenant-scoped (the worker token selected the tenant in production), so the probe
        // selects the same tenant the claim did instead of a system scope - that is what the filter
        // around a tenant-owned write expects.
        var results = await host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<Zenkins.Server.Data.Tenancy.TenantContext>().OrganizationId = organizationId;
            await using var context = await services
                .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Zenkins.Server.Data.ZenkinsDbContext>>()
                .CreateDbContextAsync(TestContext.Current.CancellationToken);

            var first = await context.TryAcceptBuildRunAsync(
                runId, workerId, now, now.AddMinutes(30), now, TestContext.Current.CancellationToken);
            var second = await context.TryAcceptBuildRunAsync(
                runId, workerId, now, now.AddMinutes(30), now.AddMilliseconds(10), TestContext.Current.CancellationToken);
            return (First: first, Second: second);
        });

        Assert.Equal(1, results.First);
        Assert.Equal(0, results.Second);
        Assert.Equal(BuildRunStatus.Running, (await host.ReadRunAsync(runId)).Status);
    }

    [Fact]
    public async Task Two_concurrent_accepted_reports_for_one_run_cannot_both_win()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync("cas-race-project");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "cas-race-agent");
        var runId = await host.AddRunAsync(projectId);

        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        // Two clients, one run, one lease: both accepted reports enter the server at the same time.
        using var first = host.Host.CreateClient();
        using var second = host.Host.CreateClient();
        var responses = await Task.WhenAll(
            RunTestHost.AcceptAsync(first, workerToken, runId),
            RunTestHost.AcceptAsync(second, workerToken, runId));

        try
        {
            var statuses = responses.Select(response => (int)response.StatusCode).Order().ToArray();
            Assert.Equal([200, 409], statuses);

            var conflict = responses.Single(response => (int)response.StatusCode == 409);
            Assert.Equal(RunErrorCodes.InvalidTransition, await ReadErrorPropertyAsync(conflict));

            // Exactly one transition: the run is Running, started once, with a deadline.
            var run = await host.ReadRunAsync(runId);
            Assert.Equal(BuildRunStatus.Running, run.Status);
            Assert.NotNull(run.StartedAt);
            Assert.NotNull(run.DeadlineAt);
            Assert.Null(run.LeaseExpiresAt);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static async Task<string> ReadErrorPropertyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetString()
            ?? throw new InvalidOperationException("The error response had no code.");
    }
}
