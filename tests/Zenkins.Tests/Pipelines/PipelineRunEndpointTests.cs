using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-014's server surfaces, driven through the real HTTP pipeline: the trigger snapshots a branch
/// and the project's pipeline path, the claim carries what the worker must clone, the definition report
/// freezes the validated spec (once) and the run page renders its step list, and the completed report
/// accepts the closed failure vocabulary.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineRunEndpointTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // Trigger → claim
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_claim_carries_the_repository_branch_and_pipeline_path_of_the_run()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline("claimed", PipelineTestData.Step("Echo", "echo", "ok")));
        using var host = await RunTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync("claim-project", repository.FileUrl, pipelinePath: "ci/pipeline.lua");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "claim-agent");

        var owner = await host.Inner.SignInOwnerAsync();
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId, branch: "release/1.0");

        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);

        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        Assert.Equal(repository.FileUrl, await RunTestHost.ReadClaimStringAsync(claimed, "repositoryUrl"));
        Assert.Equal("release/1.0", await RunTestHost.ReadClaimStringAsync(claimed, "branch"));
        Assert.Equal("ci/pipeline.lua", await RunTestHost.ReadClaimStringAsync(claimed, "pipelinePath"));

        // The run row is the snapshot, not a join: the project keeps its own values.
        var run = await host.ReadRunAsync(runId);
        Assert.Equal("release/1.0", run.GitBranch);
        Assert.Equal("ci/pipeline.lua", run.PipelinePath);
        Assert.Null(run.PipelineSpecJson);
    }

    [Fact]
    public async Task A_repository_url_saved_through_the_real_form_is_what_the_claim_tells_the_worker_to_clone()
    {
        // Card Q-003 (debt handed over by F-014): the pipeline fixtures seed the project row directly,
        // because their file:// repositories are deliberately refused by the form. That left the
        // product path - the URL a user types, through the real create form, into the row the claim
        // reads - unproven. Here the URL goes through the form (it passes the product's own
        // validation, so no fixture repository is needed), the run is triggered through the real form,
        // and the assertion is the claim payload itself, not a seeded row.
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        const string repositoryUrl = "https://git.example.test/acme/form-saved.git";

        var created = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "form-url-project",
                ["RepositoryUrl"] = repositoryUrl,
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
            });
        Assert.True(
            (int)created.StatusCode is 302 or 303,
            $"the create form rejected the URL with {(int)created.StatusCode}");
        var location = created.Headers.Location!.OriginalString;
        Assert.Contains("?notice=created", location, StringComparison.Ordinal);
        Assert.Contains(repositoryUrl, await AuthTestHost.GetStringAsync(owner, location), StringComparison.Ordinal);
        var projectId = Guid.Parse(location["/projects/".Length..].Split('?')[0]);

        var (workerToken, _) = await host.RegisterWorkerAsync(name: "form-url-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));

        Assert.Equal(repositoryUrl, await RunTestHost.ReadClaimStringAsync(claimed, "repositoryUrl"));
    }

    // ------------------------------------------------------------------
    // The definition report freezes the spec
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_definition_report_freezes_the_spec_and_the_run_page_shows_its_steps()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("definition-project");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "definition-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();

        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));

        // The accept answer carries the frozen deadline (F-014): the worker's definition phase runs
        // against the run's own wall clock.
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);
        var deadline = await ReadDeadlineAsync(accepted);
        Assert.NotNull(deadline);

        var spec = Spec("Build", "Restore", "Build", "Package");
        using var definition = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, runId, PipelineSpecJson.Serialize(spec), gitCommit: "0123456789abcdef0123456789abcdef01234567");
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);

        var frozen = await host.ReadRunAsync(runId);
        Assert.Equal(PipelineSpecJson.Serialize(spec), frozen.PipelineSpecJson);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", frozen.GitCommit);
        Assert.Equal(BuildRunStatus.Running, frozen.Status);

        // The page lists the steps in order - for a run that is only just starting.
        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains("id=\"run-steps\"", page, StringComparison.Ordinal);
        var restore = page.IndexOf("run-step-Restore", StringComparison.Ordinal);
        var build = page.IndexOf("run-step-Build", StringComparison.Ordinal);
        var package = page.IndexOf("run-step-Package", StringComparison.Ordinal);
        Assert.True(restore >= 0 && build > restore && package > build, "the step list is not in declaration order.");
        Assert.Contains("id=\"run-branch\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-pipeline-path\"", page, StringComparison.Ordinal);
        Assert.Contains("0123456789abcdef0123456789abcdef01234567", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_commit_only_report_records_the_commit_before_any_definition_exists()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("commit-only-report");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "commit-only-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        // AC-3: the worker reports the commit right after the clone, with no spec yet - so a run whose
        // definition then fails (pipeline-missing, -syntax, -invalid, -timeout) still records it.
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        using (var reported = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, runId, pipelineSpecJson: null, gitCommit: commit))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        var afterCommit = await host.ReadRunAsync(runId);
        Assert.Equal(commit, afterCommit.GitCommit);
        Assert.Null(afterCommit.PipelineSpecJson);
        Assert.Equal(BuildRunStatus.Running, afterCommit.Status);

        // The definition failure that follows changes nothing about the commit, and the page shows it.
        using (var completed = await RunTestHost.CompleteAsync(
            workerClient, workerToken, runId, exitCode: null,
            failureReason: PipelineFailureReasons.PipelineMissing))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        var finished = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Failed, finished.Status);
        Assert.Equal(PipelineFailureReasons.PipelineMissing, finished.FailureReason);
        Assert.Equal(commit, finished.GitCommit);

        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains(commit, page, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"run-git-commit\" class=\"bb:break-all\">-</dd>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_with_neither_a_commit_nor_a_spec_is_a_400()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("empty-definition-report");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "empty-report-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        using var reported = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, runId, pipelineSpecJson: null, gitCommit: null);

        Assert.Equal(HttpStatusCode.BadRequest, reported.StatusCode);
        var run = await host.ReadRunAsync(runId);
        Assert.Null(run.GitCommit);
        Assert.Null(run.PipelineSpecJson);
    }

    [Fact]
    public async Task A_re_delivered_definition_report_is_idempotent_and_never_rewrites_the_frozen_spec()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("idempotent-definition");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "idempotent-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        var first = PipelineSpecJson.Serialize(Spec("Build", "Only"));
        using (var reported = await RunTestHost.ReportDefinitionAsync(workerClient, workerToken, runId, first))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        // A retry after a lost response: same body, 200, nothing changed.
        using (var retried = await RunTestHost.ReportDefinitionAsync(workerClient, workerToken, runId, first))
        {
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        }

        // A DIFFERENT spec (a buggy worker, a stale attempt) is refused rather than silently ignored:
        // the spec is immutable once frozen.
        var second = PipelineSpecJson.Serialize(Spec("Other", "A", "B"));
        using (var conflicting = await RunTestHost.ReportDefinitionAsync(workerClient, workerToken, runId, second))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
            Assert.Equal(RunErrorCodes.PipelineSpecFrozen, await ReadErrorPropertyAsync(conflicting));
        }

        Assert.Equal(first, (await host.ReadRunAsync(runId)).PipelineSpecJson);
    }

    [Fact]
    public async Task A_second_definition_report_carrying_a_different_commit_is_refused_and_the_pinned_commit_is_unchanged()
    {
        // Card Q-003 (debt handed over by F-014): the compare-and-set is covered at the database level;
        // this pins the endpoint's answer. A report that disagrees with the run's pinned commit means
        // the worker built something other than the run says - a conflict, not a detail to absorb.
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("commit-cas-project");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "commit-cas-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        const string pinned = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string conflicting = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var spec = Spec("Build", "Only");
        using (var first = await RunTestHost.ReportDefinitionAsync(
                   workerClient, workerToken, runId, PipelineSpecJson.Serialize(spec), gitCommit: pinned))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // The disagreeing commit is refused with the stable code, and nothing is written: the pinned
        // commit and the frozen spec survive the refusal untouched (the run stays re-reportable).
        using (var second = await RunTestHost.ReportDefinitionAsync(
                   workerClient, workerToken, runId, pipelineSpecJson: null, gitCommit: conflicting))
        {
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal(RunErrorCodes.GitCommitMismatch, await ReadErrorPropertyAsync(second));
        }

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(pinned, run.GitCommit);
        Assert.Equal(PipelineSpecJson.Serialize(spec), run.PipelineSpecJson);
        Assert.Equal(BuildRunStatus.Running, run.Status);
    }

    [Theory]
    [InlineData("""{ "version": 4, "name": "X", "steps": [ { "name": "a", "run": [ "x" ] } ] }""")]
    [InlineData("""{ "version": 1, "name": "X", "steps": [] }""")]
    [InlineData("not json at all")]
    public async Task An_invalid_definition_payload_is_a_400_and_freezes_nothing(string specJson)
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("invalid-definition");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "invalid-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        using var definition = await RunTestHost.ReportDefinitionAsync(workerClient, workerToken, runId, specJson);

        Assert.Equal(HttpStatusCode.BadRequest, definition.StatusCode);
        Assert.Equal(
            RunErrorCodes.PipelineSpecInvalid,
            await ReadErrorPropertyAsync(definition));
        Assert.Null((await host.ReadRunAsync(runId)).PipelineSpecJson);
    }

    [Fact]
    public async Task Another_worker_cannot_report_a_definition_for_a_foreign_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("foreign-definition");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "own-agent");
        var (foreignToken, _) = await host.RegisterWorkerAsync(name: "foreign-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        using var definition = await RunTestHost.ReportDefinitionAsync(
            workerClient, foreignToken, runId, PipelineSpecJson.Serialize(Spec("Build", "Only")));

        Assert.Equal(HttpStatusCode.Conflict, definition.StatusCode);
        Assert.Equal(RunErrorCodes.RunNotAssignedToWorker, await ReadErrorPropertyAsync(definition));
        Assert.Null((await host.ReadRunAsync(runId)).PipelineSpecJson);
    }

    [Fact]
    public async Task A_definition_report_without_a_token_is_401()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("anonymous-definition");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        using var client = host.Host.CreateClient();
        using var definition = await RunTestHost.ReportDefinitionAsync(
            client, null!, runId, PipelineSpecJson.Serialize(Spec("Build", "Only")));

        Assert.Equal(HttpStatusCode.Unauthorized, definition.StatusCode);
    }

    // ------------------------------------------------------------------
    // The completed report's closed failure vocabulary
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_failing_step_is_recorded_with_its_exit_code_and_step_failed()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("step-failed-run");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "step-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        using var completed = await RunTestHost.CompleteAsync(
            workerClient, workerToken, runId, exitCode: 7, failureReason: PipelineFailureReasons.StepFailed);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(BuildRunStatus.Failed, await RunTestHost.ReadReportStatusAsync(completed));
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Failed, run.Status);
        Assert.Equal(7, run.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, run.FailureReason);
    }

    [Fact]
    public async Task A_run_that_failed_before_any_process_ran_records_the_reason_and_no_exit_code()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("pipeline-missing-run");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "missing-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        using var completed = await RunTestHost.CompleteAsync(
            workerClient, workerToken, runId, exitCode: null, failureReason: PipelineFailureReasons.PipelineMissing);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Failed, run.Status);
        Assert.Null(run.ExitCode);
        Assert.Equal(PipelineFailureReasons.PipelineMissing, run.FailureReason);
    }

    [Theory]
    [InlineData(null, "worker-offline")] // server-owned reason, never worker-reportable
    [InlineData(null, "not-a-reason")] // outside the closed set
    [InlineData(0, "step-failed")] // success cannot carry a failure reason
    [InlineData(null, null)] // nothing to record at all
    public async Task An_impossible_completed_report_is_a_400_and_changes_nothing(
        int? exitCode,
        string? failureReason)
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("bad-report-run");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "bad-report-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);

        using var completed = await RunTestHost.CompleteAsync(
            workerClient, workerToken, runId, exitCode, failureReason);

        Assert.Equal(HttpStatusCode.BadRequest, completed.StatusCode);
        Assert.Equal(RunErrorCodes.FieldInvalid, await ReadErrorPropertyAsync(completed));
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Running, run.Status);
        Assert.Null(run.ExitCode);
        Assert.Null(run.FailureReason);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>A spec with one <c>dotnet --info</c> step per name.</summary>
    private static PipelineSpec Spec(string name, params string[] stepNames) =>
        new(
            PipelineSpecSchema.Version,
            name,
            [.. stepNames.Select(stepName => new PipelineStep(stepName, Run: ["dotnet", "--info"]))]);

    private static async Task<DateTimeOffset?> ReadDeadlineAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("deadlineAt", out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetDateTimeOffset()
                : null;
    }

    private static async Task<string> ReadErrorPropertyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetString()
            ?? throw new InvalidOperationException("The error response had no code.");
    }
}
