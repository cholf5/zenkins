using System.Collections.Concurrent;
using System.Net;
using Xunit;
using Zenkins.Tests.WorkerRuntime;
using Zenkins.Worker;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card Q-003 (debt handed over by F-014): the abandoned-run branch, one layer up from
/// <see cref="Zenkins.Worker.Pipelines.PipelineRunner"/>. The runner-level tests prove that a refused
/// report produces <c>GiveUp</c>; this proves what the <b>loop</b> does with that answer: a run the
/// server has definitively disowned (a 4xx on its definition report - another worker owns it, it was
/// canceled, it is already terminal) starts no step and sends no completed report, and the loop moves
/// straight on to the next claim. A plain <see cref="HttpMessageHandler"/> stub, like
/// <c>WorkerApiDefinitiveReportingTests</c>: every answer and attempt count is deterministic.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineRunLoopAbandonmentTests : IDisposable
{
    /// <summary>Bound for observing the loop's progress; generous for slow CI, still a red proof's timeout.</summary>
    private static readonly TimeSpan GiveUpDeadline = TimeSpan.FromSeconds(30);

    private readonly string _workRoot = Path.Combine(
        Path.GetTempPath(), $"zenkins-q003-loop-{Guid.NewGuid():N}");

    /// <summary>Every claim is a pipeline run, so the stub hands out a real repository fixture.</summary>
    private readonly PipelineRepository _repository = PipelineRepository.Create(
        PipelineTestData.Pipeline("abandoned", PipelineTestData.Step("Echo", "echo", "ok")));

    [Fact]
    public async Task A_run_the_server_disowns_at_its_definition_report_starts_no_step_and_sends_no_completed_report()
    {
        var disowned = Guid.NewGuid();
        var next = Guid.NewGuid();
        using var stub = new StubWorkerApiServer(_repository.FileUrl);
        stub.QueueClaim(disowned);
        stub.QueueClaim(next);

        // The commit report (sent right after the clone) is answered with a definitive 409 - in
        // production: the run is not ours any more, or it is already terminal server-side.
        stub.DefinitionStatus[disowned] = HttpStatusCode.Conflict;

        using var client = new HttpClient(stub, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        var (loop, stop) = CreateLoop(client);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            // The loop moved on: the run after the disowned one is claimed, executed and completed.
            Assert.True(
                await WaitUntilAsync(() => stub.Completions.ContainsKey(next)),
                "the loop never completed the run after giving up on the disowned run's definition report");

            // The disowned run: its definition report was attempted exactly once (refused, never
            // retried - a 4xx on a run report is definitive), no step was reported (the runner gives up
            // before the definition phase even reads the pipeline file), and no completed report was
            // sent - the server owns that run's state.
            Assert.Equal(1, stub.DefinitionAttempts(disowned));
            Assert.Equal(0, stub.StepAttempts(disowned));
            Assert.Equal(0, stub.CompletedAttempts(disowned));
            Assert.False(stub.Completions.ContainsKey(disowned));

            // The healthy run behind it: claimed next, executed to a completed exit 0. (Two definition
            // sends are F-014's normal shape: the commit-only one, then the frozen spec.)
            Assert.Equal(2, stub.DefinitionAttempts(next));
            Assert.Equal(1, stub.CompletedAttempts(next));
            Assert.Equal(0, stub.Completions[next]);
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
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
            [WorkerConfiguration.WorkerNameVariable] = "stub-abandon-agent",
            [WorkerConfiguration.WorkRootVariable] = _workRoot,
            [WorkerConfiguration.ClaimSecondsVariable] = "0.2",
        }).Configuration
            ?? throw new InvalidOperationException("The stub loop test configuration did not parse.");

        var api = new WorkerApiClient(client);
        // The log stream client is never started (HasEverConnected stays false), so the loop's drain
        // step cannot wait on a connection this stub server has no endpoint for.
        var logStreams = new LogStreamClient(
            configuration, "stub-worker-token", BackoffScheduler.Default(), Console.Out, Console.Error);
        var loop = new WorkerRunLoop(
            configuration,
            api,
            "stub-worker-token",
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
    /// The stub worker API: claim answers come from a FIFO queue (204 once empty), accepted answers
    /// 200 with a far-future deadline, definition/step/completed answer 200 per run unless that run is
    /// pinned to a status, and every attempt is counted so a test can prove give-up (one attempt, no
    /// side effects) versus execution.
    /// </summary>
    private sealed class StubWorkerApiServer(string repositoryUrl) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Guid> _claims = new();
        private readonly ConcurrentDictionary<Guid, int> _definitions = [];
        private readonly ConcurrentDictionary<Guid, int> _steps = [];
        private readonly ConcurrentDictionary<Guid, int> _completed = [];
        private readonly ConcurrentDictionary<Guid, int> _completions = [];

        /// <summary>Status answered to a run's definition report (commit-only or spec) instead of 200.</summary>
        public ConcurrentDictionary<Guid, HttpStatusCode> DefinitionStatus { get; } = [];

        /// <summary>How many definition reports (commit-only or spec) this stub saw for a run.</summary>
        public int DefinitionAttempts(Guid runId) => _definitions.GetValueOrDefault(runId);

        /// <summary>How many step-transition reports this stub saw for a run (F-015).</summary>
        public int StepAttempts(Guid runId) => _steps.GetValueOrDefault(runId);

        /// <summary>How many completed-report attempts this stub saw for a run.</summary>
        public int CompletedAttempts(Guid runId) => _completed.GetValueOrDefault(runId);

        /// <summary>The completed reports this stub accepted (2xx), keyed by run, with their exit codes.</summary>
        public IReadOnlyDictionary<Guid, int> Completions => _completions;

        public void QueueClaim(Guid runId) => _claims.Enqueue(runId);

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
                        {"runId":"{{runId}}","projectId":"{{Guid.NewGuid()}}","repositoryUrl":"{{repositoryUrl}}","branch":"main","pipelinePath":"pipeline.lua","leaseExpiresAt":"2026-11-14T12:00:00Z"}
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
                    return Json(HttpStatusCode.OK, """{"status":"Running","deadlineAt":"2099-11-14T13:00:00Z"}""");
                }

                if (segments[4] == "steps")
                {
                    _steps.AddOrUpdate(reportedRun, 1, (_, count) => count + 1);
                    return Json(
                        HttpStatusCode.OK,
                        $$"""{"stepId":"{{Guid.NewGuid()}}","status":"Running","runStatus":"Running"}""");
                }

                if (segments[4] == "definition")
                {
                    _definitions.AddOrUpdate(reportedRun, 1, (_, count) => count + 1);
                    var status = DefinitionStatus.GetValueOrDefault(reportedRun, HttpStatusCode.OK);
                    return status is HttpStatusCode.OK
                        ? Json(HttpStatusCode.OK, """{"status":"Running"}""")
                        : new HttpResponseMessage(status);
                }

                if (segments[4] == "completed")
                {
                    _completed.AddOrUpdate(reportedRun, 1, (_, count) => count + 1);
                    var body = request.Content is null
                        ? "{}"
                        : await request.Content.ReadAsStringAsync(cancellationToken);
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    _completions[reportedRun] = document.RootElement.GetProperty("exitCode").GetInt32();
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string payload) => new(status)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    public void Dispose()
    {
        _repository.Dispose();
        try
        {
            Directory.Delete(_workRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
