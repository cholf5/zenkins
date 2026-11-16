using System.Diagnostics;
using Xunit;
using Xunit.Sdk;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// Card B-004's own probes: the compiled recording shim has to be a real process the worker can start
/// on Windows, and it has to record exactly what the POSIX script records. This machine cannot exercise
/// the Windows <c>Process.Start</c> path, so the two things that can be measured here are measured:
/// the binary is an executable process (not a script) whose lines are the documented ones, whose exec
/// of git propagates git's output and exit code, and whose POSIX counterpart writes byte-identical
/// text for the same invocation.
/// </summary>
[Trait("Category", "Git")]
public sealed class RecordingShimAssetTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// The compiled recording git shim, started directly as a native process: the twelve documented
    /// lines, the real git's output, and the real git's exit code - including a failing invocation.
    /// </summary>
    [Fact]
    public async Task The_compiled_git_shim_writes_the_documented_lines_and_propagates_gits_result()
    {
        using var directory = new GitTestDirectory("shim-asset-git");
        var logPath = directory.Combine("git-argv.log");
        var environment = ChildEnvironment(directory, logPath);

        var success = await RunAsync(RecordingShimAsset.ExecutablePath, environment, "--version");

        Assert.Equal(0, success.ExitCode);
        Assert.Contains("git version", success.StdOut, StringComparison.Ordinal);

        var lines = File.ReadAllLines(logPath);
        Assert.Equal(
            new[]
            {
                "ARGV:--version",
                "ASKPASS:" + directory.Combine("askpass-from-test"),
                "SSH:ssh-from-test",
                "PROMPT:0",
                "GLOBAL:" + directory.GlobalConfigPath,
                "NOSYSTEM:1",
                "CONFIGCOUNT:1",
                "HELPERKEY:credential.helper",
                "HELPERVALUE:",
                "AUTHSOCK:",
                "HOME:" + directory.HomePath,
                "-----",
            },
            lines);

        // Trailing newline only: the POSIX script's `printf` writes whole lines, and the compiled form
        // must not add a CR (Windows) or a second blank line.
        var raw = await File.ReadAllTextAsync(logPath, CancellationToken);
        Assert.EndsWith("-----" + "\n", raw, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', raw);

        // A failing invocation: the shim must hand git's own exit code through, not swallow it.
        var direct = await RunAsync(
            GitCli.ResolveGitPath(),
            new Dictionary<string, string?>(StringComparer.Ordinal),
            "--definitely-not-a-git-option");
        Assert.NotEqual(0, direct.ExitCode);
        var shimmed = await RunAsync(RecordingShimAsset.ExecutablePath, environment, "--definitely-not-a-git-option");
        Assert.Equal(direct.ExitCode, shimmed.ExitCode);

        // The second invocation appended one more block, and the failure is recorded as git's own.
        Assert.Equal(24, File.ReadAllLines(logPath).Length);
    }

    /// <summary>
    /// The format promise of card B-004 AC-3, on the one machine that has both forms: the same
    /// invocation through the POSIX script and through the compiled asset produces byte-identical
    /// logs. Only POSIX has the script, so this fact is recorded as skipped on Windows - where the
    /// script is not a supported form at all.
    /// </summary>
    [Fact]
    public async Task The_posix_script_and_the_compiled_shim_agree_line_for_line()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip(
                "the shell-script form of the shim only exists on POSIX; on Windows the compiled asset "
                + "is the only form, and fact one already pins its lines.");
        }

        using var directory = new GitTestDirectory("shim-asset-parity");
        var realGitPath = GitCli.ResolveGitPath();

        var scriptLog = directory.Combine("script-git-argv.log");
        var compiledLog = directory.Combine("compiled-git-argv.log");
        var scriptPath = GitCli.WriteScript(
            directory.Combine("script-bin"),
            "git",
            RecordingGitShim.PosixScript(scriptLog, realGitPath));

        var scriptRun = await RunAsync(scriptPath, ChildEnvironment(directory, scriptLog), "--version");
        var compiledRun = await RunAsync(
            RecordingShimAsset.ExecutablePath,
            ChildEnvironment(directory, compiledLog),
            "--version");

        Assert.Equal(0, scriptRun.ExitCode);
        Assert.Equal(scriptRun.ExitCode, compiledRun.ExitCode);
        Assert.Equal(scriptRun.StdOut, compiledRun.StdOut);
        Assert.Equal(
            await File.ReadAllBytesAsync(scriptLog, CancellationToken),
            await File.ReadAllBytesAsync(compiledLog, CancellationToken));
    }

    /// <summary>
    /// The hanging-git stub in its compiled form: it records its own pid and a long-lived child's pid,
    /// and the child really is a separate live process - the two facts GitTimeoutTests asserts the
    /// timeout kill removes. The sleeping child is a copy of the asset, the cross-platform stand-in for
    /// the POSIX stub's <c>sleep 300 &amp;</c>.
    /// </summary>
    [Fact]
    public async Task The_compiled_hanging_git_records_its_own_and_its_childs_pid()
    {
        using var directory = new GitTestDirectory("shim-asset-hang");
        var pidFile = directory.Combine("pids.txt");

        using var process = Start(
            RecordingShimAsset.ExecutablePath,
            RecordingShimAsset.HangingGitEnvironment(pidFile));
        try
        {
            var pids = await ReadPidsAsync(pidFile, expected: 2);
            Assert.Equal(process.Id, pids[0]);
            Assert.NotEqual(pids[0], pids[1]);
            Assert.True(IsAlive(pids[1]), $"the recorded child {pids[1]} is not a live process.");

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken);

            foreach (var pid in pids)
            {
                await AssertGoneAsync(pid);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// The compiled shim wired in where Windows will use it: as the worker's own
    /// <c>GitExecutable</c>, with its log and real-git paths arriving through the product's environment
    /// overrides. The clone really succeeds and the shim's log shows what the git child was given - the
    /// same chain the Windows facts depend on, exercised here as far as this machine can.
    /// </summary>
    [Fact]
    public async Task The_worker_runs_the_compiled_shim_as_its_git_and_the_shim_records_the_invocation()
    {
        using var fixture = GitFixtureRepository.Create("shim-asset-worker");
        using var directory = new GitTestDirectory("shim-asset-worker-run");
        var logPath = directory.Combine("git-argv.log");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work,
                fixture.Environment,
                lines.Add,
                gitExecutable: RecordingShimAsset.ExecutablePath,
                extraEnvironment: RecordingShimAsset.GitShimEnvironment(logPath, GitCli.ResolveGitPath())));

        var result = await workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            CancellationToken);

        Assert.Equal(fixture.MainTip, result.Commit);

        var entries = File.ReadAllLines(logPath);
        Assert.Contains(entries, entry => entry.StartsWith("ARGV:clone ", StringComparison.Ordinal));
        Assert.Contains("PROMPT:0", entries);
        Assert.Contains("NOSYSTEM:1", entries);
        Assert.Contains("HOME:" + fixture.Directory.HomePath, entries);
        // The product's own hardening reached the git child through the shim, and the test's
        // environment override won over it - which is how the shim's log path and real-git path reach
        // the compiled form on Windows too.
        Assert.Contains("GLOBAL:" + fixture.Directory.GlobalConfigPath, entries);
        Assert.Contains("CONFIGCOUNT:1", entries);
        Assert.Contains("HELPERKEY:credential.helper", entries);
        Assert.Contains("ASKPASS:", entries);
    }

    /// <summary>
    /// The environment for one shim invocation: the compiled shim's own variables (log path, real git,
    /// role) plus fixed credential-environment values, so the recorded lines are the same on every
    /// machine and in both forms. The global git config is an existing empty file - git reads it even
    /// for <c>--version</c>.
    /// </summary>
    private static Dictionary<string, string?> ChildEnvironment(GitTestDirectory directory, string logPath)
    {
        var environment = RecordingShimAsset.GitShimEnvironment(logPath, GitCli.ResolveGitPath());
        environment["GIT_ASKPASS"] = directory.Combine("askpass-from-test");
        environment["GIT_SSH_COMMAND"] = "ssh-from-test";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_CONFIG_GLOBAL"] = directory.GlobalConfigPath;
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_COUNT"] = "1";
        environment["GIT_CONFIG_KEY_0"] = "credential.helper";
        environment["GIT_CONFIG_VALUE_0"] = string.Empty;
        environment["SSH_AUTH_SOCK"] = string.Empty;
        environment["HOME"] = directory.HomePath;
        return environment;
    }

    private static Process Start(
        string executable,
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"'{executable}' could not be started.");
        process.StandardInput.Close();
        return process;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string executable,
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments)
    {
        using var process = Start(executable, environment, arguments);
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken);
        await process.WaitForExitAsync(CancellationToken);
        return (process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task<int[]> ReadPidsAsync(string path, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var pids = (await File.ReadAllLinesAsync(path, CancellationToken))
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Select(int.Parse)
                    .ToArray();
                if (pids.Length >= expected)
                {
                    return pids;
                }
            }

            await Task.Delay(50, CancellationToken);
        }

        Assert.Fail($"the compiled hanging git recorded no pids in '{path}'.");
        return [];
    }

    private static async Task AssertGoneAsync(int pid)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsAlive(pid))
            {
                return;
            }

            await Task.Delay(100, CancellationToken);
        }

        Assert.Fail($"process {pid} survived the kill of the compiled hanging git's tree.");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
