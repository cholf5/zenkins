using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Card I-003's smoke: the <b>published</b> artifacts, not <c>dotnet run</c>. Runs as
/// <c>Category=PublishSmoke</c> - excluded from the fast suite like Milestone and Recovery, run with
/// <c>dotnet test -c Release --filter "Category=PublishSmoke"</c> (docs/development.md §2). A
/// per-class fixture publishes the Server (self-contained folder), the Worker (self-contained
/// folder) and the Worker's single-file variant for this machine's RID; every fact then copies that
/// output into its own install directory and runs the real Q-002 scenario against the apphosts
/// executed <b>directly</b> - no
/// <c>dotnet exec</c>, no <c>ASPNETCORE_ENVIRONMENT</c>, no path configuration beyond what a fact
/// deliberately sets. Publishing runs with the product's <c>TreatWarningsAsErrors</c>, so a publish
/// that emits any warning (trim, AOT, NuGet audit) fails here before a single process starts.
/// <para>
/// TC-001 also proves the card's default-path claim: with no configuration at all the Server keeps its
/// data beside its own binaries (<c>App_Data/</c> under the publish directory), and then AC-6's
/// upgrade cycle - stop, replace the binaries, start again - preserves the database, the key ring and
/// the artifacts, and the upgraded pair still builds. TC-003 runs the <b>same</b> binaries a second
/// time against a different environment (a fixed port, three explicit paths) with the single-file
/// Worker, and nothing is rebuilt.
/// </para>
/// </summary>
[Trait("Category", "PublishSmoke")]
public sealed class PublishedSmokeTests : IClassFixture<PublishedSmokeTests.PublishedArtifacts>
{
    private readonly PublishedArtifacts _published;

    public PublishedSmokeTests(PublishedArtifacts published) => _published = published;

