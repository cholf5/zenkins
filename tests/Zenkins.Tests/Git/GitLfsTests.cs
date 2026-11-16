using Xunit;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// LFS behaviour (card F-013, AC-7): honest in both directions. With a <c>git-lfs</c> binary the
/// pointer is materialised, and the test proves the binary was actually invoked (the stub logs every
/// call) instead of merely existing on <c>PATH</c>. Without it the run logs exactly one warning naming
/// <c>git-lfs</c> and continues with the pointer file still in place - never a silent success.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitLfsTests
{
    /// <summary>The first line of an LFS pointer file.</summary>
    private const string PointerHeader = "version https://git-lfs.github.com/spec/v1";

    [Fact]
    public async Task With_git_lfs_present_the_pointer_is_materialised_by_the_binary()
    {
        using var fixture = GitLfsFixture.Create();
        var (binDirectory, logPath) = fixture.CreateStub();
        using var directory = new GitTestDirectory("lfs-present");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment, lines.Add, extraPathDirectory: binDirectory));

        var result = await workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken);

        // The payload replaced the pointer: the fixture's real content is on disk.
        var cloned = Path.Combine(work.RepositoryPath, "data.bin");
        Assert.True(File.Exists(cloned));
        Assert.Equal(GitLfsFixture.Payload, File.ReadAllText(cloned));
        Assert.DoesNotContain(PointerHeader, File.ReadAllText(cloned), StringComparison.Ordinal);
        Assert.Empty(result.Warnings);

        // ...and git really invoked the stub (a stub that is merely present on PATH would not do).
        Assert.True(File.Exists(logPath), "the git-lfs stub was never invoked");
        var calls = await File.ReadAllLinesAsync(logPath, TestContext.Current.CancellationToken);
        Assert.NotEmpty(calls);
        Assert.Contains(calls, call => call.StartsWith("version", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.StartsWith("pull", StringComparison.Ordinal));
    }

    /// <summary>
    /// The child environment for the shim-run clone: the shim's own variables (the compiled form on
    /// Windows reads its log and real-git paths from them; empty on POSIX, where the script bakes them)
    /// plus the <c>PATH</c> that cannot resolve a <c>git-lfs</c>.
    /// </summary>
    private static Dictionary<string, string?> ChildEnvironment(RecordingGitShim shim, GitTestDirectory directory)
    {
        return new Dictionary<string, string?>(shim.Environment, StringComparer.Ordinal)
        {
            ["PATH"] = PathWithoutGitLfs(shim.BinDirectory, directory),
        };
    }

    /// <summary>
    /// <paramref name="prefix"/> plus every inherited <c>PATH</c> entry, with a <c>git-lfs</c> that
    /// provably cannot resolve. An entry holding a git-lfs binary is not dropped outright - a CI
    /// runner's tool directory also holds the utilities git's own helper scripts shell out to
    /// (basename, sed, uname), and dropping it killed the clone with git exit 127 there. Instead the
    /// entry's executables are mirrored into a scratch directory (inside <paramref name="directory"/>,
    /// so it dies with the fixture) minus every <c>git-lfs*</c> binary: the utilities stay reachable
    /// and no git-lfs can be resolved anywhere on the PATH.
    /// <para>
    /// Windows names the binary <c>git-lfs.exe</c> and cannot create a symbolic link without a
    /// privilege a test machine may not grant, so the mirror is by copy there (the entries this touch
    /// are the git installation's own bin directories, a few hundred files at most).
    /// </para>
    /// </summary>
    private static string PathWithoutGitLfs(string prefix, GitTestDirectory directory)
    {
        var inherited = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = new List<string> { prefix };
        var mirrorIndex = 0;
        foreach (var entry in inherited.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (!HasGitLfs(entry))
            {
                entries.Add(entry);
                continue;
            }

            var mirror = directory.Combine($"path-mirror-{mirrorIndex++}");
            Directory.CreateDirectory(mirror);
            foreach (var binary in Directory.EnumerateFiles(entry))
            {
                var name = Path.GetFileName(binary);
                if (name.StartsWith("git-lfs", StringComparison.Ordinal))
                {
                    continue;
                }

                var target = Path.Combine(mirror, name);
                if (OperatingSystem.IsWindows())
                {
                    File.Copy(binary, target);
                }
                else
                {
                    Directory.CreateSymbolicLink(target, binary);
                }
            }

            entries.Add(mirror);
        }

        return string.Join(Path.PathSeparator, entries);
    }

    /// <summary>Whether <paramref name="directory"/> holds an executable git-lfs for this OS.</summary>
    private static bool HasGitLfs(string directory) =>
        File.Exists(Path.Combine(directory, "git-lfs"))
        || File.Exists(Path.Combine(directory, "git-lfs.exe"));

    [Fact]
    public async Task Without_git_lfs_one_warning_names_the_tool_and_the_run_continues()
    {
        using var fixture = GitLfsFixture.Create();

        // The child's PATH is the recording shim's directory plus every inherited entry, with any
        // git-lfs-bearing entry mirrored minus git-lfs (see PathWithoutGitLfs): git's own helper
        // scripts still work (they need basename/sed/uname), and no git-lfs can be resolved. If a
        // machine still resolved an lfs binary through git's exec path, the "exactly one warning"
        // assertion fails loudly rather than letting the missing-binary branch go untested.
        using var directory = new GitTestDirectory("lfs-absent");
        using var shim = RecordingGitShim.Create(directory, GitCli.ResolveGitPath());
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work,
                fixture.Environment,
                lines.Add,
                gitExecutable: shim.GitPath,
                extraEnvironment: ChildEnvironment(shim, directory)));

        var result = await workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken);

        // The clone succeeded and the pointer file is honestly still a pointer.
        var cloned = Path.Combine(work.RepositoryPath, "data.bin");
        Assert.True(File.Exists(cloned));
        Assert.StartsWith(PointerHeader, File.ReadAllText(cloned), StringComparison.Ordinal);

        // Exactly one warning, and it names the missing tool.
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("git-lfs", warning, StringComparison.Ordinal);
        Assert.Contains("not installed", warning, StringComparison.Ordinal);
        var logged = lines.Where(line => line.Contains("git-lfs", StringComparison.Ordinal)).ToArray();
        Assert.Single(logged);
        Assert.Equal(warning, logged[0]);
    }
}
