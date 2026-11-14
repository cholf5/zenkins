using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.Milestone;

/// <summary>One row of the run's own record, read from the server's SQLite file.</summary>
internal sealed record MilestoneRun(
    Guid Id,
    Guid ProjectId,
    string Status,
    string TriggerType,
    Guid? WorkerId,
    int? ExitCode,
    string? FailureReason,
    string? GitCommit,
    string? PipelineSpecJson,
    DateTimeOffset? EndedAt);

/// <summary>One recorded step of a run.</summary>
internal sealed record MilestoneStep(
    Guid Id,
    int Index,
    string Name,
    string Status,
    int? ExitCode,
    string? FailureReason);

/// <summary>One persisted log line, with the step it was attributed to.</summary>
internal sealed record MilestoneLogLine(long Seq, string Stream, string Message, Guid? StepId);

/// <summary>One registered worker row (the server's own record of the process that connected).</summary>
internal sealed record MilestoneWorker(
    Guid Id,
    string Name,
    string Os,
    string Architecture,
    string Tags,
    string Status,
    int MaxConcurrency);

/// <summary>One artifact row (the bytes themselves are what the download endpoint serves).</summary>
internal sealed record MilestoneArtifact(
    Guid Id,
    string Name,
    string Kind,
    string Status,
    int FileCount,
    long TotalBytes,
    string? ManifestJson);

/// <summary>Knobs a milestone test may move; everything else runs on the product's own defaults.
/// Q-004's recovery harness adds the F-010 heartbeat knobs (compressed, never faked: the real
/// <see cref="Zenkins.Server.Workers.WorkerOfflineMonitor"/> drives them).</summary>
internal sealed record MilestoneHostOptions(
    double? DefaultTimeoutMinutes = null,
    int SweepIntervalSeconds = 1,
    int? OfflineAfterSeconds = null,
    int? WorkerMonitorIntervalSeconds = null);

/// <summary>
/// Q-002's process harness: the <b>real Server</b> as its own process over a throwaway SQLite file and a
/// throwaway artifact store, the <b>real Worker</b> as its own process against it, and read-only
/// assertions against the state those processes really produced (database rows, HTTP responses, files on
/// disk, process exit codes).
/// <para>
/// <b>Nothing product-internal is used.</b> Every interaction is a product surface: the real
/// first-run/owner forms, the project form, the run trigger form, the secrets form, the webhook settings
/// form, the webhook delivery endpoint, the log download endpoint and the artifact download endpoint.
/// The two things a form cannot express - a <c>file://</c> repository URL and a wait for a lease-free
/// queue - are seeded through the same database the server owns, which is observable state either way.
/// </para>
/// <para>
/// <b>Every temporary belongs to this instance</b>: database, Data Protection key ring, artifact store,
/// worker data directory and work root all live under <see cref="RootPath"/>, which <see cref="Dispose"/>
/// deletes. Both child processes are killed as a tree and waited for, so a failing assertion can never
/// leave a server or a worker (or a build's grandchildren) behind.
/// </para>
/// </summary>
internal class MilestoneHost : IDisposable
{
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan WorkerOnlineTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The signature header a GitHub-shaped delivery carries.</summary>
    public const string SignatureHeader = "X-Hub-Signature-256";

    /// <summary>The header the replay protection keys on.</summary>
    public const string DeliveryIdHeader = "X-GitHub-Delivery";

    private readonly string _dotnetPath;
    private readonly string _workerDll;
    private readonly CookieContainer _ownerCookies = new();
    private readonly List<Process> _workers = [];
    private readonly List<ProcessLog> _workerLogs = [];
    private readonly List<bool> _childExitStates = [];
    private Process? _server;
    private ProcessLog? _serverLogs;
    private bool _disposed;

    // I-003's published mode: the self-contained apphosts run directly (never `dotnet exec`), so the
    // smoke proves the publish output boots and serves without the SDK or a runtime on its PATH, in
    // the Production environment, with the product's own path defaults.
    private bool _published;
    private string _serverAppHost = string.Empty;
    private string _workerAppHost = string.Empty;
    private string _publishedServerWorkingDirectory = string.Empty;
    private string _publishedUrls = string.Empty;
    private string? _publishedDatabasePath;
    private string? _publishedStoreDirectory;
    private string? _publishedKeysDirectory;

    // Q-004's restart capability: the facts a restart needs (the same binaries, the same options) are
    // remembered from the first start, so a restarted server is byte-for-byte the same deployment over
    // the same database, key ring and store.
    private string _repositoryRoot = string.Empty;
    private string _serverDll = string.Empty;
    private MilestoneHostOptions _startOptions = new();

    protected internal MilestoneHost(string rootPath, string dotnetPath, string workerDll)
    {
        RootPath = rootPath;
        _dotnetPath = dotnetPath;
        _workerDll = workerDll;
        DatabasePath = Path.Combine(rootPath, "zenkins.db");
        WorkRoot = Path.Combine(rootPath, "work");
        WorkerDataDirectory = Path.Combine(rootPath, "worker-data");
        KeysDirectory = Path.Combine(rootPath, "keys");
        StoreDirectory = Path.Combine(rootPath, "artifacts");
    }

    /// <summary>This instance's temporary root: database, keys, store, worker data and work root.</summary>
    public string RootPath { get; }

    /// <summary>The server's SQLite file (the run/step/log/artifact record lives here).</summary>
    public string DatabasePath { get; private set; }

    /// <summary>The worker's work root (<c>work/{run-id}/repository|artifacts|temp</c>).</summary>
    public string WorkRoot { get; private set; }

    /// <summary>The worker's data directory (its persisted token).</summary>
    public string WorkerDataDirectory { get; private set; }