    [Fact(Timeout = 15 * 60 * 1000)]
    public async Task The_published_pair_builds_a_real_repository_keeps_its_data_beside_itself_and_survives_an_upgrade()
    {
        const string secretValue = "published-s3cr3t-value";
        var cancellationToken = TestContext.Current.CancellationToken;

        using var repository = MilestoneRepository.Create(MilestonePipelines.PublishedHappy);
        var scratch = NewScratchRoot();
        try
        {
            var (installDir, workerAppHost) = InstallPublishedArtifacts(scratch, singleFileWorker: false);
            var port = PickFreePort();

            // A fixed port, so the upgrade cycle below can come back on the address the worker is
            // dialing - which is exactly what a service deployment configures.
            using var host = await MilestoneHost.StartPublishedAsync(
                Path.Combine(installDir, PublishedArtifacts.ServerAppHostFileName),
                workerAppHost,
                port: port);
            Assert.StartsWith($"http://127.0.0.1:{port}", host.ServerUrl, StringComparison.Ordinal);

            // ----- The scenario: bootstrap, project, secret, worker, a real build. -----
            var projectId = await host.CreateProjectAsync("published-smoke");
            await host.PointProjectAtAsync(projectId, repository.FileUrl);
            await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);
            await host.StartWorkerAsync();

            var runId = await host.TriggerRunAsync(projectId);
            var workerRow = await host.SampleWorkerDuringRunAsync(runId);
            var run = await host.WaitForTerminalAsync(runId);

            Assert.Equal(workerRow.Id, run.WorkerId);
            Assert.NotEqual(Environment.ProcessId, host.WorkerProcessId);
            Assert.NotEqual(Environment.ProcessId, host.ServerProcessId);
            Assert.Equal("Success", run.Status);
            Assert.Equal("Manual", run.TriggerType);
            Assert.Equal(0, run.ExitCode);
            Assert.Null(run.FailureReason);
            Assert.Equal(repository.MainTip, run.GitCommit);

            var steps = await host.ReadStepsAsync(runId);
            Assert.Equal(new[] { "Restore", "Build", "Publish" }, steps.Select(step => step.Name));
            Assert.All(steps, step =>
            {
                Assert.Equal("Success", step.Status);
                Assert.Equal(0, step.ExitCode);
            });

            var log = await host.ReadLogLinesAsync(runId);
            Assert.Single(log, line => line.Message == MilestonePipelines.Greeting);
            Assert.Equal(steps[0].Id, log.First(line => line.Message == MilestonePipelines.Greeting).StepId);
            Assert.Single(log, line => line.Message == "building");
            Assert.Equal(steps[1].Id, log.First(line => line.Message == "building").StepId);
            Assert.Single(log, line => line.Message == "probe-written");
            Assert.Equal(steps[2].Id, log.First(line => line.Message == "probe-written").StepId);

            // The definition stored the secret *reference*; the log carries the redacted value only.
            Assert.NotNull(run.PipelineSpecJson);
            Assert.Contains("${secret:" + MilestonePipelines.SecretName, run.PipelineSpecJson, StringComparison.Ordinal);
            Assert.DoesNotContain(log, line => line.Message.Contains(secretValue, StringComparison.Ordinal));
            Assert.Single(log, line => line.Message == "***");

            var artifact = Assert.Single(await host.ReadArtifactsAsync(runId));
            Assert.Equal("report.txt", artifact.Name);
            Assert.Equal("Ready", artifact.Status);
            Assert.Equal(MilestonePipelines.ArtifactBytes.Length, artifact.TotalBytes);
            var downloaded = await host.DownloadAsync($"/runs/{runId}/artifacts/{artifact.Id}/download");
            Assert.Equal(MilestonePipelines.ArtifactBytes, downloaded);
            var logFile = Encoding.UTF8.GetString(await host.DownloadAsync($"/runs/{runId}/log/download"));
            Assert.Contains("probe-written", logFile, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, logFile, StringComparison.Ordinal);

            // The published static web assets are really served in the Production environment.
            await host.DownloadAsync("/js/theme-persistence.js");

            // ----- The card's default-path claim: data beside the binaries, nothing in the CWD. -----
            Assert.True(File.Exists(host.DatabasePath),
                $"the published server did not keep its database at {host.DatabasePath}.");
            Assert.True(Directory.Exists(host.KeysDirectory),
                $"the published server did not keep its key ring at {host.KeysDirectory}.");
            Assert.True(Directory.Exists(host.StoreDirectory),
                $"the published server did not keep its artifact store at {host.StoreDirectory}.");
            Assert.False(Directory.Exists(Path.Combine(host.RootPath, "server-cwd", "App_Data")),
                "the published server resolved a default path from its working directory - the "
                + "AppContext.BaseDirectory discipline (I-003) did not hold.");

            // ----- AC-6: stop -> replace the binaries -> start -> data intact, pair still builds. -----
            var runsBeforeUpgrade = (await host.ReadProjectRunsAsync(projectId)).Count;
            var artifactIdBefore = artifact.Id;
            host.StopServer();
            ReplaceBinaries(installDir, _published.ServerPublishDir);
            await host.StartServerAgainAsync();
            Assert.Equal($"http://127.0.0.1:{port}", host.ServerUrl);

            // The Owner's session (the key ring is data) and the build history survived the replace.
            var projectsPage = await host.GetPageAsync("/projects");
            Assert.Contains("published-smoke", projectsPage, StringComparison.Ordinal);
            Assert.Equal(runsBeforeUpgrade, (await host.ReadProjectRunsAsync(projectId)).Count);
            Assert.Equal(
                MilestonePipelines.ArtifactBytes,
                await host.DownloadAsync($"/runs/{runId}/artifacts/{artifactIdBefore}/download"));

            // The upgraded pair is fully functional: the worker reconnects, a new run claims and builds.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (await host.CountOnlineWorkersAsync() == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "the worker never came back online after the upgrade restart.");
                await Task.Delay(200, cancellationToken);
            }

