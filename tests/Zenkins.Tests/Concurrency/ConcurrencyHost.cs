using System.Net;
using System.Net.Http;
using System.Diagnostics;
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

    private ConcurrencyHost(string rootPath, string dotnetPath, string workerDll)
        : base(rootPath, dotnetPath, workerDll)
    {
    }

    /// <summary>Starts the real server with Q-004's compressed timing and signs the Owner in.</summary>
    public static new async Task<ConcurrencyHost> StartAsync(MilestoneHostOptions? options = null)
    {
        options ??= new MilestoneHostOptions(
            SweepIntervalSeconds: 1,
            OfflineAfterSeconds: 5,
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
    public async Task<RecoveryWorker> StartWorkerAsync(string name, string tags, int maxConcurrency)
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
        startInfo.Environment["ZENKINS_SERVER_URL"] = ServerUrl;
        startInfo.Environment["ZENKINS_REGISTRATION_TOKEN"] = registrationToken;
        startInfo.Environment["ZENKINS_WORKER_NAME"] = name;
        startInfo.Environment["ZENKINS_WORKER_TAGS"] = tags;
        startInfo.Environment["ZENKINS_WORKER_CONCURRENCY"] = maxConcurrency.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["ZENKINS_HEARTBEAT_SECONDS"] = "1";
        startInfo.Environment["ZENKINS_CLAIM_SECONDS"] = "1";
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

        Assert.Contains(
            owner.Output,
            line => line.Contains($"run {run.Id:D} executing in", StringComparison.Ordinal));
        foreach (var other in otherWorkers)
        {
            Assert.DoesNotContain(
                other.Output,
                line => line.Contains($"run {run.Id:D} executing in", StringComparison.Ordinal));
        }

        var lines = await ReadLogLinesAsync(run.Id);
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
        var lines = await ReadLogLinesAsync(runId);
        Assert.All(
            lines,
            line => Assert.True(
                line.StepId is null || stepIds.Contains(line.StepId.Value),
                $"a log line of run {runId:D} is attributed to a step the run has no row for."));
    }
}
