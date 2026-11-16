using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text;
using Xunit;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Recovery;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.Concurrency;

/// <summary>One worker row, with the capability field Q-005's ceiling assertions read.</summary>
internal sealed record ConcurrencyWorkerRow(
    Guid Id,
    string Name,
    string Status,
    string Tags,
    int MaxConcurrency,
    DateTimeOffset? LastHeartbeatAt);

/// <summary>
/// Q-005's multi-worker ring: Q-004's recovery harness (a <b>real Server</b> process over a throwaway
/// SQLite file, <b>real Worker</b> executables, compressed F-010 timing driven by the real
/// <c>WorkerOfflineMonitor</c>) widened from "several workers over a lifetime" to "several workers
/// <b>online at the same time</b>". Each started worker declares its own tags and
/// <c>maxConcurrency</c> exactly the way a real operator would - through the worker's environment -
/// and every temporary belongs to this instance, killed and deleted by
/// <see cref="MilestoneHost.Dispose"/> as a tree.
/// <para>
/// <b>Nothing product-internal is used.</b> Runs are triggered through the real trigger form (with or
/// without required tags), claims are read through real worker processes' own behavior plus probe
/// workers' direct <c>/api/workers/claim</c> answers, and every state assertion is a read-only query
/// against the rows the server itself wrote.
/// </para>
/// </summary>
internal sealed class ConcurrencyHost : RecoveryHost
{
    private static readonly TimeSpan WorkerOnlineTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The compressed F-010 offline window the whole ring runs under: <b>15 s</b>, not the 5 s Q-005
    /// first picked (Q-020).
    /// <para>
    /// <b>Why 5 s was not enough, and it is not a load problem.</b> A worker sends one heartbeat per
    /// <see cref="HeartbeatSeconds"/> and, on a transport failure,
    /// <c>WorkerRunner.HeartbeatLoopAsync</c> backs off 1 s, 2 s, 4 s ... (±25 % jitter, reset on
    /// success), while a single request may sit for its 10 s timeout before the loop even learns it
    /// failed. A 5 s window is therefore five skipped beats: one stalled request already consumes
    /// most of it, three consecutive failures exceed it, and a two-core runner hosting the server,
    /// three workers and the test host over SQLite's single writer (three claim polls a second) has
    /// potholes that size. Windows CI proved the arithmetic in the only way that counts - three of
    /// twelve runs `Lost` with `worker-offline`, no worker dead, every one of them heartbeating again
    /// afterwards.
    /// </para>
    /// <para>
    /// <b>Why 15 s is enough.</b> Two full request timeouts plus a beat fit inside it, so a stall has
    /// to be a real one (the card's reproduction needed 6 s of a fully suspended process to lose a
    /// run; with this window the same suspension is silent, and 15 s of it would not be). The cost is
    /// wall-clock: a genuinely dead worker's row takes 10 s longer to flip Offline, which only
    /// <c>ConcurrencyTagAssignmentTests</c>' killed-worker wait pays, inside its own timeout. The
    /// margin is <b>proved</b>, not assumed - see
    /// <c>ConcurrencyOfflineWindowTests.A_stalled_worker_inside_the_window_loses_no_run</c>.
    /// </para>
    /// </summary>
    private const int OfflineWindowSeconds = 15;

    /// <summary>
    /// The heartbeat period every ring worker is started with (<c>ZENKINS_HEARTBEAT_SECONDS</c>); 1 s
    /// is the finest the worker accepts. It stays at 1 s deliberately: the window above is the knob
    /// Q-020 moves, so the ratio the offline decision runs on gets <b>wider</b> rather than the
    /// traffic between three workers and one SQLite writer getting denser.
    /// </summary>
    private const int HeartbeatSeconds = 1;

    /// <summary>The claim poll period every ring worker is started with.</summary>
    private const int ClaimSeconds = 1;

    /// <summary>The offline window in seconds, for the facts that reason about it.</summary>
    public static int OfflineWindow => OfflineWindowSeconds;

    private ConcurrencyHost(string rootPath, string dotnetPath, string workerDll)
        : base(rootPath, dotnetPath, workerDll)
    {
    }

