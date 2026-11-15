using Xunit;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Q-002 TC-004: the <b>Lua sandbox at the product boundary</b>. A repository whose <c>pipeline.lua</c>
/// reaches for the host - a file handle, a module loader, a package registry, a shell - must fail before
/// anything executes, must touch nothing outside the checkout, and must leave the worker healthy enough
/// to run the next build.
/// <para>
/// Each probe names <b>its own</b> escape path under this suite's temporary root (never a path shared with
/// another test or a previous run), so "the file does not exist" is a statement about this run. The step
/// the definition declares would append a marker if a step had ever started; the marker's absence is the
/// product-level "no process ran".
/// </para>
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneSandboxTests
{
    [Fact(Timeout = 8 * 60 * 1000)]
    public async Task A_pipeline_that_reaches_for_the_host_fails_and_touches_nothing()
    {
        const string secretValue = "sandbox-s3cr3t";
        var cancellationToken = TestContext.Current.CancellationToken;

        using var host = await MilestoneHost.StartAsync();
        using var repository = MilestoneRepository.Create(MilestonePipelines.Happy);

        var projectId = await host.CreateProjectAsync("milestone-sandbox");
        await host.PointProjectAtAsync(projectId, repository.FileUrl);
        await host.StartWorkerAsync();

        // Each probe: the sink it would write through, the Lua that reaches for the host, and the refusal
        // the runtime gives. The vendored Lua engine reports the *position* and the kind of failure, not
        // the global's name (docs/lua-runtime.md pins `pipeline.lua:(1,31-60): attempt to index a nil
        // value`), so the assertion is the position plus the kind: indexing or calling a stripped global.
        var probes = new (string Name, string Lua, string Refusal)[]
        {
            ("io", "local handle = io.open(ESCAPE, \"w\")", "attempt to index a nil value"),
            ("require", "local os_lib = require(\"os\")", "attempt to call a nil value"),
            ("package", "package.loaded.dynamic.eval(\"1+1\")", "attempt to index a nil value"),
            // os survives only as OS_Time (time/clock): execute is nil, so calling it is the refusal.
            ("os", "os.execute(\"touch \" .. ESCAPE)", "attempt to call a nil value"),
        };

        foreach (var (name, primitive, refusal) in probes)
        {
            var escapePath = Path.Combine(host.RootPath, $"sandbox-escape-{name}.txt");
            var markerPath = Path.Combine(host.RootPath, $"sandbox-marker-{name}.txt");

            // The escape path goes in as a Lua string literal, so a Windows separator cannot be read as
            // an escape sequence and the probe is about the sandbox, not about quoting.
            var lua = primitive.Replace(
                "ESCAPE",
                "\"" + MilestonePipelines.LuaString(escapePath) + "\"",
                StringComparison.Ordinal);
            repository.CommitPipeline(MilestonePipelines.SandboxProbe(markerPath, lua));

            var runId = await host.TriggerRunAsync(projectId);
            var run = await host.WaitForTerminalAsync(runId);

            Assert.Equal("Failed", run.Status);
            Assert.Null(run.ExitCode);
            Assert.Equal(PipelineFailureReasons.PipelineInvalid, run.FailureReason);
            Assert.Equal(repository.MainTip, run.GitCommit);

            // Nothing ran: the definition never produced a spec, so no step reached Running and the
            // step's marker was never written.
            Assert.Empty(await host.ReadStepsAsync(runId));
            Assert.False(File.Exists(markerPath), $"a step process ran for the '{name}' probe.");
            Assert.False(File.Exists(escapePath), $"the '{name}' probe wrote a file outside the checkout.");

            // The run log carries the Lua error at the primitive's call site: the chunk name, a position,
            // and the engine's own wording for the refusal. F-008's drain lands the lines around the
            // terminal report, so the lines are waited for, not read once - a loaded runner
            // legitimately saw an empty log for these green runs (recorded on both ubuntu and
            // windows).
            await host.WaitForLogLinesAsync(
                runId,
                lines => lines.Any(line => line.Message.Contains("pipeline.lua:(", StringComparison.Ordinal)
                    && line.Message.Contains(refusal, StringComparison.Ordinal)));
        }

        // The worker never crashed: four refusals later, a healthy build still runs on the same process.
        repository.CommitPipeline(MilestonePipelines.Happy);
        await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);
        var recoveryRunId = await host.TriggerRunAsync(projectId);
        var recovery = await host.WaitForTerminalAsync(recoveryRunId);
        Assert.Equal("Success", recovery.Status);
        Assert.Equal(3, (await host.ReadStepsAsync(recoveryRunId)).Count);

        // ... and nothing any probe tried to create exists anywhere under the temporary root.
        var escaped = Directory.EnumerateFiles(host.RootPath, "sandbox-escape-*", SearchOption.AllDirectories)
            .ToArray();
        Assert.Empty(escaped);

        // Five runs (four refusals and one healthy build), all terminal, and nothing retried itself.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var runs = await host.ReadProjectRunsAsync(projectId);
            Assert.Equal(5, runs.Count);
            Assert.All(runs, run => Assert.NotNull(run.EndedAt));
            await Task.Delay(200, cancellationToken);
        }

        // The harness's own cleanup is part of the claim "the suite leaves nothing behind": stop it here
        // and assert both halves. The `using` declaration above is a no-op after this point.
        host.Dispose();
        Assert.True(host.ChildProcessesExited, "a server or worker process survived the harness's own cleanup.");
        Assert.True(host.TemporaryRootRemoved, "the harness left its temporary root on disk.");
    }
}
