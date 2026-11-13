using System.Net;
using Xunit;
using Zenkins.Shared.Artifacts;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Pipelines;
using Zenkins.Worker;
using Zenkins.Worker.Artifacts;
using Zenkins.Worker.Git;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// Card F-016, AC-8/AC-9 at the executor level: publishing happens <b>after</b> a step succeeds, a refused
/// path or a breached cap fails <b>that step</b> with the closed artifact reason, and a cancellation during
/// publishing ends the run through F-009's canceled path (never as a silently failed step, and never with a
/// <c>Ready</c> row).
/// <para>
/// No server is involved here: the run is a real fixture repository, the definition is read by the real
/// runtime, and the steps are real processes. The publisher is a fake, because what is under test is the
/// executor's <b>sequencing and attribution</b>, not the protocol (which the HTTP tests cover).
/// </para>
/// </summary>
[Trait("Category", "Artifacts")]
public sealed class ArtifactExecutorTests
{
    [Fact(Timeout = 180 * 1000)]
    public async Task A_step_that_succeeds_publishes_what_it_declared_before_it_is_reported_successful()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ExecutorFixture(PublishPipeline());
        var publisher = new RecordingPublisher();

        var outcome = await fixture.ExecuteAsync(publisher, token);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);
        Assert.Equal(["out", "dist"], publisher.Published);

        // The ordering is the contract: the step is claimed Running, its artifacts are published, and only then is
        // it reported Success - the next step starts afterwards.
        Assert.Equal(
            [
                "report:1:Running",
                "publish:out",
                "publish:dist",
                "report:1:Success",
                "report:2:Running",
                "report:2:Success",
            ],
            fixture.Events);
    }

    [Fact(Timeout = 180 * 1000)]
    public async Task A_refused_artifact_path_fails_the_step_and_stops_the_run()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ExecutorFixture(PublishPipeline());
        var publisher = new RecordingPublisher
        {
            Fail = _ => ArtifactPublishException.PathRejected("the artifact path '../../etc/passwd' leaves the artifact root."),
        };

        var outcome = await fixture.ExecuteAsync(publisher, token);

        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);

        var build = fixture.Reports.Single(report => report.Index == 1 && report.Status == PipelineStepStatus.Failed);
        Assert.Equal(PipelineStepFailureReasons.ArtifactPathRejected, build.FailureReason);

        // The log names the path, which is what a person reading the run needs.
        Assert.Contains(
            fixture.LogLines,
            line => line.Contains("../../etc/passwd", StringComparison.Ordinal));

        // The second step never ran: the run stopped at the failing step.
        Assert.DoesNotContain(fixture.Reports, report => report.Index == 2 && report.Status == PipelineStepStatus.Running);
        Assert.Contains(fixture.Reports, report =>
            report.Index == 2 && report.Status == PipelineStepStatus.Skipped
            && report.FailureReason == PipelineStepFailureReasons.PreviousStepFailed);
    }

    [Fact(Timeout = 180 * 1000)]
    public async Task A_cap_breach_fails_the_step_with_the_artifact_reason_and_names_the_limit()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ExecutorFixture(PublishPipeline());
        var publisher = new RecordingPublisher
        {
            Fail = _ => ArtifactPublishException.UploadFailed(
                "the artifact 'out' holds 20001 files, over Artifacts:MaxFiles (20000 files per artifact)."),
        };

        var outcome = await fixture.ExecuteAsync(publisher, token);

        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);
        var build = fixture.Reports.Single(report => report.Index == 1 && report.Status == PipelineStepStatus.Failed);
        Assert.Equal(PipelineStepFailureReasons.ArtifactFailed, build.FailureReason);
        Assert.Contains(
            fixture.LogLines,
            line => line.Contains("Artifacts:MaxFiles", StringComparison.Ordinal));
    }

    [Fact(Timeout = 180 * 1000)]
    public async Task A_cancellation_during_publishing_ends_the_run_as_canceled_and_marks_the_step_killed()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ExecutorFixture(PublishPipeline());
        var publisher = new RecordingPublisher
        {
            // The upload was killed by the run's token: the protocol surfaces an OperationCanceledException, and
            // the executor must map it onto F-009's cancellation path (not onto a step failure the run ignores).
            Fail = _ => new OperationCanceledException("the artifact upload was canceled."),
        };

        var outcome = await fixture.ExecuteAsync(
            publisher,
            token,
            cancellationFailureReason: PipelineStepFailureReasons.TimedOut);

        Assert.True(outcome.Canceled);
        Assert.Null(outcome.ExitCode);
        Assert.Null(outcome.FailureReason);

        var build = fixture.Reports.Single(report => report.Index == 1 && report.Status == PipelineStepStatus.Failed);
        Assert.Equal(PipelineStepFailureReasons.TimedOut, build.FailureReason);

        Assert.Contains(fixture.Reports, report =>
            report.Index == 2 && report.Status == PipelineStepStatus.Skipped
            && report.FailureReason == PipelineStepFailureReasons.RunCanceled);
    }

    [Fact(Timeout = 180 * 1000)]
    public async Task A_transport_failure_during_publish_becomes_the_artifact_failure_reason()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-f016-transport-{Guid.NewGuid():N}");
        var work = new WorkDirectoryService(root).Prepare(Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(work.RepositoryPath, "out"));
            await File.WriteAllTextAsync(Path.Combine(work.RepositoryPath, "out", "report.txt"), "report", token);

            // The real publisher over a protocol client whose connection dies mid-upload: the pre-fix code let the
            // HttpRequestException escape, which left the step Running forever. It must become the closed reason.
            var publisher = new ArtifactPublisher(
                new ThrowingProtocolClient(new HttpRequestException("the connection was reset")),
                Guid.NewGuid(),
                work.RepositoryPath,
                work.RootPath,
                ArtifactLimits.Defaults,
                _ => { });

            var failure = await Assert.ThrowsAsync<ArtifactPublishException>(
                () => publisher.PublishAsync("out/report.txt", token));

            Assert.Equal(PipelineStepFailureReasons.ArtifactFailed, failure.FailureReason);
            Assert.Contains("could not be uploaded", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(Timeout = 180 * 1000)]
    public async Task An_unexpected_publisher_exception_still_fails_the_step_instead_of_leaving_it_running()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ExecutorFixture(PublishPipeline());

        // Defence in depth: even a publisher bug that throws something unmapped must not leave the step Running -
        // the executor reports it Failed with the artifact reason and the run stops.
        var outcome = await fixture.ExecuteAsync(
            new RecordingPublisher { Fail = _ => new HttpRequestException("a bug escaped the publisher") },
            token);

        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);
        var build = fixture.Reports.Single(report => report.Index == 1 && report.Status == PipelineStepStatus.Failed);
        Assert.Equal(PipelineStepFailureReasons.ArtifactFailed, build.FailureReason);
        Assert.DoesNotContain(fixture.Reports, report =>
            report.Index == 1 && report.Status == PipelineStepStatus.Running && report == fixture.Reports[^1]);
    }

    [Fact(Timeout = 180 * 1000)]
    public async Task A_failing_step_publishes_nothing()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new ExecutorFixture(
            """
            pipeline {
              name = "publish",
              steps = {
                { name = "Build", run = { [[DOTNET]], [[ARGVECHO]], "exit", "3" }, artifacts = { "out" } },
              },
            }
            """.Replace("[[DOTNET]]", $"[[{PipelineTestData.DotNet}]]", StringComparison.Ordinal)
                .Replace("[[ARGVECHO]]", $"[[{PipelineTestData.ArgvEcho}]]", StringComparison.Ordinal));
        var publisher = new RecordingPublisher();

        var outcome = await fixture.ExecuteAsync(publisher, token);

        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);
        Assert.Empty(publisher.Published);
    }

    private static string PublishPipeline() =>
        """
        pipeline {
          name = "publish",
          steps = {
            { name = "Build", run = { [[DOTNET]], [[ARGVECHO]], "exit", "0" }, artifacts = { "out", "dist" } },
            { name = "Test", run = { [[DOTNET]], [[ARGVECHO]], "append", "marker.txt", "test" } },
          },
        }
        """
            .Replace("[[DOTNET]]", $"[[{PipelineTestData.DotNet}]]", StringComparison.Ordinal)
            .Replace("[[ARGVECHO]]", $"[[{PipelineTestData.ArgvEcho}]]", StringComparison.Ordinal);

    /// <summary>A protocol client whose every call fails with the given transport-style exception.</summary>
    private sealed class ThrowingProtocolClient(Exception failure) : IArtifactProtocolClient
    {
        public Task<Guid> CreateAsync(Guid runId, string name, string kind, CancellationToken cancellationToken) =>
            Task.FromException<Guid>(failure);

        public Task<Zenkins.Shared.Artifacts.ArtifactFileEntry> UploadFileAsync(
            Guid runId,
            Guid artifactId,
            string relativePath,
            string sha256,
            string filePath,
            CancellationToken cancellationToken) =>
            Task.FromException<Zenkins.Shared.Artifacts.ArtifactFileEntry>(failure);

        public Task<Zenkins.Shared.Artifacts.ArtifactFinalizeResponse> FinalizeAsync(
            Guid runId,
            Guid artifactId,
            Zenkins.Shared.Artifacts.ArtifactFileEntry[] files,
            CancellationToken cancellationToken) =>
            Task.FromException<Zenkins.Shared.Artifacts.ArtifactFinalizeResponse>(failure);
    }

    /// <summary>Records what the executor asked to publish, and fails on demand.</summary>
    private sealed class RecordingPublisher : IRunArtifactPublisher
    {
        public List<string> Published { get; } = [];

        /// <summary>Where the publish call is noted, so the fixture can assert the executor's ordering.</summary>
        public List<string>? Events { get; set; }

        /// <summary>Answers a publish with an exception instead of success.</summary>
        public Func<string, Exception>? Fail { get; init; }

        public Task PublishAsync(string declaredPath, CancellationToken cancellationToken)
        {
            Published.Add(declaredPath);
            Events?.Add($"publish:{declaredPath}");
            if (Fail?.Invoke(declaredPath) is { } failure)
            {
                return Task.FromException(failure);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>One executor run over a real fixture repository, with the reports recorded for assertions.</summary>
    private sealed class ExecutorFixture : IDisposable
    {
        private readonly PipelineRepository _repository;
        private readonly string _workRoot;
        private readonly List<string> _logLines = [];

        public ExecutorFixture(string pipelineLua)
        {
            _repository = PipelineRepository.Create(pipelineLua);
            _workRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f016-executor-{Guid.NewGuid():N}");
            Work = new WorkDirectoryService(_workRoot).Prepare(Guid.NewGuid());
        }

        public RunWorkDirectory Work { get; }

        public List<string> LogLines => _logLines;

        public List<PipelineStepReport> Reports { get; } = [];

        /// <summary>The interleaved step reports and publish calls, in the order the executor made them.</summary>
        public List<string> Events { get; } = [];

        public async Task<PipelineRunOutcome> ExecuteAsync(
            IRunArtifactPublisher publisher,
            CancellationToken cancellationToken,
            string? cancellationFailureReason = null)
        {
            var definitions = new PipelineDefinitionRunner(
                PipelineDefinitionRunner.CreateRuntime(PipelineRunner.CreateLogSink(Log)));
            if (publisher is RecordingPublisher recording)
            {
                recording.Events = Events;
            }

            var runner = new PipelineRunner(
                new PipelineRunOptions
                {
                    Work = Work,
                    RepositoryUrl = _repository.FileUrl,
                    Branch = "main",
                    PipelinePath = "pipeline.lua",
                    Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
                    Log = Log,
                    Artifacts = publisher,
                    StepSink = new RecordingSink(Reports, Events),
                    CancellationFailureReason = () => cancellationFailureReason
                        ?? PipelineStepFailureReasons.Canceled,
                },
                definitions,
                _ => Task.FromResult<GitCredentialMaterial?>(null),
                (_, _, _) => Task.FromResult(true));

            return await runner.RunAsync(cancellationToken);
        }

        private void Log(string line)
        {
            lock (_logLines)
            {
                _logLines.Add(line);
            }
        }

        public void Dispose()
        {
            _repository.Dispose();
            try
            {
                if (Directory.Exists(_workRoot))
                {
                    Directory.Delete(_workRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private sealed class RecordingSink(List<PipelineStepReport> reports, List<string> events) : IPipelineStepSink
        {
            public Task<PipelineStepReceipt> ReportAsync(
                PipelineStepReport report,
                CancellationToken cancellationToken)
            {
                reports.Add(report);
                events.Add($"report:{report.Index}:{report.Status}");
                return Task.FromResult(PipelineStepReceipt.Recorded(Guid.NewGuid()));
            }
        }
    }
}
