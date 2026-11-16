using Xunit;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// TC-002 (card F-013): an HTTPS token reaches git only through <c>GIT_ASKPASS</c>. The assertions
/// cover the three places the card names - the child processes' argv, the ask-pass helper's path and
/// content after cleanup, and every captured log line - and the local HTTP server independently
/// confirms that a Basic credential really was presented.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitCredentialSecrecyTests
{
    /// <summary>A token shaped like a real one: unique per test, so a leak is unambiguous.</summary>
    private static string NewToken() => "zkpat_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task The_HTTPS_token_travels_only_through_the_askpass_helper()
    {
        using var fixture = GitFixtureRepository.Create("https");
        var token = NewToken();
        using var server = LocalHttpGitServer.Start(fixture.BarePath, token);
        using var directory = new GitTestDirectory("https-secret");
        using var shim = RecordingGitShim.Create(directory, GitCli.ResolveGitPath());

        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work, fixture.Environment, lines.Add, gitExecutable: shim.GitPath, extraEnvironment: shim.Environment));

        var result = await workspace.CloneAsync(
            server.Url,
            "main",
            new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.HttpsToken, token, "x-access-token"),
            TestContext.Current.CancellationToken);

        Assert.Equal(fixture.MainTip, result.Commit);

        // The server's own view: it challenged, then accepted a Basic credential carrying the token
        // under the documented user name.
        Assert.True(server.RejectedRequests > 0, "the fixture server never challenged the client");
        Assert.True(server.AuthenticatedRequests > 0, "the fixture server never accepted a credential");
        Assert.Contains("x-access-token", server.PresentedUserNames);

        var entries = shim.ReadEntries();
        Assert.NotEmpty(entries);

        // (1) The child processes' argv: the token is in none of them (including git's own children).
        Assert.DoesNotContain(
            entries.Where(entry => entry.StartsWith("ARGV:", StringComparison.Ordinal)),
            entry => entry.Contains(token, StringComparison.Ordinal));

        // (2) The ask-pass helper really was the channel, and it lived in the run's temp directory.
        var askPassPath = entries
            .Select(entry => entry.StartsWith("ASKPASS:", StringComparison.Ordinal)
                ? entry["ASKPASS:".Length..]
                : null)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        Assert.NotNull(askPassPath);
        Assert.StartsWith(work.TempPath, askPassPath, StringComparison.Ordinal);
        Assert.EndsWith(GitCli.AskPassFileNameForThisOs, askPassPath, StringComparison.Ordinal);

        // ...and it is gone, along with every other trace of the token under the run's tree.
        Assert.False(File.Exists(askPassPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work.TempPath));
        AssertNoFileContains(work.RootPath, token);

        // (3) Every captured log line: none carries the token, and the filter agrees.
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.DoesNotContain(token, line, StringComparison.Ordinal));
        Assert.DoesNotContain(token, shim.ReadText(), StringComparison.Ordinal);
        Assert.False(workspace.Filter.ContainsSecret(string.Join('\n', lines)));
        Assert.True(workspace.Filter.Count > 0, "the credential was never registered with the filter");

        // (4) Not just the raw text: no encoded form of the token appears in anything this run left
        // behind. The code has no encoding path, which is exactly why the evidence is asserted rather
        // than argued - a future helper that base64s the password would fail here.
        var forms = TokenForms(token);
        AssertNoFormAppears(entries.Where(entry => entry.StartsWith("ARGV:", StringComparison.Ordinal)), forms, "argv");
        AssertNoFormAppears([shim.ReadText()], forms, "the shim log");
        AssertNoFormAppears(lines, forms, "the run log");
        AssertNoFileContainsAnyForm(work.RootPath, forms);
    }

    [Fact]
    public async Task A_rejected_token_fails_honestly_and_is_deleted_anyway()
    {
        using var fixture = GitFixtureRepository.Create("https-rejected");
        var presentedToken = NewToken();
        var acceptedToken = NewToken();
        using var server = LocalHttpGitServer.Start(fixture.BarePath, acceptedToken);
        using var directory = new GitTestDirectory("https-rejected-run");
        using var shim = RecordingGitShim.Create(directory, GitCli.ResolveGitPath());

        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work, fixture.Environment, lines.Add, gitExecutable: shim.GitPath, extraEnvironment: shim.Environment));

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            server.Url,
            "main",
            new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.HttpsToken, presentedToken),
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.AuthenticationRejected, failure.Kind);
        Assert.Contains("127.0.0.1", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(presentedToken, failure.Message, StringComparison.Ordinal);
        AssertNoFormAppears([failure.Message], TokenForms(presentedToken), "the failure message");

        // The failure path cleans up too: no helper, no token anywhere under the run's tree.
        Assert.Empty(Directory.EnumerateFileSystemEntries(work.TempPath));
        AssertNoFileContains(work.RootPath, presentedToken);
        Assert.DoesNotContain(
            shim.ReadEntries().Where(entry => entry.StartsWith("ARGV:", StringComparison.Ordinal)),
            entry => entry.Contains(presentedToken, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.DoesNotContain(presentedToken, line, StringComparison.Ordinal));

        // The server really was presented the (wrong) credential - the ask-pass path is what failed,
        // not the plumbing - and it never accepted one.
        Assert.True(server.RejectedRequests > 0, "the fixture server never challenged the client");
        Assert.Equal(0, server.AuthenticatedRequests);
    }

    [Fact]
    public async Task The_credential_files_are_deleted_even_when_the_worker_keeps_the_run_temp()
    {
        using var fixture = GitFixtureRepository.Create("https-temp-retained");
        var token = NewToken();
        using var server = LocalHttpGitServer.Start(fixture.BarePath, token);
        using var directory = new GitTestDirectory("https-temp-retained-run");

        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment, lines.Add));

        // ZENKINS_CLEANUP_TEMP=false is the shape under test: the run loop keeps the run's temp/, so it
        // must be the credential files - not temp/ - that are deleted. The Git layer has no cleanup knob
        // at all (its options cannot remove a directory), which is what this pins.
        var configuration = Zenkins.Worker.WorkerConfiguration.FromEnvironment(
            new Dictionary<string, string?>
            {
                [Zenkins.Worker.WorkerConfiguration.ServerUrlVariable] = "http://localhost",
                [Zenkins.Worker.WorkerConfiguration.CleanupTempVariable] = "false",
            }).Configuration;
        Assert.NotNull(configuration);
        Assert.False(configuration.CleanupTemp);
        var keptFilePath = Path.Combine(work.TempPath, "stdout.log");
        await File.WriteAllTextAsync(keptFilePath, "a run log that must survive", TestContext.Current.CancellationToken);

        var result = await workspace.CloneAsync(
            server.Url,
            "main",
            new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.HttpsToken, token),
            TestContext.Current.CancellationToken);

        Assert.Equal(fixture.MainTip, result.Commit);
        Assert.True(server.AuthenticatedRequests > 0, "the fixture server never accepted a credential");

        // The temp directory is still there and still holds the non-credential file...
        Assert.True(Directory.Exists(work.TempPath));
        Assert.True(File.Exists(keptFilePath));
        Assert.Equal("a run log that must survive", await File.ReadAllTextAsync(keptFilePath, TestContext.Current.CancellationToken));

        // ...but neither credential carrier survived, and no remaining file holds the token.
        Assert.False(File.Exists(Path.Combine(work.TempPath, WorkerGit.GitCredentialFiles.AskPassFileName)));
        Assert.False(File.Exists(Path.Combine(work.TempPath, WorkerGit.GitCredentialFiles.SshKeyFileName)));
        AssertNoFileContainsAnyForm(work.RootPath, TokenForms(token));
    }

    [Fact]
    public async Task A_url_that_embeds_a_token_is_refused_before_anything_runs()
    {
        using var directory = new GitTestDirectory("url-credential");
        using var shim = RecordingGitShim.Create(directory, GitCli.ResolveGitPath());
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work, GitCli.Environment(directory), gitExecutable: shim.GitPath, extraEnvironment: shim.Environment));

        var token = NewToken();
        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            $"https://user:{token}@example.test/repo.git",
            "main",
            credential: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.InvalidUrl, failure.Kind);
        Assert.DoesNotContain(token, failure.Message, StringComparison.Ordinal);
        Assert.Empty(shim.ReadEntries());
        Assert.Empty(workspace.Trace);
    }

    /// <summary>
    /// The token's plain and encoded shapes: raw, URL-encoded (a URL would carry this), standard base64
    /// of the token, base64 without padding, base64url, and the exact Basic-header payload
    /// (<c>user:token</c>). A leak in any of them is a leak.
    /// </summary>
    private static string[] TokenForms(string token)
    {
        var credentialBytes = System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token}");
        var basic = Convert.ToBase64String(credentialBytes);
        var tokenBytes = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(token));
        var base64Url = tokenBytes.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return
        [
            token,
            Uri.EscapeDataString(token),
            tokenBytes,
            tokenBytes.TrimEnd('='),
            base64Url,
            basic,
            basic.TrimEnd('='),
        ];
    }

    private static void AssertNoFormAppears(IEnumerable<string> haystacks, string[] forms, string what)
    {
        foreach (var haystack in haystacks)
        {
            foreach (var form in forms)
            {
                Assert.False(
                    haystack.Contains(form, StringComparison.Ordinal),
                    $"{what} contains the credential value (form: {form[..Math.Min(6, form.Length)]}…)");
            }
        }
    }

    private static void AssertNoFileContainsAnyForm(string root, string[] forms)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            AssertNoFormAppears([text], forms, $"the file '{file}'");
        }
    }

    private static void AssertNoFileContains(string root, string secret)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            Assert.False(
                text.Contains(secret, StringComparison.Ordinal),
                $"the file '{file}' contains the credential value");
        }
    }
}
