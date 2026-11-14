using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Zenkins.Tests.Git;

/// <summary>
/// A minimal <b>HTTPS</b> git server with a Basic-auth challenge, for card F-018's acceptance evidence:
/// a private repository reached over TLS with a certificate this fixture issues itself, so the clone
/// really negotiates TLS and really answers a credential challenge.
/// <para>
/// <b>Why a hand-written TLS server.</b> <c>HttpListener</c> does not serve <c>https://</c> prefixes on
/// macOS or Linux, so the HTTPS half of the card's proof cannot use it. This fixture is a
/// <see cref="TcpListener"/> plus an <see cref="SslStream"/> per connection and the dumb HTTP git
/// protocol (the same files <see cref="LocalHttpGitServer"/> serves over plain HTTP: <c>info/refs</c>,
/// <c>HEAD</c>, <c>objects/…</c>). One request per connection, <c>Connection: close</c>: git opens as
/// many connections as it needs.
/// </para>
/// <para>
/// <b>The certificate is the fixture's, not the machine's.</b> A self-signed certificate with an
/// <c>127.0.0.1</c> IP SAN is generated per instance and written out as PEM, so a client trusts it
/// through <c>GIT_SSL_CAINFO</c> - which is exactly how a private certificate authority would be
/// installed without touching the host's trust store. Nothing is installed system-wide and nothing is
/// left behind: the PEM lives in this instance's temporary directory.
/// </para>
/// <para>
/// <b>What it records.</b> Challenged and accepted requests, and the user name git actually presented,
/// so "the delivered token is what authenticated" is the server's own observation rather than an
/// inference from our side.
/// </para>
/// </summary>
internal sealed class AuthenticatedHttpsGitServer : IDisposable
{
    /// <summary>The repository path component git requests (<c>/repo.git/…</c>).</summary>
    public const string RepositoryComponent = "/repo.git";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly X509Certificate2 _serverCertificate;
    private readonly string _repositoryPath;
    private readonly string _certificateAuthorityPath;
    private readonly List<string> _userNames = [];
    private readonly object _userNameGate = new();
    private int _authenticated;
    private int _rejected;
    private bool _disposed;

