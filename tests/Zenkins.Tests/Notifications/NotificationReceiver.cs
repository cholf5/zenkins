using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Zenkins.Shared.Notifications;

namespace Zenkins.Tests.Notifications;

/// <summary>One request the receiver got: the raw bytes (what the signature covers), the headers, and the parsed body.</summary>
public sealed record ReceivedNotification(
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers,
    NotificationPayload? Payload)
{
    /// <summary>One header's value, or null.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A real HTTP receiver for card F-039's deliveries: a loopback <see cref="HttpListener"/> that records every request
/// (raw body, headers) and answers with the status the test asked for - optionally after a delay, and optionally with
/// a redirect.
/// <para>
/// <b>Why a real socket.</b> The feature under test is an HTTP client with a timeout, a no-redirect policy and a
/// bounded response read; a mocked <c>HttpMessageHandler</c> would pin the call shape and nothing about the wire. A
/// loopback listener also lets the test point at a <em>closed</em> port (TC-002) with no test-only seam in the product.
/// </para>
/// </summary>
public sealed class NotificationReceiver : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<ReceivedNotification> _received = new();
    private readonly Task _acceptLoop;
    private readonly int _statusCode;
    private readonly TimeSpan? _delay;
    private readonly string? _location;

    private NotificationReceiver(int port, int statusCode, TimeSpan? delay, string? location)
    {
        Url = $"http://127.0.0.1:{port}/hook";
        _statusCode = statusCode;
        _delay = delay;
        _location = location;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The URL to configure as the notification endpoint (plain http: the tests opt in).</summary>
    public string Url { get; }

    /// <summary>Every request received so far, in arrival order.</summary>
    public IReadOnlyList<ReceivedNotification> Received => [.. _received];

    /// <summary>A receiver that answers <c>204 No Content</c> - the happy path.</summary>
    public static NotificationReceiver Accepting() => Start(204, null, null);

    /// <summary>
    /// A receiver that answers the redirect the SSRF rule must not follow. The target is this same receiver's
    /// <c>/elsewhere</c> path, so "did anyone follow?" is one more recorded request.
    /// </summary>
    public static NotificationReceiver Redirecting(string location) => Start(302, null, location);

    /// <summary>A receiver that accepts the connection and then says nothing for <paramref name="delay"/>.</summary>
    public static NotificationReceiver Slow(TimeSpan delay) => Start(204, delay, null);

    /// <summary>A receiver that refuses with a status (and a body the recorded error should quote).</summary>
    public static NotificationReceiver Refusing(int statusCode) => Start(statusCode, null, null);

    /// <summary>A port nobody listens on: the closed-receiver case of TC-002.</summary>
    public static string ClosedPortUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/hook";
    }

    private static NotificationReceiver Start(int statusCode, TimeSpan? delay, string? location)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new NotificationReceiver(port, statusCode, delay, location);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                await HandleAsync(context);
            }
            catch (Exception)
            {
                // A receiver that breaks one request must not kill the listener; the test asserts on what it recorded.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body, _stop.Token);
        var bytes = body.ToArray();
        var headers = context.Request.Headers.AllKeys
            .Where(key => key is not null)
            .ToDictionary(key => key!, key => context.Request.Headers[key]!, StringComparer.OrdinalIgnoreCase);

        NotificationPayload? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize(bytes, NotificationJsonContext.Default.NotificationPayload);
        }
        catch (JsonException)
        {
            // A body that is not our payload is still recorded: "what did it send?" is the question.
        }

        _received.Enqueue(new ReceivedNotification(bytes, headers, payload));

        if (_delay is { } delay)
        {
            // The listener stays silent: the sender's own timeout is the thing under test.
            await Task.Delay(delay, _stop.Token);
        }

        context.Response.StatusCode = _statusCode;
        if (_location is not null)
        {
            context.Response.Headers["Location"] = _location;
        }

        var answer = Encoding.UTF8.GetBytes(_statusCode >= 400 ? "the receiver says no" : string.Empty);
        context.Response.ContentLength64 = answer.Length;
        await context.Response.OutputStream.WriteAsync(answer, _stop.Token);
        context.Response.Close();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        _listener.Close();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was torn down mid-accept; nothing to report.
        }

        _stop.Dispose();
    }
}
