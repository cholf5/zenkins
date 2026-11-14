namespace Zenkins.Worker.Git;

/// <summary>
/// The child environment every git invocation runs under (card F-013, hardened by card F-018). One
/// place builds it, so the "credentials never travel in argv" property is a property of this class
/// rather than of each call site.
/// <para>
/// <b>HTTPS.</b> <c>GIT_TERMINAL_PROMPT=0</c> makes git fail instead of hanging on a prompt when
/// there is no credential, and <c>GIT_ASKPASS</c> points at the generated helper that answers the
/// password prompt. The token is in the helper file (0700, deleted after the operation), never in the
/// environment of the run's command and never on a command line.
/// </para>
/// <para>
/// <b>Nothing from the host (card F-018).</b> The worker authenticates with the credential the server
/// delivered for <em>this run</em> and with nothing else, so the inherited sources of a credential are
/// cut off in <see cref="Base"/>, which every composition starts from:
/// </para>
/// <list type="bullet">
/// <item><c>GIT_CONFIG_GLOBAL</c> points at an empty file and <c>GIT_CONFIG_NOSYSTEM=1</c>: the
/// operator's <c>~/.gitconfig</c> and <c>/etc/gitconfig</c> cannot contribute a
/// <c>credential.helper</c>, a <c>url.*.insteadOf</c> or an ask-pass program.</item>
/// <item><c>credential.helper</c> is additionally reset through git's
/// <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_0</c>/<c>GIT_CONFIG_VALUE_0</c> mechanism, so a helper
/// configured in the repository itself (a cloned <c>.git/config</c>, a submodule) is dropped too.</item>
/// <item><c>GIT_ASKPASS</c> is set to the empty string, so an ask-pass program exported by the host
/// (a desktop credential dialog, a CI shim) answers nothing.</item>
/// <item><c>SSH_AUTH_SOCK</c> is set to the empty string: no ssh-agent on the host can offer a key.</item>
/// <item><c>GIT_SSH_COMMAND</c> is built here from fixed tokens and quoted paths, and it always names
/// an ssh config file of its own (<c>-F</c>), so <c>~/.ssh/config</c> cannot add an
/// <c>IdentityFile</c>, a <c>ProxyCommand</c> or a <c>ForwardAgent</c>.</item>
/// </list>
/// <para>
/// <b>Why this is a product rule, not tidiness.</b> Before card F-018 a project had no way to be
/// assigned a credential, so every run "worked" with whatever key sat on the worker machine. That is
/// exactly what the product must not do silently: the run page states that no credential is assigned,
/// and the clone then either succeeds anonymously or fails with the remote's own rejection. The
/// trade-off is deliberate and documented: a deployment that relied on the worker host's global git
/// configuration (a private CA, a proxy) must now express it per run instead.
/// </para>
/// <para>
/// <b>SSH.</b> With a delivered key: <c>ssh -F &lt;empty config&gt; -i &lt;key&gt; -o IdentitiesOnly=yes
/// -o IdentityAgent=none -o StrictHostKeyChecking=&lt;policy&gt; -o UserKnownHostsFile=&lt;file&gt;</c>.
/// Without one (no credential assigned): the same shape minus <c>-i</c>, with <c>BatchMode=yes</c>, so
/// ssh offers no identity at all instead of reaching for <c>~/.ssh/id_*</c>.
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

    /// <summary>The ssh agent socket; emptied so a host agent cannot offer a key.</summary>
    public const string SshAuthSockVariable = "SSH_AUTH_SOCK";

    /// <summary>Points git at the per-run empty global config instead of the operator's.</summary>
    public const string GlobalConfigVariable = "GIT_CONFIG_GLOBAL";

    /// <summary>Keeps <c>/etc/gitconfig</c> out of every invocation.</summary>
    public const string NoSystemConfigVariable = "GIT_CONFIG_NOSYSTEM";

    /// <summary>How many config key/value pairs are injected through the environment (git ≥ 2.31).</summary>
    public const string ConfigCountVariable = "GIT_CONFIG_COUNT";

    /// <summary>The file name of the empty git config the hardening points <c>GIT_CONFIG_GLOBAL</c> at.</summary>
    public const string EmptyGitConfigFileName = "gitconfig.hardened";

    /// <summary>The file name of the empty ssh config the hardening points <c>-F</c> at.</summary>
    public const string EmptySshConfigFileName = "ssh_config.hardened";

    /// <summary>The config key the hardening resets (an empty value clears the helper list).</summary>
    public const string CredentialHelperKey = "credential.helper";

    /// <summary>Trust-on-first-use policy: used when the operator has not pinned a known-hosts file.</summary>
    public const string DefaultHostKeyPolicy = "accept-new";

    /// <summary>Strict policy: used when <c>ZENKINS_GIT_KNOWN_HOSTS</c> points at a pinned file.</summary>
    public const string PinnedHostKeyPolicy = "yes";

    /// <summary>The index-qualified name of the <c>GIT_CONFIG_KEY_n</c> variable.</summary>
    public static string ConfigKeyVariable(int index) => $"GIT_CONFIG_KEY_{index}";

    /// <summary>The index-qualified name of the <c>GIT_CONFIG_VALUE_n</c> variable.</summary>
    public static string ConfigValueVariable(int index) => $"GIT_CONFIG_VALUE_{index}";

    /// <summary>
    /// The environment every invocation starts from: no interactive prompt, and no credential or
    /// identity source the host might hold (see the type's remarks). <paramref name="hardeningDirectory"/>
    /// is the run's <c>hardening/</c> directory, where the empty config files live; it must be writable.
    /// </summary>
    public static Dictionary<string, string?> Base(string hardeningDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardeningDirectory);

        var gitConfigPath = EnsureEmptyFile(Path.Combine(hardeningDirectory, EmptyGitConfigFileName));
        return new(StringComparer.Ordinal)
        {
            [TerminalPromptVariable] = "0",
            [AskPassVariable] = string.Empty,
            [SshAuthSockVariable] = string.Empty,
            [GlobalConfigVariable] = gitConfigPath,
            [NoSystemConfigVariable] = "1",
            [ConfigCountVariable] = "1",
            [ConfigKeyVariable(0)] = CredentialHelperKey,
            [ConfigValueVariable(0)] = string.Empty,
        };
    }

    /// <summary>Base plus the ask-pass helper for an HTTPS token credential.</summary>
    public static Dictionary<string, string?> ForHttpsToken(GitCredentialFiles carrier, string hardeningDirectory)
    {
        ArgumentNullException.ThrowIfNull(carrier);

        var environment = Base(hardeningDirectory);
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
        string hostKeyPolicy,
        string hardeningDirectory)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentException.ThrowIfNullOrWhiteSpace(knownHostsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKeyPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardeningDirectory);

        var environment = Base(hardeningDirectory);
        if (carrier.SshKeyPath is not { } keyPath)
        {
            throw new GitException(
                "an SSH credential is configured but no key file was materialised for this operation.",
                GitFailureKind.Credential);
        }

        environment[SshCommandVariable] = BuildSshCommand(
            keyPath,
            knownHostsPath,
            hostKeyPolicy,
            EnsureEmptyFile(Path.Combine(hardeningDirectory, EmptySshConfigFileName)));
        return environment;
    }

    /// <summary>
    /// Base plus an ssh command that offers <b>no</b> identity (card F-018): the project has no
    /// credential assigned, so ssh must not fall back to the host's keys or agent. Used for an SSH
    /// remote with no credential; the clone then succeeds only if the remote allows anonymous access.
    /// </summary>
    public static Dictionary<string, string?> ForSshWithoutCredential(
        string knownHostsPath,
        string hostKeyPolicy,
        string hardeningDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knownHostsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKeyPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardeningDirectory);

        var environment = Base(hardeningDirectory);
        environment[SshCommandVariable] = BuildNoIdentitySshCommand(
            knownHostsPath,
            hostKeyPolicy,
            EnsureEmptyFile(Path.Combine(hardeningDirectory, EmptySshConfigFileName)));
        return environment;
    }

    /// <summary>
    /// The exact <c>GIT_SSH_COMMAND</c> value for a delivered key. Unit-pinned by the SSH construction
    /// test: the options and their order are part of the contract, not an implementation detail.
    /// </summary>
    public static string BuildSshCommand(string keyPath, string knownHostsPath, string hostKeyPolicy, string sshConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(knownHostsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKeyPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(sshConfigPath);

        return string.Join(
            ' ',
            "ssh",
            "-F",
            Quote(sshConfigPath),
            "-i",
            Quote(keyPath),
            "-o",
            "IdentitiesOnly=yes",
            "-o",
            "IdentityAgent=none",
            "-o",
            $"StrictHostKeyChecking={hostKeyPolicy}",
            "-o",
            $"UserKnownHostsFile={Quote(knownHostsPath)}");
    }

    /// <summary>
    /// The exact <c>GIT_SSH_COMMAND</c> value when the project has no credential: the same ssh
    /// options, no <c>-i</c>, with <c>BatchMode=yes</c> so a missing identity fails instead of
    /// prompting. Pinned by the same test.
    /// </summary>
    public static string BuildNoIdentitySshCommand(string knownHostsPath, string hostKeyPolicy, string sshConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knownHostsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostKeyPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(sshConfigPath);

        return string.Join(
            ' ',
            "ssh",
            "-F",
            Quote(sshConfigPath),
            "-o",
            "IdentitiesOnly=yes",
            "-o",
            "IdentityAgent=none",
            "-o",
            "BatchMode=yes",
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

    /// <summary>Creates (once per run) the empty file git or ssh is pointed at, and returns its path.</summary>
    private static string EnsureEmptyFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty);
        }

        return path;
    }
}