    /// <summary>The server's Data Protection key ring (never the machine's or the repository's).</summary>
    public string KeysDirectory { get; private set; }

    /// <summary>The server's artifact store (never <c>src/Server/App_Data</c>).</summary>
    public string StoreDirectory { get; private set; }

    /// <summary>Where the server listens (a port the OS picked).</summary>
    public string ServerUrl { get; private set; } = string.Empty;

    /// <summary>The signed-in bootstrap Owner (cookie-authenticated, no redirect following).</summary>
    public HttpClient Owner { get; private set; } = null!;

    /// <summary>The server process this host started.</summary>
    public Process ServerProcess => _server ?? throw new InvalidOperationException("the server is not running.");

    /// <summary>The server's operating-system process id (a real process, not an in-host instance).</summary>
    public int ServerProcessId => ServerProcess.Id;

    /// <summary>
    /// The worker's operating-system process id. The card's central claim is "a real worker process", so
    /// the tests assert this id is not the test's own and is really present in the process table.
    /// </summary>
    public int WorkerProcessId => WorkerProcess.Id;

    /// <summary>The worker process this host started.</summary>
    public Process WorkerProcess => _workers.Count > 0
        ? _workers[0]
        : throw new InvalidOperationException("no worker has been started for this host.");

    /// <summary>The name this host's worker registered under (the Workers row it must match).</summary>
    public string WorkerName { get; private set; } = string.Empty;

    /// <summary>The worker's stdout/stderr lines, for diagnosis when an assertion fails.</summary>
    public string[] WorkerOutput => [.. _workerLogs.SelectMany(logs => logs.Snapshot())];

    /// <summary>The server's stdout/stderr lines, for diagnosis when an assertion fails.</summary>
    public string[] ServerOutput => _serverLogs?.Snapshot() ?? [];

    /// <summary>The one-time registration token the worker of this host presents.</summary>
    public string RegistrationToken { get; private set; } = string.Empty;

    /// <summary>The built Worker executable this host launches (Release output; resolved at start).</summary>
    protected internal string WorkerDllPath => _workerDll;

    // -------------------------------------------------------------------------------------------
    // Start / stop
    // -------------------------------------------------------------------------------------------

