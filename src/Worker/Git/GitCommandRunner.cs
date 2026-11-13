using Zenkins.Worker;

namespace Zenkins.Worker.Git;

/// <summary>One recorded git invocation (the trace the idempotency tests assert against).</summary>
/// <param name="Arguments">The argv after <c>git</c>, already redacted, joined with spaces.</param>
/// <param name="ExitCode">The exit code, or <c>null</c> when the process never produced one.</param>
public sealed record GitTraceEntry(string Arguments, int? ExitCode);

/// <summary>One git invocation's captured result. <see cref="Lines"/> are already redacted.</summary>
public sealed record GitCommandResult(int ExitCode, IReadOnlyList<string> Lines, string StandardErrorTail)
{
    /// <summary>Whether git reported success.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>What to run: argv, working directory, environment, and this operation's own budget.</summary>
public sealed record GitInvocation(
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan Timeout,
    bool EchoOutput = true);

/// <summary>
/// The only place the worker starts a <c>git</c> process (card F-013, PRD §8 constraint 6).
/// <para>
/// <b>argv only.</b> Every argument is added through <see cref="ProcessStartInfo.ArgumentList"/> by
/// <see cref="ProcessRunner"/>; nothing here builds a shell string, so no argument can be re-split or
/// re-interpreted - which is also why the credential never has to be quoted into a command line.
/// </para>
/// <para>
/// <b>Budget and kill.</b> Each invocation runs under its own timeout through a linked token, so an
/// expired budget kills the whole process tree with F-009's semantics
/// (<see cref="ProcessRunner"/>'s cancellation path) and surfaces as
/// <see cref="GitFailureKind.Timeout"/>. Cancellation by the caller is distinguished from that.
/// </para>
/// <para>
/// <b>Redaction and trace.</b> Output lines pass through the run's <see cref="GitLogFilter"/> before
/// they reach the sink or the captured result, and the trace records the redacted argv - both are
/// asserted by the secrecy tests.
/// </para>
/// </summary>
public sealed class GitCommandRunner
{
    /// <summary>How many captured output lines one invocation keeps (clone output can be long).</summary>
    public const int CapturedLineLimit = 2_000;

    /// <summary>How many trailing stderr lines a failure message may quote.</summary>
    public const int ErrorTailLines = 12;

    private readonly List<GitTraceEntry> _trace = [];
    private readonly object _gate = new();
    private readonly string _gitExecutable;
    private readonly GitLogFilter _filter;
    private readonly Action<string>? _sink;

    /// <param name="gitExecutable">argv[0]; <c>git</c> from <c>PATH</c> in production.</param>
    /// <param name="filter">The run's redaction filter (already holds the credential).</param>
    /// <param name="sink">Where redacted git output goes (the run log).</param>
    public GitCommandRunner(string gitExecutable, GitLogFilter filter, Action<string>? sink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutable);
        ArgumentNullException.ThrowIfNull(filter);
        _gitExecutable = gitExecutable;
        _filter = filter;
        _sink = sink;
    }

    /// <summary>The redacted invocations so far, in order.</summary>
    public IReadOnlyList<GitTraceEntry> Trace
    {
        get
        {
            lock (_gate)
            {
                return [.. _trace];
            }
        }
    }

    /// <summary>Runs one git invocation and captures its result.</summary>
    /// <exception cref="GitException">
    /// <see cref="GitFailureKind.Timeout"/>, <see cref="GitFailureKind.Canceled"/> or
    /// <see cref="GitFailureKind.GitUnavailable"/> (the executable could not be started).
    /// </exception>
    public async Task<GitCommandResult> RunAsync(
        GitInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Arguments.Count == 0)
        {
            throw new ArgumentException("A git invocation needs at least one argument.", nameof(invocation));
        }

        var lines = new List<string>();
        var standardError = new List<string>();

