namespace Zenkins.Worker.Git;

/// <summary>
/// How one run's Git layer is configured (card F-013). The knobs come from
/// <see cref="WorkerConfiguration"/> (<c>ZENKINS_GIT_DEPTH</c>, <c>ZENKINS_GIT_KNOWN_HOSTS</c>,
/// <c>ZENKINS_GIT_TIMEOUT_SECONDS</c>); the per-run target comes from
/// <see cref="RunWorkDirectory"/>.
/// </summary>
public sealed class GitWorkspaceOptions
{
    /// <summary>The run's directory trio: the clone goes to <c>repository/</c>, credentials to <c>temp/</c>.</summary>
    public required RunWorkDirectory Work { get; init; }

    /// <summary>argv[0] of every git invocation; <c>git</c> from <c>PATH</c> in production.</summary>
    public string GitExecutable { get; init; } = "git";

    /// <summary>
    /// <c>ZENKINS_GIT_DEPTH</c>: <c>0</c> (the default) is a full clone; a positive value clones with
    /// <c>--depth</c>. Only meaningful for a transport git can actually truncate (http/https, ssh,
    /// <c>file://</c>): git ignores <c>--depth</c> for a plain local directory, which is why the
    /// shallow-clone test uses a <c>file://</c> URL and asserts
    /// <c>git rev-parse --is-shallow-repository</c> rather than the flag alone.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    /// <c>ZENKINS_GIT_KNOWN_HOSTS</c>: when set, SSH host keys are checked strictly against this file.
    /// When unset the policy is <c>accept-new</c> against a per-run known-hosts file (the documented
    /// trust-on-first-use trade-off, never the operator's <c>~/.ssh/known_hosts</c>).
    /// </summary>
    public string? KnownHostsPath { get; init; }

    /// <summary><c>ZENKINS_GIT_TIMEOUT_SECONDS</c>: the budget of one git invocation.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// The run's remaining wall clock, frozen by the server at accept time (F-009). An invocation's
    /// budget is the smaller of <see cref="OperationTimeout"/> and what is left of this, so a clone can
    /// never outlive the run it belongs to.
    /// </summary>
    public DateTimeOffset? RunDeadline { get; init; }

    /// <summary>
    /// Extra environment for the git child (tests set <c>PATH</c> and a git config override here). The
    /// inherited environment is kept; these entries are added on top.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>Where this layer's own lines go (already redacted). The run log in production.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>The outcome of a clone: where the workspace sits, and what the run should report.</summary>
/// <param name="Url">The validated, credential-free repository URL.</param>
/// <param name="Branch">The branch that was cloned.</param>
/// <param name="Commit">The resolved <c>HEAD</c> object name (never a branch label).</param>
/// <param name="IsShallow">Whether git reports a shallow repository.</param>
/// <param name="Warnings">Honest, credential-free warnings the run log carries (e.g. missing git-lfs).</param>
public sealed record GitCloneResult(
    GitUrl Url,
    string Branch,
    string Commit,
    bool IsShallow,
    IReadOnlyList<string> Warnings);

/// <summary>The outcome of a <c>checkout()</c>: the pinned commit and whether anything had to move.</summary>
/// <param name="Commit">The full object name now at <c>HEAD</c>.</param>
/// <param name="AlreadyAtCommit">True when the workspace already sat at the pinned commit (a no-op).</param>
public sealed record GitCheckoutResult(string Commit, bool AlreadyAtCommit);

/// <summary>
/// One run's Git workspace (card F-013): a fresh clone of the project's repository into
/// <c>work/{run-id}/repository</c> at a branch, the exact commit it resolved, and an idempotent
/// <c>checkout()</c> back to that commit.
/// <para>
/// <b>Fresh clone per run, no shared object cache.</b> Isolation is the F-007 property the product
/// sells; a cache would be a later optimisation with its own corruption story. A clone is refused when
/// the target already holds anything, so a retried run never silently inherits the previous tree.
/// </para>
/// <para>
/// <b>The credential never reaches argv.</b> It is registered with the <see cref="GitLogFilter"/>
/// before the first command runs, materialised into <c>temp/</c> for the duration of one invocation
/// (ask-pass helper 0700 for HTTPS, key file 0600 for SSH) and deleted in a <c>finally</c> - on the
/// failure and timeout paths too. The URL handed to git is credential-free
/// (<see cref="GitUrl.Parse"/> refuses one that embeds a secret).
/// </para>
/// <para>
/// <b>Commit resolution is explicit.</b> <c>git rev-parse HEAD</c> runs twice, with a
/// <c>git status --porcelain</c> sanity check in between, and the two readings must agree: the reported
/// commit is an object name that did not move while it was being read, never something derived from a
/// branch label.
/// </para>
/// <para>
/// <b>LFS is honest.</b> When the repository declares LFS and a <c>git-lfs</c> binary answers, the
/// pointers are materialised; when the binary is missing the run logs exactly one warning naming
/// <c>git-lfs</c> and continues - it never leaves pointer files behind silently. Submodules are cloned
/// with <c>--recurse-submodules</c> under the same credential environment.
/// </para>
/// </summary>
public sealed class GitWorkspace
{
    /// <summary>The marker git writes into <c>.gitattributes</c> for an LFS-managed path.</summary>
    public const string LfsAttributeMarker = "filter=lfs";

