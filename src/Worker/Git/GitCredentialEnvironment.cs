namespace Zenkins.Worker.Git;

/// <summary>
/// The one composition of a git child's credential environment, shared by the clone path (card F-013)
/// and the remote-probe path (card I-005).
/// <para>
/// <b>Why one helper instead of a copy per feature.</b> The property the product sells is "git runs with
/// the credential the Server delivered for this operation and with nothing else". A second feature that
/// composed its own environment could silently reintroduce the host's credential sources - so the probe
/// does not build an environment, it calls this, and a review of the isolation story stays one file.
/// </para>
/// </summary>
public static class GitCredentialEnvironment
{
    /// <summary>
    /// The environment for one git invocation against <paramref name="url"/>.
    /// </summary>
    /// <param name="url">The validated repository URL (its transport selects the credential plumbing).</param>
    /// <param name="credential">The material for this operation, or null for an anonymous remote.</param>
    /// <param name="carrier">The materialised ask-pass/key files (see <see cref="GitCredentialFiles"/>).</param>
    /// <param name="knownHostsPath">The pinned known-hosts file (<c>ZENKINS_GIT_KNOWN_HOSTS</c>), or null.</param>
    /// <param name="hardeningDirectory">Where the empty git/ssh config files live (the run's/probe's temp tree).</param>
    /// <param name="overrides">The worker's own overrides (tests set <c>PATH</c> and git config here).</param>
    /// <exception cref="GitException">
    /// A credential whose kind does not match the transport (an SSH key on an <c>https</c> URL, or a token
    /// on an <c>ssh</c> one): a deployment mistake worth naming instead of a mystery failure.
    /// </exception>
    public static Dictionary<string, string?> Build(
        GitUrl url,
        GitCredentialMaterial? credential,
        GitCredentialFiles carrier,
        string? knownHostsPath,
        string hardeningDirectory,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(carrier);

        Dictionary<string, string?> environment;
        switch (url.Transport)
        {
            case GitTransport.Http when credential is { Kind: GitCredentialKind.HttpsToken }:
                environment = GitEnvironment.ForHttpsToken(carrier, hardeningDirectory);
                break;

            case GitTransport.Http when credential is { Kind: GitCredentialKind.SshKey }:
                throw new GitException(
                    "the project's Git credential is an SSH key but the repository URL uses http(s); "
                    + "use an SSH URL or store an HTTPS token.",
                    GitFailureKind.Credential);

            case GitTransport.Http:
                // No credential for an http(s) remote: run anonymously. The base environment already
                // dropped every helper, so a token stored on the host cannot answer the challenge.
                environment = GitEnvironment.Base(hardeningDirectory);
                break;

            case GitTransport.Ssh when credential is { Kind: GitCredentialKind.SshKey }:
                environment = GitEnvironment.ForSshKey(
                    carrier,
                    KnownHostsFile(carrier, knownHostsPath),
                    HostKeyPolicy(knownHostsPath),
                    hardeningDirectory);
                break;

            case GitTransport.Ssh when credential is { Kind: GitCredentialKind.HttpsToken }:
                throw new GitException(
                    "the project's Git credential is an HTTPS token but the repository URL uses ssh; "
                    + "use an http(s) URL or store an SSH key.",
                    GitFailureKind.Credential);

            case GitTransport.Ssh:
                // No credential for an ssh remote: offer no identity at all, so a key sitting in the
                // worker's ~/.ssh is not used.
                environment = GitEnvironment.ForSshWithoutCredential(
                    KnownHostsFile(carrier, knownHostsPath),
                    HostKeyPolicy(knownHostsPath),
                    hardeningDirectory);
                break;

            default:
                environment = GitEnvironment.Base(hardeningDirectory);
                break;
        }

        if (overrides is not null)
        {
            foreach (var (name, value) in overrides)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary>The base environment (no credential plumbing) plus the worker's overrides.</summary>
    public static Dictionary<string, string?> Base(
        string hardeningDirectory,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var environment = GitEnvironment.Base(hardeningDirectory);
        if (overrides is not null)
        {
            foreach (var (name, value) in overrides)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary>
    /// The known-hosts file for this operation: the configured pin when <c>ZENKINS_GIT_KNOWN_HOSTS</c> is
    /// set, otherwise an empty per-operation file - never the operator's <c>~/.ssh/known_hosts</c>, which a
    /// worker must not write to.
    /// </summary>
    public static string KnownHostsFile(GitCredentialFiles carrier, string? configuredPath)
    {
        ArgumentNullException.ThrowIfNull(carrier);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Path.GetFullPath(configuredPath);
            if (!File.Exists(configured))
            {
                throw new GitException(
                    $"ZENKINS_GIT_KNOWN_HOSTS points at '{configured}', which does not exist.",
                    GitFailureKind.Credential);
            }

            return configured;
        }

        var perOperation = Path.Combine(carrier.Directory, "known_hosts");
        if (!File.Exists(perOperation))
        {
            File.WriteAllText(perOperation, string.Empty);
        }

        return perOperation;
    }

    /// <summary>The host-key policy the configured pin selects (strict when pinned, TOFU otherwise).</summary>
    public static string HostKeyPolicy(string? configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath)
            ? GitEnvironment.DefaultHostKeyPolicy
            : GitEnvironment.PinnedHostKeyPolicy;
}

/// <summary>
/// The failure classification both git paths share (cards F-013 and I-005): "the remote refused the
/// credential" and "the remote could not be reached" are read from git's own stderr, in the two message
/// families git actually emits.
/// <para>
/// <b>Why one table.</b> The probe maps these two kinds onto the typed outcomes
/// (<c>git-auth-rejected</c>, <c>git-unreachable</c>); the clone throws them as
/// <see cref="GitFailureKind"/>. Two copies would drift, and then a wrong token would be a typed failure
/// in one path and a generic one in the other.
/// </para>
/// </summary>
public static class GitFailureClassification
{
    /// <summary>
    /// The auth/unreachable verdict for <paramref name="result"/>, or <see langword="null"/> when git's
    /// output names neither. The message is the caller's own (the clone and the probe describe their
    /// operation differently), so this returns the kind and the matched text only.
    /// </summary>
    /// <param name="result">The failed invocation.</param>
    /// <param name="stderrTail">The redacted stderr tail, for the message the caller builds.</param>
    public static GitFailureKind? ClassifyKnown(GitCommandResult result, out string stdoutAndTail, out string stderrTail)
    {
        ArgumentNullException.ThrowIfNull(result);

        stdoutAndTail = string.Join('\n', result.Lines);
        stderrTail = result.StandardErrorTail;

        if (Contains(stdoutAndTail, "Authentication failed")
            || Contains(stdoutAndTail, "could not read Username")
            || Contains(stdoutAndTail, "could not read Password")
            || Contains(stdoutAndTail, "Permission denied (publickey)")
            || Contains(stdoutAndTail, "terminal prompts disabled")
            || Contains(stdoutAndTail, "Invalid username or password")
            || Contains(stdoutAndTail, "HTTP Basic: Access denied"))
        {
            return GitFailureKind.AuthenticationRejected;
        }

        if (Contains(stdoutAndTail, "Could not resolve host")
            || Contains(stdoutAndTail, "Connection refused")
            || Contains(stdoutAndTail, "Connection timed out")
            || Contains(stdoutAndTail, "unable to access")
            || Contains(stdoutAndTail, "SSL certificate")
            || Contains(stdoutAndTail, "Network is unreachable"))
        {
            return GitFailureKind.Unreachable;
        }

        return null;
    }

    /// <summary>
    /// Classifies a failed clone. <b>The order is part of the diagnosis</b>, not an implementation detail: a
    /// missing branch first, then the shared auth/unreachable table, then the generic fallback. git prints
    /// several message families in one stderr (a bogus branch of a private repository can mention both the
    /// ref and a rejected credential), and the branch answer is the one the operator can act on - it names
    /// the setting that is wrong. This lives here, beside the two-family table it shares with the probe, and
    /// is <c>internal</c> so a test can pin the priority and the empty-stderr wording directly.
    /// </summary>
    /// <param name="result">The failed clone invocation.</param>
    /// <param name="displayName">The sanitised repository name for the message.</param>
    /// <param name="branch">The branch the clone asked for.</param>
    public static GitException ClassifyCloneFailure(
        GitCommandResult result,
        string displayName,
        string branch)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(branch);

        // The tail always has a value ("no stderr output" when git said nothing on stderr), so no message
        // below can end in a bare ": )".
        var tail = Summarize(result);
        var text = string.Join('\n', result.Lines);

        // 1. The branch: the clone's own vocabulary (the probe has no branch), and the answer that names a
        //    fixable setting.
        if (Contains(text, "Remote branch") && Contains(text, "not found")
            || Contains(text, "couldn't find remote ref")
            || Contains(text, "Could not find remote branch"))
        {
            return new GitException(
                $"the branch '{branch}' does not exist in '{displayName}'. "
                + "Check the project's default branch and the repository URL.",
                GitFailureKind.BranchNotFound);
        }

        // 2/3. The two families both git paths share (the probe maps the same kinds onto its typed outcomes).
        return ClassifyKnown(result, out _, out _) switch
        {
            GitFailureKind.AuthenticationRejected => new GitException(
                $"the remote '{displayName}' rejected the credential for this project (git exit {result.ExitCode}: {tail}).",
                GitFailureKind.AuthenticationRejected),
            GitFailureKind.Unreachable => new GitException(
                $"the remote '{displayName}' could not be reached (git exit {result.ExitCode}: {tail}).",
                GitFailureKind.Unreachable),

            // 4. Anything else: git's own words, never a guessed kind.
            _ => new GitException(
                $"git clone of the branch '{branch}' from '{displayName}' failed (git exit {result.ExitCode}: {tail}).",
                GitFailureKind.Failed),
        };
    }

    /// <summary>The trailing stderr lines a message may quote, or the honest stand-in when git was silent.</summary>
    public static string Summarize(GitCommandResult result) =>
        string.IsNullOrEmpty(result.StandardErrorTail)
            ? "no stderr output"
            : result.StandardErrorTail;

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