    /// <summary>Starts the real server with Q-004's compressed timing and signs the Owner in.</summary>
    public static new async Task<ConcurrencyHost> StartAsync(MilestoneHostOptions? options = null)
    {
        options ??= new MilestoneHostOptions(
            SweepIntervalSeconds: 1,
            OfflineAfterSeconds: OfflineWindowSeconds,
            WorkerMonitorIntervalSeconds: 1);
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-q005-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var repositoryRoot = FindRepositoryRoot();
        var dotnet = ResolveDotnetPath();
        var serverDll = FindBuiltBinary(repositoryRoot, Path.Combine("src", "Server", "bin"), "Zenkins.Server.dll");
        var workerDll = FindBuiltBinary(repositoryRoot, Path.Combine("src", "Worker", "bin"), "Zenkins.Worker.dll");

        var host = new ConcurrencyHost(root, dotnet, workerDll);
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
    // The ring: workers with their own declared capabilities
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts one real worker process under a name, tag set and declared concurrency the caller
    /// chooses - the environment an operator sets, so the server registers exactly the capabilities
    /// the test intends (<c>ZENKINS_WORKER_TAGS</c>, <c>ZENKINS_WORKER_CONCURRENCY</c>). The single-use
    /// registration token is minted through the Owner's real form, and the process is a member of the
    /// ring: it stays online (heartbeating every second) until dispose kills it.
    /// </summary>
    public async Task<RecoveryWorker> StartWorkerAsync(
        string name,
        string tags,
        int maxConcurrency,
        string? serverUrl = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDirectory = Path.Combine(RootPath, $"worker-data-{name}");
        var workRoot = Path.Combine(RootPath, $"work-{name}");

        // (Assert.Fail inside the catch never returns, but definite assignment needs the initializer.)
        string registrationToken = string.Empty;
        try
        {
            (registrationToken, var mintResponse) = await WorkerTestHost.MintRegistrationTokenAsync(Owner);
            Assert.True(
                mintResponse.StatusCode is HttpStatusCode.OK,
                $"Minting a registration token for worker '{name}' failed with {(int)mintResponse.StatusCode}.");
        }
        catch (Exception failure)
        {
            Assert.Fail(
                $"minting a registration token for worker '{name}' failed: {failure.Message}"
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
        startInfo.Environment["ZENKINS_SERVER_URL"] = serverUrl ?? ServerUrl;
        startInfo.Environment["ZENKINS_REGISTRATION_TOKEN"] = registrationToken;
        startInfo.Environment["ZENKINS_WORKER_NAME"] = name;
        startInfo.Environment["ZENKINS_WORKER_TAGS"] = tags;
        startInfo.Environment["ZENKINS_WORKER_CONCURRENCY"] = maxConcurrency.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["ZENKINS_HEARTBEAT_SECONDS"] = HeartbeatSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["ZENKINS_CLAIM_SECONDS"] = ClaimSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["ZENKINS_DATA_DIR"] = dataDirectory;
        startInfo.Environment["ZENKINS_WORK_ROOT"] = workRoot;

        var worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The Worker process '{name}' could not be started.");
        var logs = new MilestoneHost.ProcessLog(worker);
        TrackWorker(worker, logs);
        await logs.WaitForLineAsync(
            line => line.Contains("online as worker", StringComparison.Ordinal),
            WorkerOnlineTimeout,
            cancellationToken);

        var started = new RecoveryWorker(worker, logs, name, dataDirectory, workRoot, worker.Id);
        return started;
    }

    /// <summary>
    /// Registers a probe worker row with the tags a scenario needs (the same real register endpoint a
    /// worker process uses) and returns its plaintext token. The probe lets a test poll
    /// <c>/api/workers/claim</c> itself and read the product's verdict first-hand - the 204 a
    /// non-matching worker gets while a tag-restricted run waits. The probe never executes anything.
    /// </summary>
    public async Task<string> RegisterTaggedProbeWorkerAsync(string name, string tags)
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
                tags: tags,
                cpuCount: 1,
                maxConcurrency: 1));
        Assert.True(
            (int)response.StatusCode is 200,
            $"registering probe worker '{name}' failed with {(int)response.StatusCode}.");
        return await WorkerTestHost.ReadStringPropertyAsync(response, "workerToken");
    }

