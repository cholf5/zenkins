using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.LogStreaming;
using Zenkins.Server.Workers;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Tenancy;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;
using Zenkins.Worker;
using Zenkins.Worker.Git;
using Zenkins.Worker.Pipelines;

// The worker project's namespace is Zenkins.Worker, which shadows the entity types inside any Zenkins.*
// namespace; the aliases keep them readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;
using WorkerTokenEntity = Zenkins.Server.Data.Entities.WorkerToken;

namespace Zenkins.Tests.Steps;

/// <summary>One line the run's log pipeline emitted, with the step it was stamped for (card F-015).</summary>
internal sealed record EmittedLine(string Stream, string Message, Guid? StepId);

/// <summary>
/// One end-to-end pipeline run for card F-015's step tests: the <b>real</b> server (through
/// <see cref="RunTestHost"/>, so the step and secret endpoints are exercised over HTTP), the <b>real</b>
/// worker pipeline layer (clone, definition phase, executor, redaction filter), and the <b>real</b> server
/// ingest for the log lines - so every assertion reads what the product would actually have recorded.
/// <para>
/// The run is started through the product's own path by default (trigger form → claim → accept). The
/// second-organization cases (TC-005) seed the project, the run and the worker directly in the system scope,
/// because a second organization has no Owner session flow in this test host - the assertion is about the
/// tenant scoping of the endpoints, not about how the run got there.
/// </para>
/// </summary>
internal sealed class StepsRun : IDisposable
{
    private static readonly TimeSpan PipelineDeadline = TimeSpan.FromMinutes(2);

    private readonly string _workRoot;

    /// <summary>
    /// The emitted lines. Written by the pipeline thread (synthetic lines) and by each step's two output
    /// reader threads, read by the assertions, so every access takes the lock - a <see cref="List{T}"/>
    /// enumerated while another thread appends is not safe.
    /// </summary>
    private readonly List<EmittedLine> _lines = [];
    private readonly object _linesGate = new();
    private readonly List<PipelineStepReport> _reports = [];