        void OnLine(ProcessOutputLine line)
        {
            var redacted = _filter.Redact(line.Line);
            lock (_gate)
            {
                if (lines.Count < CapturedLineLimit)
                {
                    lines.Add(redacted);
                }
            }

            if (line.Stream == ProcessOutputStream.StdError)
            {
                lock (_gate)
                {
                    standardError.Add(redacted);
                    if (standardError.Count > ErrorTailLines * 4)
                    {
                        standardError.RemoveAt(0);
                    }
                }
            }

            if (!invocation.EchoOutput)
            {
                return;
            }

            _sink?.Invoke(redacted);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (invocation.Timeout > TimeSpan.Zero)
        {
            budget.CancelAfter(invocation.Timeout);
        }

        int exitCode;
        using (var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = _gitExecutable,
                Arguments = invocation.Arguments,
                WorkingDirectory = invocation.WorkingDirectory,
                EnvironmentVariables = invocation.Environment,
            },
            OnLine))
        {
            try
            {
                exitCode = await runner.RunAsync(budget.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Record(invocation, exitCode: null);
                throw new GitException(
                    $"git {Describe(invocation)} was canceled; its process tree was killed.",
                    GitFailureKind.Canceled);
            }
            catch (OperationCanceledException)
            {
                // The budget fired (not the caller's token): the tree is killed and reaped by the
                // runner before this surfaces, so no partial git process survives the timeout.
                Record(invocation, exitCode: null);
                throw new GitException(
                    $"git {Describe(invocation)} exceeded its {invocation.Timeout.TotalSeconds:0.#}s budget "
                    + "and its process tree was killed.",
                    GitFailureKind.Timeout);
            }
            catch (System.ComponentModel.Win32Exception failure)
            {
                Record(invocation, exitCode: null);
                throw new GitException(
                    $"the git executable '{_gitExecutable}' could not be started ({failure.Message}). "
                    + "Install git and make sure it is on the worker's PATH.",
                    GitFailureKind.GitUnavailable,
                    failure);
            }
            catch (InvalidOperationException failure)
            {
                Record(invocation, exitCode: null);
                throw new GitException(
                    $"the git process could not be started in '{invocation.WorkingDirectory}' ({failure.Message}).",
                    GitFailureKind.Failed,
                    failure);
            }
        }

        Record(invocation, exitCode);

        var tail = string.Join(" / ", TakeLast(standardError, ErrorTailLines));
        return new GitCommandResult(exitCode, [.. lines], tail);
    }

    /// <summary>The redacted display form of an invocation, used in every message.</summary>
    public static string Describe(GitInvocation invocation) =>
        invocation.Arguments.Count == 0
            ? "(no arguments)"
            : string.Join(' ', invocation.Arguments.Select(RedactArgumentShape));

    private void Record(GitInvocation invocation, int? exitCode)
    {
        lock (_gate)
        {
            _trace.Add(new GitTraceEntry(Describe(invocation), exitCode));
        }
    }

    /// <summary>
    /// A defensive second line for the trace and the messages: any argument that looks like a URL with
    /// embedded user info is reduced to its scheme and host. The product never builds such an argument
    /// (<see cref="GitUrl"/> refuses), so this only ever masks a bug - which is the point.
    /// </summary>
    private static string RedactArgumentShape(string argument)
    {
        var schemeEnd = argument.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return argument;
        }

        var at = argument.IndexOf('@', schemeEnd);
        var slash = argument.IndexOf('/', schemeEnd + 3);
        if (at <= schemeEnd || (slash >= 0 && at > slash))
        {
            return argument;
        }

        // The same replacement token the run's log filter uses (F-015): one marker everywhere, so nobody has
        // to reason about which token means "a value was removed here".
        return string.Concat(argument.AsSpan(0, schemeEnd + 3), GitLogFilter.Replacement, "@", argument.AsSpan(at + 1));
    }

    private static IEnumerable<string> TakeLast(List<string> values, int count)
    {
        var start = Math.Max(0, values.Count - count);
        for (var index = start; index < values.Count; index++)
        {
            yield return values[index];
        }
    }
}
