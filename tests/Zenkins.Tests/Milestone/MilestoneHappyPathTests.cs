using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Q-002 TC-001: <b>a real build runs from <c>pipeline.lua</c> in a Git repository</b>. The assertions
/// are the PRD's own words, checked against what four real processes left behind: a real Server, a real
/// Worker, the real child processes of three steps, and a real download of the artifact.
/// <para>
/// Everything asserted here is observable from outside the product: rows in the server's database, the
/// bytes an HTTP endpoint served, and the repository commit the run says it built. No worker-internal
/// object is involved, so a refactor of the executor cannot make this test lie in either direction.
/// </para>
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneHappyPathTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_manual_run_builds_the_pushed_commit_and_publishes_its_artifact()
    {
        // The secret the definition claims. Its value must never reach a persisted log line.
        const string secretValue = "milestone-s3cr3t-value";
        var cancellationToken = TestContext.Current.CancellationToken;

        using var repository = MilestoneRepository.Create(
            MilestonePipelines.Happy,
            MilestonePipelines.FeatureBranch);
        using var host = await MilestoneHost.StartAsync();

        var projectId = await host.CreateProjectAsync("milestone-happy");
        await host.PointProjectAtAsync(projectId, repository.FileUrl);
        await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);
        await host.StartWorkerAsync();

        // ----- The run itself. -----
        var runId = await host.TriggerRunAsync(projectId);

        // The card's central claim, sampled from inside the suite while the run executes: the worker is
        // its own live OS process (not this test process, not an in-host instance), the process table
        // really holds its pid with the start instant of the handle this host started, and the server's
        // own Workers row for that worker is Online and is the row this run is assigned to.
        var workerRow = await host.SampleWorkerDuringRunAsync(runId);

        var run = await host.WaitForTerminalAsync(runId);
        Assert.Equal(workerRow.Id, run.WorkerId);
        Assert.NotEqual(Environment.ProcessId, host.WorkerProcessId);
        Assert.NotEqual(Environment.ProcessId, host.ServerProcessId);

        Assert.Equal("Success", run.Status);
        Assert.Equal("Manual", run.TriggerType);
        Assert.NotNull(run.WorkerId);
        Assert.NotNull(run.EndedAt);
        Assert.Equal(0, run.ExitCode);
        Assert.Null(run.FailureReason);

        // "GitCommit on the run equals the repository's HEAD" - the fixture pushed that commit, so the
        // column and the repository are two independent statements of the same fact.
        Assert.Equal(repository.MainTip, run.GitCommit);

        // ... and the bare origin agrees: the commit is a real object in the repository the worker
        // cloned, not a value the row invented.
        Assert.Equal(repository.MainTip, repository.OriginMainTip);
        Assert.Contains("milestone", repository.ShowMainFile("pipeline.lua"), StringComparison.Ordinal);

        // ----- Three recorded step rows, all Success. -----
        var steps = await host.ReadStepsAsync(runId);
        Assert.Equal(3, steps.Count);
        Assert.Equal(new[] { "Restore", "Build", "Publish" }, steps.Select(step => step.Name));
        Assert.Equal(new[] { 1, 2, 3 }, steps.Select(step => step.Index));
        Assert.All(steps, step =>
        {
            Assert.Equal("Success", step.Status);
            Assert.Equal(0, step.ExitCode);
            Assert.Null(step.FailureReason);
        });

        // ----- The persisted log: each step's own output, attributed to its own step. -----
        // F-008's drain lands the lines around the terminal report; the wait anchors on the
        // definition's last emitted line - with per-run Seq-ordered delivery, the last line's
        // arrival means every earlier line is already there.
        var log = await host.WaitForLogLinesAsync(
            runId,
            lines => lines.Any(line => line.Message == "probe-written"));

        // The pipeline-level env var really reached the child process of step 1.
        Assert.Single(log, line => line.Message == MilestonePipelines.Greeting);
        Assert.Equal(steps[0].Id, log.First(line => line.Message == MilestonePipelines.Greeting).StepId);

        // Step 2's stdout is under step 2.
        Assert.Single(log, line => line.Message == "building");
        Assert.Equal(steps[1].Id, log.First(line => line.Message == "building").StepId);

        // Step 3's stdout is under step 3, and the artifact it published exists.
        Assert.Single(log, line => line.Message == "probe-written");
        Assert.Equal(steps[2].Id, log.First(line => line.Message == "probe-written").StepId);

        // The definition stored the secret *reference*, never a value, and the printed value is redacted.
        Assert.NotNull(run.PipelineSpecJson);
        Assert.Contains("${secret:" + MilestonePipelines.SecretName, run.PipelineSpecJson, StringComparison.Ordinal);
        Assert.DoesNotContain(log, line => line.Message.Contains(secretValue, StringComparison.Ordinal));
        Assert.Single(log, line => line.Message == "***");
        Assert.Equal(steps[1].Id, log.First(line => line.Message == "***").StepId);

        // ----- The artifact downloads byte-identically. -----
        var artifact = Assert.Single(await host.ReadArtifactsAsync(runId));
        Assert.Equal("report.txt", artifact.Name);
        Assert.Equal("File", artifact.Kind);
        Assert.Equal("Ready", artifact.Status);
        Assert.Equal(MilestonePipelines.ArtifactBytes.Length, artifact.TotalBytes);

        var downloaded = await host.DownloadAsync($"/runs/{runId}/artifacts/{artifact.Id}/download");

        // Both hashes are computed here, in this test: the expected bytes from the pipeline's own text
        // and the downloaded bytes from the endpoint. Matching lengths are not the claim; identical
        // content is.
        var expectedSha = Sha256(MilestonePipelines.ArtifactBytes);
        Assert.Equal(expectedSha, Sha256(downloaded));
        Assert.Equal(MilestonePipelines.ArtifactBytes, downloaded);

        // The server's own manifest names the same digest the endpoint just served.
        Assert.Equal(expectedSha, ManifestSha256(artifact));

        // ----- The run page renders the three steps, in declaration order (the product's own view). -----
        var page = await host.GetPageAsync($"/runs/{runId}");
        Assert.Contains("id=\"run-console\"", page, StringComparison.Ordinal);
        var restoreOnPage = page.IndexOf("run-step-Restore", StringComparison.Ordinal);
        var buildOnPage = page.IndexOf("run-step-Build", StringComparison.Ordinal);
        var publishOnPage = page.IndexOf("run-step-Publish", StringComparison.Ordinal);
        Assert.True(
            restoreOnPage >= 0 && buildOnPage > restoreOnPage && publishOnPage > buildOnPage,
            "the run page did not list the three steps in declaration order.");

        // ----- The log download endpoint is the same log, and carries no secret. -----
        var logFile = Encoding.UTF8.GetString(await host.DownloadAsync($"/runs/{runId}/log/download"));
        Assert.Contains(MilestonePipelines.Greeting, logFile, StringComparison.Ordinal);
        Assert.Contains("***", logFile, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, logFile, StringComparison.Ordinal);

        // ----- The second branch is real: its own definition, its own commit. -----
        var featureRunId = await host.TriggerRunAsync(projectId, branch: "feature");
        var featureRun = await host.WaitForTerminalAsync(featureRunId);
        Assert.Equal("Success", featureRun.Status);
        Assert.Equal(repository.FeatureTip, featureRun.GitCommit);
        Assert.Equal(repository.FeatureTip, repository.OriginFeatureTip);
        var featureSteps = await host.ReadStepsAsync(featureRunId);
        Assert.Equal(new[] { "FeatureOnly" }, featureSteps.Select(step => step.Name));

        // Two runs, two different definitions from two different commits.
        Assert.Equal(2, (await host.ReadProjectRunsAsync(projectId)).Count);
        Assert.NotEqual(run.GitCommit, featureRun.GitCommit);

        // Bounded observation: the two runs are the whole story - nothing else queued itself behind them.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            Assert.Equal(2, (await host.ReadProjectRunsAsync(projectId)).Count);
            await Task.Delay(200, cancellationToken);
        }
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The single file's SHA-256 as the server recorded it when it verified the upload.</summary>
    private static string ManifestSha256(MilestoneArtifact artifact)
    {
        Assert.NotNull(artifact.ManifestJson);
        using var manifest = JsonDocument.Parse(artifact.ManifestJson);
        return manifest.RootElement.GetProperty("files")[0].GetProperty("sha256").GetString()
            ?? throw new InvalidOperationException("the artifact manifest carried no sha256.");
    }
}
