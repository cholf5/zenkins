using Zenkins.Shared.Probes;

namespace Zenkins.Worker.Git;

/// <summary>How one probe should run: where its scratch files live, which git, and its budget.</summary>
public sealed class GitRefsProbeOptions
{
    /// <summary>
    /// The probe's own scratch directory (<c>work/probes/{probe-id}</c>): the credential carrier and the
    /// hardening config files live here and are deleted with the operation.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>Where the per-operation credential files are materialised (the probe's <c>temp/</c>).</summary>
    public required string TempPath { get; init; }

    /// <summary>Where the empty git/ssh config files live (the probe's <c>hardening/</c>).</summary>
    public required string HardeningPath { get; init; }

    /// <summary>argv[0] of every git invocation; <c>git</c> from <c>PATH</c> in production.</summary>
    public string GitExecutable { get; init; } = "git";

    /// <summary>The budget of the one <c>git ls-remote</c> invocation (the probe's timeout).</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary><c>ZENKINS_GIT_KNOWN_HOSTS</c>: a pinned known-hosts file for SSH remotes, or null.</summary>
    public string? KnownHostsPath { get; init; }

    /// <summary>Extra child environment (tests set <c>PATH</c> and git config overrides here).</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>Where the probe's own lines go (already redacted). The worker console in production.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// One remote probe's answer in the worker's own terms: the shared outcome word plus the refs it names.
/// The worker maps <see cref="GitFailureKind"/> onto <see cref="GitProbeOutcomes"/> here and nowhere else,
/// so "wrong token" can never travel as "no branches".
/// </summary>
public sealed record GitRefsProbeResult(
    string Outcome,
    GitProbeRef[] Heads,
    string? HeadSha,
    string? HeadBranch,
    string? Message)
{
    /// <summary>A success result.</summary>
    public static GitRefsProbeResult Success(GitProbeRef[] heads, string? headSha, string? headBranch) =>
        new(GitProbeOutcomes.Refs, heads, headSha, headBranch, Message: null);

    /// <summary>A typed failure (the git conversation failed; the message says how).</summary>
    public static GitRefsProbeResult Failure(string outcome, string message) =>
        new(outcome, [], null, null, message);
}

/// <summary>
/// Card I-005's git half: <c>git ls-remote</c> against a repository URL with the project's delivered
/// credential, answered as heads + remote HEAD or as a typed failure.
/// <para>
/// <b>No clone, no work tree.</b> One invocation in the probe's own scratch directory lists the
/// remote's <c>refs/heads/*</c> and its <c>HEAD</c> in a single round trip, so "which branches exist"
/// costs one network conversation and no disk. The pattern form (<c>refs/heads/*</c> and <c>HEAD</c>) is
/// the same set <c>--heads</c> selects, plus the HEAD line and its <c>--symref</c> target - which is why
/// the remote's default branch is known without a second call.
/// </para>
/// <para>
/// <b>Credential plumbing is not re-invented.</b> The environment comes from
/// <see cref="GitCredentialEnvironment"/> - the same composition the clone uses - and the material is
/// materialised into the probe's scratch directory and deleted on every path. The value is registered
/// with the redaction filter before git runs, so the worker's own log cannot carry it.
/// </para>
/// <para>
/// <b>One budget, one kill.</b> The invocation runs under
/// <see cref="GitRefsProbeOptions.OperationTimeout"/> through the shared
/// <see cref="GitCommandRunner"/>, whose expiry kills the whole process tree and surfaces as
/// <see cref="GitFailureKind.Timeout"/> - the probe's <c>git-timeout</c>.
/// </para>
/// </summary>
public sealed class GitRefsProbe
{
    private readonly GitRefsProbeOptions _options;
    private readonly GitLogFilter _filter = new();
    private readonly GitCommandRunner _git;

    public GitRefsProbe(GitRefsProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _git = new GitCommandRunner(options.GitExecutable, _filter, options.Log);
    }

    /// <summary>The redacted invocations so far (the tests' "exactly one git request" evidence).</summary>
    public IReadOnlyList<GitTraceEntry> Trace => _git.Trace;

