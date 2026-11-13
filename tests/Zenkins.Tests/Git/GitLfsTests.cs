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
    /// <paramref name="prefix"/> plus every inherited <c>PATH</c> entry that does not contain a
    /// <c>git-lfs</c> executable - a PATH on which <c>git lfs</c> provably cannot resolve, while git's
    /// own POSIX helper scripts still find the utilities they shell out to.
    /// </summary>
    private static string PathWithoutGitLfs(string prefix)
    {
        var inherited = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var kept = inherited
            .Split(Path.PathSeparator)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Where(directory => !File.Exists(Path.Combine(directory, "git-lfs")))
            .ToArray();
        return string.Join(Path.PathSeparator, [prefix, .. kept]);
    }

    [Fact]
    public async Task Without_git_lfs_one_warning_names_the_tool_and_the_run_continues()
    {
        using var fixture = GitLfsFixture.Create();

        // The child's PATH is the recording shim's directory plus every inherited directory that does
        // NOT hold a git-lfs binary: git's own helper scripts still work (they need basename/sed/uname),
        // and no git-lfs can be resolved. If a machine still resolved an lfs binary through git's exec
        // path, the "exactly one warning" assertion fails loudly rather than letting the missing-binary
        // branch go untested.
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
                extraEnvironment: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["PATH"] = PathWithoutGitLfs(shim.BinDirectory),
                }));

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
