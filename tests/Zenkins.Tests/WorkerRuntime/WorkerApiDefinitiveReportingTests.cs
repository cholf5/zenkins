using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Tests.Pipelines;
using Zenkins.Worker;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Pins the reporting calls' every-4xx-is-definitive semantics (F-007 review fix) against a stub
/// worker API: a 409 on accepted/completed must give the run up after exactly one attempt and keep
/// the loop claiming (the pre-fix dead-parameter condition sent those 409s to the retryable branch,
/// where a concurrency-1 worker starves forever), while the claim call keeps its
/// <c>treatAllClientErrorsAsDefinitive=false</c> path unchanged. The stub is a plain
/// <see cref="HttpMessageHandler"/>, so every answer and attempt count is deterministic - no real
/// server, no timing beyond the loop's own (fast) backoff.
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class WorkerApiDefinitiveReportingTests : IDisposable
{
    /// <summary>Bound for observing the loop's progress; generous for slow CI, still a red proof's timeout.</summary>
    private static readonly TimeSpan GiveUpDeadline = TimeSpan.FromSeconds(30);

    private readonly string _workRoot = Path.Combine(
        Path.GetTempPath(), $"zenkins-f007-stub-{Guid.NewGuid():N}");

    /// <summary>F-014: every claim is a pipeline run, so the stub hands out a real repository fixture.</summary>
    private readonly PipelineRepository _repository = PipelineRepository.Create(
        PipelineTestData.Pipeline("stub", PipelineTestData.Step("Echo", "echo", "ok")));

    [Fact]
    public async Task A_409_on_the_completed_report_gives_the_run_up_and_the_loop_claims_the_next_run()
    {
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        using var stub = new StubWorkerApiServer(_repository.FileUrl);
        stub.QueueClaim(run1);
        stub.QueueClaim(run2);
        // The pre-fix starvation scenario: the first completed report landed but its response was
        // lost, so the retry is answered with 409 (invalid transition) - definitive per-run.
        stub.CompleteStatus[run1] = HttpStatusCode.Conflict;

        using var client = new HttpClient(stub, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        var (loop, stop) = CreateLoop(client);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            // Run 2 must still be claimed, executed and completed after run 1 was given up.
            Assert.True(
                await WaitUntilAsync(() => stub.Completions.ContainsKey(run2)),
                "the loop never completed run 2 after giving up on run 1's 409-completed report");

            // Run 1: exactly one completed attempt, then give up - never retried.
            Assert.Equal(1, stub.CompletedAttempts(run1));
            Assert.False(stub.Completions.ContainsKey(run1));
            // Run 2: claimed next, executed, completed once with exit 0.
            Assert.Equal(1, stub.CompletedAttempts(run2));
            Assert.Equal(0, stub.Completions[run2]);
            Assert.Equal(1, stub.AcceptedAttempts(run1));
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_409_on_the_accepted_report_gives_the_run_up_and_the_loop_claims_the_next_run()
    {
        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        using var stub = new StubWorkerApiServer(_repository.FileUrl);
        stub.QueueClaim(run1);
        stub.QueueClaim(run2);
        // A 409 on the accepted edge (expired/foreign lease) is a definitive per-run answer.
        stub.AcceptStatus[run1] = HttpStatusCode.Conflict;

        using var client = new HttpClient(stub, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        var (loop, stop) = CreateLoop(client);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            Assert.True(
                await WaitUntilAsync(() => stub.Completions.ContainsKey(run2)),
                "the loop never completed run 2 after giving up on run 1's 409-accepted report");

            // Run 1: exactly one accepted attempt, nothing executed, no completed report.
            Assert.Equal(1, stub.AcceptedAttempts(run1));
            Assert.Equal(0, stub.CompletedAttempts(run1));
            Assert.False(stub.Completions.ContainsKey(run1));
            // Run 2: claimed next and carried to a completed report.
            Assert.Equal(1, stub.CompletedAttempts(run2));
            Assert.Equal(0, stub.Completions[run2]);
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task The_claim_call_keeps_its_retryable_409_and_definitive_other_4xx_semantics()
    {
        using var handler = new FixedStatusHandler(HttpStatusCode.Conflict);
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        var api = new WorkerApiClient(client);

        // Claim passes treatAllClientErrorsAsDefinitive=false: a 409 there is the same transient
        // answer as before the review fix - a retryable HttpRequestException carrying the status
        // (the loop lands it in its back-off-and-retry arm), never a definitive per-run rejection.
        var transient = await Assert.ThrowsAsync<HttpRequestException>(
            () => api.ClaimAsync("stub-token", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Conflict, transient.StatusCode);

        // Non-transient 4xx on claim stay definitive through the same shared path.
        handler.Status = HttpStatusCode.BadRequest;
        var definitive = await Assert.ThrowsAsync<WorkerApiRejectedException>(
            () => api.ClaimAsync("stub-token", TestContext.Current.CancellationToken));
        Assert.Equal(400, definitive.StatusCode);
    }

    // ------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------

    private (WorkerRunLoop Loop, CancellationTokenSource Stop) CreateLoop(HttpClient client)
    {
        Directory.CreateDirectory(_workRoot);
        var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost", // unused: the client's base address wins
            [WorkerConfiguration.WorkerNameVariable] = "stub-test-agent",
            [WorkerConfiguration.WorkRootVariable] = _workRoot,
            [WorkerConfiguration.ClaimSecondsVariable] = "0.2",
        }).Configuration
            ?? throw new InvalidOperationException("The stub loop test configuration did not parse.");

        var api = new WorkerApiClient(client);
        // The log stream client is never started here (HasEverConnected stays false), so the
        // loop's drain step cannot wait on a connection this stub server has no endpoint for.
        var logStreams = new LogStreamClient(
            configuration, new WorkerCredential("zwrk_stub-worker-token"), BackoffScheduler.Default(), Console.Out, Console.Error);
        var loop = new WorkerRunLoop(
            configuration,
            api,
            new WorkerCredential("zwrk_stub-worker-token"),
            new WorkDirectoryService(configuration.WorkRoot),
            logStreams,
            new BackoffScheduler(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
            Console.Out,
            Console.Error);
        return (loop, new CancellationTokenSource());
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + GiveUpDeadline;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    /// <summary>
    /// The stub worker API: claim answers come from a FIFO queue (204 once empty), accepted and
    /// completed answer 200 per run unless that run is pinned to a status, and every attempt is
    /// counted so the tests can prove give-up (one attempt) versus retry (many).
    /// </summary>
    private sealed class StubWorkerApiServer(string repositoryUrl) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Guid> _claims = new();
        private readonly ConcurrentDictionary<Guid, int> _accepted = [];
        private readonly ConcurrentDictionary<Guid, int> _completed = [];
        private readonly ConcurrentDictionary<Guid, int> _completions = [];

        /// <summary>Status answered to a run's accepted report instead of 200.</summary>
        public ConcurrentDictionary<Guid, HttpStatusCode> AcceptStatus { get; } = [];

        /// <summary>Status answered to a run's completed report instead of 200.</summary>
        public ConcurrentDictionary<Guid, HttpStatusCode> CompleteStatus { get; } = [];

        /// <summary>The completed reports this stub accepted (2xx), keyed by run, with their exit codes.</summary>
        public IReadOnlyDictionary<Guid, int> Completions => _completions;

        public void QueueClaim(Guid runId) => _claims.Enqueue(runId);

        public int AcceptedAttempts(Guid runId) => _accepted.GetValueOrDefault(runId);

        public int CompletedAttempts(Guid runId) => _completed.GetValueOrDefault(runId);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/workers/claim")
            {
                if (_claims.TryDequeue(out var runId))
                {
                    return Json(
                        HttpStatusCode.OK,
                        $$"""
                        {"runId":"{{runId}}","projectId":"{{Guid.NewGuid()}}","repositoryUrl":"{{repositoryUrl}}","branch":"main","pipelinePath":"pipeline.lua","leaseExpiresAt":"2026-11-13T12:00:00Z"}
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 5
                && segments[0] == "api"
                && segments[1] == "workers"
                && segments[2] == "runs"
                && Guid.TryParse(segments[3], out var reportedRun))
            {
                if (segments[4] == "accepted")
                {
                    _accepted.AddOrUpdate(reportedRun, 1, (_, count) => count + 1);
                    var acceptStatus = AcceptStatus.GetValueOrDefault(reportedRun, HttpStatusCode.OK);
                    return acceptStatus is HttpStatusCode.OK
                        ? Json(acceptStatus, """{"status":"Running","deadlineAt":"2099-11-13T13:00:00Z"}""")
                        : new HttpResponseMessage(acceptStatus);
                }

                if (segments[4] == "steps")
                {
                    // F-015: the per-step transition reports. Accepted here (the card's own tests pin the
                    // refusals); the answer carries the recorded row's id, which the worker stamps on the
                    // step's log lines.
                    return Json(
                        HttpStatusCode.OK,
                        $$"""{"stepId":"{{Guid.NewGuid()}}","status":"Running","runStatus":"Running"}""");
                }

                if (segments[4] == "definition")
                {
                    // F-014: two sends share this endpoint - the commit-only one right after the clone,
                    // then the spec. Both are accepted here; the card's own tests pin the refusals.
                    return Json(HttpStatusCode.OK, """{"status":"Running"}""");
                }

                if (segments[4] == "completed")
                {
                    _completed.AddOrUpdate(reportedRun, 1, (_, count) => count + 1);
                    var status = CompleteStatus.GetValueOrDefault(reportedRun, HttpStatusCode.OK);
                    if (status is HttpStatusCode.OK)
                    {
                        var body = request.Content is null
                            ? "{}"
                            : await request.Content.ReadAsStringAsync(cancellationToken);
                        using var document = JsonDocument.Parse(body);
                        _completions[reportedRun] = document.RootElement.GetProperty("exitCode").GetInt32();
                    }

                    return new HttpResponseMessage(status);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string payload) => new(status)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Answers every request with one scripted status (for the client-level claim pin).</summary>
    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status));
    }

    public void Dispose()
    {
        _repository.Dispose();
        // Q-011's cleanup contract via TestCleanup: the tree holds a cloned repository with
        // read-only pack files (a Windows delete reports UnauthorizedAccessException for them).
        TestCleanup.DeleteTree(_workRoot);
    }
}