    private readonly GitWorkspaceOptions _options;
    private readonly GitCommandRunner _git;
    private readonly GitLogFilter _filter = new();

    private GitUrl? _url;
    private string? _pinnedCommit;
    private GitCredentialMaterial? _credential;

    public GitWorkspace(GitWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _git = new GitCommandRunner(options.GitExecutable, _filter, options.Log);
    }

    /// <summary>The run's redaction filter; the credential is registered here before any command runs.</summary>
    public GitLogFilter Filter => _filter;

    /// <summary>The redacted invocations so far (the idempotency tests assert on this).</summary>
    public IReadOnlyList<GitTraceEntry> Trace => _git.Trace;

    /// <summary>The pinned commit, once a clone has resolved one.</summary>
    public string? PinnedCommit => _pinnedCommit;

    /// <summary>The absolute path the run's repository lives at.</summary>
    public string RepositoryPath => _options.Work.RepositoryPath;

    /// <summary>
    /// Clones <paramref name="repositoryUrl"/> at <paramref name="branch"/> into the run's
    /// <c>repository/</c>, resolves the commit, materialises LFS content (or warns honestly) and
    /// returns what the run should report.
    /// </summary>
    public async Task<GitCloneResult> CloneAsync(
        string? repositoryUrl,
        string? branch,
        GitCredentialMaterial? credential,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate everything before touching the file system, so a bad configuration is a clear
        //    message rather than a half-built tree.
        var url = GitUrl.Parse(repositoryUrl);
        var branchName = ValidateBranch(branch);
        var target = ValidateTarget();

        // 2. Register the secret with the filter BEFORE the first git command: from here on, no line
        //    this workspace writes - and nothing git prints - can carry it verbatim.
        _credential = credential;
        _filter.Register(credential);

        var warnings = new List<string>();
        var carrier = GitCredentialFiles.Materialise(_options.Work.TempPath, credential);
        try
        {
            var environment = BuildEnvironment(url, carrier, credential);

            var arguments = new List<string> { "clone" };

            // Submodules are part of the checkout (PRD §3.3); the same credential environment applies
            // to them because every child inherits it.
            arguments.Add("--recurse-submodules");

            if (_options.Depth > 0)
            {
                arguments.Add("--depth");
                arguments.Add(_options.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
                arguments.Add("--shallow-submodules");
            }

            arguments.Add("--branch");
            arguments.Add(branchName);

            // "--" ends option parsing: neither the URL nor the target can be read as a flag.
            arguments.Add("--");
            arguments.Add(url.CloneUrl);
            arguments.Add(target);

            var clone = await RunAsync(arguments, Path.GetDirectoryName(target)!, environment, cancellationToken);
            if (!clone.Succeeded)
            {
                throw ClassifyCloneFailure(clone, url, branchName);
            }

            await MaterialiseLfsAsync(target, warnings, cancellationToken);
            var commit = await ResolveCommitAsync(cancellationToken);
            var isShallow = await IsShallowAsync(cancellationToken);

            _url = url;
            _pinnedCommit = commit;
            return new GitCloneResult(url, branchName, commit, isShallow, warnings);
        }
        finally
        {
            // Deleted on every path: success, failure, timeout, cancellation. No ask-pass helper and
            // no key file outlives the operation that needed it.
            carrier.Dispose();
        }
    }

    /// <summary>
    /// Resolves the checked-out commit: <c>rev-parse HEAD</c> twice with a <c>status --porcelain</c>
    /// sanity check between the readings. The two readings must agree, otherwise the tree moved under
    /// the reader and the value is not reportable.
    /// </summary>
    public async Task<string> ResolveCommitAsync(CancellationToken cancellationToken = default)
    {
        var first = await RevParseHeadAsync(cancellationToken);

        var status = await RunAsync(
            ["status", "--porcelain"],
            RepositoryPath,
            LocalEnvironment(),
            cancellationToken,
            echoOutput: false);
        if (status.Succeeded && status.Lines.Count > 0)
        {
            Warn(
                $"the working tree is not clean after checkout ({status.Lines.Count} entries); "
                + $"the reported commit is still {first}.");
        }

        var second = await RevParseHeadAsync(cancellationToken);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new GitException(
                $"HEAD moved while it was being read ({first} then {second}); refusing to report a commit "
                + "that was not stable.",
                GitFailureKind.Failed);
        }

        return first;
    }

