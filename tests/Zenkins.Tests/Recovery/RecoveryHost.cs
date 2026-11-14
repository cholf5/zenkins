using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Xunit;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.Recovery;

/// <summary>One worker row, as the server's own <c>Workers</c> table holds it.</summary>
internal sealed record RecoveryWorkerRow(
    Guid Id,
    string Name,
    string Status,
    string Tags,
    DateTimeOffset? LastHeartbeatAt);

/// <summary>
/// One real worker process Q-004 started, with everything the recovery scenarios need to kill it,
/// restart it, prove it was a live process, and read what it wrote: its own captured output and its
/// persisted token (the file <c>WorkerTokenStore</c> writes under the worker's data directory - the
/// same file a genuinely restarted worker would load, and the credential a late report must present).
/// </summary>
internal sealed record RecoveryWorker(
    Process Process,
    MilestoneHost.ProcessLog Logs,
    string Name,
    string DataDirectory,
    string WorkRoot,
    int ProcessId)
{
    /// <summary>This worker's own stdout/stderr lines (never another worker's).</summary>
    public string[] Output => Logs.Snapshot();
}

/// <summary>
/// Q-004's recovery harness: Q-002's process launcher (a <b>real Server</b> over a throwaway SQLite
/// file, a <b>real Worker</b> executable, read-only assertions on rows, HTTP responses, files and
/// process ids) extended with the two abilities recovery needs - the <b>server can be stopped and
/// started again</b> over the same database, and <b>workers can be killed and restarted</b> as
/// processes. Nothing product-internal is used; every temporary belongs to this instance and
/// <see cref="MilestoneHost.Dispose"/> kills every child as a tree and deletes the root, including
/// mid-test kills (killed workers stay tracked so dispose can never leak one).
/// <para>
/// <b>Time is compressed, not faked.</b> The host starts the server with a one-second sweep interval
/// and a five-second offline threshold (production defaults to sixty), so the real
/// <c>LeaseSweeper</c> and the real <c>WorkerOfflineMonitor</c> drive every transition; no test
/// ever forces a state change. The threshold leaves the "still Running" observation window more
/// than a second of margin even on a stalled machine: a killed worker's run cannot flip before the
/// worker's last heartbeat is five seconds stale, and no sample runs that late.
/// </para>
/// </summary>
internal class RecoveryHost : MilestoneHost
{
    private static readonly TimeSpan WorkerOnlineTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan GraceObservation = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(3);

    private readonly List<RecoveryWorker> _recoveryWorkers = [];

    protected RecoveryHost(string rootPath, string dotnetPath, string workerDll)
        : base(rootPath, dotnetPath, workerDll)
    {
    }

    /// <summary>Every worker process this host started (first, killed, restarted).</summary>
    public IReadOnlyList<RecoveryWorker> Workers => _recoveryWorkers;

