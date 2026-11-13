using System.Text;
using Xunit;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Q-002's secret-hygiene case: a step echoes a <c>secret()</c> value and the value must never be
/// persisted, downloaded or rendered.
/// <para>
/// <b>The boundary this test pins (the Lead's ruling on Q-002's AC-6).</b> Redaction is a property of
/// the <em>log</em> side: the streamed lines, the persisted rows, the log download endpoint and the run
/// page. The <em>artifact</em> is not redacted and must not be - F-016 requires the downloaded bytes to
/// be the bytes the pipeline wrote, including for binary output, so rewriting a secret there would
/// corrupt the product. When this pipeline deliberately writes its secret into an artifact file, the
/// downloaded file is that secret: that is the pipeline's choice, not a missing defence. The two
/// assertions below state exactly that pair, so neither side can silently drift.
/// </para>
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneSecretHygieneTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_secret_reaches_the_child_but_never_the_log_and_the_artifact_keeps_its_bytes()
    {
        const string secretValue = "hygiene-s3cr3t-do-not-log";
        var cancellationToken = TestContext.Current.CancellationToken;

        using var repository = MilestoneRepository.Create(MilestonePipelines.SecretEcho);
        using var host = await MilestoneHost.StartAsync();

        var projectId = await host.CreateProjectAsync("milestone-secret");
        await host.PointProjectAtAsync(projectId, repository.FileUrl);
        await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);
        await host.StartWorkerAsync();

        var runId = await host.TriggerRunAsync(projectId);
        var run = await host.WaitForTerminalAsync(runId);

        // The run only succeeds if the secret really was fetched and substituted into the child, so
        // "redacted" cannot be the trivial outcome of an unresolved reference.
        Assert.True(
            run.Status == "Success",
            $"the secret-echo run ended {run.Status} (reason: {run.FailureReason ?? "-"}).{Environment.NewLine}"
            + host.FormatDiagnostics());
        Assert.Equal(repository.MainTip, run.GitCommit);
        var steps = await host.ReadStepsAsync(runId);
        Assert.Equal(3, steps.Count);
        Assert.All(steps, step => Assert.Equal("Success", step.Status));

        // The stored definition carries the reference, never a value.
        Assert.NotNull(run.PipelineSpecJson);
        Assert.Contains("${secret:" + MilestonePipelines.SecretName, run.PipelineSpecJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, run.PipelineSpecJson, StringComparison.Ordinal);

        // ----- The log side: persisted rows, per stream. -----
        var log = await host.ReadLogLinesAsync(runId);
        Assert.DoesNotContain(log, line => line.Message.Contains(secretValue, StringComparison.Ordinal));

        // Step 1 printed the secret to stdout; step 2 printed it to stderr. Both are `***` now, and
        // each is still attributed to its own step.
        Assert.Single(log, line => line.Message == "***" && line.Stream == "StdOut");
        Assert.Equal(
            steps[0].Id,
            log.First(line => line.Message == "***" && line.Stream == "StdOut").StepId);
        Assert.Single(log, line => line.Message == "***" && line.Stream == "StdErr");
        Assert.Equal(
            steps[1].Id,
            log.First(line => line.Message == "***" && line.Stream == "StdErr").StepId);

        // ----- The log download endpoint: the same story, on the download surface. -----
        var logFileBytes = await host.DownloadAsync($"/runs/{runId}/log/download");
        var logFile = Encoding.UTF8.GetString(logFileBytes);
        Assert.Contains("***", logFile, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, logFile, StringComparison.Ordinal);

        // ... and in the raw bytes, not merely in a decoded string (no leak a decoder would hide).
        Assert.True(
            IndexOf(logFileBytes, Encoding.UTF8.GetBytes(secretValue)) < 0,
            "the raw bytes of the log download contained the secret value.");

        // ----- The run page renders no value either. -----
        var page = await host.GetPageAsync($"/runs/{runId}");
        Assert.Contains("id=\"run-artifact-", page, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, page, StringComparison.Ordinal);

        // ----- The artifact side: byte-identical to what the pipeline wrote (the Lead's ruling). -----
        var artifact = Assert.Single(await host.ReadArtifactsAsync(runId));
        Assert.Equal("secret.txt", artifact.Name);
        Assert.Equal("Ready", artifact.Status);

        var downloaded = await host.DownloadAsync($"/runs/{runId}/artifacts/{artifact.Id}/download");
        var written = Encoding.UTF8.GetBytes(secretValue + Environment.NewLine);
        Assert.Equal(written, downloaded);
        Assert.Equal(written.Length, (int)artifact.TotalBytes);
    }

    /// <summary>The offset of <paramref name="needle"/> in <paramref name="haystack"/>, or -1.</summary>
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            var match = true;
            for (var index = 0; index < needle.Length; index++)
            {
                if (haystack[offset + index] != needle[index])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return offset;
            }
        }

        return -1;
    }
}