    private StepsRun(RunTestHost host, PipelineRepository repository)
    {
        Host = host;
        Repository = repository;
        _workRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f015-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workRoot);
    }

    public RunTestHost Host { get; }

    public PipelineRepository Repository { get; }

    public Guid RunId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid WorkerId { get; private set; }

    public string WorkerToken { get; private set; } = string.Empty;

    public HttpClient Owner { get; private set; } = null!;

    public HttpClient WorkerClient { get; private set; } = null!;

    public RunWorkDirectory Work { get; private set; } = null!;

    /// <summary>The branch the run was triggered for.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>The worker's own environment for this run (the first composition layer).</summary>
    public IReadOnlyDictionary<string, string?>? InheritedEnvironment { get; set; }

    /// <summary>
    /// Called for every step transition the executor sends, <b>after</b> the server accepted it - so a test
    /// that reacts to a transition (the cancellation case) reacts to something the product recorded.
    /// </summary>
    public Func<PipelineStepReport, Task>? OnReport { get; set; }

    /// <summary>Cancels the executor (the server-requested kill path).</summary>
    public CancellationToken CancellationToken { get; set; } = TestContext.Current.CancellationToken;

    /// <summary>Whether the harness reports the run's terminal state (as the worker loop does).</summary>
    public bool ReportTerminalState { get; set; } = true;

    /// <summary>Whether the emitted lines are pushed through the real server ingest (persisted + viewable).</summary>
    public bool PersistLogs { get; set; } = true;

    /// <summary>
    /// The reason the server's cancel request carries (F-009): <c>timeout</c> for the deadline, or a user reason.
    /// Both the killed step's failure reason and the canceled report's outcome are derived from it by the
    /// product's own predicates (<see cref="StepCancellationReason"/> and <see cref="Zenkins.Shared.RunCancellation.RunCancelReasons"/>),
    /// never by a copy of them in the harness - a copy would keep passing after the product drifted.
    /// </summary>
    public string CancelReason { get; set; } = Zenkins.Shared.RunCancellation.RunCancelReasons.ForUser("steps-test");

    /// <summary>
    /// Runs once the server has accepted the frozen spec and before the first step is reported - the seam a
    /// test needs to change the run's state (or the world) between the definition and the steps.
    /// </summary>
    public Func<Task>? AfterDefinitionAccepted { get; set; }

    /// <summary>The run's local temp stdout log, read back from disk (the worker's debugging copy).</summary>
    public string TempStdOutLog() => ReadTempLog(RunLogWriters.StdOutLogFileName);

    /// <summary>The run's local temp stderr log, read back from disk.</summary>
    public string TempStdErrLog() => ReadTempLog(RunLogWriters.StdErrorLogFileName);

    public PipelineRunOutcome Outcome { get; private set; } = null!;

    /// <summary>The lines the run emitted, in order, with the step id each was stamped with.</summary>
    public IReadOnlyList<EmittedLine> Lines
    {
        get
        {
            lock (_linesGate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>The step transitions the executor asked for, in order.</summary>
    public IReadOnlyList<PipelineStepReport> Reports => _reports;

    /// <summary>
    /// Starts a run for <paramref name="repository"/> through the product path: the project row, the trigger
    /// form's run, the worker's claim and the accept.
    /// </summary>
    public static async Task<StepsRun> StartAsync(
        RunTestHost host,
        PipelineRepository repository,
        string? branch = null,
        string workerName = "steps-agent",
        HttpClient? actingClient = null)
    {
        var run = new StepsRun(host, repository);
        try
        {
            await run.StartProductRunAsync(branch, workerName, actingClient);
            return run;
        }
        catch
        {
            run.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts a run in <paramref name="organizationId"/> - a second organization for TC-005 - by seeding the
    /// project, the worker token and the run directly and putting the run in <c>Running</c>. The endpoints
    /// under test are the same ones; only the setup path differs (there is no Owner session for that
    /// organization in this host).
    /// </summary>
    public static async Task<StepsRun> StartInOrganizationAsync(
        RunTestHost host,
        PipelineRepository repository,
        Guid organizationId,
        string? branch = null,
        string workerName = "foreign-steps-agent")
    {
        var run = new StepsRun(host, repository) { Branch = branch ?? "main" };
        try
        {
            await run.SeedForeignRunAsync(organizationId, workerName);
            return run;
        }
        catch
        {
            run.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Executes the run's pipeline with the real executor, then reports the terminal state and persists the
    /// emitted lines - the same order (and the same cleanup-before-report discipline) the worker loop uses.
    /// </summary>
    public async Task<PipelineRunOutcome> ExecuteAsync()
    {
        var api = new WorkerApiClient(Host.Host.CreateClient());
        var backoff = new BackoffScheduler(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20));
        var redactor = new GitLogFilter();
        var stamp = new StepStamp();
        // The step sink's own diagnostic channel. This harness never writes to it on a success path (nothing in
        // the product does either), so it is not asserted on - a "no plaintext here" check on an always-empty
        // writer could not fail. The console surface with real content is pinned by
        // StepLogWriterTests.The_worker_crash_diagnostic_is_redacted_and_written_through_the_same_gate.
        var errorLog = new StringWriter();

        // The worker's temp log files, written through the product's own type: the harness mirrors
        // WorkerRunLoop, so the file assertions below read what the product really writes.
        using var writers = new RunLogWriters(Work.TempPath);

        void Record(string stream, string line)
        {
            var emitted = new EmittedLine(stream, redactor.Redact(line), stamp.Current);
            lock (_linesGate)
            {
                _lines.Add(emitted);
            }

            writers.Write(stream, emitted.Message);
        }

        void Emit(string line) => Record(LogStream.StdOut, line);

        void OnOutput(ProcessOutputLine line) => Record(
            line.Stream == ProcessOutputStream.StdOut ? LogStream.StdOut : LogStream.StdErr,
            line.Line);

        var definitions = new PipelineDefinitionRunner(
            PipelineDefinitionRunner.CreateRuntime(PipelineRunner.CreateLogSink(Emit)));
        var runner = new PipelineRunner(
            new PipelineRunOptions
            {
                Work = Work,
                RepositoryUrl = Repository.FileUrl,
                Branch = Branch,
                PipelinePath = "pipeline.lua",
                Deadline = DateTimeOffset.UtcNow + PipelineDeadline,
                InheritedEnvironment = InheritedEnvironment,
                StepEnvironment = new Dictionary<string, string?>
                {
                    ["ZENKINS_RUN_ID"] = RunId.ToString(),
                    ["ZENKINS_PROJECT_ID"] = ProjectId.ToString(),
                    ["ZENKINS_WORKER_NAME"] = "steps-agent",
                },
                Log = Emit,
                OnProcessOutput = OnOutput,
                Redactor = redactor,
                Secrets = new RunSecretCache(new ServerSecretSource(api, WorkerToken, RunId)),
                CancellationFailureReason = () => StepCancellationReason.For(CancelReason),
                StepSink = new RecordingStepSink(
                    new ServerStepSink(api, WorkerToken, RunId, backoff, errorLog),
                    _reports,
                    OnReport),
                OnStepChanged = stamp.Set,
            },
            definitions,
            _ => Task.FromResult<GitCredentialMaterial?>(null),
            ReportDefinitionAsync);

        async Task<bool> ReportDefinitionAsync(
            string? commit,
            string? specJson,
            CancellationToken cancellationToken)
        {
            using var response = await RunTestHost.ReportDefinitionAsync(
                WorkerClient, WorkerToken, RunId, specJson, commit);
            var accepted = response.IsSuccessStatusCode || (int)response.StatusCode is 409 or 410;
            if (accepted && specJson is not null && AfterDefinitionAccepted is { } hook)
            {
                await hook();
            }

            return accepted;
        }

        Outcome = await runner.RunAsync(CancellationToken);

        if (PersistLogs)
        {
            await PersistEmittedLinesAsync();
        }

        if (ReportTerminalState)
        {
            await ReportTerminalStateAsync();
        }

        return Outcome;
    }

    /// <summary>
    /// Creates an organization secret through the real Owner form (card F-012's endpoint), so the fetch the
    /// executor performs goes through the real decryption path.
    /// </summary>
    public async Task CreateSecretAsync(string name, string value)
    {
        using var response = await OrganizationTestHost.PostFormAsync(
            Owner,
            "/secrets",
            "/secrets/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Value"] = value,
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating secret '{name}' failed with {(int)response.StatusCode}.");
    }

    /// <summary>The run row as the server recorded it.</summary>
    public Task<BuildRun> ReadRunAsync() => Host.ReadRunAsync(RunId);

    /// <summary>
    /// Appends lines to the run's log through the server's real ingest, bypassing the executor - the seam the
    /// <c>StepId</c>-ownership test needs (a line claiming a step that is not this run's).
    /// </summary>
    public async Task AppendRawLinesAsync(params (string Stream, string Message, Guid? StepId)[] lines)
    {
        var messages = lines
            .Select((line, index) => new LogLineMessage(
                10_000 + index,
                line.Stream,
                line.Message,
                DateTimeOffset.UtcNow,
                line.StepId))
            .ToArray();

        await Host.Host.InScopeAsync(async services =>
        {
            var ingest = services.GetRequiredService<RunLogIngest>();
            await ingest.AppendAsync(RunId, OrganizationId, messages, CancellationToken);
            await ingest.CompleteRunAsync(RunId);
            return true;
        });
    }

    private string ReadTempLog(string fileName)
    {
        var path = Path.Combine(Work.TempPath, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>Reads the run's recorded steps in declaration order (the DB is the record, not the UI).</summary>
    public Task<IReadOnlyList<BuildStep>> ReadStepsAsync() =>
        Host.InSystemScopeAsync(async context =>
            (IReadOnlyList<BuildStep>)(await context.SystemQueryAsync<BuildStep>(step => step.RunId == RunId))
                .OrderBy(step => step.Index)
                .ToArray());

    /// <summary>Reads the run's persisted log lines (the rows the download endpoint and the page read).</summary>
    public Task<IReadOnlyList<BuildLog>> ReadLogRowsAsync() =>
        Host.InSystemScopeAsync(async context =>
            (IReadOnlyList<BuildLog>)(await context.SystemQueryAsync<BuildLog>(log => log.RunId == RunId))
                .OrderBy(log => log.Seq)
                .ToArray());

    /// <summary>The run page's HTML, through the real authenticated page.</summary>
    public Task<string> ReadRunPageAsync() =>
        Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(Owner, $"/runs/{RunId}");

    /// <summary>The full-log download, through the real endpoint.</summary>
    public async Task<string> DownloadLogAsync()
    {
        using var response = await Owner.GetAsync($"/runs/{RunId}/log/download", CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(CancellationToken);
    }

    private async Task StartProductRunAsync(string? branch, string workerName, HttpClient? actingClient)
    {
        Owner = await Host.Inner.SignInOwnerAsync();
        OrganizationId = await Host.OrganizationIdAsync();
        ProjectId = await Host.SeedProjectAsync(
            $"steps-project-{Guid.NewGuid():N}",
            Repository.FileUrl);
        (WorkerToken, WorkerId) = await Host.RegisterWorkerAsync(name: workerName);
        WorkerClient = Host.Host.CreateClient();

        if (branch is not null)
        {
            Branch = branch;
        }

        // The trigger is the acting user's: any member may trigger (PRD §3.1.2), and a member-triggered run must
        // read the organization's secrets exactly like the owner's (PRD §3.8 - the script is trusted code).
        RunId = await RunTestHost.TriggerRunAsync(actingClient ?? Owner, ProjectId, branch: branch);

        using var claimed = await RunTestHost.ClaimAsync(WorkerClient, WorkerToken);
        Assert.Equal(RunId, await RunTestHost.ReadClaimedRunIdAsync(claimed));

        using var accepted = await RunTestHost.AcceptAsync(WorkerClient, WorkerToken, RunId);
        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);

        Work = new WorkDirectoryService(_workRoot).Prepare(RunId);
    }

    private async Task SeedForeignRunAsync(Guid organizationId, string workerName)
    {
        Owner = await Host.Inner.SignInOwnerAsync();
        OrganizationId = organizationId;

        (ProjectId, RunId, WorkerId, WorkerToken) = await Host.InSystemScopeAsync(async context =>
        {
            var project = new Project
            {
                OrganizationId = organizationId,
                Name = $"foreign-steps-{Guid.NewGuid():N}",
                RepositoryUrl = Repository.FileUrl,
                DefaultBranch = "main",
                PipelinePath = "pipeline.lua",
            };
            context.Projects.Add(project);

            var worker = new WorkerEntity
            {
                OrganizationId = organizationId,
                Name = workerName,
                Os = "Linux",
                Architecture = "X64",
                Version = "1.0.0",
                Tags = "ci",
                CpuCount = 2,
                MaxConcurrency = 1,
                Status = WorkerStatus.Online,
            };
            context.Workers.Add(worker);

            var token = WorkerTokenCrypto.Generate(WorkerTokenCrypto.WorkerPrefix);
            context.WorkerTokens.Add(new WorkerTokenEntity
            {
                OrganizationId = organizationId,
                Kind = WorkerTokenKind.Worker,
                WorkerId = worker.Id,
                TokenHash = WorkerTokenCrypto.Hash(token),
            });

            var run = new BuildRun
            {
                OrganizationId = organizationId,
                ProjectId = project.Id,
                Status = BuildRunStatus.Running,
                TriggerType = BuildRunTriggerType.Manual,
                RequiredTags = string.Empty,
                GitBranch = Branch,
                PipelinePath = "pipeline.lua",
                WorkerId = worker.Id,
                StartedAt = DateTimeOffset.UtcNow,
                DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10),
            };
            context.BuildRuns.Add(run);

            await context.SaveChangesAsync();
            return (project.Id, run.Id, worker.Id, token);
        });

        WorkerClient = Host.Host.CreateClient();
        Work = new WorkDirectoryService(_workRoot).Prepare(RunId);
    }

    /// <summary>
    /// Pushes the emitted lines through the server's real ingest, so the persisted rows, the download and the
    /// page are the product's, not the harness's. Sequences are assigned here exactly as the worker's buffer
    /// does (per run, from 1).
    /// </summary>
    private async Task PersistEmittedLinesAsync()
    {
        var emitted = Lines;
        var lines = new List<LogLineMessage>(emitted.Count);
        long seq = 1;
        foreach (var line in emitted)
        {
            lines.Add(new LogLineMessage(seq++, line.Stream, line.Message, DateTimeOffset.UtcNow, line.StepId));
        }

        await Host.Host.InScopeAsync(async services =>
        {
            var ingest = services.GetRequiredService<RunLogIngest>();
            await ingest.AppendAsync(RunId, OrganizationId, lines, CancellationToken);
            await ingest.CompleteRunAsync(RunId);
            return true;
        });
    }

    /// <summary>
    /// Reports the run's terminal state exactly as the worker loop does: a cancellation through F-009's
    /// canceled endpoint (the server owns the Canceled/Failed(timeout) mapping), anything else through the
    /// completed endpoint with the outcome's code and closed reason.
    /// </summary>
    private async Task ReportTerminalStateAsync()
    {
        if (Outcome.Abandoned || (Outcome.ExitCode is null && Outcome.FailureReason is null && !Outcome.Canceled))
        {
            return;
        }

        if (Outcome.Canceled)
        {
            using var canceled = await WorkerTestHost.SendWorkerJsonAsync(
                WorkerClient,
                WorkerToken,
                $"/api/workers/runs/{RunId}/canceled",
                new
                {
                    // The same production predicate the worker loop uses for this decision (F-009's outcome).
                    outcome = Zenkins.Shared.RunCancellation.RunCancelReasons.IsTimeout(CancelReason)
                        ? Zenkins.Shared.RunCancellation.RunCanceledOutcomes.Timeout
                        : Zenkins.Shared.RunCancellation.RunCanceledOutcomes.Canceled,
                });
            return;
        }

        using var completed = await RunTestHost.CompleteAsync(
            WorkerClient,
            WorkerToken,
            RunId,
            Outcome.ExitCode,
            Outcome.FailureReason);
        Assert.Equal(System.Net.HttpStatusCode.OK, completed.StatusCode);
    }

    public void Dispose()
    {
        WorkerClient?.Dispose();
        try
        {
            Directory.Delete(_workRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Records every transition the executor sends, so a test can assert on the protocol itself.</summary>
    private sealed class RecordingStepSink(
        IPipelineStepSink inner,
        List<PipelineStepReport> reports,
        Func<PipelineStepReport, Task>? onReport) : IPipelineStepSink
    {
        public async Task<PipelineStepReceipt> ReportAsync(
            PipelineStepReport report,
            CancellationToken cancellationToken)
        {
            reports.Add(report);

            // Deliver first, observe second: a test that reacts to a transition (the cancellation case)
            // must react to a report the server has already accepted, otherwise it would cancel the report
            // itself instead of the step.
            var receipt = await inner.ReportAsync(report, cancellationToken);
            if (onReport is not null)
            {
                await onReport(report);
            }

            return receipt;
        }
    }

    /// <summary>The current step's id, published as one atomic reference (see the worker loop's holder).</summary>
    private sealed class StepStamp
    {
        private volatile object? _value;

        public Guid? Current => _value is Guid id ? id : null;

        public void Set(Guid? id) => _value = id;
    }
}
