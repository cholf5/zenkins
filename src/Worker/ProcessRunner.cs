using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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

    /// <summary>
    /// The encoding a captured line is expected to be in (card B-016). Strict UTF-8 by default:
    /// the Worker's own tools, git, Node and .NET all write UTF-8 when their output is piped.
    /// </summary>
    public Encoding OutputEncoding { get; init; } = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The encoding used for a line <see cref="OutputEncoding"/> rejected, or <see langword="null"/>
    /// to decode it as UTF-8 with replacement. Defaults to the Windows OEM code page (936 on a
    /// Simplified Chinese host, 1252-family elsewhere); off Windows it is <see langword="null"/>.
    /// </summary>
    public Encoding? FallbackEncoding { get; init; } = WindowsCodePages.Oem;
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
    private readonly Encoding _outputEncoding;
    private readonly Encoding? _fallbackEncoding;
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
        _outputEncoding = options.OutputEncoding;
        _fallbackEncoding = options.FallbackEncoding;
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

        // B-016: read the bytes, not a StreamReader over the console's encoding. Each reader owns
        // one stream, so per-stream order is preserved; the two run concurrently, so there is no
        // promised cross-stream order (the same contract as the old Begin*ReadLine pair). The token
        // is deliberately not passed to ReadAsync: the kill closes the pipes, and the readers end
        // at EOF so the "pipes drained before return" promise survives.
        var stdout = ReadLinesAsync(process.StandardOutput.BaseStream, ProcessOutputStream.StdOut);
        var stderr = ReadLinesAsync(process.StandardError.BaseStream, ProcessOutputStream.StdError);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The process may or may not still be running (the cancellation can race its exit);
            // either way the tree must not survive and the handle must be reaped before surfacing.
            KillTree();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The handle was already reaped by a racing Dispose; the readers still end at EOF.
            }

            await DrainAsync(stdout, stderr);
            throw;
        }

        _exitCode = process.ExitCode;
        await Task.WhenAll(stdout, stderr);
    }

    /// <summary>
    /// Drains one stream's bytes into lines and decodes each one (card B-016). The primary encoding
    /// is strict, so a line the child wrote in the system code page does not become replacement
    /// characters; the fallback decodes exactly that line. A UTF-8 BOM is not text; it is removed
    /// from the first line only.
    /// </summary>
    private async Task ReadLinesAsync(Stream stream, ProcessOutputStream kind)
    {
        var buffer = new byte[16 * 1024];
        var pending = new List<byte>();
        var firstLine = true;
        var truncating = false;
        var lastWasCarriageReturn = false;

        void Emit(bool truncated = false)
        {
            var text = Decode(pending);
            pending.Clear();
            if (firstLine)
            {
                firstLine = false;
                if (text.Length > 0 && text[0] == '\uFEFF')
                {
                    text = text[1..];
                }
            }

            _onOutputLine?.Invoke(new ProcessOutputLine(
                kind,
                truncated ? text + "<line truncated>" : text));
        }

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer);
            }
            catch (IOException)
            {
                // The kill path closes the pipe under an in-flight read; that is EOF, not a run error.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (truncating)
                {
                    if (value == (byte)'\n')
                    {
                        lastWasCarriageReturn = false;
                        truncating = false;
                    }
                    else if (value == (byte)'\r')
                    {
                        lastWasCarriageReturn = true;
                        truncating = false;
                    }

                    continue;
                }

                if (value == (byte)'\n')
                {
                    if (lastWasCarriageReturn)
                    {
                        lastWasCarriageReturn = false;
                        continue;
                    }

                    Emit();
                }
                else if (value == (byte)'\r')
                {
                    Emit();
                    lastWasCarriageReturn = true;
                }
                else
                {
                    lastWasCarriageReturn = false;
                    pending.Add(value);
                    if (pending.Count >= MaxLineBytes)
                    {
                        Emit(truncated: true);
                        truncating = true;
                    }
                }
            }
        }

        if (pending.Count > 0)
        {
            Emit();
        }
    }

    private string Decode(List<byte> bytes)
    {
        try
        {
            return _outputEncoding.GetString([.. bytes]);
        }
        catch (DecoderFallbackException)
        {
            return _fallbackEncoding?.GetString([.. bytes]) ?? Encoding.UTF8.GetString([.. bytes]);
        }
    }

    private static async Task DrainAsync(params Task[] readers)
    {
        try
        {
            await Task.WhenAll(readers);
        }
        catch
        {
            // The run is already surfacing a cancellation; a reader callback that threw on the way
            // out must not mask it. The normal path surfaces reader failures through WhenAll.
        }
    }

    /// <summary>The most bytes one unterminated line may hold before it is truncated.</summary>
    private const int MaxLineBytes = 1024 * 1024;

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