    // -------------------------------------------------------------------------------------------
    // Triggering (with and without required tags)
    // -------------------------------------------------------------------------------------------

    /// <summary>POSTs the real trigger form, with or without required tags, and returns the raw answer.</summary>
    public async Task<HttpResponseMessage> PostTriggerFormAsync(Guid projectId, string requiredTags, string branch = "")
    {
        return await OrganizationTestHost.PostFormAsync(
            Owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/runs/start",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Branch"] = branch,
                ["RequiredTags"] = requiredTags,
            });
    }

    /// <summary>
    /// Triggers a manual run through the real trigger form (the <c>RequiredTags</c> field is the
    /// product's own) and returns the new run's id. A validation failure is a failed test, not a
    /// silently skipped scenario.
    /// </summary>
    public async Task<Guid> TriggerTaggedRunAsync(Guid projectId, string requiredTags, string branch = "")
    {
        var response = await PostTriggerFormAsync(projectId, requiredTags, branch);
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Triggering the run (required tags '{requiredTags}') failed with {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
        var location = response.Headers.Location!.OriginalString;
        Assert.DoesNotContain("runError=", location, StringComparison.Ordinal);
        Assert.StartsWith("/runs/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/runs/".Length..].Split('?')[0]);
    }

    // -------------------------------------------------------------------------------------------
    // The stalled heartbeat (Q-020): a live worker whose heartbeat stops arriving, and the evidence
    // a failure carries
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Waits until the server's stored last heartbeat for <paramref name="name"/> is at least
    /// <paramref name="age"/> old, and returns the age it measured. While the worker's traffic is
    /// gated (<see cref="HeartbeatGate"/>) the age only grows, so the wait is a <b>measurement</b> of
    /// how long the heartbeat has been silent, not a sleep: the fact can claim "older than the window
    /// Q-020 replaced" from the server's own row instead of from the clock it started.
    /// </summary>
    public async Task<TimeSpan> WaitForHeartbeatAgeAsync(string name, TimeSpan age, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var row = await ReadConcurrencyWorkerRowAsync(name);
            var measured = row.LastHeartbeatAt is { } last
                ? DateTimeOffset.UtcNow - last
                : TimeSpan.Zero;
            if (measured >= age)
            {
                return measured;
            }

            Assert.True(
                DateTime.UtcNow < deadline,
                $"worker '{name}' never went {age.TotalSeconds:0.#}s without a heartbeat "
                + $"(last one {measured.TotalSeconds:0.#}s ago, status {row.Status}).{Environment.NewLine}"
                + FormatDiagnostics());
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Polls until at least one of <paramref name="runIds"/> is <c>Running</c> and returns that run,
    /// so a scenario can act on the worker that really holds work (Q-020). With
    /// <paramref name="workerId"/> the wait is for a run <b>that worker</b> holds. The wait is on the
    /// runs' own rows; a run that has already finished is never waited for twice.
    /// </summary>
    public async Task<MilestoneRun> WaitForAnyRunningAsync(
        IReadOnlyCollection<Guid> runIds,
        TimeSpan? timeout = null,
        Guid? workerId = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        while (DateTime.UtcNow < deadline)
        {
            var list = string.Join(", ", runIds.Select(id => $"'{id.ToString("D").ToUpperInvariant()}'"));
            var running = await ReadAsync(
                "SELECT \"Id\", \"ProjectId\", \"Status\", \"TriggerType\", \"WorkerId\", \"ExitCode\", "
                + "\"FailureReason\", \"GitCommit\", \"PipelineSpecJson\", \"EndedAt\", \"ParametersJson\" "
                + $"FROM \"BuildRuns\" WHERE \"Status\" = 'Running' AND \"Id\" IN ({list})",
                reader => new MilestoneRun(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetDateTimeOffset(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            if (running.Count > 0 && (workerId is null || running.Any(run => run.WorkerId == workerId)))
            {
                return running.First(run => workerId is null || run.WorkerId == workerId);
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            "no run was ever Running"
            + (workerId is { } id ? $" on worker {id:D}" : string.Empty)
            + $" among the {runIds.Count} triggered, so no worker held work to stall."
            + Environment.NewLine + FormatDiagnostics());
        return null!;
    }

    /// <summary>
    /// The ring's own output, one worker at a time, with the server's row for each - what a failure
    /// message needs to be diagnosable. Q-020 exists because the CI log named the terminal state
    /// (<c>Lost</c>, <c>worker-offline</c>) and nothing else: the reason was in the workers' output and
    /// the rows (which one stopped heartbeating, what it was doing when it did).
    /// </summary>
    public async Task<string> FormatWorkerTailsAsync(IEnumerable<RecoveryWorker> workers, int lines = 40)
    {
        var rows = await ReadAsync(
            "SELECT \"Name\", \"Status\", \"LastHeartbeatAt\" FROM \"Workers\"",
            reader => (
                Name: reader.GetString(0),
                Status: reader.GetString(1),
                LastHeartbeat: reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(2)));

        var builder = new StringBuilder();
        foreach (var worker in workers)
        {
            var row = rows.FirstOrDefault(candidate => candidate.Name == worker.Name);
            var serverSide = row.Name is null
                ? "no server row"
                : $"server row {row.Status}, last heartbeat "
                  + (row.LastHeartbeat is { } beat
                      ? $"{(DateTimeOffset.UtcNow - beat).TotalSeconds:0.#}s ago"
                      : "never");
            builder.AppendLine(
                $"--- worker '{worker.Name}' (pid {worker.ProcessId}), {serverSide} - stdout/stderr tail ---");
            var output = worker.Output;
            foreach (var line in output.Length <= lines ? output : output[^lines..])
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    // -------------------------------------------------------------------------------------------
    // Observable state: rows and counts the ceiling and attribution assertions read
    // -------------------------------------------------------------------------------------------

    /// <summary>Reads one worker row by the name it registered under, capabilities included.</summary>
    public async Task<ConcurrencyWorkerRow> ReadConcurrencyWorkerRowAsync(string name)
    {
        var rows = await ReadAsync(
            "SELECT \"Id\", \"Name\", \"Status\", \"Tags\", \"MaxConcurrency\", \"LastHeartbeatAt\" FROM \"Workers\" "
            + $"WHERE \"Name\" = '{name}'",
            reader => new ConcurrencyWorkerRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5)));
        Assert.True(
            rows.Count == 1,
            $"expected exactly one Workers row for '{name}', found {rows.Count}.{Environment.NewLine}"
            + FormatDiagnostics());
        return rows[0];
    }

    /// <summary>How many <c>Running</c> runs each worker id holds, in one read.</summary>
    public Task<IReadOnlyList<(Guid WorkerId, int Count)>> ReadRunningCountsAsync() =>
        ReadAsync(
            "SELECT \"WorkerId\", COUNT(*) FROM \"BuildRuns\" "
            + "WHERE \"Status\" = 'Running' AND \"WorkerId\" IS NOT NULL GROUP BY \"WorkerId\"",
            reader => (WorkerId: Guid.Parse(reader.GetString(0)), Count: reader.GetInt32(1)));

    /// <summary>
    /// How many runs each worker id is assigned overall (<c>Running</c> plus the claimed-but-not-yet-
    /// accepted leases, which the claim slot check counts too) - what "never holds more than its
    /// declared concurrency" means between the claim and the accept.
    /// </summary>
    public Task<IReadOnlyList<(Guid WorkerId, int Count)>> ReadAssignedCountsAsync() =>
        ReadAsync(
            "SELECT \"WorkerId\", COUNT(*) FROM \"BuildRuns\" "
            + "WHERE \"Status\" IN ('Running', 'Queued') AND \"WorkerId\" IS NOT NULL GROUP BY \"WorkerId\"",
            reader => (WorkerId: Guid.Parse(reader.GetString(0)), Count: reader.GetInt32(1)));

    /// <summary>The statuses of exactly the given runs, in one read.</summary>
    public Task<IReadOnlyList<(Guid Id, string Status)>> ReadRunStatusesAsync(IReadOnlyCollection<Guid> runIds)
    {
        var list = string.Join(", ", runIds.Select(id => $"'{id.ToString("D").ToUpperInvariant()}'"));
        return ReadAsync(
            "SELECT \"Id\", \"Status\" FROM \"BuildRuns\" WHERE \"Id\" IN (" + list + ")",
            reader => (Id: Guid.Parse(reader.GetString(0)), Status: reader.GetString(1)));
    }

    // -------------------------------------------------------------------------------------------
    // Attribution and exactly-once (shared by the concurrency scenarios)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The executed-once attribution proof for one finished run, from three independent records:
    /// <list type="number">
    /// <item>the run row names exactly one worker - the one <paramref name="owner"/> registered as;</item>
    /// <item>the owner's own process output claims the execution, and <b>no other ring member's
    /// output ever mentions the run</b> (a second execution would have to come from a process);</item>
    /// <item>the run's persisted log names the owner's worker identity (the pipeline echoes
    /// <c>ZENKINS_WORKER_NAME</c>, which the worker injects) and no other worker's.</item>
    /// </list>
    /// </summary>
    public async Task AssertRunExecutedByExactlyOneWorkerAsync(
        MilestoneRun run,
        RecoveryWorker owner,
        IReadOnlyList<RecoveryWorker> otherWorkers)
    {
        Assert.NotNull(run.WorkerId);
        var ownerRow = await ReadConcurrencyWorkerRowAsync(owner.Name);
        Assert.Equal(ownerRow.Id, run.WorkerId);

        // The worker's console reaches this test through ProcessLog's asynchronous line events, and
        // on a loaded runner those events queue behind everything else the threadpool owes - the
        // capture arrives late, it is never wrong (the worker's whole output fits the pipe buffer,
        // so the worker itself never blocks). The owner's claim of the execution is therefore
        // waited for, not sampled once: the windows runner recorded a green run whose rows and log
        // both named this worker while its console snapshot still stopped at "online as worker".
        var executedLine = $"run {run.Id:D} executing in";
        var claimDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!owner.Output.Any(line => line.Contains(executedLine, StringComparison.Ordinal)))
        {
            Assert.True(
                DateTime.UtcNow < claimDeadline,
                $"the owner worker '{owner.Name}' never reported executing run {run.Id:D}.");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        foreach (var other in otherWorkers)
        {
            Assert.DoesNotContain(
                other.Output,
                line => line.Contains($"run {run.Id:D} executing in", StringComparison.Ordinal));
        }

        // The drained log lines land around the terminal report; they are waited for too.
        var lines = await WaitForLogLinesAsync(
            run.Id,
            waited => waited.Any(line => line.Message.Contains(owner.Name, StringComparison.Ordinal)));
        Assert.Contains(lines, line => line.Message.Contains(owner.Name, StringComparison.Ordinal));
        foreach (var other in otherWorkers)
        {
            Assert.DoesNotContain(lines, line => line.Message.Contains(other.Name, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// One run's step record is one uncorrupted sequence: exactly the declared steps, one row per
    /// position in declaration order, each a successful exit - and every persisted log line is
    /// attributed to one of those rows (or to the definition phase, which precedes the first step).
    /// </summary>
    public async Task AssertRunStepsAreOneUncorruptedSequenceAsync(
        Guid runId,
        IReadOnlyList<string> declaredNames)
    {
        var steps = await ReadStepsAsync(runId);
        Assert.Equal(declaredNames.Count, steps.Count);
        for (var index = 0; index < steps.Count; index++)
        {
            Assert.Equal(index + 1, steps[index].Index);
            Assert.Equal(declaredNames[index], steps[index].Name);
            Assert.Equal("Success", steps[index].Status);
            Assert.Equal(0, steps[index].ExitCode);
        }

        Assert.Equal(steps.Count, steps.Select(step => step.Id).Distinct().Count());

        var stepIds = steps.Select(step => step.Id).ToHashSet();
        // F-008's drain lands the lines around the terminal report; the wait anchors on the last
        // declared step's start line so the attribution check never runs over an undrained log.
        var lines = await WaitForLogLinesAsync(
            runId,
            waited => waited.Any(line => line.Message.StartsWith(
                $"zenkins: step {declaredNames.Count}/",
                StringComparison.Ordinal)));
        Assert.All(
            lines,
            line => Assert.True(
                line.StepId is null || stepIds.Contains(line.StepId.Value),
                $"a log line of run {runId:D} is attributed to a step the run has no row for."));
    }
}