    /// <summary>Starts the real server over a fresh temporary database and signs the Owner in.</summary>
    public static async Task<MilestoneHost> StartAsync(MilestoneHostOptions? options = null)
    {
        options ??= new MilestoneHostOptions();
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-q002-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var repositoryRoot = FindRepositoryRoot();
        var dotnet = ResolveDotnetPath();
        var serverDll = FindBuiltBinary(repositoryRoot, Path.Combine("src", "Server", "bin"), "Zenkins.Server.dll");
        var workerDll = FindBuiltBinary(repositoryRoot, Path.Combine("src", "Worker", "bin"), "Zenkins.Worker.dll");

        var host = new MilestoneHost(root, dotnet, workerDll);
        try
        {
            await host.StartCoreAsync(repositoryRoot, serverDll, options);

            var (token, response) = await WorkerTestHost.MintRegistrationTokenAsync(host.Owner);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            host.RegistrationToken = token;
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>The start every host shares: start the server, wait for it, sign the Owner in.</summary>
    protected async Task StartCoreAsync(string repositoryRoot, string serverDll, MilestoneHostOptions options)
    {
        StartServer(repositoryRoot, serverDll, options);
        ServerUrl = await WaitForServerAsync(TestContext.Current.CancellationToken);
        Owner = await SignInOwnerAsync(ServerUrl);
    }

    /// <summary>
    /// I-003's published smoke: starts the <b>published</b> Server and Worker self-contained apphosts
    /// directly - no <c>dotnet exec</c>, no <c>ASPNETCORE_ENVIRONMENT</c> (so the Production environment
    /// the artifacts ship with), no path configuration unless one is given - so the scenario proves the
    /// publish output alone completes the real Q-002 story.
    /// <para>
    /// Without explicit paths the Server's persistent state lands where the product's defaults put it:
    /// <c>App_Data/</c> under the publish directory (the AppContext.BaseDirectory discipline), which the
    /// smoke asserts. With <paramref name="databasePath"/> / <paramref name="storeDirectory"/> /
    /// <paramref name="keysDirectory"/> the harness instead configures that placement through the
    /// documented configuration keys (TC-003's same-binaries-different-environment run). The worker's
    /// token and work directories always follow this instance's harness root, like every other host.
    /// </para>
    /// </summary>
    public static async Task<MilestoneHost> StartPublishedAsync(
        string serverAppHost,
        string workerAppHost,
        int? port = null,
        string? databasePath = null,
        string? storeDirectory = null,
        string? keysDirectory = null,
        MilestoneHostOptions? options = null)
    {
        options ??= new MilestoneHostOptions();
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-i003-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var repositoryRoot = FindRepositoryRoot();
        var dotnet = ResolveDotnetPath();

        var host = new MilestoneHost(root, dotnet, workerAppHost);
        host.ConfigurePublishedLayout(
            serverAppHost,
            workerAppHost,
            Directory.CreateDirectory(Path.Combine(root, "server-cwd")).FullName,
            $"http://127.0.0.1:{port?.ToString(CultureInfo.InvariantCulture) ?? "0"}",
            databasePath,
            storeDirectory,
            keysDirectory);

        try
        {
            await host.StartCoreAsync(repositoryRoot, serverAppHost, options);

            var (token, response) = await WorkerTestHost.MintRegistrationTokenAsync(host.Owner);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            host.RegistrationToken = token;
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Published-mode layout: the Server runs from its own apphost with an arbitrary working directory,
    /// and its persistent paths follow the publish directory's <c>App_Data</c> (or the caller's explicit
    /// configuration) for the harness's assertions. The worker's scratch paths stay under the harness
    /// root, as they were constructed.
    /// </summary>
    private void ConfigurePublishedLayout(
        string serverAppHost,
        string workerAppHost,
        string serverWorkingDirectory,
        string urls,
        string? databasePath,
        string? storeDirectory,
        string? keysDirectory)
    {
        _published = true;
        _serverAppHost = serverAppHost;
        _workerAppHost = workerAppHost;
        _publishedServerWorkingDirectory = serverWorkingDirectory;
        _publishedUrls = urls;
        _publishedDatabasePath = databasePath;
        _publishedStoreDirectory = storeDirectory;
        _publishedKeysDirectory = keysDirectory;

        var appData = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(serverAppHost))!,
            "App_Data");
        DatabasePath = databasePath ?? Path.Combine(appData, "zenkins.db");
        KeysDirectory = keysDirectory ?? Path.Combine(appData, "keys");
        StoreDirectory = storeDirectory ?? Path.Combine(appData, "artifacts");
    }

    protected void StartServer(string repositoryRoot, string serverDll, MilestoneHostOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (_published)
        {
            // The published artifact boots as itself: the apphost executed directly (no `dotnet`), the
            // Production environment, and no path configuration beyond what the published layout asked
            // for. The Server's own defaults resolve from AppContext.BaseDirectory - the publish
            // directory - so the data lands there whatever the working directory is (card I-003).
            startInfo.FileName = _serverAppHost;
            startInfo.WorkingDirectory = _publishedServerWorkingDirectory;
            startInfo.Environment["ASPNETCORE_URLS"] = _publishedUrls;
            startInfo.Environment["Database__Provider"] = "Sqlite";
            startInfo.Environment["Database__ApplyMigrationsOnStartup"] = "true";
            if (_publishedDatabasePath is { } publishedDatabasePath)
            {
                startInfo.Environment["ConnectionStrings__Default"] = $"Data Source={publishedDatabasePath}";
            }

            if (_publishedStoreDirectory is { } publishedStoreDirectory)
            {
                startInfo.Environment["Artifacts__RootPath"] = publishedStoreDirectory;
            }

            if (_publishedKeysDirectory is { } publishedKeysDirectory)
            {
                startInfo.Environment["DataProtection__KeysDirectory"] = publishedKeysDirectory;
            }
        }
        else
        {
            startInfo.FileName = _dotnetPath;
            startInfo.WorkingDirectory = Path.Combine(repositoryRoot, "src", "Server");
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(serverDll);
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            startInfo.Environment["Database__Provider"] = "Sqlite";
            startInfo.Environment["ConnectionStrings__Default"] = $"Data Source={DatabasePath}";
            startInfo.Environment["Database__ApplyMigrationsOnStartup"] = "true";

            // Every piece of server state that would otherwise default into the repository or the machine's
            // profile follows this instance's temporary root (the F-016/F-012 discipline).
            startInfo.Environment["Artifacts__RootPath"] = StoreDirectory;
            startInfo.Environment["DataProtection__KeysDirectory"] = KeysDirectory;
        }

        // A short lease sweep and - when a test moves it - a short default deadline, so the milestone
        // suite proves the real timeout path without waiting out a production timeout.
        startInfo.Environment["Runs__SweepIntervalSeconds"] =
            options.SweepIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        if (options.DefaultTimeoutMinutes is { } minutes)
        {
            startInfo.Environment["Runs__DefaultTimeoutMinutes"] =
                minutes.ToString(CultureInfo.InvariantCulture);
        }

        // Q-004: compressed F-010 timing (the real WorkerOfflineMonitor drives it) - a worker whose
        // heartbeat goes stale is flipped Offline and its Running runs become Lost, at test scale.
        if (options.OfflineAfterSeconds is { } offlineAfterSeconds)
        {
            startInfo.Environment["Workers__OfflineAfterSeconds"] =
                offlineAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        if (options.WorkerMonitorIntervalSeconds is { } monitorIntervalSeconds)
        {
            startInfo.Environment["Workers__MonitorIntervalSeconds"] =
                monitorIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        }

        _server = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Server process could not be started.");
        _serverLogs = new ProcessLog(_server);
        _repositoryRoot = repositoryRoot;
        _serverDll = serverDll;
        _startOptions = options;
    }

    /// <summary>
    /// Stops the running server (as a tree, waited for) and forgets it, so a later
    /// <see cref="StartServerAgainAsync"/> starts a fresh process over the <b>same</b> database, key
    /// ring and store. The Owner's session cookie survives the restart because the key ring does -
    /// which is itself part of the restart story a recovery test asserts. A no-op when the server is
    /// already stopped (a test may stop and restart in steps).
    /// </summary>
    public void StopServer()
    {
        if (_server is null)
        {
            return;
        }

        Kill(_server);
        _server = null;
        _serverLogs?.Dispose();
        _serverLogs = null;
    }

    /// <summary>
    /// Starts the server again after <see cref="StopServer"/> - the same binaries and the same options
    /// as the first start, over the same database (the queue must survive the restart, not the
    /// process). <see cref="ServerUrl"/> is updated: a restarted server takes the port the OS picks.
    /// </summary>
    public async Task StartServerAgainAsync()
    {
        if (_server is not null)
        {
            throw new InvalidOperationException("the server is still running; stop it first.");
        }

        StartServer(_repositoryRoot, _serverDll, _startOptions);
        ServerUrl = await WaitForServerAsync(TestContext.Current.CancellationToken);

        // The restarted server took a new port; the Owner's session outlives the process because the
        // Data Protection key ring does - the same cookie jar is re-pointed at the new address, so the
        // Owner keeps the identity (and the organization membership) the first server issued. This is
        // part of what a restart asserts, not a harness shortcut.
        if (Owner is not null)
        {
            Owner.Dispose();
            Owner = CreateOwnerClient(ServerUrl);
        }
    }

    /// <summary>
    /// Registers a child process (and its log) into this host's tracked set, so <see cref="Dispose"/>
    /// kills it as a tree even if the test already killed or abandoned it. Recovery's restart path
    /// starts workers through this seam.
    /// </summary>
    protected internal void TrackWorker(Process worker, ProcessLog logs)
    {
        _workers.Add(worker);
        _workerLogs.Add(logs);
    }

    /// <summary>Starts the real worker against this host (one worker per host; started late by one test).</summary>
    /// <param name="name">The worker's registered name.</param>
    /// <param name="extraEnvironment">
    /// Extra variables for the worker process (card F-018 uses this to pin an isolated <c>HOME</c> and the
    /// fixture's certificate authority). They are applied last, so a test can never accidentally undo the
    /// configuration the harness needs.
    /// </param>
    public async Task StartWorkerAsync(
        string? name = null,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        Assert.Empty(_workers);
        var startInfo = new ProcessStartInfo
        {
            // Published mode runs the self-contained apphost directly; the framework-dependent harness
            // goes through the dotnet host. Either way it is a real worker process (I-003).
            FileName = _published ? _workerAppHost : _dotnetPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!_published)
        {
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(_workerDll);
        }
        startInfo.Environment["ZENKINS_SERVER_URL"] = ServerUrl;
        startInfo.Environment["ZENKINS_REGISTRATION_TOKEN"] = RegistrationToken;
        WorkerName = name ?? $"milestone-{Guid.NewGuid():N}".ToLowerInvariant();
        startInfo.Environment["ZENKINS_WORKER_NAME"] = WorkerName;
        startInfo.Environment["ZENKINS_WORKER_TAGS"] = "milestone,ci";
        startInfo.Environment["ZENKINS_HEARTBEAT_SECONDS"] = "1";
        startInfo.Environment["ZENKINS_CLAIM_SECONDS"] = "1";
        startInfo.Environment["ZENKINS_DATA_DIR"] = WorkerDataDirectory;
        startInfo.Environment["ZENKINS_WORK_ROOT"] = WorkRoot;

        if (extraEnvironment is not null)
        {
            foreach (var (variable, value) in extraEnvironment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(variable);
                }
                else
                {
                    startInfo.Environment[variable] = value;
                }
            }
        }

        var worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Worker process could not be started.");
        _workers.Add(worker);
        var logs = new ProcessLog(worker);
        _workerLogs.Add(logs);
        await logs.WaitForLineAsync(
            line => line.Contains("online as worker", StringComparison.Ordinal),
            WorkerOnlineTimeout,
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Whether every child process this host started has really exited. Sampled while the handles were
    /// still valid (a <see cref="Process"/> cannot be asked once it has been disposed), so it answers the
    /// question a test cares about - did cleanup really stop everything - after <see cref="Dispose"/>.
    /// </summary>
    public bool ChildProcessesExited =>
        _childExitStates.Count > 0 && _childExitStates.All(exited => exited);

    /// <summary>Whether the temporary root is really gone.</summary>
    public bool TemporaryRootRemoved => !Directory.Exists(RootPath);

    /// <summary>
    /// Stops every child as a tree, deletes the temporary root, and is idempotent: a test may call it
    /// itself to assert the harness's own cleanup and a <c>using</c> declaration stays safe afterwards.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var worker in _workers)
        {
            Kill(worker);
        }

        _workerLogs.ForEach(logs => logs.Dispose());
        if (_server is not null)
        {
            Kill(_server);
        }

        _serverLogs?.Dispose();
        Owner?.Dispose();
        DeleteDirectory(RootPath);
    }

    /// <summary>Kills a child and its tree, waits, and records whether it really exited.</summary>
    protected void Kill(Process process)
    {
        var exited = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            exited = process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds)
                && process.HasExited;
        }
        catch (Exception failure) when (failure is InvalidOperationException or SystemException)
        {
            // The process already went away; nothing to clean up.
            exited = true;
        }
        finally
        {
            _childExitStates.Add(exited);
            process.Dispose();
        }
    }

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

    // -------------------------------------------------------------------------------------------
    // Product surfaces: accounts, projects, runs, secrets, webhooks
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates a project through the real project form; returns its id. <paramref name="repositoryUrl"/>
    /// is the URL a person would paste (card F-018's proof uses a real <c>https://127.0.0.1</c> remote, so
    /// the project is created the way an operator creates one - no seeded row).
    /// </summary>
    public async Task<Guid> CreateProjectAsync(
        string name,
        string defaultBranch = "main",
        string repositoryUrl = "https://example.test/milestone.git")
    {
        var response = await OrganizationTestHost.PostFormAsync(
            Owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["RepositoryUrl"] = repositoryUrl,
                ["DefaultBranch"] = defaultBranch,
                ["PipelinePath"] = "pipeline.lua",
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating the project failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }

    /// <summary>
    /// Points an existing project row at the fixture repository. The project form refuses a
    /// <c>file://</c> URL on purpose (it would make the server read its own disk), and a test needs a
    /// repository the worker can really clone, so the row is seeded - the same thing a deployment would
    /// do through a real remote.
    /// </summary>
    public async Task PointProjectAtAsync(Guid projectId, string repositoryUrl)
    {
        await ExecuteAsync(
            "UPDATE \"Projects\" SET \"RepositoryUrl\" = $url WHERE \"Id\" = $id",
            ("$url", repositoryUrl),
            ("$id", projectId.ToString().ToUpperInvariant()));
    }

    /// <summary>Triggers a manual run through the real trigger form and returns the new run's id.</summary>
    public async Task<Guid> TriggerRunAsync(Guid projectId, string branch = "")
    {
        var response = await OrganizationTestHost.PostFormAsync(
            Owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/runs/start",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Branch"] = branch,
                ["RequiredTags"] = string.Empty,
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Triggering the run failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/runs/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/runs/".Length..].Split('?')[0]);
    }

    /// <summary>Requests a user cancel through the run page's own form.</summary>
    public async Task<HttpResponseMessage> CancelRunAsync(Guid runId) =>
        await OrganizationTestHost.PostFormAsync(
            Owner,
            $"/runs/{runId}",
            $"/runs/{runId}/cancel",
            new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Creates an organization git credential through the project settings tab; returns its id.</summary>
    public async Task<Guid> CreateGitCredentialAsync(Guid projectId, string name, string value)
    {
        var settingsPath = $"/projects/{projectId}/settings";
        var response = await OrganizationTestHost.PostFormAsync(
            Owner,
            settingsPath,
            "/git-credentials/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = "HttpsToken",
                ["Value"] = value,
                ["UserName"] = "zenkins-bot",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating the Git credential failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var list = await AuthTestHost.GetStringAsync(Owner, $"/projects/{projectId}/git-credentials");
        using var document = JsonDocument.Parse(list);
        return Assert.Single(document.RootElement.EnumerateArray()).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Creates an organization <b>SSH-key</b> Git credential through the project settings tab; returns
    /// its id. The value is the full private-key block (card Q-009's fixture serves the matching public
    /// half); the optional HTTPS user name does not exist for this kind and is not posted.
    /// </summary>
    public async Task<Guid> CreateSshGitCredentialAsync(Guid projectId, string name, string privateKey)
    {
        var settingsPath = $"/projects/{projectId}/settings";
        var response = await OrganizationTestHost.PostFormAsync(
            Owner,
            settingsPath,
            "/git-credentials/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = "SshKey",
                ["Value"] = privateKey,
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating the SSH Git credential failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var list = await AuthTestHost.GetStringAsync(Owner, $"/projects/{projectId}/git-credentials");
        using var document = JsonDocument.Parse(list);
        // A project may already hold credentials (a test attaches a rejected one, then the good one),
        // so the id is resolved by name rather than by position.
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (string.Equals(element.GetProperty("name").GetString(), name, StringComparison.Ordinal))
            {
                return element.GetProperty("id").GetGuid();
            }
        }

        Assert.Fail($"the credential '{name}' was not in the organization's credential list: {list}");
        return Guid.Empty;
    }

    /// <summary>Attaches a credential through the settings tab's form (card F-018's write path).</summary>
    public async Task AttachGitCredentialAsync(Guid projectId, Guid credentialId)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            Owner,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/git-credential/attach",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GitCredentialId"] = credentialId.ToString(),
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Attaching the Git credential failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Creates an organization secret through the real secrets form.</summary>
    public async Task CreateSecretAsync(string name, string value)
    {
        var response = await OrganizationTestHost.PostFormAsync(
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
            $"Creating the secret failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var location = response.Headers.Location!.OriginalString;
        Assert.Contains("notice=created", location, StringComparison.Ordinal);
    }

    /// <summary>Creates a webhook through the project page's form; returns its id and its one-time secret.</summary>
    public async Task<(Guid WebhookId, string Secret)> CreateWebhookAsync(
        Guid projectId,
        string name,
        string branchFilter = "main")
    {
        var response = await OrganizationTestHost.PostFormAsync(
            Owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/webhooks/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["BranchFilter"] = branchFilter,
                ["RequiredTags"] = string.Empty,
            });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var secret = Regex.Match(
            html,
            "id=\"webhook-secret-plaintext\">(?<secret>whsec_[A-Za-z0-9_-]+)<",
            RegexOptions.CultureInvariant);
        Assert.True(secret.Success, $"the webhook form did not show a one-time secret: {html}");

        var id = Regex.Match(html, "/webhooks/(?<id>[0-9a-fA-F-]{36})", RegexOptions.CultureInvariant);
        Assert.True(id.Success, $"the webhook form did not name the webhook id: {html}");
        return (Guid.Parse(id.Groups["id"].Value), secret.Groups["secret"].Value);
    }

    /// <summary>POSTs a signed delivery - exactly the bytes the signature covers - to the delivery endpoint.</summary>
    public async Task<HttpResponseMessage> DeliverAsync(
        Guid webhookId,
        string body,
        string secret,
        string deliveryId)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{webhookId}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(bytes, secret));
        request.Headers.TryAddWithoutValidation(DeliveryIdHeader, deliveryId);

        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(ServerUrl.TrimEnd('/') + "/"),
        };
        return await anonymous.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The GitHub-shaped signature of <paramref name="body"/>, computed here and not by the product.</summary>
    public static string Sign(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>Fetches a page as the Owner (the cookie jar is the Owner's).</summary>
    public async Task<string> GetPageAsync(string relativeUrl) =>
        await AuthTestHost.GetStringAsync(Owner, relativeUrl);

    /// <summary>Fetches a body as the Owner, returning the raw bytes (an artifact or the log download).</summary>
    public async Task<byte[]> DownloadAsync(string relativeUrl)
    {
        using var response = await Owner.GetAsync(relativeUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------------------------
    // Observable state: the server's own database
    // -------------------------------------------------------------------------------------------

    /// <summary>Reads one run row.</summary>
    public async Task<MilestoneRun> ReadRunAsync(Guid runId)
    {
        var rows = await ReadAsync(
            "SELECT \"Id\", \"ProjectId\", \"Status\", \"TriggerType\", \"WorkerId\", \"ExitCode\", "
            + "\"FailureReason\", \"GitCommit\", \"PipelineSpecJson\", \"EndedAt\" FROM \"BuildRuns\" "
            + $"WHERE \"Id\" = '{runId.ToString("D").ToUpperInvariant()}'",
            MapRun);
        Assert.Single(rows);
        return rows[0];
    }

    /// <summary>Every run of one project, oldest first.</summary>
    public Task<IReadOnlyList<MilestoneRun>> ReadProjectRunsAsync(Guid projectId) =>
        ReadAsync(
            "SELECT \"Id\", \"ProjectId\", \"Status\", \"TriggerType\", \"WorkerId\", \"ExitCode\", "
            + "\"FailureReason\", \"GitCommit\", \"PipelineSpecJson\", \"EndedAt\" FROM \"BuildRuns\" "
            + $"WHERE \"ProjectId\" = '{projectId.ToString("D").ToUpperInvariant()}' ORDER BY \"CreatedAt\"",
            MapRun);

    /// <summary>Maps one <c>BuildRuns</c> row (the projection every run read shares).</summary>
    private static MilestoneRun MapRun(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetDateTimeOffset(9));

    /// <summary>Every recorded step of one run, in spec order.</summary>
    public Task<IReadOnlyList<MilestoneStep>> ReadStepsAsync(Guid runId) =>
        ReadAsync(
            "SELECT \"Id\", \"Index\", \"Name\", \"Status\", \"ExitCode\", \"FailureReason\" FROM \"BuildSteps\" "
            + $"WHERE \"RunId\" = '{runId.ToString("D").ToUpperInvariant()}' ORDER BY \"Index\"",
            reader => new MilestoneStep(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));

    /// <summary>Every persisted log line of one run, in sequence order.</summary>
    public Task<IReadOnlyList<MilestoneLogLine>> ReadLogLinesAsync(Guid runId) =>
        ReadAsync(
            "SELECT \"Seq\", \"Stream\", \"Message\", \"StepId\" FROM \"BuildLogs\" "
            + $"WHERE \"RunId\" = '{runId.ToString("D").ToUpperInvariant()}' ORDER BY \"Seq\"",
            reader => new MilestoneLogLine(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3))));

    /// <summary>Every artifact row of one run.</summary>
    public Task<IReadOnlyList<MilestoneArtifact>> ReadArtifactsAsync(Guid runId) =>
        ReadAsync(
            "SELECT \"Id\", \"Name\", \"Kind\", \"Status\", \"FileCount\", \"TotalBytes\", \"ManifestJson\" "
            + $"FROM \"Artifacts\" WHERE \"RunId\" = '{runId.ToString("D").ToUpperInvariant()}' ORDER BY \"CreatedAt\"",
            reader => new MilestoneArtifact(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));

    /// <summary>
    /// Proves the card's "real worker process" claim from evidence the suite itself can see:
    /// <list type="number">
    /// <item>the worker and the server are <b>different processes from the test host</b> (so a harness
    /// that silently degraded to an in-process worker would fail here);</item>
    /// <item>the id is really in the operating system's <b>process table</b> right now, and it is the
    /// same process instance this host started (same start instant) - so a recycled pid cannot stand in;</item>
    /// <item>the server's own <c>Workers</c> row for the name this worker registered under is
    /// <c>Online</c> with this host's tags and platform, and - when a run is given - that row is the one
    /// the run is assigned to.</item>
    /// </list>
    /// </summary>
    public async Task<MilestoneWorker> AssertWorkerRunsAsItsOwnLiveProcessAsync(Guid? runId = null)
    {
        // (a) Neither child is this test process, and they are not each other.
        Assert.NotEqual(Environment.ProcessId, WorkerProcessId);
        Assert.NotEqual(Environment.ProcessId, ServerProcessId);
        Assert.NotEqual(WorkerProcessId, ServerProcessId);

        // (b) The OS process table holds it, with the start instant of the handle we own.
        var tracked = WorkerProcess;
        DateTime trackedStart;
        try
        {
            trackedStart = tracked.StartTime.ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            Assert.Fail($"the worker process {WorkerProcessId} has already exited - it was never a live process during the run.");
            throw;
        }

        using var lookedUp = LookUpProcess(WorkerProcessId);
        Assert.False(lookedUp.HasExited, $"the worker process {WorkerProcessId} is not alive.");
        Assert.Equal(trackedStart, lookedUp.StartTime.ToUniversalTime());

        // The framework-dependent harness runs under the dotnet host; the published apphost (I-003)
        // runs under its own executable name - which is exactly the claim the published smoke tests.
        var expectedProcessName = _published ? "zenkins" : "dotnet";
        Assert.Contains(expectedProcessName, lookedUp.ProcessName, StringComparison.OrdinalIgnoreCase);

        // (c) The server registered exactly this worker (by the name this host passed it).
        var workers = await ReadAsync(
            "SELECT \"Id\", \"Name\", \"Os\", \"Architecture\", \"Tags\", \"Status\", \"MaxConcurrency\" "
            + $"FROM \"Workers\" WHERE \"Name\" = '{WorkerName}'",
            reader => new MilestoneWorker(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6)));
        var worker = Assert.Single(workers);

        Assert.Equal(WorkerName, worker.Name);
        Assert.Equal("Online", worker.Status);
        Assert.Equal("milestone,ci", worker.Tags);
        Assert.Equal(ExpectedWorkerOs(), worker.Os);

        if (runId is { } id)
        {
            var run = await ReadRunAsync(id);
            Assert.Equal(worker.Id, run.WorkerId);
        }

        return worker;
    }

    /// <summary>
    /// Samples the real-process facts <b>while the run executes</b>: it waits (bounded) until the run has
    /// left the queue - a claim stamps <c>WorkerId</c>, and acceptance starts the clock, so the run is
    /// executing at that instant - and then applies every check of
    /// <see cref="AssertWorkerRunsAsItsOwnLiveProcessAsync"/>, including "this run is assigned to that
    /// row". Concurrent runs are not waited for: a run that finished before the sample is still a run
    /// this pid executed.
    /// </summary>
    public async Task<MilestoneWorker> SampleWorkerDuringRunAsync(Guid runId)
    {
        var deadline = DateTime.UtcNow + TerminalTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await ReadRunAsync(runId)).Status != "Queued")
            {
                return await AssertWorkerRunsAsItsOwnLiveProcessAsync(runId);
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"run {runId:D} never left the queue, so no execution-time sample was possible.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The process-table lookup by id (an independent handle, not the one this host keeps).</summary>
    protected internal static Process LookUpProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            Assert.Fail($"no live process with id {processId} exists in the process table.");
            throw;
        }
    }

    /// <summary>The <c>Os</c> string the worker reports for this host's platform.</summary>
    protected internal static string ExpectedWorkerOs() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsMacOS() ? "macOS"
        : "Unknown";

    /// <summary>How many workers the server records as <c>Online</c> right now.</summary>
    public async Task<int> CountOnlineWorkersAsync()
    {
        var rows = await ReadAsync(
            "SELECT \"Id\" FROM \"Workers\" WHERE \"Status\" = 'Online'",
            reader => reader.GetString(0));
        return rows.Count;
    }

    // -------------------------------------------------------------------------------------------
    // Bounded waits (polling, never a fixed sleep chain)
    // -------------------------------------------------------------------------------------------

    /// <summary>Polls until the run leaves Queued/Running, then returns its row.</summary>
    public async Task<MilestoneRun> WaitForTerminalAsync(Guid runId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TerminalTimeout);
        MilestoneRun? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadRunAsync(runId);
            if (last.Status is not ("Queued" or "Running"))
            {
                return last;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"Run {runId:D} never reached a terminal state (last status: {last?.Status}).{Environment.NewLine}"
            + FormatDiagnostics());
        return last!;
    }

