using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Zenkins.Tests.TestAssets.RecordingShim;

/// <summary>
/// The compiled form of the F-013 recording shims (card B-004). On POSIX the shims stay
/// <c>#!/bin/sh</c> scripts - that is what puts the observation inside the child's exec path - but
/// Windows cannot execute a shebang script through its own <c>Process.Start</c> ("not a valid
/// application for this OS platform"). This asset makes the same observations from a real executable:
/// the recording git shim logs the argv and the credential environment it was handed and then runs the
/// real git, and the hanging-git stub records its own pid plus its sleeping child's pid and waits.
/// <para>
/// <b>Parameterised by the environment</b>, because one binary serves every test run and every test
/// directory (the POSIX script bakes those values into its text instead):
/// <list type="bullet">
/// <item><c>ZENKINS_RECORDING_SHIM_ROLE</c> - <c>git</c> (record and exec) or <c>hang</c> (record pids
/// and wait). Required: the executable's own name cannot carry the role, because the file copied onto
/// a <c>PATH</c> has to keep a name the platform will execute.</item>
/// <item><c>ZENKINS_RECORDING_SHIM_LOG</c> - where the recording git shim appends its lines.</item>
/// <item><c>ZENKINS_RECORDING_SHIM_REAL</c> - the real <c>git</c> the recording shim execs.</item>
/// <item><c>ZENKINS_RECORDING_SHIM_PID_FILE</c> - where the hanging stub appends its two pids.</item>
/// <item><c>ZENKINS_RECORDING_SHIM_SLEEP_SECONDS</c> - the sleeping child's life span (internal to the
/// hanging stub; 300 s unless set).</item>
/// </list>
/// </para>
/// <para>
/// <b>The log format is the POSIX scripts', byte for byte.</b> The recording git shim writes the same
/// twelve lines per invocation (<c>ARGV:</c>, <c>ASKPASS:</c>, <c>SSH:</c>, <c>PROMPT:</c>,
/// <c>GLOBAL:</c>, <c>NOSYSTEM:</c>, <c>CONFIGCOUNT:</c>, <c>HELPERKEY:</c>, <c>HELPERVALUE:</c>,
/// <c>AUTHSOCK:</c>, <c>HOME:</c>, <c>-----</c>) with an empty value where the variable is unset, and
/// newline-terminated lines rather than <see cref="Environment.NewLine"/>, so a log written on Windows
/// is the same text a log written on POSIX is. <c>The_posix_script_and_the_compiled_shim_agree_line_for_line</c>
/// (Zenkins.Tests/Git/RecordingShimAssetTests.cs) is the guard for that promise.
/// </para>
/// </summary>
internal static class Program
{
    internal const string RoleVariable = "ZENKINS_RECORDING_SHIM_ROLE";
    internal const string LogVariable = "ZENKINS_RECORDING_SHIM_LOG";
    internal const string RealVariable = "ZENKINS_RECORDING_SHIM_REAL";
    internal const string PidFileVariable = "ZENKINS_RECORDING_SHIM_PID_FILE";
    internal const string SleepSecondsVariable = "ZENKINS_RECORDING_SHIM_SLEEP_SECONDS";

    internal const string GitRole = "git";
    internal const string HangRole = "hang";
    internal const string SleepRole = "sleep";

    /// <summary>Exit code a shell reports for "command not found" (what <c>exec</c> would have failed with).</summary>
    private const int NotRunnableExitCode = 127;

    private const int UsageExitCode = 64;

    public static int Main(string[] args)
    {
        switch (Environment.GetEnvironmentVariable(RoleVariable))
        {
            case GitRole:
                return RecordGitInvocation(args);
            case HangRole:
                return RunHangingGit();
            case SleepRole:
                return Sleep();
            default:
                Console.Error.WriteLine(
                    $"usage: RecordingShim with {RoleVariable}={GitRole}|{HangRole} (the role is never guessed).");
                return UsageExitCode;
        }
    }

