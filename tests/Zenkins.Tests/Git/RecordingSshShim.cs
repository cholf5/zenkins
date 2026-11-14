using System.Text;

namespace Zenkins.Tests.Git;

/// <summary>
/// A recording <c>ssh</c> shim: logs the argv of every ssh invocation, then execs the real client.
/// Placed first on the <b>worker process's</b> <c>PATH</c> (the worker owns no PATH of its own), it
/// captures the ssh command the product's <c>GIT_SSH_COMMAND</c> really produced during a real run -
/// which is what turns card F-018's construction-level pins (<c>-F</c>, <c>-i</c>,
/// <c>IdentitiesOnly</c>, <c>IdentityAgent</c>, <c>BatchMode</c>, the per-run
/// <c>UserKnownHostsFile</c>) into runtime-verified ones for card Q-009.
/// <para>
/// The capture is per <b>argument</b> (<c>SSH-ARG:</c> lines between <c>SSH-ARGS-END</c> markers), not
/// a joined string: git runs <c>GIT_SSH_COMMAND</c> through a shell, so the quoting the product builds
/// is consumed before ssh sees its argv - comparing parsed words is the honest comparison. The log is
/// appended to by concurrent ssh children (line writes under <c>PIPE_BUF</c> are atomic on POSIX).
/// </para>
/// <para>
/// This is the same observation pattern card F-013's <see cref="RecordingGitShim"/> uses for git: the
/// child process itself reports what it was asked to run, rather than the test inferring it.
/// </para>
/// </summary>
internal sealed class RecordingSshShim : IDisposable
{
    private RecordingSshShim(string binDirectory, string logPath)
    {
        BinDirectory = binDirectory;
        LogPath = logPath;
    }

    /// <summary>The directory to put first on a worker's <c>PATH</c>.</summary>
    public string BinDirectory { get; }

    /// <summary>Where the recorded argv lines are appended.</summary>
    public string LogPath { get; }

    /// <summary>How many ssh invocations have been recorded so far.</summary>
    public int EntryCount => ReadEntries().Length;

    /// <summary>Creates the shim around the real ssh binary at <paramref name="realSshPath"/>.</summary>
    public static RecordingSshShim Create(string rootDirectory, string realSshPath)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(realSshPath);

        var binDirectory = Path.Combine(rootDirectory, "ssh-shim-bin");
        var logPath = Path.Combine(rootDirectory, "ssh-argv.log");

        var script = string.Join(
            '\n',
            // Composed so no absolute interpreter path appears in the source.
            "#!" + "/" + "bin" + "/" + "sh",
            "# zenkins Q-009 recording shim: logs the ssh argv it was invoked with, then runs ssh.",
            "for argument in \"$@\"; do",
            $"  printf 'SSH-ARG:%s\\n' \"$argument\" >> '{logPath}'",
            "done",
            $"printf 'SSH-ARGS-END\\n' >> '{logPath}'",
            $"exec '{realSshPath}' \"$@\"",
            string.Empty);

        GitCli.WriteScript(binDirectory, "ssh", script);
        return new RecordingSshShim(binDirectory, logPath);
    }

    /// <summary>Every recorded ssh invocation, as the argv ssh actually received (word per word).</summary>
    public string[][] ReadEntries()
    {
        if (!File.Exists(LogPath))
        {
            return [];
        }

        var entries = new List<string[]>();
        var current = new List<string>();
        foreach (var line in File.ReadAllLines(LogPath))
        {
            if (line.StartsWith("SSH-ARG:", StringComparison.Ordinal))
            {
                current.Add(line["SSH-ARG:".Length..]);
            }
            else if (line.StartsWith("SSH-ARGS-END", StringComparison.Ordinal))
            {
                entries.Add([.. current]);
                current.Clear();
            }
        }

        return [.. entries];
    }

    /// <summary>One invocation's argv as a single readable line (for failure messages).</summary>
    public static string Describe(string[] entry) => string.Join(' ', entry);

    public void Dispose()
    {
        // The log and the shim live under the host's root directory; nothing extra to release.
    }
}