    private AuthenticatedHttpsGitServer(string repositoryPath, string requiredToken, int port, string rootPath)
    {
        _repositoryPath = repositoryPath;
        RequiredToken = requiredToken;
        Url = $"https://127.0.0.1:{port}{RepositoryComponent}";

        var material = CreateCertificate();
        _serverCertificate = material.ServerCertificate;
        _certificateAuthorityPath = Path.Combine(rootPath, "git-server-ca.pem");
        File.WriteAllText(_certificateAuthorityPath, material.CertificateAuthorityPem);

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The bare repository's directory.</summary>
    public string RepositoryPath => _repositoryPath;

    /// <summary>The clone URL (<c>https://127.0.0.1:{port}/repo.git</c>).</summary>
    public string Url { get; }

    /// <summary>The password the server accepts (never empty: this fixture always challenges).</summary>
    public string RequiredToken { get; }

    /// <summary>
    /// The PEM file a client passes as <c>GIT_SSL_CAINFO</c> to trust this server's certificate. It is
    /// the same certificate the server presents (a self-signed CA of one).
    /// </summary>
    public string CertificateAuthorityPath => _certificateAuthorityPath;

    /// <summary>Requests that carried a valid Basic credential.</summary>
    public int AuthenticatedRequests => Volatile.Read(ref _authenticated);

    /// <summary>Requests the server challenged (missing or wrong credential).</summary>
    public int RejectedRequests => Volatile.Read(ref _rejected);

    /// <summary>The user names git presented, in arrival order.</summary>
    public IReadOnlyCollection<string> PresentedUserNames
    {
        get
        {
            lock (_userNameGate)
            {
                return [.. _userNames];
            }
        }
    }

    /// <summary>Starts a server over <paramref name="repositoryPath"/> that requires <paramref name="requiredToken"/>.</summary>
    public static AuthenticatedHttpsGitServer Start(string repositoryPath, string requiredToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredToken);
        Directory.CreateDirectory(repositoryPath);

        var root = Path.Combine(Path.GetTempPath(), $"zenkins-f018-tls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = FindFreePort();
            try
            {
                return new AuthenticatedHttpsGitServer(repositoryPath, requiredToken, port, root);
            }
            catch (SocketException) when (attempt < 4)
            {
                // The port was taken between the probe and the bind; try another.
            }
        }

        throw new InvalidOperationException("The local HTTPS git server could not bind a port.");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await stream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _serverCertificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    },
                    _stop.Token);

                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    return;
                }

                await AnswerAsync(stream, request).ConfigureAwait(false);
            }
            catch (AuthenticationException)
            {
                // A client that did not trust our certificate: nothing to answer.
            }
            catch (IOException)
            {
                // The client hung up (a killed clone); nothing to answer.
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task AnswerAsync(SslStream stream, HttpRequest request)
    {
        if (!Authenticate(request.Authorization))
        {
            Interlocked.Increment(ref _rejected);
            await WriteAsync(
                stream,
                "401 Unauthorized",
                "text/plain",
                Encoding.UTF8.GetBytes("authentication required"),
                "WWW-Authenticate: Basic realm=\"zenkins-f018\", charset=\"UTF-8\"\r\n").ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref _authenticated);

        // /repo.git/{relative}: anything else is not ours. The query string is ignored on purpose -
        // git's dumb-HTTP probe sends ?service=git-upload-pack and a plain file answer makes git fall
        // back to the dumb protocol, which is the protocol this fixture serves.
        var path = request.Path;
        var prefix = RepositoryComponent + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            await WriteAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found"))
                .ConfigureAwait(false);
            return;
        }

        var relative = path[prefix.Length..];
        if (relative.Length == 0)
        {
            relative = "HEAD";
        }

        var fullPath = Path.GetFullPath(Path.Combine(_repositoryPath, relative));
        if (!fullPath.StartsWith(Path.GetFullPath(_repositoryPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(fullPath))
        {
            await WriteAsync(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found"))
                .ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
        await WriteAsync(stream, "200 OK", ContentTypeFor(relative), bytes).ConfigureAwait(false);
    }

    private bool Authenticate(string? header)
    {
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

        lock (_userNameGate)
        {
            _userNames.Add(decoded[..separator]);
        }

        return string.Equals(decoded[(separator + 1)..], RequiredToken, StringComparison.Ordinal);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(SslStream stream)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(single).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Count == 0 ? null : Parse(Encoding.UTF8.GetString([.. buffer]));
            }

            buffer.Add(single[0]);
            if (buffer.Count >= 4
                && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                return Parse(Encoding.UTF8.GetString([.. buffer]));
            }
        }

        return null;
    }

    private static HttpRequest? Parse(string head)
    {
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }

        string? authorization = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            if (string.Equals(line[..colon], "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                authorization = line[(colon + 1)..].Trim();
            }
        }

        var target = requestLine[1];
        var query = target.IndexOf('?', StringComparison.Ordinal);
        return new HttpRequest(query >= 0 ? target[..query] : target, authorization);
    }

    private static async Task WriteAsync(
        SslStream stream,
        string status,
        string contentType,
        byte[] body,
        string extraHeaders = "")
    {
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + extraHeaders
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
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

    /// <summary>
    /// The server certificate (with its private key, for <see cref="SslStream"/>) and the PEM a client
    /// trusts. Both come from one self-signed certificate, so this fixture is its own certificate
    /// authority.
    /// </summary>
    private static (X509Certificate2 ServerCertificate, string CertificateAuthorityPem) CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=zenkins-f018-git",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(2));

        // Reload from PKCS#12 so the private key is usable by SslStream on every platform.
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        var serverCertificate = X509CertificateLoader.LoadPkcs12(pfx, password: null);
        return (serverCertificate, ephemeral.ExportCertificatePem());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _serverCertificate.Dispose();
        _stop.Dispose();

        try
        {
            var directory = Path.GetDirectoryName(_certificateAuthorityPath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record HttpRequest(string Path, string? Authorization);
}
