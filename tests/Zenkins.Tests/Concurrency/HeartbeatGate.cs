using System.Net;
using System.Net.Sockets;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// A loopback TCP relay in front of the real server, with a gate that can be shut (Q-020): while the
/// gate is shut, the gated worker's bytes stop reaching the server, so nothing it owes the server
/// arrives - its next heartbeat request simply hangs until the worker's own request timeout gives up
/// on it. The process stays alive, its run keeps executing, and the server's stored heartbeat for that
/// worker ages. That is the CI incident's state, produced without touching the process.
/// <para>
/// <b>Why a relay and not a stopped process.</b> Suspending the worker (<c>SIGSTOP</c>) is what the
/// card's Windows diagnosis used and it reproduces the state faithfully - but on POSIX it cannot be
/// done from the test host, which is the worker's parent: .NET's <c>SIGCHLD</c> bookkeeping wedges in
/// <c>Process.OnSigChild</c>/<c>ProcessWaitState</c> as soon as one of its children stops, and the
/// next <see cref="System.Diagnostics.Process"/> call (even <c>Process.Dispose</c>) never returns.
/// Measured here on macOS 26.5 + .NET 10.0.403 with a three-child repro, twice: the gate below needs
/// no signals, no privileges and no platform switch, and it stalls exactly what the incident stalled -
/// the heartbeat's arrival - while leaving the worker process and its run untouched.
/// </para>
/// </summary>
internal sealed class HeartbeatGate : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _accept;
    private volatile bool _open = true;

    private HeartbeatGate(TcpListener listener, Uri upstream)
    {
        _listener = listener;
        _upstreamHost = upstream.Host;
        _upstreamPort = upstream.Port;
        _accept = AcceptLoopAsync();
    }

    /// <summary>Starts a gate in front of <paramref name="upstream"/> (the real server's URL), open.</summary>
    public static HeartbeatGate Start(Uri upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new HeartbeatGate(listener, upstream);
    }

    /// <summary>The URL a worker must be started with to sit behind this gate.</summary>
    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Shuts the gate: from now on nothing the gated worker sends reaches the server.</summary>
    public void Close() => _open = false;

    /// <summary>Opens it again; the bytes held while it was shut go through in order.</summary>
    public void Open() => _open = true;

    public async ValueTask DisposeAsync()
    {
        _open = true;
        await _stop.CancelAsync();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        lock (_clients)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _clients.Clear();
        }

        try
        {
            await _accept;
        }
        catch (Exception)
        {
            // The listener was stopped under the accept loop; nothing is left to report.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = RelayAsync(inbound);
        }
    }

    /// <summary>One accepted connection: dial the server and pump both directions until either ends.</summary>
    private async Task RelayAsync(TcpClient inbound)
    {
        var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _stop.Token);
        }
        catch (Exception)
        {
            inbound.Dispose();
            upstream.Dispose();
            return;
        }

        lock (_clients)
        {
            _clients.Add(inbound);
            _clients.Add(upstream);
        }

        try
        {
            await Task.WhenAny(
                CopyAsync(inbound.GetStream(), upstream.GetStream()),
                CopyAsync(upstream.GetStream(), inbound.GetStream()));
        }
        catch (Exception)
        {
            // Either side closing or resetting ends the pair; the fact's evidence is in the rows,
            // not in a relay connection, so nothing here is worth reporting.
        }
        finally
        {
            inbound.Dispose();
            upstream.Dispose();
        }
    }

    /// <summary>
    /// One direction of one connection. While the gate is shut the bytes already read are <b>held</b>
    /// here - not forwarded, not dropped - so the server never sees the request and the worker never
    /// sees an answer, and everything resumes in order once the gate opens.
    /// </summary>
    private async Task CopyAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await from.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                while (!_open && !_stop.IsCancellationRequested)
                {
                    await Task.Delay(25, _stop.Token);
                }

                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
        }
        catch (Exception)
        {
            // The connection ended (or the gate was disposed): both are the end of this pump.
        }
    }
}
