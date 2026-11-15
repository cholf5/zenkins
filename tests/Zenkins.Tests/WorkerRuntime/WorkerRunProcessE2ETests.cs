using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Card F-007's process-level end-to-end: the <b>real Server</b> runs as its own process over a
/// throwaway SQLite file, the <b>real Worker</b> runs against it with the card's environment, and a
/// run triggered through the real project form is claimed and executed as a native process
/// (<c>dotnet exec ArgvEcho.dll echo-each …</c>). The test asserts the whole loop: the run turns
/// Success/Failed with the child's real exit code, the per-run work directory trio exists under the
/// worker's <c>ZENKINS_WORK_ROOT</c>, the captured output lands incrementally in the temp stream
/// logs while the child is still running, and <c>temp/</c> is cleaned while
/// <c>repository/</c> and <c>artifacts/</c> stay.
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkerRunProcessE2ETests
{
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WorkerRegisterTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunTerminalTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_triggered_run_is_claimed_executed_in_its_own_work_directory_and_reported()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var serverDll = FindBuiltBinary(root, "src/Server/bin", "Zenkins.Server.dll");
        var workerDll = FindBuiltBinary(root, "src/Worker/bin", "Zenkins.Worker.dll");
        var argvEchoDll = FindBuiltBinary(
            root, "tests/Zenkins.Tests/TestAssets/ArgvEcho/bin", "ArgvEcho.dll");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f007-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var databasePath = Path.Combine(tempRoot, "zenkins.db");
        var workerDataDir = Path.Combine(tempRoot, "worker-data");
        var workRoot = Path.Combine(tempRoot, "work");
        var workerName = $"e2e-run-{Guid.NewGuid():N}".ToLowerInvariant();

        var dotnet = ResolveDotnetPath();
        using var server = StartServer(dotnet, serverDll, root, databasePath);
        using var serverLogs = new ProcessLog(server);
        var serverUrl = await WaitForServerAsync(server, serverLogs, cancellationToken);

        try
        {
            using var owner = await SignInOwnerAsync(serverUrl, cancellationToken);
            var registrationToken = await MintRegistrationTokenAsync(owner, cancellationToken);

            // F-014: a run executes the project's pipeline from its Git repository, so the fixture is a
            // real repository (a clone the worker really performs) and the project row is pointed at it.
            // The 5s spacing keeps the temp stream file alive ~15s: the file dies with temp/ at the
            // cleanup-before-report edge, and a CI runner under load can starve this test's polls for
            // longer than a short file's whole lifetime (recorded red on windows while the run itself
            // was green) - the widest window the child can offer is part of the observation.
            using var repository = PipelineRepository.Create(
                PipelineTestData.Pipeline(
                    "e2e",
                    PipelineTestData.Step("Echo", "echo-each", "5000", "first", "second", "third")));
            var projectId = await CreateProjectAsync(owner, $"e2e-run-{Guid.NewGuid():N}".ToLowerInvariant());
            await SetProjectRepositoryAsync(databasePath, projectId, repository.FileUrl, cancellationToken);

            using var worker = StartWorker(
                dotnet, workerDll, serverUrl, registrationToken, workerName, workerDataDir, workRoot);
            using var workerLogs = new ProcessLog(worker);
            await workerLogs.WaitForLineAsync(
                line => line.Contains("online as worker", StringComparison.Ordinal),
                WorkerRegisterTimeout,
                cancellationToken);

            // ----- Run 1: slow echoed output, then exit 0 → Success. -----
            var run1 = await TriggerRunAsync(owner, projectId, cancellationToken);
            var work1 = Path.Combine(workRoot, run1.ToString("D"));

            // The temp stream files live from the run's pipeline start to the cleanup-before-report
            // edge, and the status wait reads the same loaded-runner database the server writes to
            // while it streams: its reads can stretch long enough to consume the file's whole
            // lifetime before the file wait takes its first look - recorded red on windows twice,
            // the run green both times. The two waits therefore run together, from the trigger: the
            // file wait covers the file's entire lifetime (the first echoed line on disk while the
            // child still has two 5s sleeps ahead of it is the incremental-capture property), and
            // stderr must exist inside the same window. The complete content and its order are
            // asserted from the database rows below, which persist.
            var stdoutPath = Path.Combine(work1, "temp", "stdout.log");
            var stderrPath = Path.Combine(work1, "temp", "stderr.log");
            try
            {
                await Task.WhenAll(
                    WaitForRunStatusAsync(databasePath, run1, "Running", RunTerminalTimeout, cancellationToken),
                    WaitForLogFileAsync(
                        stdoutPath,
                        content => ContainsInOrder(content, ["first"]) && File.Exists(stderrPath),
                        RunTerminalTimeout,
                        cancellationToken));
            }
            catch (TimeoutException failure)
            {
                // The temp files are created eagerly with the run's execution (RunLogWriters), so a
                // missing file means the worker never reached its pipeline - the state that matters
                // is the worker's own console and the run's terminal row, which no other failure
                // path of this test prints. Dump both, or the red stays undiagnosable in CI.
                throw new TimeoutException(
                    failure.Message
                    + Environment.NewLine
                    + await DescribeAsync(databasePath, run1, work1, worker, workerLogs, serverLogs, cancellationToken));
            }

            var (status1, exitCode1) = await WaitUntilTerminalAsync(databasePath, run1, cancellationToken);
            Assert.Equal("Success", status1);
            Assert.Equal(0, exitCode1);

            // F-008: the three lines were streamed upstream over SignalR and persisted
            // incrementally - the database holds them in Seq order by the time the run is terminal.
            // F-014 added the run's own step lines, so the assertion is on the echoed lines' order
            // (they must appear as the child produced them), not on the row count.
            var logRows = await WaitForLogRowsAsync(databasePath, run1, cancellationToken);
            var echoed = logRows
                .Where(row => row.Message is "first" or "second" or "third")
                .Select(row => row.Message)
                .ToArray();
            Assert.Equal(new[] { "first", "second", "third" }, echoed);
            Assert.Contains(logRows, row => row.Message.StartsWith("zenkins: step 1/1", StringComparison.Ordinal));

            // The trio exists per run; temp is cleaned (ZENKINS_CLEANUP_TEMP default true), the
            // repository and artifacts directories stay.
            AssertWorkDirectoryTrio(work1, tempCleaned: true);

            // ----- Run 2: exit code 3 → Failed, reported verbatim. -----
            // The branch moves: the next run must pick the new definition up from a fresh clone.
            repository.CommitPipeline(
                PipelineTestData.Pipeline("e2e-fail", PipelineTestData.Step("Build", "exit", "3")));
            var run2 = await TriggerRunAsync(owner, projectId, cancellationToken);
            var work2 = Path.Combine(workRoot, run2.ToString("D"));

            var (status2, exitCode2) = await WaitUntilTerminalAsync(databasePath, run2, cancellationToken);
            Assert.Equal("Failed", status2);
            Assert.Equal(3, exitCode2);
            AssertWorkDirectoryTrio(work2, tempCleaned: true);

            // The two runs never shared a writable directory.
            Assert.NotEqual(work1, work2);

            // Graceful stop: the worker exits 0 on SIGTERM (the killed-children path is F-009's).
            if (!OperatingSystem.IsWindows())
            {
                SendSigterm(worker);
                WaitForWorkerExit(worker, "The worker did not exit after SIGTERM within the shutdown timeout.");
                Assert.Equal(0, worker.ExitCode);
                var shutdownLines = workerLogs.Snapshot();
                Assert.Contains(shutdownLines, line => line.Contains("stopped cleanly", StringComparison.Ordinal));
            }
        }
        finally
        {
            TryStop(server);
            DeleteTempRoot(tempRoot);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Work directory and run assertions
    // ---------------------------------------------------------------------------------------------

    private static void AssertWorkDirectoryTrio(string workPath, bool tempCleaned)
    {
        Assert.True(Directory.Exists(Path.Combine(workPath, "repository")), $"repository/ missing in {workPath}");
        Assert.True(Directory.Exists(Path.Combine(workPath, "artifacts")), $"artifacts/ missing in {workPath}");
        if (tempCleaned)
        {
            Assert.False(Directory.Exists(Path.Combine(workPath, "temp")), $"temp/ survived cleanup in {workPath}");
        }
        else
        {
            Assert.True(Directory.Exists(Path.Combine(workPath, "temp")), $"temp/ missing in {workPath}");
        }
    }

    private static bool ContainsInOrder(string content, string[] expectedLines)
    {
        var lines = content.Split('\n');
        var index = 0;
        foreach (var line in lines)
        {
            if (index < expectedLines.Length && line.TrimEnd('\r') == expectedLines[index])
            {
                index++;
            }
        }

        return index == expectedLines.Length;
    }

    private static async Task WaitForLogFileAsync(
        string path,
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? lastContent = null;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    lastContent = await File.ReadAllTextAsync(path, cancellationToken);
                    if (predicate(lastContent))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // The writer holds the file; retry.
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException(
            $"The log file {path} never satisfied the predicate. Last content: {lastContent ?? "(file never appeared)"}");
    }

    /// <summary>
    /// Polls the server's SQLite file until the run is terminal and its log rows contain the three
    /// echoed lines, then returns every row in Seq order. F-014 added the run's own step lines, so the
    /// count is no longer fixed - the caller asserts on the lines it cares about.
    /// </summary>
    private static async Task<IReadOnlyList<(long Seq, string Stream, string Message)>> WaitForLogRowsAsync(
        string databasePath,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        IReadOnlyList<(long Seq, string Stream, string Message)> last = [];
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadLogRowsAsync(databasePath, runId, cancellationToken);
            if (last.Count >= 3
                && last.Count(row => row.Message is "first" or "second" or "third") == 3)
            {
                return last;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException(
            $"Run {runId} never persisted the echoed log rows (last seen: {last.Count}).");
    }

    private static async Task<IReadOnlyList<(long Seq, string Stream, string Message)>> ReadLogRowsAsync(
        string databasePath,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var lockRetryDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT \"Seq\", \"Stream\", \"Message\" FROM \"BuildLogs\" WHERE \"RunId\" = $id ORDER BY \"Seq\"";
                command.Parameters.AddWithValue("$id", runId.ToString().ToUpperInvariant());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var rows = new List<(long, string, string)>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
                }

                return rows;
            }
            catch (SqliteException) when (DateTime.UtcNow < lockRetryDeadline)
            {
                // The server may hold a write lock; poll again.
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    /// <summary>Polls the server's SQLite file for the run's (status, exit code) until terminal.</summary>
    private static async Task<(string Status, int? ExitCode)> WaitUntilTerminalAsync(
        string databasePath,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + RunTerminalTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var row = await ReadRunRowAsync(databasePath, runId, cancellationToken);
            if (row.Status is not ("Queued" or "Running"))
            {
                return row;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Run {runId} never reached a terminal state.");
    }

    /// <summary>Polls until the run reaches exactly the expected status (or the timeout fires).</summary>
    private static async Task WaitForRunStatusAsync(
        string databasePath,
        Guid runId,
        string expectedStatus,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var row = await ReadRunRowAsync(databasePath, runId, cancellationToken);
            if (row.Status == expectedStatus)
            {
                return;
            }

            if (row.Status is not ("Queued" or "Running"))
            {
                throw new InvalidOperationException(
                    $"Run {runId} reached {row.Status} instead of the expected {expectedStatus}.");
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Run {runId} never reached {expectedStatus} within the timeout.");
    }

    /// <summary>
    /// Everything the CI log needs when a phase of this test times out: the run's row (a Failed row
    /// means the worker reported; a Running row means it never did) with its stored start/end
    /// timestamps - the run's real duration is what separates "the child never ran long" from "the
    /// test never got scheduled" -, the work directory's shape (temp/ dies at the
    /// cleanup-before-report edge, so its absence at diagnosis time is itself a timestamp), the
    /// persisted log-row count, the worker process's liveness, the worker's whole console (its
    /// claim, accept and pipeline lines are the story of where it stopped) and the server's last
    /// console lines. The E2E redirects both processes' output, so none of this reaches the CI log
    /// any other way.
    /// </summary>
    private static async Task<string> DescribeAsync(
        string databasePath,
        Guid runId,
        string workPath,
        Process worker,
        ProcessLog workerLogs,
        ProcessLog serverLogs,
        CancellationToken cancellationToken)
    {
        string runRow;
        try
        {
            var (status, exitCode, startedAt, endedAt) =
                await ReadRunRowForDiagnosisAsync(databasePath, runId, cancellationToken);
            var elapsed = startedAt is { } start && endedAt is { } end
                ? $" ({(end - start).TotalSeconds:0.#}s)"
                : string.Empty;
            runRow = $"status={status}, exitCode={(exitCode?.ToString() ?? "null")}"
                + $", startedAt={startedAt?.ToString("O") ?? "null"}, endedAt={endedAt?.ToString("O") ?? "null"}{elapsed}";
        }
        catch (Exception failure)
        {
            runRow = $"unreadable ({failure.GetType().Name}: {failure.Message})";
        }

        string logRows;
        try
        {
            var (count, minSeq, maxSeq) = await ReadLogRowSummaryAsync(databasePath, runId, cancellationToken);
            logRows = $"count={count}, seq=[{minSeq}..{maxSeq}]";
        }
        catch (Exception failure)
        {
            logRows = $"unreadable ({failure.GetType().Name}: {failure.Message})";
        }

        var workerState = worker.HasExited ? $"exited with {worker.ExitCode}" : "still running";

        return $"--- E2E diagnosis: run row: {runRow}; worker process: {workerState}."
            + Environment.NewLine + $"--- persisted log rows: {logRows}."
            + Environment.NewLine + $"--- work directory: {DescribeTree(workPath)}"
            + Environment.NewLine + "--- worker console:"
            + Environment.NewLine + Indent(workerLogs.Snapshot())
            + "--- server console (last 40 lines):"
            + Environment.NewLine + Indent(Tail(serverLogs.Snapshot(), 40));
    }

    /// <summary>The run row's status, exit code and stored start/end timestamps.</summary>
    private static async Task<(string? Status, int? ExitCode, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt)>
        ReadRunRowForDiagnosisAsync(string databasePath, Guid runId, CancellationToken cancellationToken)
    {
        var lockRetryDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT \"Status\", \"ExitCode\", \"StartedAt\", \"EndedAt\" FROM \"BuildRuns\" WHERE \"Id\" = $id";
                command.Parameters.AddWithValue("$id", runId.ToString().ToUpperInvariant());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    return (
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2),
                        reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3));
                }

                return (null, null, null, null);
            }
            catch (SqliteException) when (DateTime.UtcNow < lockRetryDeadline)
            {
                // The server may hold a write lock; poll again.
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    /// <summary>How many log rows the run persisted, and their sequence span.</summary>
    private static async Task<(long Count, long MinSeq, long MaxSeq)> ReadLogRowSummaryAsync(
        string databasePath,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var lockRetryDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*), MIN(\"Seq\"), MAX(\"Seq\") FROM \"BuildLogs\" WHERE \"RunId\" = $id";
                command.Parameters.AddWithValue("$id", runId.ToString().ToUpperInvariant());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
            }
            catch (SqliteException) when (DateTime.UtcNow < lockRetryDeadline)
            {
                // The server may hold a write lock; poll again.
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    /// <summary>The work directory's top-level shape, with temp/'s files when it survived.</summary>
    private static string DescribeTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return "(the run's work directory does not exist)";
        }

        var lines = new List<string>();
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            lines.Add(entry is DirectoryInfo directory
                ? $"{entry.Name}/ ({Directory.GetFiles(entry.FullName, "*", SearchOption.AllDirectories).Length} files inside)"
                : $"{entry.Name} ({((FileInfo)entry).Length} bytes)");
        }

        var temp = Path.Combine(root, "temp");
        if (Directory.Exists(temp))
        {
            lines.Add("temp/ holds: " + string.Join(", ", Directory.EnumerateFiles(temp).Select(Path.GetFileName)));
        }

        return lines.Count > 0 ? string.Join("; ", lines) : "(empty)";
    }

    private static string Indent(string[] lines) =>
        string.Join(Environment.NewLine, lines.Select(line => "    " + line));

    private static string[] Tail(string[] lines, int count) =>
        lines.Length <= count ? lines : lines[^count..];

    private static async Task<(string Status, int? ExitCode)> ReadRunRowAsync(
        string databasePath,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var lockRetryDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT \"Status\", \"ExitCode\" FROM \"BuildRuns\" WHERE \"Id\" = $id";
                command.Parameters.AddWithValue("$id", runId.ToString().ToUpperInvariant());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    return (
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetInt32(1));
                }

                throw new InvalidOperationException($"No BuildRuns row for {runId} exists.");
            }
            catch (SqliteException) when (DateTime.UtcNow < lockRetryDeadline)
            {
                // The server may hold a write lock; poll again.
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Real-HTTP product surfaces (project create + run trigger through the actual forms)
    // ---------------------------------------------------------------------------------------------

    private static async Task<Guid> CreateProjectAsync(HttpClient owner, string name)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["RepositoryUrl"] = "https://example.test/e2e-run.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "",
            });
        Assert.True((int)response.StatusCode is 302 or 303,
            $"Creating the project failed with {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }

    /// <summary>Points an existing project row at the local repository fixture (F-014).</summary>
    private static async Task SetProjectRepositoryAsync(
        string databasePath,
        Guid projectId,
        string repositoryUrl,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE \"Projects\" SET \"RepositoryUrl\" = $url WHERE \"Id\" = $id";
        command.Parameters.AddWithValue("$url", repositoryUrl);
        command.Parameters.AddWithValue("$id", projectId.ToString().ToUpperInvariant());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task<Guid> TriggerRunAsync(
        HttpClient owner,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/runs/start",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Branch"] = string.Empty,
                ["RequiredTags"] = string.Empty,
            });
        Assert.True((int)response.StatusCode is 302 or 303,
            $"Triggering the run failed with {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/runs/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/runs/".Length..]);
    }

    private static async Task<HttpClient> SignInOwnerAsync(string serverUrl, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
            UseCookies = true,
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };

        var response = await AuthTestHost.RegisterAsync(client, $"owner-{Guid.NewGuid():N}@example.test");
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Registering the owner failed with {(int)response.StatusCode}.");
        return client;
    }

    private static async Task<string> MintRegistrationTokenAsync(HttpClient owner, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var (token, response) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return token;
    }

    // ---------------------------------------------------------------------------------------------
    // Process plumbing (the F-005 E2E pattern, extended with ZENKINS_WORK_ROOT)
    // ---------------------------------------------------------------------------------------------

    private static Process StartServer(string dotnetPath, string serverDll, string repoRoot, string databasePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetPath,
            WorkingDirectory = Path.Combine(repoRoot, "src", "Server"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(serverDll);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        startInfo.Environment["Database__Provider"] = "Sqlite";
        startInfo.Environment["ConnectionStrings__Default"] = $"Data Source={databasePath}";
        startInfo.Environment["Database__ApplyMigrationsOnStartup"] = "true";

        // F-016: the artifact store root follows this test's temp database, never src/Server/App_Data/artifacts
        // (the working directory below is the repository's server project).
        startInfo.Environment["Artifacts__RootPath"] = Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            $"zenkins-f016-store-{Guid.NewGuid():N}");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Server process could not be started.");
        return process;
    }

    private static async Task<string> WaitForServerAsync(Process server, ProcessLog logs, CancellationToken cancellationToken)
    {
        if (server.HasExited)
        {
            throw new InvalidOperationException(
                "The Server process exited immediately. Output: " + string.Join(Environment.NewLine, logs.Snapshot()));
        }

        var listeningLine = await logs.WaitForLineAsync(
            line => line.Contains("Now listening on:", StringComparison.Ordinal),
            ServerStartTimeout,
            cancellationToken);
        var url = Regex.Match(listeningLine, @"Now listening on: (?<url>http://\S+)");
        Assert.True(url.Success, $"Could not parse the listening URL from: {listeningLine}");

        // Poll until the server answers, so the first form request cannot race the pipeline.
        using var probe = new HttpClient();
        var deadline = DateTime.UtcNow + ServerStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await probe.GetAsync(url.Groups["url"].Value, cancellationToken);
                return url.Groups["url"].Value;
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        throw new TimeoutException($"The Server did not answer within {ServerStartTimeout.TotalSeconds}s.");
    }

    private static void WaitForWorkerExit(Process worker, string failureMessage)
    {
        Assert.True(worker.WaitForExit(ShutdownTimeout), failureMessage);
        worker.WaitForExit();
    }

    private static Process StartWorker(
        string dotnetPath,
        string workerDll,
        string serverUrl,
        string registrationToken,
        string workerName,
        string workerDataDir,
        string workRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(workerDll);
        startInfo.Environment["ZENKINS_SERVER_URL"] = serverUrl;
        startInfo.Environment["ZENKINS_REGISTRATION_TOKEN"] = registrationToken;
        startInfo.Environment["ZENKINS_WORKER_NAME"] = workerName;
        startInfo.Environment["ZENKINS_WORKER_TAGS"] = "e2e,ci";
        startInfo.Environment["ZENKINS_HEARTBEAT_SECONDS"] = "1";
        startInfo.Environment["ZENKINS_DATA_DIR"] = workerDataDir;
        startInfo.Environment["ZENKINS_WORK_ROOT"] = workRoot;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Worker process could not be started.");
    }

    /// <summary>Sends SIGTERM through the `kill` utility (the graceful-stop signal).</summary>
    private static void SendSigterm(Process process)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill", $"-s TERM {process.Id}")
        {
            UseShellExecute = false,
        });
        kill?.WaitForExit(5_000);
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(5_000);
        }
        catch (Exception failure) when (failure is InvalidOperationException or SystemException)
        {
        }
    }

    private static void DeleteTempRoot(string tempRoot)
    {
        // Q-011's cleanup contract via TestCleanup: the temp root holds the worker's cloned
        // repositories, whose pack and object files are read-only (a Windows delete reports
        // UnauthorizedAccessException for them), and a surviving root is reported, never silent.
        TestCleanup.DeleteTree(tempRoot);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zenkins.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the Zenkins repository root from the test output.");
    }

    private static string FindBuiltBinary(string repoRoot, string binRelative, string fileName)
    {
        var binRoot = Path.Combine(repoRoot, binRelative);
        var releaseMarker = $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}";
        var candidates = Directory.EnumerateFiles(binRoot, fileName, SearchOption.AllDirectories)
            .Where(path => path.Contains(releaseMarker, StringComparison.Ordinal)
                && path.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains("ref", StringComparison.Ordinal))
            // A self-contained publish (I-003) writes its build output to bin/Release/net10.0/<rid>/ -
            // a runtimeconfig that names no framework, which `dotnet exec` cannot run here. The E2E
            // drives the framework-dependent build, so only those candidates qualify.
            .Where(NamesAFramework)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();

        Assert.True(
            candidates.Length > 0,
            $"No built '{fileName}' found under '{binRoot}' (Release). Run 'dotnet build -c Release' before the E2E. "
            + "(A self-contained publish under the same bin tree does not count: its runtimeconfig names no framework.)");
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
    /// The dotnet host that can `exec` an app DLL. <see cref="Environment.ProcessPath"/> is the test
    /// host's apphost under xunit v3 (which does not understand `exec`), so fall back to DOTNET_ROOT
    /// and the shared runtime location before the PATH.
    /// </summary>
    private static string ResolveDotnetPath()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null
            && Path.GetFileName(processPath).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetPathExists(dotnetRoot))
        {
            return dotnetRoot!;
        }

        try
        {
            // <root>/shared/Microsoft.NETCore.App/<version> → the host sits at <root>/dotnet.
            var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var host = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", GetHostFileName()));
            if (File.Exists(host))
            {
                return host;
            }
        }
        catch (PlatformNotSupportedException)
        {
        }

        return GetHostFileName();
    }

    private static string GetHostFileName() =>
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    private static bool dotnetPathExists(string? path) =>
        path is not null && File.Exists(path);

    /// <summary>Collects a process's stdout lines (thread-safe) and waits for markers in them.</summary>
    private sealed class ProcessLog : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<string> _lines = [];

        public ProcessLog(Process process)
        {
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (_lock)
                    {
                        _lines.Add(args.Data);
                    }
                }
            };
            process.BeginOutputReadLine();
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (_lock)
                    {
                        _lines.Add("[stderr] " + args.Data);
                    }
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

        public void Dispose()
        {
        }
    }
}