    /// <summary>
    /// Lists the remote's heads and HEAD. Never throws for a git failure - every failure is one of the
    /// shared outcome words, with a message a person can act on.
    /// </summary>
    public async Task<GitRefsProbeResult> ProbeAsync(
        string? repositoryUrl,
        GitCredentialMaterial? credential,
        CancellationToken cancellationToken = default)
    {
        GitUrl url;
        try
        {
            url = GitUrl.Parse(repositoryUrl);
        }
        catch (GitException invalid)
        {
            return GitRefsProbeResult.Failure(
                MapOutcome(invalid.Kind),
                $"the repository URL cannot be probed ({invalid.Message})");
        }

        // Register the secret with the filter BEFORE the first command: nothing git prints and nothing
        // this probe writes can carry it verbatim.
        _filter.Register(credential);

        var display = url.Transport == GitTransport.Local ? url.CloneUrl : url.Describe();
        var carrier = GitCredentialFiles.Materialise(_options.TempPath, credential);
        try
        {
            var environment = GitCredentialEnvironment.Build(
                url,
                credential,
                carrier,
                _options.KnownHostsPath,
                _options.HardeningPath,
                _options.EnvironmentOverrides);

            var result = await _git.RunAsync(
                new GitInvocation(
                    // --symref names the branch HEAD is a symbolic ref to; the two patterns are the
                    // heads set plus HEAD itself, so one invocation answers both questions.
                    ["ls-remote", "--symref", "--", url.CloneUrl, $"{GitProbeRules.HeadPrefix}*", "HEAD"],
                    _options.RootPath,
                    environment,
                    _options.OperationTimeout,
                    EchoOutput: false),
                cancellationToken);

            if (!result.Succeeded)
            {
                return ClassifyFailure(result, display);
            }

            return ParseHeads(result.Lines, display);
        }
        catch (GitException failure)
        {
            return GitRefsProbeResult.Failure(MapOutcome(failure.Kind), failure.Message);
        }
        finally
        {
            // Deleted on every path: success, failure, timeout, cancellation. No ask-pass helper and no
            // key file outlives the probe that needed it.
            carrier.Dispose();
        }
    }

    /// <summary>
    /// Parses git's <c>ls-remote --symref</c> output: <c>&lt;sha&gt;\t&lt;ref&gt;</c> lines plus the
    /// <c>ref: &lt;target&gt;\tHEAD</c> line that names the remote's default branch.
    /// </summary>
    private static GitRefsProbeResult ParseHeads(IReadOnlyList<string> lines, string display)
    {
        var heads = new List<GitProbeRef>();
        string? headSha = null;
        string? headBranch = null;

        foreach (var line in lines)
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            var left = line[..tab].Trim();
            var right = line[(tab + 1)..].Trim();

            // "ref: refs/heads/main" + "HEAD": the symbolic target, not an object name.
            if (left.StartsWith("ref:", StringComparison.Ordinal))
            {
                if (string.Equals(right, "HEAD", StringComparison.Ordinal))
                {
                    headBranch = left["ref:".Length..].Trim();
                }

                continue;
            }

            if (string.Equals(right, "HEAD", StringComparison.Ordinal))
            {
                headSha = left;
                continue;
            }

            if (GitProbeRules.IsHeadName(right) && GitProbeRules.IsObjectName(left))
            {
                heads.Add(new GitProbeRef(right, left));
            }
        }

        if (heads.Count > GitProbeRules.MaxHeads)
        {
            // A refusal the worker can explain, rather than a report the Server would 400: the cap is the
            // shared contract's, so both sides refuse at the same number.
            return GitRefsProbeResult.Failure(
                GitProbeOutcomes.Failed,
                $"'{display}' advertises {heads.Count} branches, which is above the probe's limit of "
                + $"{GitProbeRules.MaxHeads}; not reporting an answer that would be silently truncated.");
        }

        return GitRefsProbeResult.Success(
            [.. heads.OrderBy(head => head.Name, StringComparer.Ordinal)],
            headSha,
            headBranch);
    }

    /// <summary>
    /// The typed mapping: git's two named failure families become the card's outcomes, everything else is
    /// <see cref="GitProbeOutcomes.Failed"/> with git's own (redacted) words. Nothing here can produce an
    /// empty ref list, which is the property branch discovery depends on.
    /// </summary>
    private static GitRefsProbeResult ClassifyFailure(GitCommandResult result, string display)
    {
        var known = GitFailureClassification.ClassifyKnown(result, out _, out var tail);
        var detail = string.IsNullOrEmpty(tail) ? "no stderr output" : tail;

        return known switch
        {
            GitFailureKind.AuthenticationRejected => GitRefsProbeResult.Failure(
                GitProbeOutcomes.AuthRejected,
                $"the remote '{display}' rejected the credential for this project "
                + $"(git exit {result.ExitCode}: {detail})."),
            GitFailureKind.Unreachable => GitRefsProbeResult.Failure(
                GitProbeOutcomes.Unreachable,
                $"the remote '{display}' could not be reached (git exit {result.ExitCode}: {detail})."),
            _ => GitRefsProbeResult.Failure(
                GitProbeOutcomes.Failed,
                $"git ls-remote against '{display}' failed (git exit {result.ExitCode}: {detail})."),
        };
    }

    /// <summary>The one worker-side mapping from <see cref="GitFailureKind"/> to a probe outcome.</summary>
    private static string MapOutcome(GitFailureKind kind) => kind switch
    {
        GitFailureKind.AuthenticationRejected => GitProbeOutcomes.AuthRejected,
        GitFailureKind.Unreachable => GitProbeOutcomes.Unreachable,
        GitFailureKind.Timeout => GitProbeOutcomes.Timeout,
        _ => GitProbeOutcomes.Failed,
    };
}
