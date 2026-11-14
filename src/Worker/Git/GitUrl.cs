using System.Text.RegularExpressions;

namespace Zenkins.Worker.Git;

/// <summary>Which transport a repository URL selects.</summary>
public enum GitTransport
{
    /// <summary><c>https://</c> / <c>http://</c>: credentials travel through <c>GIT_ASKPASS</c>.</summary>
    Http = 0,

    /// <summary><c>ssh://</c> or the scp-like <c>git@host:path</c>: credentials travel through a key file.</summary>
    Ssh = 1,

    /// <summary><c>file://</c> or a local absolute path: no transport credentials at all.</summary>
    Local = 2,
}

/// <summary>
/// A validated repository URL (card F-013). One place decides the transport, rejects credentials
/// embedded in the URL, and produces the sanitised form used in <b>every</b> message and log line.
/// <para>
/// <b>Why reject <c>https://user:token@host/…</c>.</b> That spelling is exactly the argv vector the
/// card forbids: it lands in the child process's command line (visible to <c>ps</c>) and in git's own
/// error messages. The project's credential belongs in <c>GitCredentialId</c>, so a URL that embeds
/// one is refused with a message that names the problem and echoes neither part.
/// </para>
/// <para>
/// <b>Why the SSH user name is not a credential.</b> <c>ssh://git@host/…</c> (and the scp form) carry
/// the account name, not a secret; it stays in <see cref="Describe"/> so failures are diagnosable.
/// </para>
/// </summary>
public sealed partial class GitUrl
{
    // I-004: source-generated regex. RegexOptions.Compiled is a JIT codegen path that does not
    // survive NativeAOT; [GeneratedRegex] emits the scanner as compile-time source, so the
    // same pattern stays available under AOT with identical matching behaviour.
    [GeneratedRegex("^(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9._-]+):(?<path>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLikePattern();

    private GitUrl(string cloneUrl, string scheme, GitTransport transport, string host, string? userName)
    {
        CloneUrl = cloneUrl;
        Scheme = scheme;
        Transport = transport;
        Host = host;
        UserName = userName;
    }

    /// <summary>The credential-free URL handed to <c>git clone</c> as an argument.</summary>
    public string CloneUrl { get; }

    /// <summary>The display scheme (<c>http</c>, <c>https</c>, <c>ssh</c>, <c>file</c>): messages must
    /// not turn an <c>http</c> remote into an <c>https</c> one.</summary>
    public string Scheme { get; }

    public GitTransport Transport { get; }

    /// <summary>The host, or <c>(local)</c> for a local path. Safe to log.</summary>
    public string Host { get; }

    /// <summary>The SSH account name, when the URL names one.</summary>
    public string? UserName { get; }

    /// <summary>The display form for messages: never contains a password or token.</summary>
    public string Describe() => Transport == GitTransport.Local ? CloneUrl : $"{Scheme}://{Host}";

    /// <summary>Parses and validates a repository URL.</summary>
    /// <exception cref="GitException"><see cref="GitFailureKind.InvalidUrl"/> for anything unusable.</exception>
    public static GitUrl Parse(string? repositoryUrl)
    {
        var trimmed = repositoryUrl?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new GitException(
                "the project has no repository URL configured.",
                GitFailureKind.InvalidUrl);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
        {
            return ParseAbsolute(trimmed, uri);
        }

        if (ScpLikePattern().Match(trimmed) is { Success: true } scp)
        {
            return new GitUrl(
                trimmed,
                "ssh",
                GitTransport.Ssh,
                scp.Groups["host"].Value,
                scp.Groups["user"].Value);
        }

        if (Path.IsPathRooted(trimmed))
        {
            return new GitUrl(trimmed, "file", GitTransport.Local, "(local)", null);
        }

        throw new GitException(
            $"the repository URL '{trimmed}' is not an http(s), ssh or file URL, and not a local path.",
            GitFailureKind.InvalidUrl);
    }

    private static GitUrl ParseAbsolute(string original, Uri uri)
    {
        switch (uri.Scheme)
        {
            case "http":
            case "https":
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    // Deliberately does not echo the user info: the whole point is that it is a secret.
                    throw new GitException(
                        $"the repository URL for '{uri.Host}' embeds credentials in the URL, which would put them "
                        + "on the git command line. Remove them from the URL and use a Git credential instead.",
                        GitFailureKind.InvalidUrl);
                }

                return new GitUrl(
                    uri.GetLeftPart(UriPartial.Path),
                    uri.Scheme,
                    GitTransport.Http,
                    HostOf(uri),
                    null);

            case "ssh":
                // ssh://git@host/path: the user name is an account, not a secret.
                return new GitUrl(original, uri.Scheme, GitTransport.Ssh, HostOf(uri), UserNameOf(uri));

            case "file":
                return new GitUrl(original, uri.Scheme, GitTransport.Local, "(local)", null);

            default:
                throw new GitException(
                    $"the repository URL scheme '{uri.Scheme}' is not supported (http, https, ssh, file).",
                    GitFailureKind.InvalidUrl);
        }
    }

    private static string HostOf(Uri uri) =>
        uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

    private static string? UserNameOf(Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo) ? null : uri.UserInfo.Split(':')[0];

}
