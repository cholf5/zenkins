using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Workers;
using Zenkins.Worker;

namespace Zenkins.Tests.RunLifecycle;

/// <summary>
/// The real <see cref="WorkerRunner"/> (real registration through the API, heartbeat loop, run
/// loops, upstream log stream) executed in-process against the TestServer pipeline - the
/// cancel/timeout integration tests' executing worker.
/// <para>
/// With <see cref="StartAsync"/>'s <c>connectUpstream</c>, the SignalR connection is routed through
/// the TestServer (long polling, the F-008 hub-test pattern) so the cancel signal arrives over the
/// primary channel and the partial log is persisted by the real ingest. Without it the upstream
/// points at an unreachable port - exactly the dropped-connection scenario the card describes -
/// and the heartbeat response's <c>cancelRequestedRuns</c> list is the delivery channel (one
/// heartbeat period of added latency, <c>ZENKINS_HEARTBEAT_SECONDS=1</c>).
/// </para>
/// </summary>
public sealed class InProcessWorker : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _runnerTask;
    private readonly HttpMessageHandler? _hubHandler;

    private InProcessWorker(
        Task runnerTask,
        string workRoot,
        string dataDirectory,
        CancellationTokenSource stop,
        HttpMessageHandler? hubHandler)
    {
        _runnerTask = runnerTask;
        WorkRoot = workRoot;
        DataDirectory = dataDirectory;
        _stop = stop;
        _hubHandler = hubHandler;
    }

    public string WorkRoot { get; }

    public string DataDirectory { get; }

    public static Task<InProcessWorker> StartAsync(
        RunLifecycleHost host,
        string workerName,
        bool connectUpstream = true) =>
        StartAsync(host, workerName, connectUpstream, cleanupTemp: true);

    public static async Task<InProcessWorker> StartAsync(
        RunLifecycleHost host,
        string workerName,
        bool connectUpstream,
        bool cleanupTemp)
    {
        // A registration token minted through the real Owner form; the runner registers itself
        // through the real API exactly as a process-level worker does.
        var owner = await host.Inner.Inner.SignInOwnerAsync();
        var (registrationToken, mintResponse) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        Assert.True(
            (int)mintResponse.StatusCode is 200 or 302 or 303,
            $"Minting the registration token failed with {(int)mintResponse.StatusCode}.");
        owner.Dispose();

        var workRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f009-worker-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(workRoot, "data");
        Directory.CreateDirectory(workRoot);

        var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            // The upstream base URL: routed through the TestServer handler when connected;
            // deliberately unreachable (port 1) for the heartbeat-fallback scenario.
            [WorkerConfiguration.ServerUrlVariable] = connectUpstream ? "http://localhost" : "http://localhost:1",
            [WorkerConfiguration.RegistrationTokenVariable] = registrationToken,
            [WorkerConfiguration.WorkerNameVariable] = workerName,
            [WorkerConfiguration.WorkRootVariable] = workRoot,
            [WorkerConfiguration.DataDirectoryVariable] = dataDirectory,
            [WorkerConfiguration.HeartbeatSecondsVariable] = "1",
            [WorkerConfiguration.ClaimSecondsVariable] = "0.2",
            [WorkerConfiguration.CleanupTempVariable] = cleanupTemp ? "true" : "false",
        }).Configuration
            ?? throw new InvalidOperationException("The in-process worker configuration did not parse.");

        var api = new WorkerApiClient(host.Host.CreateClient());
        var tokenStore = new WorkerTokenStore(dataDirectory, configuration.ServerUrl, workerName);

        // The hub route through the in-memory TestServer (no real TCP listener exists): long
        // polling, exactly like the F-008 hub tests. The handler is kept alive by the worker.
        HttpMessageHandler? hubHandler = null;
        Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>? configureConnection = null;
        if (connectUpstream)
        {
            hubHandler = host.Host.Server.CreateHandler();
            configureConnection = options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => hubHandler!;
            };
        }

        var runner = new WorkerRunner(
            configuration,
            api,
            tokenStore,
            new WorkDirectoryService(workRoot),
            BackoffScheduler.Default(),
            Console.Out,
            Console.Error,
            configureConnection);

        var stop = new CancellationTokenSource();
        var runnerTask = Task.Run(() => runner.RunAsync(stop.Token));
        return new InProcessWorker(runnerTask, workRoot, dataDirectory, stop, hubHandler);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _runnerTask.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // The runner's shutdown is best effort in test teardown; the work root is removed below.
        }

        // Q-011's cleanup contract via TestCleanup: the tree holds a cloned repository with
        // read-only pack files (a Windows delete reports UnauthorizedAccessException for them).
        TestCleanup.DeleteTree(WorkRoot);
    }
}
