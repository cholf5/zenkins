using Xunit;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// Submodules (card F-013, AC-8): the clone recurses, and the submodule's own fetch really carries the
/// run's credential channel.
/// <para>
/// <b>Why this test goes through HTTP.</b> A review of the first version found the claim unproven: the
/// recording shim sits on <c>PATH</c>, but git runs its submodule child through its own executable, so
/// shimming proved nothing about the child's environment. Here the superproject and its submodule are
/// two token-protected repositories behind one server, joined by a relative submodule URL
/// (<c>../sub.git</c>, git's own relative form) - so the child can only succeed if it inherited
/// <c>GIT_ASKPASS</c> and <c>GIT_TERMINAL_PROMPT=0</c>. The server counts authenticated requests per
/// repository, which turns "the environment was applied" into an observation instead of an assumption.
/// </para>
/// </summary>
[Trait("Category", "Git")]
public sealed class GitSubmoduleTests
{
    private const string TokenValue = "zkpat_0123456789abcdef0123456789abcdef";
    private const string SubmodulePayload = "submodule payload\n";

    [Fact]
    public async Task A_submodule_is_fetched_recursively_through_the_runs_credential_channel()
    {
        using var directory = new GitTestDirectory("submodule");
        var environment = GitCli.Environment(directory);

        // 1. The submodule's own repository, served as /sub.git.
        var submoduleSource = directory.Combine("submodule-source");
        Directory.CreateDirectory(submoduleSource);
        GitCli.MustRun(submoduleSource, environment, "init", "--initial-branch", "main");
        GitFixtureRepository.CommitFile(submoduleSource, environment, "lib.txt", SubmodulePayload, "sub one");
        var submoduleBare = directory.Combine("sub-origin.git");
        GitCli.MustRun(directory.RootPath, environment, "clone", "--bare", submoduleSource, submoduleBare);
        GitCli.MustRun(submoduleBare, environment, "update-server-info");

        // 2. The superproject, with the submodule referenced by git's relative URL form so the child
        //    resolves it against the superproject's remote - the same server, the same credential.
        var superSource = directory.Combine("super-source");
        Directory.CreateDirectory(superSource);
        GitCli.MustRun(superSource, environment, "init", "--initial-branch", "main");
        GitFixtureRepository.CommitFile(superSource, environment, "README.md", "# super\n", "super one");
        GitCli.MustRun(
            superSource,
            environment,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            submoduleSource,
            "vendor/lib");
        await File.WriteAllTextAsync(
            Path.Combine(superSource, ".gitmodules"),
            "[submodule \"vendor/lib\"]\n\tpath = vendor/lib\n\turl = ../sub.git\n",
            TestContext.Current.CancellationToken);
        GitCli.MustRun(superSource, environment, "add", ".gitmodules");
        GitCli.MustRun(superSource, environment, "commit", "-m", "point the submodule at the served repo");

        var superBare = directory.Combine("super-origin.git");
        GitCli.MustRun(directory.RootPath, environment, "clone", "--bare", superSource, superBare);
        GitCli.MustRun(superBare, environment, "update-server-info");

        // 3. One server, two repositories, one token.
        using var server = LocalHttpGitServer.Start(superBare, TokenValue);
        server.Serve("sub.git", submoduleBare);

        using var shim = RecordingGitShim.Create(directory, GitCli.ResolveGitPath());
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work, environment, lines.Add, gitExecutable: shim.GitPath, extraEnvironment: shim.Environment));

        var result = await workspace.CloneAsync(
            server.Url,
            "main",
            new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.HttpsToken, TokenValue),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            GitCli.RevParse(work.RepositoryPath, environment, "HEAD"),
            result.Commit);

        // 4. The submodule content is really there...
        var vendored = Path.Combine(work.RepositoryPath, "vendor", "lib", "lib.txt");
        Assert.True(
            File.Exists(vendored),
            $"the submodule was not checked out: {string.Join(", ", Directory.EnumerateFiles(work.RepositoryPath, "*", SearchOption.AllDirectories).Take(20))}");
        Assert.Equal(SubmodulePayload, await File.ReadAllTextAsync(vendored, TestContext.Current.CancellationToken));

        // 5. ...and the submodule's own fetch authenticated against the server, which it could only do
        //    by inheriting the run's ask-pass channel (the token is not in argv and not in the URL).
        Assert.True(server.AuthenticatedRequestsFor("repo.git") > 0, "the superproject was never authenticated");
        Assert.True(
            server.AuthenticatedRequestsFor("sub.git") > 0,
            "the submodule fetch never authenticated: the credential environment did not reach the child");

        // The parent's own invocation (the shim's view) still carried the helper, not the token.
        var entries = shim.ReadEntries();
        Assert.Contains(entries, entry => entry.StartsWith("ARGV:", StringComparison.Ordinal)
            && entry.Contains("clone", StringComparison.Ordinal)
            && entry.Contains("--recurse-submodules", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries.Where(entry => entry.StartsWith("ARGV:", StringComparison.Ordinal)),
            entry => entry.Contains(TokenValue, StringComparison.Ordinal));
        Assert.All(
            entries.Where(entry => entry.StartsWith("PROMPT:", StringComparison.Ordinal)),
            entry => Assert.Equal("PROMPT:0", entry));
        Assert.Contains(entries, entry => entry.StartsWith("ASKPASS:", StringComparison.Ordinal)
            && entry.EndsWith(GitCli.AskPassFileNameForThisOs, StringComparison.Ordinal));
    }
}
