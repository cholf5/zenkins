using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zenkins.Tests.Git;

/// <summary>
/// A Minimal dumb-HTTP git server for the HTTPS-token test (TC-002): it serves a bare repository's
/// files and, when a token is required, answers <c>401</c> with a Basic challenge so git has to ask
/// <c>GIT_ASKPASS</c> for the credential - which is exactly the path the card must pin.
/// <para>
/// <b>Why dumb HTTP.</b> It needs nothing but file reads (<c>info/refs</c>, <c>HEAD</c>,
/// <c>objects/…</c>), so the fixture is a real HTTP conversation against a real repository without a
/// <c>git-http-backend</c> process or a second NuGet package. The point of the test is the credential
/// path, not the smart protocol.
/// </para>
/// <para>
/// <b>What it records.</b> Every authenticated and rejected request, and the user name git actually
/// presented - so "the token travelled only through ask-pass" is backed by the server's own view of
/// the exchange, not only by our absence assertions.
/// </para>
/// </summary>
internal sealed class LocalHttpGitServer : IDisposable
{
    /// <summary>The repository path component git requests (<c>/repo.git/…</c>).</summary>
    public const string RepositoryComponent = "/repo.git";

    private readonly HttpListener _listener;
    private readonly Task _acceptLoop;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<string> _userNames = [];
    private readonly ConcurrentDictionary<string, string> _repositories = new(StringComparer.Ordinal);

    // Per-repository request counters, keyed by the URL's first path segment. They are what lets a test
    // say "the submodule child authenticated too" instead of assuming it inherited the environment.
    private readonly ConcurrentDictionary<string, int> _authenticatedPerRepository = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _rejectedPerRepository = new(StringComparer.Ordinal);
    private int _authenticated;
    private int _rejected;
    private bool _disposed;

    private LocalHttpGitServer(string repositoryPath, string? requiredToken, int port)
    {
        RepositoryPath = repositoryPath;
        RequiredToken = requiredToken;
        Url = $"http://127.0.0.1:{port}{RepositoryComponent}";
        _repositories[RepositoryComponent.TrimStart('/')] = repositoryPath;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The bare repository's directory.</summary>
    public string RepositoryPath { get; }

    /// <summary>The clone URL (<c>http://127.0.0.1:{port}/repo.git</c>).</summary>
    public string Url { get; }

    /// <summary>The password the server accepts, or <see langword="null"/> for anonymous reads.</summary>
    public string? RequiredToken { get; }

    /// <summary>Requests that carried a valid Basic credential.</summary>
    public int AuthenticatedRequests => Volatile.Read(ref _authenticated);

    /// <summary>Requests the server challenged (missing or wrong credential).</summary>
    public int RejectedRequests => Volatile.Read(ref _rejected);

    /// <summary>The user names git presented, in arrival order.</summary>
    public IReadOnlyCollection<string> PresentedUserNames => [.. _userNames];

    /// <summary>
    /// Also serve <paramref name="directory"/> under <c>/{name}</c>. Used by the submodule test, where
    /// the superproject and its submodule are two repositories behind one server (the only way to show
    /// that the child really inherited the credential channel).
    /// </summary>
    public void Serve(string name, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _repositories[name.Trim('/')] = directory;
    }

    /// <summary>The URL of one served repository (<c>http://host:port/{name}</c>).</summary>
    public string UrlFor(string name) =>
        $"http://127.0.0.1:{new Uri(Url).Port}/{name.Trim('/')}";

    /// <summary>Authenticated requests for one served repository, by its URL segment.</summary>
    public int AuthenticatedRequestsFor(string name) =>
        _authenticatedPerRepository.GetValueOrDefault(name.Trim('/'));

    /// <summary>Challenged requests for one served repository, by its URL segment.</summary>
    public int RejectedRequestsFor(string name) =>
        _rejectedPerRepository.GetValueOrDefault(name.Trim('/'));

    /// <summary>Starts a server over <paramref name="repositoryPath"/>; a token enables the Basic challenge.</summary>
    public static LocalHttpGitServer Start(string repositoryPath, string? requiredToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        Directory.CreateDirectory(repositoryPath);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = FindFreePort();
            try
            {
                return new LocalHttpGitServer(repositoryPath, requiredToken, port);
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                // The port was taken between the probe and the bind; try another.
            }
        }

        throw new InvalidOperationException("The local git HTTP server could not bind a port.");
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
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            // /{repository}/{relative-path}: the first segment selects a served repository.
            var segments = request.Url!.AbsolutePath.Trim('/').Split('/', 2);
            var repositoryName = segments[0];
            if (!_repositories.TryGetValue(repositoryName, out var repositoryDirectory))
            {
                await WriteStatusAsync(response, 404, "not found").ConfigureAwait(false);
                return;
            }

            if (RequiredToken is not null && !Authenticate(request))
            {
                Interlocked.Increment(ref _rejected);
                _rejectedPerRepository.AddOrUpdate(repositoryName, 1, (_, count) => count + 1);
                response.AddHeader("WWW-Authenticate", "Basic realm=\"zenkins-git\"");
                await WriteStatusAsync(response, 401, "authentication required").ConfigureAwait(false);
                return;
            }

            if (RequiredToken is not null)
            {
                Interlocked.Increment(ref _authenticated);
                _authenticatedPerRepository.AddOrUpdate(repositoryName, 1, (_, count) => count + 1);
            }

            var relative = segments.Length > 1 ? segments[1] : "HEAD";
            if (relative.Length == 0)
            {
                relative = "HEAD";
            }

            var filePath = Path.GetFullPath(Path.Combine(repositoryDirectory, relative));
            if (!filePath.StartsWith(Path.GetFullPath(repositoryDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(filePath))
            {
                await WriteStatusAsync(response, 404, "not found").ConfigureAwait(false);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            response.StatusCode = 200;
            response.ContentType = ContentTypeFor(relative);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            response.OutputStream.Close();
        }
        catch (HttpListenerException)
        {
            // The client hung up (a killed clone); nothing to answer.
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool Authenticate(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        _userNames.Add(decoded[..separator]);
        return string.Equals(decoded[(separator + 1)..], RequiredToken, StringComparison.Ordinal);
    }

    private static async Task WriteStatusAsync(HttpListenerResponse response, int statusCode, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = statusCode;
        response.ContentType = "text/plain";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static string ContentTypeFor(string relative) => relative switch
    {
        var value when value.StartsWith("objects/info/", StringComparison.Ordinal) => "text/plain",
        var value when value.StartsWith("objects/", StringComparison.Ordinal) => "application/x-git-loose-object",
        "info/refs" => "text/plain; charset=utf-8",
        _ => "text/plain",
    };

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();

        // Stop first so the pending GetContextAsync returns, wait for the loop, and only then release
        // the listener. Both platform implementations are allowed to throw while doing this (the
        // managed one reports "Address already in use" from the prefix bookkeeping when a close races a
        // pending accept), and a test fixture must never fail a test from its own teardown.
        try
        {
            _listener.Stop();
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener.Close();
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }
}