    /// <summary>
    /// Whether git reports the workspace as a shallow clone
    /// (<c>git rev-parse --is-shallow-repository</c>). Exposed because <c>--depth</c> is silently
    /// ignored on some transports, so the configuration is verified through git's own answer rather
    /// than through the flag that was passed.
    /// </summary>
    public async Task<bool> IsShallowAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["rev-parse", "--is-shallow-repository"],
            RepositoryPath,
            LocalEnvironment(),
            cancellationToken,
            echoOutput: false);
        return result.Succeeded
            && result.Lines.Count > 0
            && string.Equals(result.Lines[0].Trim(), "true", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>checkout()</c>'s implementation: idempotent and pinned.
    /// <para>
    /// Already at the requested commit: a no-op - no fetch is issued, so the tree cannot change and
    /// the trace shows only the two read-only commands. Otherwise: fetch, re-check-out the commit
    /// detached, and verify that <c>HEAD</c> really is the requested object.
    /// </para>
    /// </summary>
    /// <param name="commit">An object name, or <see langword="null"/> for the run's pinned commit.</param>
    public async Task<GitCheckoutResult> CheckoutAsync(
        string? commit = null,
        CancellationToken cancellationToken = default)
    {
        var requested = commit?.Trim();
        if (string.IsNullOrEmpty(requested))
        {
            requested = _pinnedCommit
                ?? throw new GitException(
                    "checkout() was called before a commit was pinned for this run; clone the repository first.",
                    GitFailureKind.Failed);
        }

        if (!GitCommitSyntax.IsValid(requested))
        {
            throw new GitException(
                $"'{requested}' is not a commit object name (a hex sha); checkout() takes the commit the run "
                + "is pinned to, never a branch or a revision expression.",
                GitFailureKind.Failed);
        }

        var head = await RevParseHeadAsync(cancellationToken);
        if (head.StartsWith(requested, StringComparison.Ordinal)
            || requested.StartsWith(head, StringComparison.Ordinal))
        {
            // Idempotent no-op: nothing is fetched and nothing is written.
            return new GitCheckoutResult(head, AlreadyAtCommit: true);
        }

        var url = _url ?? throw new GitException(
            "checkout() was called before the repository was cloned.",
            GitFailureKind.Failed);

        var carrier = GitCredentialFiles.Materialise(_options.Work.TempPath, _credential);
        try
        {
            var environment = BuildEnvironment(url, carrier, _credential);

            var fetch = await RunAsync(
                ["fetch", "--prune", "--tags", "--force", "origin"],
                RepositoryPath,
                environment,
                cancellationToken);
            if (!fetch.Succeeded)
            {
                // Offline or refused: the object may still be local, so the checkout below decides.
                Warn($"fetching before checkout did not succeed ({Summarize(fetch)}); trying the local objects.");
            }

            var checkout = await RunAsync(
                ["checkout", "--detach", "--force", requested],
                RepositoryPath,
                environment,
                cancellationToken);
            if (!checkout.Succeeded)
            {
                throw new GitException(
                    $"could not check out commit {requested} in '{DisplayName(url)}' ({Summarize(checkout)}).",
                    GitFailureKind.Failed);
            }

            var resolved = await RunAsync(
                ["rev-parse", "--verify", requested],
                RepositoryPath,
                LocalEnvironment(),
                cancellationToken,
                echoOutput: false);
            var expected = resolved.Succeeded && resolved.Lines.Count > 0 ? resolved.Lines[0].Trim() : requested;
            var actual = await RevParseHeadAsync(cancellationToken);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new GitException(
                    $"checkout of commit {requested} left HEAD at {actual}, not at the requested commit.",
                    GitFailureKind.Failed);
            }

            return new GitCheckoutResult(actual, AlreadyAtCommit: false);
        }
        finally
        {
            carrier.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Credential environment
    // ------------------------------------------------------------------

    private Dictionary<string, string?> BuildEnvironment(
        GitUrl url,
        GitCredentialFiles carrier,
        GitCredentialMaterial? credential)
    {
        Dictionary<string, string?> environment;
        switch (url.Transport)
        {
            case GitTransport.Http when credential is { Kind: GitCredentialKind.HttpsToken }:
                environment = GitEnvironment.ForHttpsToken(carrier);
                break;

            case GitTransport.Http when credential is { Kind: GitCredentialKind.SshKey }:
                throw new GitException(
                    "the project's Git credential is an SSH key but the repository URL uses http(s); "
                    + "use an SSH URL or store an HTTPS token.",
                    GitFailureKind.Credential);

            case GitTransport.Http:
                environment = GitEnvironment.Base();
                break;

            case GitTransport.Ssh when credential is { Kind: GitCredentialKind.SshKey }:
                environment = GitEnvironment.ForSshKey(
                    carrier,
                    KnownHostsFile(),
                    _options.KnownHostsPath is null
                        ? GitEnvironment.DefaultHostKeyPolicy
                        : GitEnvironment.PinnedHostKeyPolicy);
                break;

            case GitTransport.Ssh when credential is { Kind: GitCredentialKind.HttpsToken }:
                throw new GitException(
                    "the project's Git credential is an HTTPS token but the repository URL uses ssh; "
                    + "use an http(s) URL or store an SSH key.",
                    GitFailureKind.Credential);

            default:
                environment = GitEnvironment.Base();
                break;
        }

        foreach (var (name, value) in _options.EnvironmentOverrides)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    /// The known-hosts file for this run: the configured pin when
    /// <c>ZENKINS_GIT_KNOWN_HOSTS</c> is set, otherwise an empty per-run file - never the operator's
    /// <c>~/.ssh/known_hosts</c>, which a worker must not write to.
    /// </summary>
    private string KnownHostsFile()
    {
        if (!string.IsNullOrWhiteSpace(_options.KnownHostsPath))
        {
            var configured = Path.GetFullPath(_options.KnownHostsPath);
            if (!File.Exists(configured))
            {
                throw new GitException(
                    $"ZENKINS_GIT_KNOWN_HOSTS points at '{configured}', which does not exist.",
                    GitFailureKind.Credential);
            }

            return configured;
        }

        var perRun = Path.Combine(_options.Work.TempPath, "known_hosts");
        if (!File.Exists(perRun))
        {
            File.WriteAllText(perRun, string.Empty);
        }

        return perRun;
    }

    // ------------------------------------------------------------------
    // LFS
    // ------------------------------------------------------------------

    private async Task MaterialiseLfsAsync(
        string repository,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!DeclaresLfs(repository))
        {
            return;
        }

        var probe = await RunAsync(
            ["lfs", "version"],
            repository,
            LocalEnvironment(),
            cancellationToken,
            echoOutput: false);

        if (!probe.Succeeded)
        {
            // Exactly one warning, naming the missing tool: the run continues, and the operator can
            // tell from the log why the LFS payload is still a pointer file.
            Warn(
                "git-lfs is not installed on this worker, so the LFS content of this repository was left as "
                + "pointer files. Install git-lfs to materialise them.",
                warnings);
            return;
        }

        var install = await RunAsync(
            ["lfs", "install", "--local"],
            repository,
            LocalEnvironment(),
            cancellationToken);
        if (!install.Succeeded)
        {
            throw new GitException(
                $"git-lfs is installed but 'git lfs install --local' failed ({Summarize(install)}).",
                GitFailureKind.Failed);
        }

        var pull = await RunAsync(
            ["lfs", "pull"],
            repository,
            LocalEnvironment(),
            cancellationToken);
        if (!pull.Succeeded)
        {
            throw new GitException(
                $"git-lfs is installed but 'git lfs pull' failed ({Summarize(pull)}).",
                GitFailureKind.Failed);
        }
    }

    /// <summary>Whether the checked-out tree declares any LFS-managed path.</summary>
    private static bool DeclaresLfs(string repository)
    {
        var attributes = Path.Combine(repository, ".gitattributes");
        if (!File.Exists(attributes))
        {
            return false;
        }

        foreach (var line in File.ReadLines(attributes))
        {
            if (line.Contains(LfsAttributeMarker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Validation, failure classification, small helpers
    // ------------------------------------------------------------------

    private string ValidateBranch(string? branch)
    {
        var name = branch?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new GitException(
                "the project has no default branch configured.",
                GitFailureKind.BranchNotFound);
        }

        if (name.Length > 255
            || name.StartsWith('-')
            || name.Contains("..", StringComparison.Ordinal)
            || name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            // The name is passed as its own argv entry, so this is not a quoting problem; it is a
            // refusal to hand git a value that could be read as an option or a revision expression.
            throw new GitException(
                $"the branch name '{name}' is not a usable ref name.",
                GitFailureKind.BranchNotFound);
        }

        return name;
    }

    private string ValidateTarget()
    {
        var target = Path.GetFullPath(_options.Work.RepositoryPath);
        var root = Path.GetFullPath(_options.Work.RootPath);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!target.StartsWith(rootWithSeparator, comparison))
        {
            throw new GitException(
                $"the clone target '{target}' escapes the run's work root '{root}'; refusing to touch it.",
                GitFailureKind.PathEscape);
        }

        if (File.Exists(target))
        {
            throw new GitException(
                $"the clone target '{target}' is a file, not an empty directory.",
                GitFailureKind.NonEmptyTarget);
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new GitException(
                $"the clone target '{target}' is not empty; a run always starts from a fresh clone.",
                GitFailureKind.NonEmptyTarget);
        }

        Directory.CreateDirectory(target);
        return target;
    }

    private GitException ClassifyCloneFailure(GitCommandResult result, GitUrl url, string branch)
    {
        var tail = Summarize(result);
        var text = string.Join('\n', result.Lines);
        var display = DisplayName(url);

        if (Contains(text, "Remote branch") && Contains(text, "not found")
            || Contains(text, "couldn't find remote ref")
            || Contains(text, "Could not find remote branch"))
        {
            return new GitException(
                $"the branch '{branch}' does not exist in '{display}'. "
                + "Check the project's default branch and the repository URL.",
                GitFailureKind.BranchNotFound);
        }

        if (Contains(text, "Authentication failed")
            || Contains(text, "could not read Username")
            || Contains(text, "could not read Password")
            || Contains(text, "Permission denied (publickey)")
            || Contains(text, "terminal prompts disabled")
            || Contains(text, "Invalid username or password")
            || Contains(text, "HTTP Basic: Access denied"))
        {
            return new GitException(
                $"the remote '{display}' rejected the credential for this project (git exit {result.ExitCode}: {tail}).",
                GitFailureKind.AuthenticationRejected);
        }

        if (Contains(text, "Could not resolve host")
            || Contains(text, "Connection refused")
            || Contains(text, "Connection timed out")
            || Contains(text, "unable to access")
            || Contains(text, "SSL certificate")
            || Contains(text, "Network is unreachable"))
        {
            return new GitException(
                $"the remote '{display}' could not be reached (git exit {result.ExitCode}: {tail}).",
                GitFailureKind.Unreachable);
        }

        return new GitException(
            $"git clone of the branch '{branch}' from '{display}' failed (git exit {result.ExitCode}: {tail}).",
            GitFailureKind.Failed);
    }

    private async Task<string> RevParseHeadAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["rev-parse", "--verify", "HEAD"],
            RepositoryPath,
            LocalEnvironment(),
            cancellationToken,
            echoOutput: false);
        if (!result.Succeeded || result.Lines.Count == 0)
        {
            throw new GitException(
                $"the repository at '{RepositoryPath}' has no readable HEAD commit ({Summarize(result)}).",
                GitFailureKind.Failed);
        }

        return result.Lines[0].Trim();
    }

    private Task<GitCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken,
        bool echoOutput = true) =>
        _git.RunAsync(
            new GitInvocation(arguments, workingDirectory, environment, EffectiveTimeout(), echoOutput),
            cancellationToken);

    /// <summary>
    /// The environment for the read-only/plumbing commands (rev-parse, status, lfs): no credential
    /// plumbing, but the operator's overrides - which is what lets a test's <c>PATH</c> reach the
    /// <c>git-lfs</c> stub and git's config overrides apply.
    /// </summary>
    private Dictionary<string, string?> LocalEnvironment()
    {
        var environment = GitEnvironment.Base();
        foreach (var (name, value) in _options.EnvironmentOverrides)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    /// One invocation's budget: the configured per-operation timeout, cut down by what is left of the
    /// run's own deadline so a clone can never outlive its run (F-009 semantics on expiry).
    /// </summary>
    private TimeSpan EffectiveTimeout()
    {
        var timeout = _options.OperationTimeout;
        if (_options.RunDeadline is not { } deadline)
        {
            return timeout;
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        return remaining < timeout ? remaining : timeout;
    }

    private void Warn(string message, List<string>? sink = null)
    {
        _options.Log?.Invoke(_filter.Redact(message));
        sink?.Add(_filter.Redact(message));
    }

    private static string DisplayName(GitUrl url) =>
        url.Transport == GitTransport.Local ? url.CloneUrl : url.Describe();

    private static string Summarize(GitCommandResult result) =>
        string.IsNullOrEmpty(result.StandardErrorTail)
            ? "no stderr output"
            : result.StandardErrorTail;

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
