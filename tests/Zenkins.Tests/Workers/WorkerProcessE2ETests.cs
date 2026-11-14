using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Xunit;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Card F-005's process-level end-to-end: the <b>real Server binary</b> runs as its own process on a
/// random port over a throwaway SQLite file, the <b>real Worker binary</b> is spawned against it
/// with the card's environment variables, and the test asserts the whole loop - registration with an
/// Owner-minted token, at least one accepted heartbeat, restrictive token-file permissions, and a
/// graceful SIGINT/SIGTERM stop with exit code 0.
/// <para>
/// Repeatability: random port (the server picks one and the test reads it from the startup log),
/// fresh temporary database and data directory per run, a unique worker name per run, and full
/// teardown in <c>finally</c>. The graceful-stop assert runs on POSIX only (where
/// <see cref="Process.Kill"/> sends SIGTERM); on Windows the functional asserts run and the exit-code
/// assert is skipped, because TerminateProcess has no graceful path.
/// </para>
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkerProcessE2ETests
{
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WorkerRegisterTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    [Fact(Timeout = 4 * 60 * 1000)]
    public async Task The_worker_process_registers_heartbeats_and_shuts_down_gracefully()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var serverDll = FindBuiltBinary(root, "src/Server/bin", "Zenkins.Server.dll");
        var workerDll = FindBuiltBinary(root, "src/Worker/bin", "Zenkins.Worker.dll");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f005-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var databasePath = Path.Combine(tempRoot, "zenkins.db");
        var workerDataDir = Path.Combine(tempRoot, "worker-data");
        var workerName = $"e2e-agent-{Guid.NewGuid():N}".ToLowerInvariant();

        var dotnet = ResolveDotnetPath();
        using var server = StartServer(dotnet, serverDll, root, databasePath);
        using var serverLogs = new ProcessLog(server);
        var serverUrl = await WaitForServerAsync(server, serverLogs, cancellationToken);

        try
        {
            // Bootstrap through the real first-run form, then mint a registration token through the
            // real Owner form - the same product paths a human would drive.
            using var owner = await SignInOwnerAsync(serverUrl, cancellationToken);
            var registrationToken = await MintRegistrationTokenAsync(owner, cancellationToken);

            using var worker = StartWorker(dotnet, workerDll, serverUrl, registrationToken, workerName, workerDataDir);
            using var workerLogs = new ProcessLog(worker);

            // Registration: the worker prints what it is doing and the server assigns an id.
            await workerLogs.WaitForLineAsync(
                line => line.Contains("registered", StringComparison.Ordinal),
                WorkerRegisterTimeout,
                cancellationToken);
            Assert.StartsWith("zreg_", registrationToken, StringComparison.Ordinal);

            // At least one accepted heartbeat: the "online" line is only printed after the server
            // answered 200 to the first heartbeat.
            await workerLogs.WaitForLineAsync(
                line => line.Contains("online as worker", StringComparison.Ordinal),
                HeartbeatTimeout,
                cancellationToken);

            // Server-side state, read straight from the server's database file.
            var row = await ReadWorkerRowAsync(databasePath, workerName, cancellationToken);
            Assert.NotNull(row.LastHeartbeatAt);
            Assert.Equal("Online", row.Status);

            // The worker is visible on the /workers page of the Owner's organization.
            var page = await owner.GetStringAsync("/workers", cancellationToken);
            Assert.Contains(workerName, page, StringComparison.Ordinal);
            Assert.Contains("Online", page, StringComparison.Ordinal);

            // The worker persisted its credential: a worker token in a private file.
            var persisted = FindTokenFile(workerDataDir);
            Assert.True(File.Exists(persisted), $"Expected a persisted worker token under {workerDataDir}.");
            Assert.StartsWith(
                Zenkins.Worker.WorkerTokenStore.TokenPrefix,
                await File.ReadAllTextAsync(persisted, cancellationToken),
                StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite,
                    File.GetUnixFileMode(persisted!));
            }

            // Graceful stop: SIGTERM (via the `kill` utility - Process.Kill sends SIGKILL on this
            // platform and cannot be used for a graceful path) → exit code 0.
            if (!OperatingSystem.IsWindows())
            {
                SendSigterm(worker);
                WaitForWorkerExit(worker, "The worker did not exit after SIGTERM within the shutdown timeout.");
                Assert.Equal(0, worker.ExitCode);

                // The final (best-effort) heartbeat attempt is logged before a clean stop.
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

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_worker_with_invalid_configuration_fails_fast_with_a_clear_message()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var workerDll = FindBuiltBinary(root, "src/Worker/bin", "Zenkins.Worker.dll");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f005-badcfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            using var worker = StartWorker(ResolveDotnetPath(), workerDll, serverUrl: null, registrationToken: null, workerName: "bad-cfg", workerDataDir: tempRoot);
            using var workerLogs = new ProcessLog(worker);

            WaitForWorkerExit(worker, "The worker ignored the bad configuration and kept running.");

            // C (second review fix): configuration errors stay on code 2 - only a definitive
            // server rejection of the registration itself exits with 3.
            Assert.Equal(Zenkins.Worker.WorkerRunner.ConfigurationExitCode, worker.ExitCode);

            var lines = workerLogs.Snapshot();
            Assert.Contains(lines, line => line.Contains("ZENKINS_SERVER_URL", StringComparison.Ordinal));
            Assert.Single(lines, line => line.Contains("configuration error", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_400_on_register_is_reported_once_and_exits_without_retrying()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var workerDll = FindBuiltBinary(root, "src/Worker/bin", "Zenkins.Worker.dll");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f005-rej400-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        // B1 (review fix): a stub that answers 400 to /api/workers/register - a definitive server
        // rejection. The worker must report it once on stderr and exit non-zero, not map it to a
        // transport failure and retry forever ("cannot reach ... the server answered 400").
        using var stub = TcpWorkerStub.RejectingWith(400);
        try
        {
            using var worker = StartWorker(
                ResolveDotnetPath(), workerDll, stub.Url, "zreg_definitely-rejected-token", "rej400-agent", tempRoot);
            using var workerLogs = new ProcessLog(worker);

            WaitForWorkerExit(worker, "The worker kept running against a definitive 400 rejection instead of exiting.");

            // C (second review fix): a permanently rejected registration exits with the dedicated
            // code 3 - distinct from a configuration error (2).
            Assert.Equal(Zenkins.Worker.WorkerRunner.RegistrationRejectedExitCode, worker.ExitCode);

            var lines = workerLogs.Snapshot();
            Assert.Single(lines, line => line.Contains("the server rejected", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("400", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("Retrying", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task A_409_on_register_is_retried_and_the_worker_goes_online_once_the_conflict_clears()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var workerDll = FindBuiltBinary(root, "src/Worker/bin", "Zenkins.Worker.dll");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f005-conflict409-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        // A (second review fix): a 409 name-duplicate is what the loser of a same-name first-start
        // race receives while the winner's row commits (WorkerEndpoints.cs maps it to 409), so it
        // must be retried, never treated as a definitive rejection. The stub answers 409 twice and
        // then serves the real register/heartbeat contract; the worker must stay alive, log the
        // backoff retries, register on a later attempt and go online.
        using var stub = TcpWorkerStub.ConflictingRegistersThenSucceeds(2);
        try
        {
            using var worker = StartWorker(
                ResolveDotnetPath(), workerDll, stub.Url, "zreg_conflicting-token", "conflict-agent", tempRoot);
            using var workerLogs = new ProcessLog(worker);

            await workerLogs.WaitForLineAsync(
                line => line.Contains("Retrying", StringComparison.Ordinal)
                    && line.Contains("409", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                cancellationToken);
            Assert.False(worker.HasExited, "The worker treated a retryable 409 as a definitive rejection and exited.");

            // The retry succeeds: registered and heartbeating against the stub's contract.
            await workerLogs.WaitForLineAsync(
                line => line.Contains("online as worker", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                cancellationToken);

            // Still a healthy worker: graceful stop, exit 0.
            if (!OperatingSystem.IsWindows())
            {
                SendSigterm(worker);
                WaitForWorkerExit(worker, "The worker did not exit after SIGTERM within the shutdown timeout.");
                Assert.Equal(0, worker.ExitCode);
            }
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task A_server_that_never_answers_times_out_and_retries_instead_of_crashing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var workerDll = FindBuiltBinary(root, "src/Worker/bin", "Zenkins.Worker.dll");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f005-blackhole-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        // F2 (review fix): the stub accepts the connection but never answers - the worker's
        // per-request timeout must fire, be treated as a transport failure (backoff + retry), and
        // the process must survive a SIGTERM afterwards with exit code 0 (F-005 AC-3).
        using var stub = TcpWorkerStub.BlackHole();
        try
        {
            using var worker = StartWorker(
                ResolveDotnetPath(), workerDll, stub.Url, "zreg_black-hole-token", "black-hole-agent", tempRoot);
            using var workerLogs = new ProcessLog(worker);

            await workerLogs.WaitForLineAsync(
                line => line.Contains("request timeout", StringComparison.Ordinal),
                TimeSpan.FromSeconds(40),
                cancellationToken);
            Assert.False(worker.HasExited, "The worker crashed on the first request timeout.");

            // A second timeout proves the loop is alive and still retrying with backoff.
            var secondTimeoutDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            while (DateTime.UtcNow < secondTimeoutDeadline && !worker.HasExited
                && workerLogs.Snapshot().Count(line => line.Contains("request timeout", StringComparison.Ordinal)) < 2)
            {
                await Task.Delay(250, cancellationToken);
            }

            var timeoutLines = workerLogs.Snapshot()
                .Count(line => line.Contains("request timeout", StringComparison.Ordinal));
            Assert.True(
                timeoutLines >= 2,
                $"Expected at least two timeout retries, got {timeoutLines}. Output:\n"
                + string.Join(Environment.NewLine, workerLogs.Snapshot()));
            Assert.False(worker.HasExited, "The worker died while retrying the black hole.");

            // Graceful stop on SIGTERM: exit code 0 within the shutdown timeout.
            if (!OperatingSystem.IsWindows())
            {
                SendSigterm(worker);
                WaitForWorkerExit(worker, "The worker did not exit after SIGTERM within the shutdown timeout.");
                Assert.Equal(0, worker.ExitCode);
            }
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Process plumbing
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A raw TCP stand-in for the Server, deliberately below the HTTP stack so no ASP.NET host is
    /// needed. It accepts the worker's connection and then either answers every request with a
    /// fixed status code (a deterministic server rejection: B1's 400-on-register) or never
    /// answers at all (a black hole whose only exit is the worker's per-request timeout: F2).
    /// </summary>
    private sealed class TcpWorkerStub : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<TcpClient> _heldClients = [];
        private readonly bool _blackHole;
        private readonly int _statusCode;
        private readonly int _conflictsBeforeSuccess;
        private int _served;

        public string Url { get; }

        private TcpWorkerStub(bool blackHole, int statusCode, int conflictsBeforeSuccess = 0)
        {
            _blackHole = blackHole;
            _statusCode = statusCode;
            _conflictsBeforeSuccess = conflictsBeforeSuccess;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        public static TcpWorkerStub RejectingWith(int statusCode) => new(false, statusCode);

        /// <summary>
        /// Answers the first <paramref name="conflicts"/> requests with 409 name-duplicate, then
        /// serves the real register/heartbeat contract (A, second review fix: the loser of a
        /// same-name first-start race must get in once the winner's row is committed).
        /// </summary>
        public static TcpWorkerStub ConflictingRegistersThenSucceeds(int conflicts) =>
            new(false, 409, conflictsBeforeSuccess: conflicts);

        public static TcpWorkerStub BlackHole() => new(true, 0);

        private async Task AcceptLoopAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (Exception failure)
                    when (failure is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                if (_blackHole)
                {
                    // Accepted on purpose, never answered: the request must die on the worker's
                    // per-request timeout, not on a refused connection.
                    lock (_heldClients)
                    {
                        _heldClients.Add(client);
                    }

                    continue;
                }

                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private const string RegistrationResponseBody =
            "{\"workerId\":\"stub-worker-1\",\"workerToken\":\"zwrk_stub-token-for-tests\",\"workerName\":\"conflict-agent\"}";

        private const string HeartbeatResponseBody =
            "{\"workerId\":\"stub-worker-1\",\"lastHeartbeatAt\":\"2026-11-13T12:00:00Z\"}";

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();

                    // Drain the full request (head + body) before answering: closing a socket with
                    // unread bytes sends RST, which can destroy the response in flight.
                    var path = await DrainRequestAsync(stream, _stopping.Token);

                    var (statusCode, body) = DecideResponse(path);
                    var reason = statusCode == 200 ? "OK" : "Rejected";
                    var bodyBytes = System.Text.Encoding.ASCII.GetBytes(body);
                    var head =
                        $"HTTP/1.1 {statusCode} {reason}\r\n"
                        + "Content-Type: application/json\r\n"
                        + $"Content-Length: {bodyBytes.Length}\r\n"
                        + "Connection: close\r\n"
                        + "\r\n";
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head), _stopping.Token);
                    await stream.WriteAsync(bodyBytes, _stopping.Token);
                    await stream.FlushAsync(_stopping.Token);
                }
                catch (Exception failure)
                    when (failure is IOException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }
        }

        /// <summary>Fixed-status mode (definitive rejection), or 409 N times then the real contract.</summary>
        private (int StatusCode, string Body) DecideResponse(string path)
        {
            if (_conflictsBeforeSuccess > 0)
            {
                var served = Interlocked.Increment(ref _served);
                if (served <= _conflictsBeforeSuccess)
                {
                    return (_statusCode, "{\"error\":\"name-duplicate\"}");
                }

                return path.Contains("/register", StringComparison.Ordinal)
                    ? (200, RegistrationResponseBody)
                    : (200, HeartbeatResponseBody);
            }

            return (_statusCode, "{\"error\":\"field-invalid\"}");
        }

        private static async Task<string> DrainRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var received = new MemoryStream();
            var buffer = new byte[2048];
            var headerEnd = -1;
            while (headerEnd < 0 && received.Length < 64 * 1024)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return string.Empty;
                }

                await received.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                headerEnd = FindHeaderEnd(received.ToArray());
            }

            if (headerEnd < 0)
            {
                return string.Empty;
            }

            var requestHead = System.Text.Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd);
            var requestLine = requestHead.Split("\r\n")[0];
            var path = requestLine.Split(' ') is { Length: > 1 } parts ? parts[1] : string.Empty;
            var contentLength = 0;
            foreach (var line in requestHead.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["Content-Length:".Length..].Trim(), out var parsed))
                {
                    contentLength = parsed;
                }
            }

            var bodyRemaining = contentLength - ((int)received.Length - headerEnd - 4);
            while (bodyRemaining > 0)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return path;
                }

                bodyRemaining -= read;
            }

            return path;
        }

        private static int FindHeaderEnd(byte[] bytes)
        {
            for (var i = 0; i + 3 < bytes.Length; i++)
            {
                if (bytes[i] == 0x0D && bytes[i + 1] == 0x0A && bytes[i + 2] == 0x0D && bytes[i + 3] == 0x0A)
                {
                    return i;
                }
            }

            return -1;
        }

        public void Dispose()
        {
            _stopping.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (Exception failure) when (failure is SocketException or ObjectDisposedException)
            {
            }

            lock (_heldClients)
            {
                foreach (var client in _heldClients)
                {
                    try
                    {
                        client.Close();
                    }
                    catch (Exception failure) when (failure is SocketException or ObjectDisposedException)
                    {
                    }
                }

                _heldClients.Clear();
            }

            _stopping.Dispose();
        }
    }

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

    /// <summary>
    /// Waits for the worker process to exit and then flushes its redirected output. The timed
    /// <see cref="Process.WaitForExit(int)"/> overload can return while the asynchronous output
    /// readers are still draining the pipes (documented Process behaviour); the parameterless
    /// overload after it blocks until they are done. Without the flush, a fast-exiting worker
    /// (bad configuration, definitive rejection) can already be gone when the assertions read the
    /// log - an empty snapshot under load, an intermittent failure that has nothing to do with
    /// the behaviour under test.
    /// </summary>
    private static void WaitForWorkerExit(Process worker, string failureMessage)
    {
        Assert.True(worker.WaitForExit(ShutdownTimeout), failureMessage);
        worker.WaitForExit();
    }

    private static Process StartWorker(
        string dotnetPath,
        string workerDll,
        string? serverUrl,
        string? registrationToken,
        string workerName,
        string workerDataDir)
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
        if (serverUrl is not null)
        {
            startInfo.Environment["ZENKINS_SERVER_URL"] = serverUrl;
        }

        if (registrationToken is not null)
        {
            startInfo.Environment["ZENKINS_REGISTRATION_TOKEN"] = registrationToken;
        }

        startInfo.Environment["ZENKINS_WORKER_NAME"] = workerName;
        startInfo.Environment["ZENKINS_WORKER_TAGS"] = "e2e,ci";
        startInfo.Environment["ZENKINS_HEARTBEAT_SECONDS"] = "1";
        startInfo.Environment["ZENKINS_DATA_DIR"] = workerDataDir;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Worker process could not be started.");
    }

    private static async Task<HttpClient> SignInOwnerAsync(string serverUrl, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var cookies = new CookieContainer();

        // No auto-redirect: the register helper asserts the POST's own 302/303, like the in-memory
        // hosts do. The handler must outlive this method (the client keeps using it).
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

    /// <summary>Sends SIGTERM through the `kill` utility (the graceful-stop signal).</summary>
    private static void SendSigterm(Process process)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill", $"-s TERM {process.Id}")
        {
            UseShellExecute = false,
        });
        kill?.WaitForExit(5_000);
    }

    private static async Task<(string? LastHeartbeatAt, string Status)> ReadWorkerRowAsync(
        string databasePath,
        string workerName,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + HeartbeatTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT \"LastHeartbeatAt\", \"Status\" FROM \"Workers\" WHERE \"Name\" = $name";
                command.Parameters.AddWithValue("$name", workerName);
                var result = await command.ExecuteScalarAsync(cancellationToken);
                if (result is not null and not DBNull)
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    Assert.True(await reader.ReadAsync(cancellationToken));
                    return (
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.GetString(1));
                }
            }
            catch (SqliteException)
            {
                // The server may hold a write lock; poll again.
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"No Workers row for '{workerName}' appeared within the heartbeat timeout.");
    }

    private static string FindTokenFile(string workerDataDir) =>
        Directory.EnumerateFiles(workerDataDir, "worker.token", SearchOption.AllDirectories).Single();

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
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
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

        // Review fix: fixed to the Release configuration. Taking the newest write across Debug and
        // Release could hand the E2E a stale Debug binary (or the wrong one after a mixed build).
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