    /// <summary>
    /// The recording git shim: append this invocation's argv and credential environment, then hand the
    /// process over to the real git and propagate its exit code. The child inherits this process's
    /// stdout/stderr, so git's own output reaches the worker exactly as it would without a shim.
    /// </summary>
    private static int RecordGitInvocation(string[] args)
    {
        var logPath = Required(LogVariable);
        var realGitPath = Required(RealVariable);
        if (logPath is null || realGitPath is null)
        {
            return UsageExitCode;
        }

        // Line for line what the POSIX script prints: "$*" is the argv joined with spaces, and
        // "${NAME-}" is the value or the empty string (never "unset").
        var block = string.Join(
            '\n',
            "ARGV:" + string.Join(' ', args),
            "ASKPASS:" + Variable("GIT_ASKPASS"),
            "SSH:" + Variable("GIT_SSH_COMMAND"),
            "PROMPT:" + Variable("GIT_TERMINAL_PROMPT"),
            "GLOBAL:" + Variable("GIT_CONFIG_GLOBAL"),
            "NOSYSTEM:" + Variable("GIT_CONFIG_NOSYSTEM"),
            "CONFIGCOUNT:" + Variable("GIT_CONFIG_COUNT"),
            "HELPERKEY:" + Variable("GIT_CONFIG_KEY_0"),
            "HELPERVALUE:" + Variable("GIT_CONFIG_VALUE_0"),
            "AUTHSOCK:" + Variable("SSH_AUTH_SOCK"),
            "HOME:" + Variable("HOME"),
            "-----") + "\n";

        Append(logPath, block);
        return Exec(realGitPath, args);
    }

    /// <summary>
    /// The hanging-git stub: record this process's pid, start a long-lived child, record the child's
    /// pid, then wait - so a test can prove the timeout killed the whole tree, not just the parent.
    /// </summary>
    private static int RunHangingGit()
    {
        var pidFile = Required(PidFileVariable);
        var self = Environment.ProcessPath;
        if (pidFile is null || self is null)
        {
            return pidFile is null ? UsageExitCode : NotRunnableExitCode;
        }

        Append(pidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");

        // The child is this same asset in its sleeping role: a long-lived process on every OS, where
        // the POSIX stub forks `sleep 300`.
        var startInfo = new ProcessStartInfo(self) { UseShellExecute = false };
        startInfo.Environment[RoleVariable] = SleepRole;
        startInfo.Environment[SleepSecondsVariable] = "300";

        using var child = Start(startInfo);
        if (child is null)
        {
            return NotRunnableExitCode;
        }

        Append(pidFile, child.Id.ToString(CultureInfo.InvariantCulture) + "\n");

        // `wait` in the POSIX stub returns the child's status; so does this.
        child.WaitForExit();
        return child.ExitCode;
    }

    private static int Sleep()
    {
        var seconds = 300;
        if (int.TryParse(
                Environment.GetEnvironmentVariable(SleepSecondsVariable),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var configured)
            && configured > 0)
        {
            seconds = configured;
        }

        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    /// <summary>
    /// Runs <paramref name="realPath"/> with <paramref name="args"/> inherited stdio, and returns its
    /// exit code. A missing binary is the shell's 127 rather than an exception: the worker's failure
    /// classification is what the fact measures, not this asset's error handling.
    /// </summary>
    private static int Exec(string realPath, string[] args)
    {
        var startInfo = new ProcessStartInfo(realPath) { UseShellExecute = false };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var child = Start(startInfo);
        if (child is null)
        {
            return NotRunnableExitCode;
        }

        child.WaitForExit();
        return child.ExitCode;
    }

    private static Process? Start(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch (Win32Exception failure)
        {
            Console.Error.WriteLine($"RecordingShim: could not start '{startInfo.FileName}': {failure.Message}");
            return null;
        }
    }

    /// <summary>
    /// Appends one block in one append-mode write. Several git children can be alive at the same moment
    /// (a submodule fetch, an LFS call), and a whole block per write keeps two children's lines from
    /// interleaving in the middle of a line.
    /// </summary>
    private static void Append(string path, string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
    }

    /// <summary>The value of <paramref name="name"/>, or the empty string when unset (shell <c>${NAME-}</c>).</summary>
    private static string Variable(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    private static string? Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        Console.Error.WriteLine($"RecordingShim: {name} is not set.");
        return null;
    }
}
