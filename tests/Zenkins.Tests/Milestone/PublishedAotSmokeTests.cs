using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Sdk;
using Zenkins.Shared;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.RunCancellation;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Card I-004's smoke: the <b>NativeAOT</b> Worker artifact, against the <b>published JIT</b> Server
/// (the Server's own verdict - stays JIT). Runs as <c>Category=PublishAot</c> - its own acceptance
/// suite, excluded from the fast suite (docs/development.md §2), run with
/// <c>dotnet test -c Release --filter "Category=PublishAot"</c>. Publishing AOT needs the machine's
/// native toolchain (macOS: Xcode CLT; Windows: VS C++ workload; Linux: clang + zlib dev), so each
/// OS publishes its own RID - .NET does not cross-compile AOT for another OS.
/// <para>
/// The per-class fixture publishes three artifacts for the local RID: the AOT Worker (the csproj's
/// <c>PublishAot</c> default), the JIT Server and the JIT Worker (both I-003 semantics,
/// <c>-p:PublishAot=false</c>). The packaging claim is asserted on the output itself: the AOT
/// directory carries one native executable and no managed assembly, no runtimeconfig and no CoreCLR
/// files - it runs with no .NET runtime installed. The facts then run the real Q-002 scenario
/// (registration, claim, clone, <c>pipeline.lua</c> with host functions and <c>secret()</c>, streamed
/// logs, artifact upload, and the cancellation path) against the published pair executed directly -
/// TC-001 - and execute the <b>same</b> <c>pipeline.lua</c> under the JIT and the AOT worker,
/// comparing every recorded step outcome (TC-002's deterministic proof: if the MoonSharp host-call
/// surface or the wire broke under AOT, this comparison fails instead of a silent runtime break).
/// </para>
/// </summary>
[Trait("Category", "PublishAot")]
public sealed partial class PublishedAotSmokeTests : IClassFixture<PublishedAotSmokeTests.PublishedAotArtifacts>
{
    private readonly PublishedAotArtifacts _published;

    public PublishedAotSmokeTests(PublishedAotArtifacts published) => _published = published;