    /// <summary>Starts the real server with Q-004's compressed timing and signs the Owner in.</summary>
    public static new async Task<RecoveryHost> StartAsync(MilestoneHostOptions? options = null)
    {
        options ??= new MilestoneHostOptions(
            SweepIntervalSeconds: 1,
            OfflineAfterSeconds: 5,
            WorkerMonitorIntervalSeconds: 1);
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-q004-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var repositoryRoot = FindRepositoryRoot();
        var dotnet = ResolveDotnetPath();
        var serverDll = FindBuiltBinary(repositoryRoot, Path.Combine("src", "Server", "bin"), "Zenkins.Server.dll");
        var workerDll = FindBuiltBinary(repositoryRoot, Path.Combine("src", "Worker", "bin"), "Zenkins.Worker.dll");

        var host = new RecoveryHost(root, dotnet, workerDll);
        try
        {
            await host.StartCoreAsync(repositoryRoot, serverDll, options);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    // -------------------------------------------------------------------------------------------
    // Workers: start, kill, restart, and the observable facts about them
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts one real worker process against this host, under its own data directory and work root
    /// (so a kill leaves its predecessor's token and work tree exactly where they were, and a restart
    /// with the same directories exercises the product's real load-persisted-token reconnect path).
    /// Every start mints a fresh registration token through the Owner's real form - registration
    /// tokens are single-use, so a second worker cannot reuse the first one's. Unlike the milestone
    /// one-worker-per-host start, a recovery host may hold several workers over its lifetime.
    /// </summary>
    public new async Task<RecoveryWorker> StartWorkerAsync(
        string? name = null,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workerName = name ?? $"recovery-{Guid.NewGuid():N}".ToLowerInvariant();
        var dataDirectory = Path.Combine(RootPath, $"worker-data-{workerName}");
        var workRoot = Path.Combine(RootPath, $"work-{workerName}");

        // (Assert.Fail inside the catch never returns, but definite assignment needs the initializer.)
        string registrationToken = string.Empty;
        try
        {
            (registrationToken, var mintResponse) = await WorkerTestHost.MintRegistrationTokenAsync(Owner);
            Assert.True(
                mintResponse.StatusCode is HttpStatusCode.OK,
                $"Minting a registration token for worker '{workerName}' failed with {(int)mintResponse.StatusCode}.");
        }
        catch (Exception failure)
        {
            // The server output is the only witness to why it is unreachable - attach it.
            Assert.Fail(
                $"minting a registration token for worker '{workerName}' failed: {failure.Message}"
                + Environment.NewLine + FormatDiagnostics());
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(WorkerDllPath);
        startInfo.Environment["ZENKINS_SERVER_URL"] = ServerUrl;
        startInfo.Environment["ZENKINS_REGISTRATION_TOKEN"] = registrationToken;
        startInfo.Environment["ZENKINS_WORKER_NAME"] = workerName;
        startInfo.Environment["ZENKINS_WORKER_TAGS"] = "recovery,ci";
        startInfo.Environment["ZENKINS_HEARTBEAT_SECONDS"] = "1";
        startInfo.Environment["ZENKINS_CLAIM_SECONDS"] = "1";
        startInfo.Environment["ZENKINS_DATA_DIR"] = dataDirectory;
        startInfo.Environment["ZENKINS_WORK_ROOT"] = workRoot;

        // Card F-018's seam (whatever a future test needs: an isolated HOME, a certificate authority).
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
            ?? throw new InvalidOperationException($"The Worker process '{workerName}' could not be started.");
        var logs = new MilestoneHost.ProcessLog(worker);
        TrackWorker(worker, logs);
        await logs.WaitForLineAsync(
            line => line.Contains("online as worker", StringComparison.Ordinal),
            WorkerOnlineTimeout,
            cancellationToken);

        var started = new RecoveryWorker(worker, logs, workerName, dataDirectory, workRoot, worker.Id);
        _recoveryWorkers.Add(started);
        return started;
    }

    /// <summary>
    /// Kills a worker's whole process tree and waits for the exit. The process stays tracked, so a
    /// failing test can never leak it: dispose simply finds it already gone.
    /// </summary>
    public void KillWorker(RecoveryWorker worker) => Kill(worker.Process);

    /// <summary>
    /// Whether the worker's process id is really in the operating system's process table right now -
    /// an independent lookup, not the handle this host keeps.
    /// </summary>
    public static bool IsProcessAlive(int processId)
    {
        try
        {
            using var lookup = Process.GetProcessById(processId);
            return !lookup.HasExited;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Waits (bounded) until a process id has really left the operating system's process table.</summary>
    public static async Task WaitForProcessGoneAsync(int processId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline && IsProcessAlive(processId))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.False(
            IsProcessAlive(processId),
            $"process {processId} is still alive - the kill did not take (or a tree member leaked).");
    }

    /// <summary>
    /// Samples the real-process facts <b>while the run executes</b> (the Q-002 pattern, for one named
    /// worker): it waits (bounded) until the run has left the queue - a claim stamps <c>WorkerId</c>,
    /// and acceptance starts the clock - and then applies every check of
    /// <see cref="AssertWorkerRunsAsItsOwnLiveProcessAsync(RecoveryWorker, Guid?)"/>.
    /// </summary>
    public async Task<RecoveryWorkerRow> SampleWorkerDuringRunAsync(RecoveryWorker worker, Guid runId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if ((await ReadRunAsync(runId)).Status != "Queued")
            {
                return await AssertWorkerRunsAsItsOwnLiveProcessAsync(worker, runId);
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"run {runId:D} never left the queue, so no execution-time sample was possible.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// The Q-002 real-process claim, for one named worker: the worker is not this test process and not
    /// the server, the process table really holds its pid (with the start instant of the handle this
    /// host started, so a recycled pid cannot stand in), and the server's own <c>Workers</c> row for
    /// the name it registered under is <c>Online</c> - and, when a run is given, the row this run is
    /// assigned to.
    /// </summary>
    public async Task<RecoveryWorkerRow> AssertWorkerRunsAsItsOwnLiveProcessAsync(
        RecoveryWorker worker,
        Guid? runId = null)
    {
        Assert.NotEqual(Environment.ProcessId, worker.ProcessId);
        Assert.NotEqual(Environment.ProcessId, ServerProcessId);
        Assert.NotEqual(worker.ProcessId, ServerProcessId);

        DateTime trackedStart;
        try
        {
            trackedStart = worker.Process.StartTime.ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            Assert.Fail($"worker '{worker.Name}' (pid {worker.ProcessId}) has already exited - it was never a live process.");
            throw;
        }

        using var lookedUp = LookUpProcess(worker.ProcessId);
        Assert.False(lookedUp.HasExited, $"worker '{worker.Name}' (pid {worker.ProcessId}) is not alive.");
        Assert.Equal(trackedStart, lookedUp.StartTime.ToUniversalTime());
        Assert.Contains("dotnet", lookedUp.ProcessName, StringComparison.OrdinalIgnoreCase);

        var row = await ReadWorkerRowAsync(worker.Name);
        Assert.Equal("Online", row.Status);
        Assert.Equal("recovery,ci", row.Tags);
        Assert.Equal(ExpectedWorkerOs(), await ReadWorkerOsAsync(worker.Name));

        if (runId is { } id)
        {
            var run = await ReadRunAsync(id);
            Assert.Equal(row.Id, run.WorkerId);
        }

        return row;
    }

    /// <summary>
    /// The worker token <c>WorkerTokenStore</c> persisted under this worker's data directory - the
    /// credential the killed worker would have presented, read from the observable file it wrote (its
    /// own data directory is this host's temporary root). One file per data directory; anything else
    /// is a harness bug.
    /// </summary>
    public string ReadPersistedWorkerToken(RecoveryWorker worker)
    {
        var files = Directory.EnumerateFiles(worker.DataDirectory, "worker.token", SearchOption.AllDirectories)
            .ToArray();
        Assert.True(
            files.Length == 1,
            $"expected exactly one persisted token under '{worker.DataDirectory}', found {files.Length}.");
        var token = File.ReadAllText(files[0]).Trim();
        Assert.StartsWith("zwrk_", token, StringComparison.Ordinal);
        return token;
    }

    // -------------------------------------------------------------------------------------------
    // The probe worker: the claim endpoint exercised as a product surface
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Registers a probe worker row through the real worker API (the same register endpoint a real
    /// worker process uses) and returns its plaintext worker token. The probe lets a test POST
    /// <c>/api/workers/claim</c> itself and read the product's answer directly - the evidence "this
    /// claim got nothing" that a real worker process can only show indirectly. The probe never
    /// executes anything; it holds no runs.
    /// </summary>
    public async Task<string> RegisterProbeWorkerAsync(string name)
    {
        var (registrationToken, mintResponse) = await WorkerTestHost.MintRegistrationTokenAsync(Owner);
        Assert.Equal(HttpStatusCode.OK, mintResponse.StatusCode);

        using var anonymous = new HttpClient { BaseAddress = new Uri(ServerUrl.TrimEnd('/') + "/") };
        var response = await WorkerTestHost.RegisterAsync(
            anonymous,
            registrationToken,
            WorkerTestHost.RegistrationPayload(
                name: name,
                os: ExpectedWorkerOs(),
                tags: "recovery,probe",
                cpuCount: 1,
                maxConcurrency: 1));
        Assert.True(
            (int)response.StatusCode is 200,
            $"registering probe worker '{name}' failed with {(int)response.StatusCode}.");
        return await WorkerTestHost.ReadStringPropertyAsync(response, "workerToken");
    }

    /// <summary>
    /// One heartbeat + one claim from a probe worker: the heartbeat keeps the row Online exactly the
    /// way a real worker's does (a stale row is refused before the queue is even read), and the claim
    /// answer is the product's own verdict on "is there something for a second worker".
    /// </summary>
    public async Task<HttpResponseMessage> ProbeClaimAsync(string probeWorkerToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ServerUrl.TrimEnd('/') + "/") };
        var heartbeat = await WorkerTestHost.HeartbeatAsync(
            client,
            probeWorkerToken,
            WorkerTestHost.HeartbeatPayload());
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        return await WorkerTestHost.SendWorkerJsonAsync(
            client,
            probeWorkerToken,
            "/api/workers/claim",
            new { });
    }

    /// <summary>Asserts one probe claim got nothing: the endpoint's 204, no body, no run.</summary>
    public static void AssertClaimGotNothing(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    // -------------------------------------------------------------------------------------------
    // Bounded observation windows
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Samples the run for a bounded window, asserting it holds <paramref name="expected"/> at every
    /// sample (the grace period: a killed worker's run must stay <c>Running</c> until the threshold
    /// expires, not flip the instant the process died).
    /// </summary>
    public async Task AssertRunHoldsStatusAsync(
        Guid runId,
        string expected,
        TimeSpan? window,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + (window ?? GraceObservation);
        while (DateTime.UtcNow < deadline)
        {
            var run = await ReadRunAsync(runId);
            Assert.Equal(expected, run.Status);
            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>
    /// The settle window (card AC): for a few seconds the run is sampled repeatedly and must be in
    /// exactly one of the given states at every sample - a Lost run must never become Success, Failed
    /// or Queued afterwards, and a terminal run must never leave its state. A single read could pass
    /// by luck; a window cannot.
    /// </summary>
    public async Task AssertRunSettlesInAsync(
        Guid runId,
        CancellationToken cancellationToken,
        params string[] allowedStates)
    {
        var deadline = DateTime.UtcNow + SettleWindow;
        while (DateTime.UtcNow < deadline)
        {
            var run = await ReadRunAsync(runId);
            Assert.Contains(run.Status, allowedStates);
            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>
    /// Samples for a bounded window, asserting the predicate holds at every sample (the "nothing
    /// changed afterwards" windows: no new run, no new step, no claim).
    /// </summary>
    public async Task AssertHoldsAsync(
        Func<Task> assertion,
        TimeSpan? window,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + (window ?? SettleWindow);
        while (DateTime.UtcNow < deadline)
        {
            await assertion();
            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>Waits until the worker row for <paramref name="name"/> is in <paramref name="status"/>.</summary>
    public async Task WaitForWorkerStatusAsync(
        string name,
        string status,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var row = await TryReadWorkerRowAsync(name);
            if (row is { } found && string.Equals(found.Status, status, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        Assert.Fail($"worker '{name}' never reached status '{status}'.{Environment.NewLine}{FormatDiagnostics()}");
    }

    // -------------------------------------------------------------------------------------------
    // Worker report surface (the late-report replay)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// POSTs a run report exactly the way a worker would: bearer worker token, JSON body, no cookies.
    /// Used to replay a killed worker's terminal report - the same payload its completion path would
    /// have sent - against the endpoint that owns run outcomes.
    /// </summary>
    public async Task<HttpResponseMessage> ReportCompletedAsync(
        string workerToken,
        Guid runId,
        int? exitCode,
        string? failureReason)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ServerUrl.TrimEnd('/') + "/") };
        return await WorkerTestHost.SendWorkerJsonAsync(
            client,
            workerToken,
            $"/api/workers/runs/{runId}/completed",
            new { exitCode, failureReason });
    }

    // -------------------------------------------------------------------------------------------
    // Observable state: workers table
    // -------------------------------------------------------------------------------------------

    /// <summary>Reads one worker row by the name it registered under.</summary>
    public async Task<RecoveryWorkerRow> ReadWorkerRowAsync(string name)
    {
        var row = await TryReadWorkerRowAsync(name);
        Assert.True(row is not null, $"no Workers row for '{name}'.{Environment.NewLine}{FormatDiagnostics()}");
        return row!;
    }

    private async Task<RecoveryWorkerRow?> TryReadWorkerRowAsync(string name)
    {
        var rows = await ReadAsync(
            "SELECT \"Id\", \"Name\", \"Status\", \"Tags\", \"LastHeartbeatAt\" FROM \"Workers\" "
            + $"WHERE \"Name\" = '{name}'",
            reader => new RecoveryWorkerRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDateTimeOffset(4)));
        return rows.Count == 0 ? null : Assert.Single(rows);
    }

    private async Task<string> ReadWorkerOsAsync(string name)
    {
        var rows = await ReadAsync(
            "SELECT \"Os\" FROM \"Workers\" " + $"WHERE \"Name\" = '{name}'",
            reader => reader.GetString(0));
        return Assert.Single(rows);
    }
}