    /// <summary>Polls until the run's status is exactly <paramref name="expected"/>.</summary>
    public async Task WaitForRunStatusAsync(Guid runId, string expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TerminalTimeout);
        while (DateTime.UtcNow < deadline)
        {
            var run = await ReadRunAsync(runId);
            if (string.Equals(run.Status, expected, StringComparison.Ordinal))
            {
                return;
            }

            if (run.Status is not ("Queued" or "Running"))
            {
                Assert.Fail($"Run {runId:D} reached {run.Status}, not {expected}.{Environment.NewLine}{FormatDiagnostics()}");
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Run {runId:D} never reached {expected}.{Environment.NewLine}{FormatDiagnostics()}");
    }

    /// <summary>Polls until the step at <paramref name="index"/> reaches <paramref name="expected"/>.</summary>
    public async Task WaitForStepStatusAsync(Guid runId, int index, string expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TerminalTimeout);
        while (DateTime.UtcNow < deadline)
        {
            var steps = await ReadStepsAsync(runId);
            var step = steps.FirstOrDefault(candidate => candidate.Index == index);
            if (step is not null && string.Equals(step.Status, expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"Step {index} of run {runId:D} never reached {expected}.{Environment.NewLine}{FormatDiagnostics()}");
    }

    /// <summary>Polls until the predicate holds over the run's persisted log lines; returns the lines.</summary>
    public async Task<IReadOnlyList<MilestoneLogLine>> WaitForLogLinesAsync(
        Guid runId,
        Func<IReadOnlyList<MilestoneLogLine>, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TerminalTimeout);
        IReadOnlyList<MilestoneLogLine> last = [];
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadLogLinesAsync(runId);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"The log of run {runId:D} never satisfied the predicate (last: {last.Count} line(s)).{Environment.NewLine}"
            + FormatDiagnostics());
        return last;
    }

