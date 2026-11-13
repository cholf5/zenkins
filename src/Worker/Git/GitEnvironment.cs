namespace Zenkins.Worker.Git;

/// <summary>
/// The child environment every git invocation runs under (card F-013). One place builds it, so the
/// "credentials never travel in argv" property is a property of this class rather than of each call
/// site.
/// <para>
/// <b>HTTPS.</b> <c>GIT_TERMINAL_PROMPT=0</c> makes git fail instead of hanging on a prompt when
/// there is no credential, and <c>GIT_ASKPASS</c> points at the generated helper that answers the
/// password prompt. The token is in the helper file (0700, deleted after the operation), never in the
/// environment of the run's command and never on a command line.
/// </para>
/// <para>
/// <b>SSH.</b> <c>GIT_SSH_COMMAND</c> is built here from fixed tokens and quoted paths:
/// <c>ssh -i &lt;key&gt; -o IdentitiesOnly=yes -o StrictHostKeyChecking=&lt;policy&gt;
/// -o UserKnownHostsFile=&lt;file&gt;</c>. <c>IdentitiesOnly=yes</c> keeps an unrelated agent key out
/// of the exchange; <c>UserKnownHostsFile</c> pins the file so a worker never writes into the
/// operator's <c>~/.ssh/known_hosts</c>.
/// </para>
/// <para>
/// <b>The documented MITM trade-off.</b> Without <c>ZENKINS_GIT_KNOWN_HOSTS</c> the policy is
/// <c>accept-new</c> against a per-run known-hosts file: the first connection to a host is trusted.
/// Setting the knob pins the file and switches the policy to <c>yes</c>, which makes an unknown host
/// key a hard failure.
/// </para>
/// </summary>
public static class GitEnvironment
{
    /// <summary>Suppresses git's interactive credential prompt.</summary>
    public const string TerminalPromptVariable = "GIT_TERMINAL_PROMPT";

    /// <summary>Names the helper git asks for the user name and password.</summary>
    public const string AskPassVariable = "GIT_ASKPASS";

    /// <summary>Overrides the ssh transport program and its fixed options.</summary>
    public const string SshCommandVariable = "GIT_SSH_COMMAND";

    /// <summary>Trust-on-first-use policy: used when the operator has not pinned a known-hosts file.</summary>
    public const string DefaultHostKeyPolicy = "accept-new";

    /// <summary>Strict policy: used when <c>ZENKINS_GIT_KNOWN_HOSTS</c> points at a pinned file.</summary>
    public const string PinnedHostKeyPolicy = "yes";

    /// <summary>The environment shared by every invocation: no interactive prompt, ever.</summary>
    public static Dictionary<string, string?> Base() => new(StringComparer.Ordinal)
    {
        [TerminalPromptVariable] = "0",
    };

    /// <summary>Base plus the ask-pass helper for an HTTPS token credential.</summary>
    public static Dictionary<string, string?> ForHttpsToken(GitCredentialFiles carrier)
    {
        ArgumentNullException.ThrowIfNull(carrier);

        var environment = Base();
        if (carrier.AskPassPath is { } askPassPath)
        {
            environment[AskPassVariable] = askPassPath;
        }

        return environment;
    }

    /// <summary>Base plus the pinned key file and host-key policy for an SSH credential.</summary>
    public static Dictionary<string, string?> ForSshKey(
        GitCredentialFiles carrier,
        string knownHostsPath,
        string hostKeyPolicy)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentException.ThrowIfNullOrWhiteSpace(knownHostsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKeyPolicy);

        var environment = Base();
        if (carrier.SshKeyPath is not { } keyPath)
        {
            throw new GitException(
                "an SSH credential is configured but no key file was materialised for this operation.",
                GitFailureKind.Credential);
        }

        environment[SshCommandVariable] = BuildSshCommand(keyPath, knownHostsPath, hostKeyPolicy);
        return environment;
    }

    /// <summary>
    /// The exact <c>GIT_SSH_COMMAND</c> value. Unit-pinned by the SSH construction test: the options
    /// and their order are part of the contract, not an implementation detail.
    /// </summary>
    public static string BuildSshCommand(string keyPath, string knownHostsPath, string hostKeyPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(knownHostsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKeyPolicy);

        return string.Join(
            ' ',
            "ssh",
            "-i",
            Quote(keyPath),
            "-o",
            "IdentitiesOnly=yes",
            "-o",
            $"StrictHostKeyChecking={hostKeyPolicy}",
            "-o",
            $"UserKnownHostsFile={Quote(knownHostsPath)}");
    }

    /// <summary>
    /// Quotes one path as a double-quoted word for git's shell-style <c>GIT_SSH_COMMAND</c> parser.
    /// The escapes are the ones that parser understands (<c>\"</c> and <c>\\</c>).
    /// </summary>
    internal static string Quote(string path) =>
        "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
