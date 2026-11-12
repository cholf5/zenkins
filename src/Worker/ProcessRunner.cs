using System.ComponentModel;
using System.Diagnostics;

namespace Zenkins.Worker;

/// <summary>Which redirected stream a captured line came from.</summary>
public enum ProcessOutputStream
{
    StdOut,
    StdError,
}

/// <summary>One captured output line. Per-stream order is preserved; cross-stream order is not promised.</summary>
public sealed record ProcessOutputLine(ProcessOutputStream Stream, string Line);

/// <summary>
/// How to start one native process (card F-007, PRD §8 constraint 6): <see cref="FileName"/> is
/// argv[0] and every entry of <see cref="Arguments"/> lands in
/// <see cref="ProcessStartInfo.ArgumentList"/> verbatim - no shell, no quoting, no interpolation,
/// ever. <see cref="WorkingDirectory"/> is the run's <c>repository/</c> directory;
/// <see cref="EnvironmentVariables"/> are added on top of the inherited parent environment.
/// </summary>
public sealed class ProcessRunnerOptions
{
    public required string FileName { get; init; }

    /// <summary>argv[1..]; empty when the command has no arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Extra environment for the child (the rest is inherited from this process).</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>
/// Runs one native process with exact argv fidelity, streaming stdout/stderr line-by-line through a
/// callback (each stream in order; the runner streams - it does not wait for exit to hand over
/// output), capturing the exit code, and exposing a cross-platform process-tree kill (used by
/// cancellation; F-009 reuses <see cref="KillTree"/>).
/// <para>
/// <b>Dispose safety.</b> A consumer that abandons the runner without waiting for
/// <see cref="RunAsync"/> still leaves no zombie: <see cref="Dispose"/> kills the whole tree, and
/// the abandoned run's own completion path reaps the process handle (Dispose never disposes the
/// <see cref="Process"/> under an in-flight run, so the two never race on one handle). A Dispose
/// landing between the child's start and its assignment to the runner's field is covered as well:
/// the start path re-checks the disposed flag right after the assignment and kills the tree itself.
/// </para>
/// <para>
/// Cancellation of <see cref="RunAsync"/> kills the tree too, reaps the process (so the pipes drain
/// and no orphan survives), and surfaces <see cref="OperationCanceledException"/> - the caller
/// decides what that means (the worker loop treats it as shutdown; F-009 will own run cancellation).
/// </para>
/// </summary>
public sealed class ProcessRunner : IDisposable
{
    /// <summary>Grace period for the dispose path's kill-then-reap before giving up on the wait.</summary>
    private static readonly TimeSpan DisposeReapTimeout = TimeSpan.FromSeconds(5);

    private readonly ProcessStartInfo _startInfo;
    private readonly Action<ProcessOutputLine>? _onOutputLine;
    private Process? _process;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Set while <see cref="RunAsync"/> is in flight: the Dispose path then only kills the tree and
    /// lets the run's own completion path reap and dispose the process.
    /// </summary>
    private bool _runInFlight;

    private int _exitCode;

    public ProcessRunner(ProcessRunnerOptions options, Action<ProcessOutputLine>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _onOutputLine = onOutputLine;
        _startInfo = new ProcessStartInfo
        {
            FileName = options.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory,
        };
        foreach (var argument in options.Arguments)
        {
            _startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in options.EnvironmentVariables)
        {
            _startInfo.Environment[name] = value;
        }
    }

    /// <summary>Whether the started process has exited (true when never started).</summary>
    public bool HasExited
    {
        get
        {
            var process = _process;
            return process is null || process.HasExited;
        }
    }

    /// <summary>
    /// Starts the process and waits for its exit, streaming output lines as they are produced.
    /// Returns the captured exit code. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> fires (after killing and reaping the tree), and
    /// <see cref="Win32Exception"/> (among others) when the process could not be started at all.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException(
                "This ProcessRunner has already started its process; create a new one per run.");
        }

        _started = true;
        _runInFlight = true;
        var process = new Process { StartInfo = _startInfo };
        try
        {
            await RunCoreAsync(process, cancellationToken);
        }
        finally
        {
            _runInFlight = false;
            process.Dispose();
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }

        return _exitCode;
    }

    private async Task RunCoreAsync(Process process, CancellationToken cancellationToken)
    {
        // BeginOutputReadLine/BeginErrorReadLine each use their own dedicated reader thread, so
        // events for one stream arrive in order; the two streams are independent (no global order).
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                _onOutputLine?.Invoke(new ProcessOutputLine(ProcessOutputStream.StdOut, eventArgs.Data));
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                _onOutputLine?.Invoke(new ProcessOutputLine(ProcessOutputStream.StdError, eventArgs.Data));
            }
        };

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _process = process;
        if (_disposed)
        {
            // Dispose lost the race against this start: it saw _process still null and returned
            // without killing. Kill now, right after the assignment - the in-flight run's own
            // completion path reaps the handle afterwards (the _runInFlight guard keeps the two
            // paths from touching one Process concurrently). (F-007 review fix.)
            KillTree();
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The process may or may not still be running (the cancellation can race its exit);
            // either way the tree must not survive and the handle must be reaped before surfacing.
            KillTree();
            // The parameterless overload (like the normal path below) also waits for the
            // asynchronous output readers to drain - WaitForExitAsync does not - so no lingering
            // reader callback can fire into a consumer's already-disposed sink after the OCE
            // surfaces. This is what makes the "pipes drained" promise true on the cancellation
            // path too (F-007 review fix).
            process.WaitForExit();
            throw;
        }

        // The parameterless overload also waits for the asynchronous output readers to drain, so
        // every line has been delivered through the callback before the exit code is returned.
        process.WaitForExit();
        _exitCode = process.ExitCode;
    }

    /// <summary>
    /// Kills the whole process tree (child and all descendants), cross-platform. Safe to call
    /// repeatedly and after the process already exited. Public so F-009's cancellation can reuse it.
    /// </summary>
    public void KillTree()
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill - nothing left to do.
        }
        catch (Win32Exception)
        {
            // Same race, surfaced by the platform APIs (e.g. "Cannot process request because the
            // process has exited" or an access-denied on an already-dead pid).
        }
    }

    /// <summary>
    /// Kills the tree if still running and releases the runner. A consumer that abandons a runner
    /// without waiting for <see cref="RunAsync"/> still leaves no zombie: the tree is killed here,
    /// and the abandoned run's own completion path reaps the process.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var process = _process;
        if (process is null)
        {
            return;
        }

        if (!process.HasExited)
        {
            KillTree();
        }

        if (_runInFlight)
        {
            // The abandoned run reaps and disposes the handle when its wait completes.
            return;
        }

        if (!process.HasExited)
        {
            process.WaitForExit(DisposeReapTimeout);
        }

        process.Dispose();
        _process = null;
    }
}
