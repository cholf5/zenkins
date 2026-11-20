using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Zenkins.Server.LogStreaming;

/// <summary>
/// Where the run console's own <see cref="Microsoft.AspNetCore.SignalR.Client.HubConnection"/> connects
/// (card F-008, bug B-017).
/// <para>
/// <b>Why not the browser's base URL:</b> the panel's connection is a server-side client in this very
/// process, so the only address that is guaranteed to reach it is the one this process is listening
/// on. Dialling <c>NavigationManager.BaseUri</c> instead means a request leaves the server and comes
/// back through whatever front door the browser used - and that door can answer for itself. Behind a
/// TLS-terminating reverse proxy (docs/install.md §7) or a forwarded port (a remote-desktop tunnel, a
/// port forward), a plain HTTP request from the server is not the authenticated browser, so the hop
/// answers 401 and the log panel reports "the log stream could not be reached (401)". The ticket was
/// never wrong; it never reached the hub.
/// </para>
/// <para>
/// <b>What is chosen:</b> the bound listen addresses reported by <see cref="IServerAddressesFeature"/>,
/// preferring a plain-HTTP loopback form - a wildcard bind (<c>0.0.0.0</c>, <c>[::]</c>, <c>*</c>) is
/// normalised to <c>127.0.0.1</c> on the same port, because a wildcard bind accepts loopback too. When
/// that server feature is absent (TestServer, IIS/HTTP.sys in-process hosting) the resolution is null
/// and the caller keeps the old browser-base behaviour.
/// </para>
/// </summary>
public sealed class RunLogSelfEndpoint
{
    /// <summary>The browser hub's path, relative to the resolved root (see <c>Program.cs</c>'s MapHub).</summary>
    public const string RunLogHubPath = "hubs/run-logs";

    private readonly Lazy<Uri?> _hubUrl;

    public RunLogSelfEndpoint(IServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        // Resolved once: the listener's addresses never change after startup, and a circuit asks for
        // this on every panel instance.
        _hubUrl = new Lazy<Uri?>(() => Choose(
            server.Features.Get<IServerAddressesFeature>()?.Addresses));
    }

    /// <summary>
    /// The absolute hub URL to dial, or null when this host reports no address of its own (the
    /// caller then falls back to the browser's base URI).
    /// </summary>
    public Uri? RunLogHubUrl => _hubUrl.Value is { } root ? new Uri(root, RunLogHubPath) : null;

    /// <summary>
    /// Picks the best listen address as a root URI (trailing slash, so a relative path composes):
    /// loopback over anything else, and plain HTTP first because a loopback HTTPS hop would have to
    /// trust the host's own certificate for no gain. Unusable entries (a pipe or unix socket, a
    /// non-HTTP scheme, an unresolved port 0) are ignored, so a host that binds one of those plus a
    /// real endpoint still resolves.
    /// </summary>
    public static Uri? Choose(IEnumerable<string?>? addresses)
    {
        if (addresses is null)
        {
            return null;
        }

        Uri? best = null;
        var bestRank = int.MaxValue;
        foreach (var address in addresses)
        {
            if (!TryNormalize(address, out var candidate, out var rank) || rank >= bestRank)
            {
                continue;
            }

            best = candidate;
            bestRank = rank;
        }

        return best;
    }

    private static bool TryNormalize(string? address, out Uri root, out int rank)
    {
        root = null!;
        rank = int.MaxValue;

        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var isHttp = string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if ((!isHttp && !isHttps) || parsed.Port <= 0)
        {
            return false;
        }

        var host = parsed.Host;
        var loopback = IsLoopback(host);
        if (!loopback && IsWildcard(host))
        {
            // A wildcard bind already accepts loopback on the same port; naming loopback explicitly
            // keeps the connection off the machine's network stack (and off any proxy that watches it).
            host = IPAddress.Loopback.ToString();
            loopback = true;
        }

        root = new UriBuilder(parsed.Scheme, host, parsed.Port).Uri;
        rank = (isHttp, loopback) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => 2,
            (false, false) => 3,
        };
        return true;
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static bool IsWildcard(string host) =>
        host is "0.0.0.0" or "::" or "[::]" or "*" or "+"
        || string.Equals(host, "any", StringComparison.OrdinalIgnoreCase);
}
