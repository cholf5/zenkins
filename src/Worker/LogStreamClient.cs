using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Zenkins.Shared.LogStreaming;

namespace Zenkins.Worker;

/// <summary>
/// What a run's drain pump needs from the upstream connection. <see cref="LogStreamClient"/> is
/// the real implementation; tests drive the buffer against fakes.
/// </summary>
public interface ILogStreamSink
{
    /// <summary>Whether the connection is currently established.</summary>
    bool IsConnected { get; }

    /// <summary>Whether the upstream ever came up in this process (see LogStreamClient's doc).</summary>
    bool HasEverConnected { get; }

    /// <summary>Waits until the connection is established (or the token fires).</summary>
    Task WaitForConnectedAsync(CancellationToken shutdown);

    /// <summary>Sends one batch of lines for one run; completes when the server accepted it.</summary>
    Task SendLinesAsync(Guid runId, IReadOnlyList<LogLineMessage> lines, CancellationToken shutdown);
}

/// <summary>
/// The worker's upstream log-stream connection (card F-008) and cancel-signal receive point
/// (card F-009): a single <see cref="HubConnection"/> to the server's <c>/hubs/worker-logs</c>,
/// authenticated with the worker token (SignalR presents it as <c>Authorization: Bearer …</c> on
/// negotiate and as the <c>access_token</c> query string on the WebSocket handshake - the server's
/// scheme reads both). While connected it also receives the server's <c>cancelRun(runId, reason)</c>
/// signals (F-009's primary cancel channel) and raises them on the shared <see cref="RunCancelSignal"/>.
/// <para>
/// <b>Lifecycle.</b> One connection for the whole worker process, owned by <see cref="RunAsync"/>:
/// connect, stay connected until the server or the network closes it, then reconnect with the
/// shared capped-exponential backoff until shutdown - the same reconnect story as the heartbeat
/// loop. Every successful (re)connect raises <see cref="Connected"/> (which wakes the per-run
/// drain pumps); reconnection never re-sends by itself: the <see cref="RunLogBuffer"/>s keep their
/// unsent lines and the (RunId, Seq) primary key on the server absorbs the overlap that a resend
/// after a lost response necessarily produces.
/// </para>
/// <para>
/// <b>Backpressure.</b> <see cref="SendLinesAsync"/> completes only when the server has finished
/// handling the batch, so a persisting server throttles the worker naturally; a failing send
/// throws and the caller keeps the lines buffered. The worker stays ASP.NET-free: this package is
/// the SignalR <em>client</em>, which does not reference the ASP.NET Core shared framework
/// (pinned by WorkerRuntimeConfigTests).
/// </para>
/// </summary>
public sealed class LogStreamClient(
    WorkerConfiguration configuration,
    string workerToken,
    BackoffScheduler backoff,
    TextWriter log,
    TextWriter errorLog,
    Action<HttpConnectionOptions>? configureConnection = null,
    RunCancelSignal? cancelSignal = null) : IAsyncDisposable, ILogStreamSink
{
    /// <summary>Raised after every successful (re)connect; wakes the per-run drain pumps.</summary>
    public event Action? Connected;

    private readonly object _gate = new();
    private HubConnection? _connection;
    private bool _connected;
    private bool _everConnected;

    /// <summary>Whether the connection is currently established.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connected;
            }
        }
    }

    /// <summary>
    /// Whether the upstream ever came up in this process. A run loop that finishes while this is
    /// false must not hold its completed report hostage to a connection that may not exist at
    /// all; the pump keeps retrying in the background either way.
    /// </summary>
    public bool HasEverConnected
    {
        get
        {
            lock (_gate)
            {
                return _everConnected;
            }
        }
    }

    /// <summary>
    /// Runs the connection loop until shutdown: start, wait for the server to close it, reconnect
    /// with backoff. Transport failures and rejected negotiations back off like every other
    /// worker loop; a definitive 401 (dead token) is logged and retried too, so a restored
    /// credential picks the stream back up without a restart (the heartbeat loop's behaviour).
    /// </summary>
    public async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(configuration.ServerUrl, "hubs/worker-logs"), options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(workerToken);
                    configureConnection?.Invoke(options);
                })
                .AddJsonProtocol(options =>
                {
                    // I-004: the JSON hub protocol serializes hub-method arguments through
                    // PayloadSerializerOptions, whose default resolver is reflection-based (the
                    // package suppresses the resulting IL2026/IL3050 internally, so the gap
                    // would only surface as a runtime failure in a published AOT binary - the
                    // first AppendLinesAsync/cancelRun exchange). Replace the options with the
                    // protocol's own default settings (camelCase naming, case-insensitive
                    // reads, MaxDepth 64) resolved through the worker's source-generated
                    // context: every payload type on this wire (LogLineMessage in its list
                    // shapes, plus the Guid/string primitives) has compile-time metadata, and
                    // the bytes on the wire are exactly what the JIT worker sent.
                    options.PayloadSerializerOptions = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                        PropertyNameCaseInsensitive = true,
                        MaxDepth = 64,
                        TypeInfoResolver = WorkerJsonContext.Default,
                    };
                })
                .Build();

            var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += failure =>
            {
                closed.TrySetResult(failure);
                return Task.CompletedTask;
            };

            // F-009: the cancelRun signal rides the same upstream connection. Registered per
            // connection (handlers are per connection); the heartbeat response's
            // cancelRequestedRuns list is the fallback channel for a connection that is down.
            if (cancelSignal is not null)
            {
                connection.On<Guid, string?>("cancelRun", (runId, reason) =>
                    cancelSignal.Raise(new RunCancelRequest(runId, reason)));
            }

            try
            {
                await connection.StartAsync(shutdown);
                lock (_gate)
                {
                    _connection = connection;
                    _connected = true;
                    _everConnected = true;
                }

                log.WriteLine("Zenkins Worker: log stream connected.");
                Connected?.Invoke();

                // Stay here until the server or the network closes the connection.
                var closeReason = await closed.Task.WaitAsync(shutdown);
                if (closeReason is not null)
                {
                    await errorLog.WriteLineAsync(
                        $"Zenkins Worker: the log stream closed ({closeReason.Message}); reconnecting.");
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: the log stream could not connect ({failure.Message}). "
                    + $"Retrying in {delay.TotalSeconds:0.#}s.");
                try
                {
                    await Task.Delay(delay, shutdown);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _connected = false;
                    if (ReferenceEquals(_connection, connection))
                    {
                        _connection = null;
                    }
                }

                // A locally abandoned connection must never linger past this iteration.
                try
                {
                    await connection.DisposeAsync();
                }
                catch
                {
                    // Disposal of a half-open connection is best effort by definition.
                }
            }
        }
    }

    /// <summary>
    /// Waits until the connection is established (or shutdown arrives). The drain pumps wait here
    /// instead of polling; the <see cref="Connected"/> event makes a late connect immediately
    /// observable, and an already-established connection returns immediately.
    /// </summary>
    public async Task WaitForConnectedAsync(CancellationToken shutdown)
    {
        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected()
        {
            Connected -= OnConnected;
            wait.TrySetResult();
        }

        Connected += OnConnected;
        try
        {
            if (IsConnected)
            {
                return;
            }

            await wait.Task.WaitAsync(shutdown);
        }
        finally
        {
            Connected -= OnConnected;
        }
    }

    /// <summary>
    /// Sends one batch of lines for one run and completes only when the server has finished the
    /// append - that is the worker's natural backpressure (a persisting server paces the drain)
    /// and the only way a definitive server rejection becomes observable. Throws when the
    /// connection is down - the caller keeps the lines and re-drains after the reconnect.
    /// </summary>
    public async Task SendLinesAsync(Guid runId, IReadOnlyList<LogLineMessage> lines, CancellationToken shutdown)
    {
        HubConnection? connection;
        lock (_gate)
        {
            connection = _connected ? _connection : null;
        }

        if (connection is null)
        {
            throw new InvalidOperationException("the log stream is not connected.");
        }

        await connection.InvokeAsync("AppendLinesAsync", runId, lines, shutdown);
    }

    public async ValueTask DisposeAsync()
    {
        HubConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
            _connected = false;
        }

        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch
            {
                // Best effort during shutdown.
            }
        }
    }
}