    [Fact(Timeout = 20 * 60 * 1000)]
    public async Task The_aot_worker_completes_the_real_scenario_against_the_published_server_and_cancels_mid_step()
    {
        const string secretValue = "aot-smoke-s3cr3t-value";
        var cancellationToken = TestContext.Current.CancellationToken;

        using var repository = MilestoneRepository.Create(MilestonePipelines.PublishedHappy);
        var scratch = NewScratchRoot();
        try
        {
            var (serverInstallDir, workerInstallDir) = _published.Install(scratch);
            var port = PickFreePort();

            using var host = await MilestoneHost.StartPublishedAsync(
                Path.Combine(serverInstallDir, PublishedAotArtifacts.ServerAppHostFileName),
                Path.Combine(workerInstallDir, PublishedAotArtifacts.WorkerAppHostFileName),
                port: port);

            // ----- The happy path: bootstrap, project, secret, worker, a real build. -----
            var projectId = await host.CreateProjectAsync("aot-smoke");
            await host.PointProjectAtAsync(projectId, repository.FileUrl);
            await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);
            await host.StartWorkerAsync();

            var runId = await host.TriggerRunAsync(projectId);
            var workerRow = await host.SampleWorkerDuringRunAsync(runId);
            var run = await host.WaitForTerminalAsync(runId);

            if (!string.Equals("Success", run.Status, StringComparison.Ordinal))
            {
                var failureLog = await host.ReadLogLinesAsync(runId);
                throw new XunitException(
                    $"the AOT worker's run ended '{run.Status}' (reason: {run.FailureReason}); "
                    + $"log: {string.Join(" | ", failureLog.Select(line => line.Message))}{Environment.NewLine}"
                    + $"diagnostics:{Environment.NewLine}{host.FormatDiagnostics()}");
            }

            Assert.Equal(workerRow.Id, run.WorkerId);
            Assert.Equal(0, run.ExitCode);
            Assert.Null(run.FailureReason);
            Assert.Equal(repository.MainTip, run.GitCommit);

            var steps = await host.ReadStepsAsync(runId);
            Assert.Equal(new[] { "Restore", "Build", "Publish" }, steps.Select(step => step.Name));
            Assert.All(steps, step =>
            {
                Assert.Equal(PipelineStepStatus.Success, step.Status);
                Assert.Equal(0, step.ExitCode);
            });

            // F-008's drain lands the drained log lines upstream around the terminal report ("on
            // their way" when the status flips), so the lines are waited for, not read once: on a
            // loaded runner the terminal row can beat the drained batches by seconds, and a single
            // read then sees an empty log for a green run (recorded red on the macos runner).
            var log = await host.WaitForLogLinesAsync(
                runId,
                lines => lines.Any(line => line.Message == MilestonePipelines.Greeting)
                    && lines.Any(line => line.Message == "building")
                    && lines.Any(line => line.Message == "probe-written")
                    && lines.Any(line => line.Message == "***"));
            Assert.Single(log, line => line.Message == MilestonePipelines.Greeting);
            Assert.Equal(steps[0].Id, log.First(line => line.Message == MilestonePipelines.Greeting).StepId);
            Assert.Single(log, line => line.Message == "building");
            Assert.Single(log, line => line.Message == "probe-written");

            // The definition stored the secret reference; the AOT worker's stream carries the
            // redacted value only - the redaction filter survives AOT unchanged.
            Assert.NotNull(run.PipelineSpecJson);
            Assert.Contains("${secret:" + MilestonePipelines.SecretName, run.PipelineSpecJson, StringComparison.Ordinal);
            Assert.DoesNotContain(log, line => line.Message.Contains(secretValue, StringComparison.Ordinal));
            Assert.Single(log, line => line.Message == "***");

            var artifact = Assert.Single(await host.ReadArtifactsAsync(runId));
            Assert.Equal("Ready", artifact.Status);
            Assert.Equal(MilestonePipelines.ArtifactBytes.Length, artifact.TotalBytes);
            Assert.Equal(
                MilestonePipelines.ArtifactBytes,
                await host.DownloadAsync($"/runs/{runId}/artifacts/{artifact.Id}/download"));

            // The process that did all of this is the published AOT executable itself. macOS's
            // temporary root sits behind the /var -> /private/var symlink, which proc_pidpath
            // reports resolved - both sides go through the same real-path resolution.
            Assert.Equal(
                ResolveRealPath(Path.Combine(workerInstallDir, PublishedAotArtifacts.WorkerAppHostFileName)),
                ResolveRealPath(host.WorkerProcess.MainModule!.FileName!));

            // ----- The cancellation path (F-009): cancel during step 1 of a second repository. -----
            var cancelMarker = Path.Combine(scratch, "cancel-marker.txt");
            repository.CommitPipeline(MilestonePipelines.CancelDuringFirstStep(cancelMarker, milliseconds: 30_000));
            var cancelRunId = await host.TriggerRunAsync(projectId);
            await host.WaitForStepStatusAsync(cancelRunId, index: 1, PipelineStepStatus.Running);

            using (var cancelResponse = await host.CancelRunAsync(cancelRunId))
            {
                Assert.True((int)cancelResponse.StatusCode is 302 or 303, "the cancel form did not accept the request.");
            }

            var canceledRun = await host.WaitForTerminalAsync(cancelRunId);
            Assert.Equal("Canceled", canceledRun.Status);
            Assert.Null(canceledRun.ExitCode);
            Assert.NotNull(canceledRun.FailureReason);
            Assert.StartsWith(RunCancelReasons.CanceledByUser, canceledRun.FailureReason, StringComparison.Ordinal);

            var canceledSteps = await host.ReadStepsAsync(cancelRunId);
            Assert.Equal(3, canceledSteps.Count);
            Assert.Equal(PipelineStepStatus.Failed, canceledSteps[0].Status);
            Assert.Equal(PipelineStepFailureReasons.Canceled, canceledSteps[0].FailureReason);
            Assert.Equal(PipelineStepStatus.Skipped, canceledSteps[1].Status);
            Assert.Equal(PipelineStepFailureReasons.RunCanceled, canceledSteps[1].FailureReason);
            Assert.Equal(PipelineStepStatus.Skipped, canceledSteps[2].Status);
            Assert.False(File.Exists(cancelMarker), "step 2 ran although the cancel was requested during step 1.");

            _ = cancellationToken;
        }
        finally
        {
            DeleteDirectory(scratch);
        }
    }

    [Fact(Timeout = 20 * 60 * 1000)]
    public async Task The_same_pipeline_produces_identical_step_outcomes_under_the_jit_and_aot_workers()
    {
        const string secretValue = "aot-consistency-s3cr3t";
        var cancellationToken = TestContext.Current.CancellationToken;

        // ONE repository fixture: both hosts' projects point at the same bare origin, so both runs
        // check out the same commit and the recorded lines are comparable.
        using var repository = MilestoneRepository.Create(MilestonePipelines.PublishedHappy);
        var scratch = NewScratchRoot();
        try
        {
            var outcomes = new Dictionary<string, AotOutcomeComparison>();
            var artifactBytes = new Dictionary<string, byte[]>();
            foreach (var (mode, workerAppHost) in new[]
                     {
                         ("JIT", _published.JitWorkerAppHostPath),
                         ("AOT", _published.AotWorkerAppHostPath),
                     })
            {
                var serverInstallDir = Path.Combine(scratch, $"server-{mode.ToLowerInvariant()}");
                CopyDirectory(_published.ServerPublishDir, serverInstallDir);
                var port = PickFreePort();

                using var host = await MilestoneHost.StartPublishedAsync(
                    Path.Combine(serverInstallDir, PublishedAotArtifacts.ServerAppHostFileName),
                    workerAppHost,
                    port: port);

                var projectId = await host.CreateProjectAsync($"aot-consistency-{mode.ToLowerInvariant()}");
                await host.PointProjectAtAsync(projectId, repository.FileUrl);
                await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);
                await host.StartWorkerAsync();

                var runId = await host.TriggerRunAsync(projectId);
                var run = await host.WaitForTerminalAsync(runId);
                Assert.True(string.Equals("Success", run.Status, StringComparison.Ordinal),
                    $"the {mode} worker's run ended '{run.Status}' (reason: {run.FailureReason}); diagnostics:{Environment.NewLine}"
                    + host.FormatDiagnostics());
                var steps = await host.ReadStepsAsync(runId);
                // F-008's drain lands the log lines around the terminal report; the JIT/AOT
                // comparison waits for the definition's last step line instead of reading once (a
                // loaded macos runner recorded a 2-of-16 log for a green AOT run).
                var log = await host.WaitForLogLinesAsync(
                    runId,
                    waited => waited.Any(line => line.Message.StartsWith("zenkins: step 3/3", StringComparison.Ordinal)));
                var artifacts = await host.ReadArtifactsAsync(runId);
                Assert.True(artifacts.Count == 1,
                    $"the {mode} worker's run produced {artifacts.Count} artifacts; diagnostics:{Environment.NewLine}"
                    + host.FormatDiagnostics());
                var artifact = artifacts.Single();

                // The clone's stderr embeds each host's own temporary root (and run id); everything
                // else must match byte for byte. Durations inside the one synthetic "exited with
                // code N after Xs" line are timing, not semantics, and are masked.
                outcomes[mode] = new AotOutcomeComparison(
                    run.Status,
                    run.ExitCode,
                    run.FailureReason,
                    run.GitCommit,
                    [.. steps.Select(step => (step.Index, step.Name, step.Status, step.ExitCode, step.FailureReason))],
                    [.. log.Select(line => NormalizeDuration(line.Message)
                        .Replace(host.RootPath, "<host-root>")
                        .Replace($"/work/{runId}/", "/work/<run-id>/"))],
                    artifact.TotalBytes,
                    log.Count(line => line.Message == "***"));
                artifactBytes[mode] = await host.DownloadAsync($"/runs/{runId}/artifacts/{artifact.Id}/download");
            }

            // The same pipeline.lua under both execution modes: every recorded step outcome identical.
            var jit = outcomes["JIT"];
            var aot = outcomes["AOT"];
            Assert.Equal(jit.Status, aot.Status);
            Assert.Equal(jit.ExitCode, aot.ExitCode);
            Assert.Equal(jit.FailureReason, aot.FailureReason);
            Assert.Equal(jit.GitCommit, aot.GitCommit);
            Assert.Equal(jit.Steps.Count, aot.Steps.Count);
            for (var index = 0; index < jit.Steps.Count; index++)
            {
                Assert.True(jit.Steps[index].Equals(aot.Steps[index]),
                    $"step {index} differs between JIT and AOT: {jit.Steps[index]} vs {aot.Steps[index]}");
            }

            Assert.Equal(jit.LogMessages.Count, aot.LogMessages.Count);
            for (var index = 0; index < jit.LogMessages.Count; index++)
            {
                Assert.True(string.Equals(jit.LogMessages[index], aot.LogMessages[index], StringComparison.Ordinal),
                    $"log line {index} differs between JIT and AOT: '{jit.LogMessages[index]}' vs '{aot.LogMessages[index]}'");
            }

            Assert.Equal(jit.ArtifactTotalBytes, aot.ArtifactTotalBytes);
            Assert.Equal(jit.RedactedLines, aot.RedactedLines);
            Assert.Equal(artifactBytes["JIT"], artifactBytes["AOT"]);

            _ = cancellationToken;
        }
        finally
        {
            DeleteDirectory(scratch);
        }
    }

    /// <summary>
    /// Masks the wall-clock duration a step's "exited with code N after Xs" line carries: the line is
    /// part of the contract, its number is timing, not semantics. Everything else must match byte for
    /// byte between the JIT and the AOT execution of the same pipeline.
    /// </summary>
    private static string NormalizeDuration(string message) =>
        AotDurationRegex().Replace(message, "$1 after <duration>s.");

    /// <summary>
    /// Resolves the symlinks along a path (macOS's /var, /tmp aliases) so two spellings of one file
    /// compare equal; a component without a link passes through unchanged.
    /// </summary>
    private static string ResolveRealPath(string path)
    {
        if (!OperatingSystem.IsWindows() && path.StartsWith('/'))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = "/";
            foreach (var part in parts)
            {
                current = Path.Combine(current, part);
                var link = Directory.Exists(current)
                    ? Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                    : File.ResolveLinkTarget(current, returnFinalTarget: true);
                if (link is not null)
                {
                    current = link.FullName;
                }
            }

            return current;
        }

        return Path.GetFullPath(path);
    }

    [GeneratedRegex("^(zenkins: step \\S+ \"[^\"]+\" exited with code \\S+) after [0-9.]+s\\.$")]
    private static partial Regex AotDurationRegex();

    // -------------------------------------------------------------------------------------------
    // Harness helpers
    // -------------------------------------------------------------------------------------------

    private static string NewScratchRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"zenkins-i004-aot-{Guid.NewGuid():N}")).FullName;

    private static void DeleteDirectory(string path)
    {
        // Q-011's cleanup contract via TestCleanup: a scratch root can hold read-only files, which
        // no retry count deletes on Windows until the attribute is cleared.
        TestCleanup.DeleteTree(path);
    }

    /// <summary>A recursive publish-output copy that preserves the executable bits (the apphosts).</summary>
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }
    }

    private static int PickFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>What one worker mode produced, in a form comparable across the two executions.</summary>
    private sealed record AotOutcomeComparison(
        string Status,
        int? ExitCode,
        string? FailureReason,
        string? GitCommit,
        IReadOnlyList<(int, string, string, int?, string?)> Steps,
        IReadOnlyList<string> LogMessages,
        long ArtifactTotalBytes,
        int RedactedLines);

    /// <summary>
    /// One publish per class for the local RID: the AOT Worker (the csproj's PublishAot default),
    /// the JIT Server and the JIT Worker (I-003 semantics, pinned off explicitly). The packaging
    /// claims are asserted on the output: the AOT directory is one native executable - no managed
    /// assembly, no runtimeconfig, no CoreCLR files - while the JIT Worker keeps all three.
    /// </summary>
    public sealed class PublishedAotArtifacts : IAsyncLifetime
    {
        private string _root = string.Empty;

        public static string ServerAppHostFileName =>
            OperatingSystem.IsWindows() ? "Zenkins.Server.exe" : "Zenkins.Server";

        public static string WorkerAppHostFileName =>
            OperatingSystem.IsWindows() ? "Zenkins.Worker.exe" : "Zenkins.Worker";

        public string Rid { get; private set; } = string.Empty;
        public string ServerPublishDir { get; private set; } = string.Empty;
        public string AotWorkerPublishDir { get; private set; } = string.Empty;
        public string JitWorkerPublishDir { get; private set; } = string.Empty;
        public string AotWorkerAppHostPath { get; private set; } = string.Empty;
        public string JitWorkerAppHostPath { get; private set; } = string.Empty;

        /// <summary>Installs the server and the AOT worker into <paramref name="scratch"/> (fresh copies, executable bits kept).</summary>
        public (string ServerInstallDir, string AotWorkerInstallDir) Install(string scratch)
        {
            var serverInstallDir = Path.Combine(scratch, "install-server");
            CopyDirectory(ServerPublishDir, serverInstallDir);
            var workerInstallDir = Path.Combine(scratch, "install-worker-aot");
            CopyDirectory(AotWorkerPublishDir, workerInstallDir);
            return (serverInstallDir, workerInstallDir);
        }

        public async ValueTask InitializeAsync()
        {
            Rid = RuntimeInformation.RuntimeIdentifier;
            _root = Path.Combine(Path.GetTempPath(), $"zenkins-i004-aot-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            ServerPublishDir = Path.Combine(_root, "Server");
            AotWorkerPublishDir = Path.Combine(_root, "WorkerAot");
            JitWorkerPublishDir = Path.Combine(_root, "WorkerJit");

            var repositoryRoot = MilestoneHost.FindRepositoryRoot();
            var dotnet = MilestoneHost.ResolveDotnetPath();

            // Serialised against the I-003 fixture through the shared lock: two publishes of the
            // same project must not race inside one obj/ tree.
            await PublishedSmokeTests.PublishedArtifacts.PublishLock.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                await PublishAsync(
                    dotnet,
                    repositoryRoot,
                    Path.Combine("src", "Server", "Zenkins.Server.csproj"),
                    ServerPublishDir,
                    PublishKind.ServerJit);
                await PublishAsync(
                    dotnet,
                    repositoryRoot,
                    Path.Combine("src", "Worker", "Zenkins.Worker.csproj"),
                    JitWorkerPublishDir,
                    PublishKind.WorkerJit);
                await PublishAsync(
                    dotnet,
                    repositoryRoot,
                    Path.Combine("src", "Worker", "Zenkins.Worker.csproj"),
                    AotWorkerPublishDir,
                    PublishKind.WorkerAot);
            }
            finally
            {
                PublishedSmokeTests.PublishedArtifacts.PublishLock.Release();
            }

            // The AOT artifact is one native executable: no managed assembly of the app, no
            // runtimeconfig.json, no CoreCLR runtime files - the "no .NET runtime installed" claim.
            AotWorkerAppHostPath = AssertAppHost(AotWorkerPublishDir, WorkerAppHostFileName);
            Assert.False(
                File.Exists(Path.Combine(AotWorkerPublishDir, "Zenkins.Worker.dll")),
                "the AOT publish output still ships the managed worker assembly.");
            Assert.False(
                File.Exists(Path.Combine(AotWorkerPublishDir, "Zenkins.Worker.runtimeconfig.json")),
                "the AOT publish output still ships a runtimeconfig.json.");
            Assert.True(
                Directory.EnumerateFiles(AotWorkerPublishDir, "*", SearchOption.TopDirectoryOnly)
                    .Any(file => Path.GetFileName(file)
                        .Contains("coreclr", StringComparison.OrdinalIgnoreCase)) == false,
                "the AOT publish output carries CoreCLR runtime files.");

            // P-001: the version surface must survive NativeAOT too - the same --version flag the
            // fast suite probes on the JIT worker, here against the native executable before the
            // scenario runs. Reading the entry assembly's AssemblyInformationalVersion is
            // metadata-only reflection, which ilc keeps; this probe fails loudly if that ever
            // regresses. The stamp equals the Tests assembly's because Directory.Build.props
            // stamps every project from the one Version property.
            Assert.Equal(
                "Zenkins Worker " + ProductVersion.From(typeof(PublishedAotSmokeTests).Assembly),
                await RunVersionProbeAsync(AotWorkerAppHostPath, TestContext.Current.CancellationToken));

            // The JIT baseline keeps everything the AOT artifact drops - it is what the consistency
            // run compares against, and the same assertion family PublishedSmokeTests makes.
            JitWorkerAppHostPath = AssertAppHost(JitWorkerPublishDir, WorkerAppHostFileName);
            Assert.True(
                File.Exists(Path.Combine(JitWorkerPublishDir, "Zenkins.Worker.dll")),
                "the JIT worker publish output is missing its managed assembly.");
            Assert.True(
                Directory.EnumerateFiles(JitWorkerPublishDir, "*", SearchOption.TopDirectoryOnly)
                    .Any(file => Path.GetFileName(file).Contains("coreclr", StringComparison.OrdinalIgnoreCase)),
                "the JIT worker publish output carries no CoreCLR runtime - it is not self-contained.");
        }

        public ValueTask DisposeAsync()
        {
            // Q-011's cleanup contract via TestCleanup (read-only files survive a plain Windows delete).
            TestCleanup.DeleteTree(_root);
            return ValueTask.CompletedTask;
        }

        /// <summary>Runs the published apphost with <c>--version</c> and returns its trimmed stdout.</summary>
        private static async Task<string> RunVersionProbeAsync(string appHostPath, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appHostPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"could not start '{appHostPath}' for the --version probe.");

            // Read both pipes before waiting for exit, so a chatty child cannot deadlock the wait.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(
                process.ExitCode == 0,
                $"the --version probe of '{appHostPath}' exited {process.ExitCode}: {stderr.Trim()}");
            return (await stdoutTask).Trim();
        }

        private enum PublishKind
        {
            /// <summary>I-003's Server row: self-contained JIT.</summary>
            ServerJit,

            /// <summary>The JIT baseline Worker row: self-contained JIT, PublishAot pinned off.</summary>
            WorkerJit,

            /// <summary>The I-004 row: the Worker's csproj default (PublishAot=true implies self-contained).</summary>
            WorkerAot,
        }

        private static async Task PublishAsync(
            string dotnetPath,
            string repositoryRoot,
            string projectPath,
            string outputDir,
            PublishKind kind)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = dotnetPath,
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("publish");
            startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, projectPath));
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("-r");
            startInfo.ArgumentList.Add(RuntimeInformation.RuntimeIdentifier);
            if (kind is not PublishKind.WorkerAot)
            {
                startInfo.ArgumentList.Add("--self-contained");
                startInfo.ArgumentList.Add("true");
            }
            if (kind is PublishKind.WorkerJit)
            {
                // I-004: the Worker project defaults to PublishAot; the JIT baseline pins it off.
                startInfo.ArgumentList.Add("-p:PublishAot=false");
            }

            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(outputDir);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("dotnet publish could not be started.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var output = await standardOutput + await standardError;
            Assert.True(process.ExitCode == 0,
                $"dotnet publish (-r {RuntimeInformation.RuntimeIdentifier}, {kind}) failed with "
                + $"{process.ExitCode}:{Environment.NewLine}{Tail(output, 60)}");

            // Warnings are errors repo-wide (the exit code above catches them); ": warning" in a
            // successful publish's output would still be noise this suite refuses.
            Assert.DoesNotContain(": warning", output, StringComparison.Ordinal);
        }

        private static string AssertAppHost(string publishDir, string appHostFileName)
        {
            var appHost = Path.Combine(publishDir, appHostFileName);
            Assert.True(File.Exists(appHost),
                $"the publish output has no apphost at {appHost}.");
            return appHost;
        }

        private static string Tail(string text, int lines)
        {
            var all = text.Split(Environment.NewLine);
            return string.Join(Environment.NewLine, all.TakeLast(lines));
        }
    }
}