    /// <summary>Polls until the project has exactly <paramref name="expected"/> runs.</summary>
    public async Task<IReadOnlyList<MilestoneRun>> WaitForRunCountAsync(
        Guid projectId,
        int expected,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TerminalTimeout);
        IReadOnlyList<MilestoneRun> last = [];
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadProjectRunsAsync(projectId);
            if (last.Count >= expected)
            {
                return last;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"Project {projectId:D} never reached {expected} run(s) - it has {last.Count}.{Environment.NewLine}"
            + FormatDiagnostics());
        return last;
    }

    /// <summary>Waits until the project has at least one run, then returns it (the no-worker case).</summary>
    public async Task<MilestoneRun> WaitForAnyRunAsync(Guid projectId, TimeSpan? timeout = null) =>
        (await WaitForRunCountAsync(projectId, 1, timeout))[0];

    /// <summary>The worker/server output, attached to a failure so a broken run explains itself.</summary>
    public string FormatDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine("--- worker output ---");
        foreach (var line in WorkerOutput.TakeLast(60))
        {
            builder.AppendLine(line);
        }

        builder.AppendLine("--- server output (tail) ---");
        foreach (var line in ServerOutput.TakeLast(40))
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    // -------------------------------------------------------------------------------------------
    // SQLite plumbing
    // -------------------------------------------------------------------------------------------

    protected internal async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, Func<SqliteDataReader, T> map)
    {
        var lockDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                var rows = new List<T>();
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    rows.Add(map(reader));
                }

                return rows;
            }
            catch (SqliteException) when (DateTime.UtcNow < lockDeadline)
            {
                // The server holds a write lock; poll again.
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        var lockDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWrite");
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
                return;
            }
            catch (SqliteException) when (DateTime.UtcNow < lockDeadline)
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
        }
    }

    // -------------------------------------------------------------------------------------------
    // Process plumbing
    // -------------------------------------------------------------------------------------------

    protected async Task<string> WaitForServerAsync(CancellationToken cancellationToken)
    {
        var server = ServerProcess;
        if (server.HasExited)
        {
            Assert.Fail("The Server process exited immediately:" + Environment.NewLine + string.Join(
                Environment.NewLine, _serverLogs!.Snapshot()));
        }

        var listening = await _serverLogs!.WaitForLineAsync(
            line => line.Contains("Now listening on:", StringComparison.Ordinal),
            ServerStartTimeout,
            cancellationToken);
        var match = Regex.Match(listening, @"Now listening on: (?<url>http://\S+)");
        Assert.True(match.Success, $"Could not parse the listening URL from: {listening}");
        var url = match.Groups["url"].Value;

        using var probe = new HttpClient();
        var deadline = DateTime.UtcNow + ServerStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await probe.GetAsync(url, cancellationToken);
                return url;
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        Assert.Fail($"The Server did not answer at {url} within {ServerStartTimeout.TotalSeconds:0}s.");
        return url;
    }

    /// <summary>An Owner client over this host's shared cookie jar (so a restarted server keeps the session).</summary>
    protected HttpClient CreateOwnerClient(string serverUrl) =>
        new(new HttpClientHandler
        {
            CookieContainer = _ownerCookies,
            AllowAutoRedirect = false,
            UseCookies = true,
        })
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
        };

    private async Task<HttpClient> SignInOwnerAsync(string serverUrl)
    {
        var client = CreateOwnerClient(serverUrl);

        var response = await AuthTestHost.RegisterAsync(client, $"owner-{Guid.NewGuid():N}@example.test");
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Registering the Owner failed with {(int)response.StatusCode}.");
        return client;
    }

    protected internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zenkins.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the Zenkins repository root from the test output.");
    }

    protected internal static string FindBuiltBinary(string repositoryRoot, string binRelative, string fileName)
    {
        var binRoot = Path.Combine(repositoryRoot, binRelative);
        var releaseMarker = $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}";
        var candidates = Directory.EnumerateFiles(binRoot, fileName, SearchOption.AllDirectories)
            .Where(path => path.Contains(releaseMarker, StringComparison.Ordinal)
                && path.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // A self-contained publish (I-003's matrix, or the published smoke's own fixture) writes its
            // build output to bin/Release/net10.0/<rid>/ - a runtimeconfig that names no framework, which
            // `dotnet exec` cannot run on this machine. The suite drives the framework-dependent build,
            // so only candidates whose runtimeconfig names a framework qualify.
            .Where(NamesAFramework)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();

        Assert.True(
            candidates.Length > 0,
            $"No built '{fileName}' found under '{binRoot}' (Release). Run 'dotnet build -c Release' first - "
            + "the milestone suite runs the real binaries, it does not compile them. (A self-contained publish "
            + "under the same bin tree does not count: its runtimeconfig names no framework, so `dotnet exec` "
            + "cannot run it on this machine.)");
        return candidates[^1].FullName;
    }

    /// <summary>Whether the runtimeconfig.json next to an app DLL names a framework (framework-dependent).
    /// A self-contained publish's runtimeconfig lists <c>includedFrameworks</c> instead.</summary>
    private static bool NamesAFramework(string appDllPath)
    {
        var runtimeConfigPath = Path.Combine(
            Path.GetDirectoryName(appDllPath)!,
            Path.GetFileNameWithoutExtension(appDllPath) + ".runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
            var runtimeOptions = document.RootElement.GetProperty("runtimeOptions");
            return runtimeOptions.TryGetProperty("framework", out _)
                || runtimeOptions.TryGetProperty("frameworks", out _);
        }
        catch (Exception failure) when (failure is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The dotnet host that can <c>exec</c> an app DLL: under xunit v3 <see cref="Environment.ProcessPath"/>
    /// is the test host's own apphost, which does not understand <c>exec</c>.
    /// </summary>
    protected internal static string ResolveDotnetPath()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null
            && Path.GetFileName(processPath).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null && File.Exists(dotnetRoot))
        {
            return dotnetRoot;
        }

        try
        {
            var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var host = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", HostFileName()));
            if (File.Exists(host))
            {
                return host;
            }
        }
        catch (PlatformNotSupportedException)
        {
        }

        return HostFileName();
    }

    private static string HostFileName() => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    /// <summary>Collects a child's stdout/stderr lines (thread-safe) and waits for markers in them.</summary>
    internal sealed class ProcessLog : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<string> _lines = [];

        public ProcessLog(Process process)
        {
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    Add(args.Data);
                }
            };
            process.BeginOutputReadLine();
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    Add("[stderr] " + args.Data);
                }
            };
            process.BeginErrorReadLine();
        }

        public string[] Snapshot()
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }

        public async Task<string> WaitForLineAsync(
            Func<string, bool> predicate,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var match = Snapshot().FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException(
                $"Expected output never appeared within {timeout.TotalSeconds:0}s. Output so far:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, Snapshot()));
        }

        public void Dispose() => _lines.Clear();

        private void Add(string line)
        {
            lock (_lock)
            {
                _lines.Add(line);
            }
        }
    }
}