            var upgradedRunId = await host.TriggerRunAsync(projectId);
            var upgradedRun = await host.WaitForTerminalAsync(upgradedRunId);
            Assert.Equal("Success", upgradedRun.Status);
            Assert.Equal(workerRow.Id, upgradedRun.WorkerId);
        }
        finally
        {
            DeleteDirectory(scratch);
        }
    }

    [Fact(Timeout = 15 * 60 * 1000)]
    public async Task The_same_published_binaries_honor_a_second_environment_and_the_single_file_worker_builds()
    {
        const string secretValue = "published-config-s3cr3t";
        var cancellationToken = TestContext.Current.CancellationToken;

        using var repository = MilestoneRepository.Create(MilestonePipelines.PublishedHappy);
        var scratch = NewScratchRoot();
        try
        {
            // The other publish, other install directories, another port, and three explicit paths -
            // configuration, not a rebuild. The Worker runs from the one-exe single-file variant.
            var (installDir, workerAppHost) = InstallPublishedArtifacts(scratch, singleFileWorker: true);
            var dataDirectory = Path.Combine(scratch, "data");
            var storeDirectory = Path.Combine(scratch, "store");
            var keysDirectory = Path.Combine(scratch, "keys");
            var port = PickFreePort();

            using var host = await MilestoneHost.StartPublishedAsync(
                Path.Combine(installDir, PublishedArtifacts.ServerAppHostFileName),
                workerAppHost,
                port: port,
                databasePath: Path.Combine(dataDirectory, "zenkins.db"),
                storeDirectory: storeDirectory,
                keysDirectory: keysDirectory);

            // TC-003: the configured port is the one that answers, not one the OS picked.
            Assert.StartsWith($"http://127.0.0.1:{port}", host.ServerUrl, StringComparison.Ordinal);

            var projectId = await host.CreateProjectAsync("published-config");
            await host.PointProjectAtAsync(projectId, repository.FileUrl);
            await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);
            await host.StartWorkerAsync();

            var runId = await host.TriggerRunAsync(projectId);
            var run = await host.WaitForTerminalAsync(runId);
            Assert.Equal("Success", run.Status);
            Assert.Equal(repository.MainTip, run.GitCommit);

            var artifact = Assert.Single(await host.ReadArtifactsAsync(runId));
            Assert.Equal("Ready", artifact.Status);
            var downloaded = await host.DownloadAsync($"/runs/{runId}/artifacts/{artifact.Id}/download");
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData(MilestonePipelines.ArtifactBytes)),
                Convert.ToHexStringLower(SHA256.HashData(downloaded)));

            // The data landed exactly where the environment pointed - and nowhere else: no App_Data
            // sprouted under the install directory or the working directory.
            Assert.True(File.Exists(host.DatabasePath),
                $"the configured database path {host.DatabasePath} was not used.");
            Assert.True(Directory.Exists(storeDirectory), "the configured artifact store was not used.");
            Assert.True(Directory.Exists(keysDirectory), "the configured key ring was not used.");
            Assert.False(Directory.Exists(Path.Combine(installDir, "App_Data")),
                "the published server created default App_Data despite explicit configuration.");
            Assert.False(Directory.Exists(Path.Combine(host.RootPath, "server-cwd", "App_Data")),
                "the published server resolved a path from its working directory.");

            // The binaries are identical to the publish output: the environment, not a rebuild, chose
            // this run's behavior.
            Assert.Equal(
                FileSha256(_published.ServerAppHostPath),
                FileSha256(Path.Combine(installDir, PublishedArtifacts.ServerAppHostFileName)));
            Assert.Equal(
                FileSha256(_published.WorkerSingleFileAppHostPath),
                FileSha256(workerAppHost));
        }
        finally
        {
            DeleteDirectory(scratch);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Harness helpers
    // -------------------------------------------------------------------------------------------

    private (string ServerInstallDir, string WorkerAppHost) InstallPublishedArtifacts(
        string scratch,
        bool singleFileWorker)
    {
        var installDir = Path.Combine(scratch, "install");
        CopyDirectory(_published.ServerPublishDir, installDir);

        var workerInstallDir = Path.Combine(scratch, "install-worker");
        CopyDirectory(
            singleFileWorker ? _published.WorkerSingleFilePublishDir : _published.WorkerPublishDir,
            workerInstallDir);

        return (installDir, Path.Combine(workerInstallDir, PublishedArtifacts.WorkerAppHostFileName));
    }

    private void ReplaceBinaries(string installDir, string publishDir)
    {
        // An upgrade replaces everything except the data (App_Data). This card has no newer build to
        // upgrade to, so the same publish output is rewritten over the emptied directory - the cycle
        // under test is stop -> replace -> start -> data intact, which is what AC-6 asks the packaging
        // to guarantee.
        foreach (var entry in Directory.EnumerateFileSystemEntries(installDir))
        {
            if (string.Equals(Path.GetFileName(entry), "App_Data", StringComparison.Ordinal))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        CopyDirectory(publishDir, installDir);
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

    private static string NewScratchRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"zenkins-i003-smoke-{Guid.NewGuid():N}")).FullName;

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
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

    private static string FileSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// One publish per class: Server (folder), Worker (folder) and Worker (single-file) for this
    /// machine's RID, self-contained, from the real projects. Each fact copies the output into its own
    /// install directories, so no run can mutate what another asserts about.
    /// </summary>
    public sealed class PublishedArtifacts : IAsyncLifetime
    {
        private static readonly SemaphoreSlim PublishLock = new(1, 1);
        private string _root = string.Empty;

        public static string ServerAppHostFileName =>
            OperatingSystem.IsWindows() ? "Zenkins.Server.exe" : "Zenkins.Server";

        public static string WorkerAppHostFileName =>
            OperatingSystem.IsWindows() ? "Zenkins.Worker.exe" : "Zenkins.Worker";

        public string Rid { get; private set; } = string.Empty;
        public string ServerPublishDir { get; private set; } = string.Empty;
        public string WorkerPublishDir { get; private set; } = string.Empty;
        public string WorkerSingleFilePublishDir { get; private set; } = string.Empty;
        public string ServerAppHostPath { get; private set; } = string.Empty;
        public string WorkerAppHostPath { get; private set; } = string.Empty;
        public string WorkerSingleFileAppHostPath { get; private set; } = string.Empty;

        /// <summary>
        /// One publish per class: Server (folder), Worker (folder) and Worker (single-file) for this
        /// machine's RID, self-contained, from the real projects. Each fact copies the output into its
        /// own install directories, so no run can mutate what another asserts about.
        /// </summary>
        public async ValueTask InitializeAsync()
        {
            Rid = RuntimeInformation.RuntimeIdentifier;
            _root = Path.Combine(Path.GetTempPath(), $"zenkins-i003-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            ServerPublishDir = Path.Combine(_root, "Server");
            WorkerPublishDir = Path.Combine(_root, "Worker");
            WorkerSingleFilePublishDir = Path.Combine(_root, "WorkerSingleFile");

            var repositoryRoot = MilestoneHost.FindRepositoryRoot();
            var dotnet = MilestoneHost.ResolveDotnetPath();

            // The publish matrix's local-RID row, executed by the suite itself: self-contained JIT,
            // no trimming, no AOT - and PublishSingleFile only for the Worker's one-exe variant.
            await PublishAsync(
                dotnet,
                repositoryRoot,
                Path.Combine("src", "Server", "Zenkins.Server.csproj"),
                ServerPublishDir,
                singleFile: false);
            await PublishAsync(
                dotnet,
                repositoryRoot,
                Path.Combine("src", "Worker", "Zenkins.Worker.csproj"),
                WorkerPublishDir,
                singleFile: false);
            await PublishAsync(
                dotnet,
                repositoryRoot,
                Path.Combine("src", "Worker", "Zenkins.Worker.csproj"),
                WorkerSingleFilePublishDir,
                singleFile: true);

            ServerAppHostPath = AssertAppHost(ServerPublishDir, ServerAppHostFileName);
            WorkerAppHostPath = AssertAppHost(WorkerPublishDir, WorkerAppHostFileName);
            WorkerSingleFileAppHostPath = AssertAppHost(WorkerSingleFilePublishDir, WorkerAppHostFileName);
            AssertSelfContained(ServerPublishDir);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }

        private static async Task PublishAsync(
            string dotnetPath,
            string repositoryRoot,
            string projectPath,
            string outputDir,
            bool singleFile)
        {
            var rid = RuntimeInformation.RuntimeIdentifier;

            // Two publishes of the same project must not race inside one obj/ tree.
            await PublishLock.WaitAsync(TestContext.Current.CancellationToken);
            try
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
                startInfo.ArgumentList.Add(rid);
                startInfo.ArgumentList.Add("--self-contained");
                startInfo.ArgumentList.Add("true");
                if (singleFile)
                {
                    startInfo.ArgumentList.Add("-p:PublishSingleFile=true");
                    startInfo.ArgumentList.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
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
                    $"dotnet publish (-r {rid}{(singleFile ? ", single-file" : "")}) failed with "
                    + $"{process.ExitCode}:{Environment.NewLine}{Tail(output, 60)}");
            }
            finally
            {
                PublishLock.Release();
            }
        }

        private static string AssertAppHost(string publishDir, string appHostFileName)
        {
            var appHost = Path.Combine(publishDir, appHostFileName);
            Assert.True(File.Exists(appHost),
                $"the publish output has no apphost at {appHost} (self-contained publishes ship one).");
            return appHost;
        }

        private static void AssertSelfContained(string publishDir)
        {
            // The publish output carries its own runtime and static web assets: no shared framework
            // lookup can happen at start, and the web app ships what it serves.
            Assert.True(
                File.Exists(Path.Combine(publishDir, "Zenkins.Server.dll")),
                "the publish output is missing the Server's managed assembly.");
            Assert.True(
                Directory.Exists(Path.Combine(publishDir, "wwwroot")),
                "the publish output is missing wwwroot (the static web assets a Blazor Server serves).");
            Assert.True(
                Directory.EnumerateFiles(publishDir, "*", SearchOption.TopDirectoryOnly)
                    .Any(file => Path.GetFileName(file).Contains("coreclr", StringComparison.OrdinalIgnoreCase)),
                "the publish output carries no CoreCLR runtime - it is not self-contained.");
        }

        private static string Tail(string text, int lines)
        {
            var all = text.Split(Environment.NewLine);
            return string.Join(Environment.NewLine, all.TakeLast(lines));
        }
    }
}
