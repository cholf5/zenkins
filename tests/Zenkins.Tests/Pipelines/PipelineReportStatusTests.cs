using System.Net;
using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// The run-reporting calls' status classification (card F-014, review m14): only 400/404/409/410 are a
/// definitive per-run answer, so the run is given up; 408/425/429 stay transient and are retried even
/// there - a throttling 429 must never starve a worker or abandon a run that a retry can finish.
/// <para>
/// This is the narrow half of the rule the polling calls already had (408/409/425/429 transient). It is
/// pinned here because the previous shape treated <em>every</em> 4xx of a reporting call as definitive,
/// which contradicted the shared <c>IsTransientStatus</c> classification in the same file.
/// </para>
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineReportStatusTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(429)] // throttling: a retry can succeed
    [InlineData(408)] // request timeout: a retry can succeed
    [InlineData(425)] // too early: a retry can succeed
    public async Task A_transient_4xx_on_the_definition_report_is_not_a_give_up(int statusCode)
    {
        using var handler = new FixedStatusHandler((HttpStatusCode)statusCode);
        var api = new WorkerApiClient(CreateClient(handler));

        // The transient arm surfaces an HttpRequestException carrying the status - the loop's
        // back-off-and-retry path - never the definitive WorkerApiRejectedException.
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => api.ReportDefinitionAsync(
            "worker-token",
            Guid.NewGuid(),
            gitCommit: null,
            pipelineSpecJson: SpecJson,
            CancellationToken));

        Assert.Equal(statusCode, (int)failure.StatusCode!.Value);
    }

    [Theory]
    [InlineData(409)] // another worker owns it / already terminal
    [InlineData(410)] // gone
    [InlineData(404)] // unknown run for this tenant
    [InlineData(400)] // the payload itself was refused
    public async Task A_definitive_4xx_on_the_definition_report_is_a_give_up(int statusCode)
    {
        using var handler = new FixedStatusHandler((HttpStatusCode)statusCode);
        var api = new WorkerApiClient(CreateClient(handler));

        var failure = await Assert.ThrowsAsync<WorkerApiRejectedException>(() => api.ReportDefinitionAsync(
            "worker-token",
            Guid.NewGuid(),
            gitCommit: null,
            pipelineSpecJson: SpecJson,
            CancellationToken));

        Assert.Equal(statusCode, failure.StatusCode);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(408)]
    public async Task A_transient_4xx_on_the_accepted_and_completed_reports_is_not_a_give_up(int statusCode)
    {
        // The same narrow rule applies to every run-reporting call, so the three of them cannot drift.
        var runId = Guid.NewGuid();
        using var handler = new FixedStatusHandler((HttpStatusCode)statusCode);
        var api = new WorkerApiClient(CreateClient(handler));

        Assert.Equal(
            statusCode,
            (int)(await Assert.ThrowsAsync<HttpRequestException>(() =>
                api.AcceptAsync("worker-token", runId, gitCommit: null, CancellationToken))).StatusCode!.Value);
        Assert.Equal(
            statusCode,
            (int)(await Assert.ThrowsAsync<HttpRequestException>(() =>
                api.CompleteAsync("worker-token", runId, exitCode: 0, failureReason: null, CancellationToken)))
                .StatusCode!.Value);
    }

    [Fact]
    public async Task The_claim_call_keeps_its_own_classification()
    {
        // The polling path is unchanged: 409 there is a race loser (transient), 400 is a contract
        // mismatch (definitive).
        using var handler = new FixedStatusHandler(HttpStatusCode.Conflict);
        var api = new WorkerApiClient(CreateClient(handler));
        Assert.Equal(
            409,
            (int)(await Assert.ThrowsAsync<HttpRequestException>(
                () => api.ClaimAsync("worker-token", CancellationToken))).StatusCode!.Value);

        handler.Status = HttpStatusCode.BadRequest;
        Assert.Equal(
            400,
            (await Assert.ThrowsAsync<WorkerApiRejectedException>(
                () => api.ClaimAsync("worker-token", CancellationToken))).StatusCode);
    }

    private const string SpecJson = """
        { "version": 1, "name": "Build", "steps": [ { "name": "Only", "run": [ "dotnet", "--info" ] } ] }
        """;

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };

    /// <summary>Answers every request with one scripted status (no server, no timing).</summary>
    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status));
    }
}
