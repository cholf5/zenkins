using Xunit;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// Card F-018's worker-side contract, proven at the git layer against a <b>real HTTPS server that
/// demands Basic authentication</b>:
/// <list type="number">
/// <item>A delivered credential authenticates over TLS, and the server itself observes the user name
/// git presented.</item>
/// <item>With <b>no</b> credential delivered, a credential the host really holds is <b>not</b> used -
/// which is what makes the run page's "no credential is assigned" an honest statement instead of a
/// guess. The test carries its own control group: the same ambient credential is proven to work by a
/// raw <c>git clone</c> run outside the worker's environment, so the worker's failure cannot be
/// explained by "the ambient credential never worked".</item>
/// </list>
/// <para>
/// <b>Why the control group matters, and why it is synthetic.</b> A "the clone failed" assertion alone
/// would prove nothing, so the same helper is first shown working outside the worker's hardening. Both
/// sides of that pair use a helper this test wrote, in a scratch HOME, with the machine's own git
/// configuration switched off (<c>GIT_CONFIG_NOSYSTEM=1</c>, <c>GIT_CONFIG_GLOBAL</c> naming the scratch
/// file). No real credential store - a login keychain, an ssh agent, <c>~/.ssh</c> - is read, written or
/// even reachable, so the proof costs the developer nothing.
/// </para>
/// </summary>
[Trait("Category", "Git")]
public sealed class GitAmbientCredentialHardeningTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_delivered_token_authenticates_over_tls_and_nothing_else_is_needed()
    {
        using var fixture = GitFixtureRepository.Create("f018-delivered");
        var token = NewToken();
        using var server = AuthenticatedHttpsGitServer.Start(fixture.BarePath, token);
        using var directory = new GitTestDirectory("f018-delivered-run");

        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(GitTestWorkspace.Options(
            work,
            ServerEnvironment(directory, server),
            lines.Add));

        var result = await workspace.CloneAsync(
            server.Url,
            "main",
            new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.HttpsToken, token, "zenkins-bot"),
            CancellationToken);

        Assert.Equal(fixture.MainTip, result.Commit);
        Assert.True(server.RejectedRequests > 0, "the server never issued its Basic challenge");
        Assert.True(server.AuthenticatedRequests > 0, "the server never accepted a credential");
        Assert.Contains("zenkins-bot", server.PresentedUserNames);

        // The token reached git through the ask-pass helper only: no log line carries it.
        Assert.All(lines, line => Assert.DoesNotContain(token, line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_credential_assigned_a_credential_helper_on_the_host_is_not_used()
    {
        using var fixture = GitFixtureRepository.Create("f018-ambient");
        // The recorded skip (Q-009's contract): Windows cannot exec the shim this fact observes
        // through, so the fact is named as skipped there rather than silently red or green.
        if (GitCli.RecordingShimUnavailableReason() is { } reason)
        {{
            throw Xunit.Sdk.SkipException.ForSkip(reason);
        }}

        var token = NewToken();
        using var server = AuthenticatedHttpsGitServer.Start(fixture.BarePath, token);
        using var directory = new GitTestDirectory("f018-ambient-run");

        // The "ambient" credential is entirely synthetic: a helper script this test wrote, kept in a
        // scratch HOME's config. No real credential store on this machine is read or written, and the
        // command below is insulated from the system configuration on purpose
        // (`GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL` pointed at the scratch file, empty ask-pass and
        // agent) - a control group must not touch what a developer's machine happens to hold.
        WriteAmbientCredentialHelper(directory.HomePath, token);

        // ---- Control group: the same synthetic helper, WITHOUT the worker's hardening -----------
        var controlPath = directory.Combine("control-clone");
        var controlEnvironment = SyntheticAmbientEnvironment(directory, server);
        var control = GitCli.Run(
            directory.RootPath,
            controlEnvironment,
            "clone", "--", server.Url, controlPath);
        Assert.True(
            control.ExitCode == 0,
            $"the control clone was expected to succeed with the synthetic helper: {control.StdErr}");
        Assert.Equal(fixture.MainTip, GitCli.RevParse(controlPath, controlEnvironment, "HEAD"));
        var authenticatedByTheControl = server.AuthenticatedRequests;
        Assert.True(authenticatedByTheControl > 0);

        // The synthetic helper really is what authenticated: it recorded its own calls.
        var helperCallsAfterControl = CountHelperCalls(directory.HomePath);
        Assert.True(helperCallsAfterControl > 0, "the synthetic helper was never invoked by the control clone");

        // ---- The worker, same HOME, no delivered credential: it must fail ------------------------
        // The recording shim runs the real git, but only after writing down the environment it was
        // handed: the assertions below read what the git child itself saw, not the dictionary built here.
        using var shim = RecordingGitShim.Create(directory, GitCli.ResolveGitPath());
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(GitTestWorkspace.Options(
            work,
            ServerEnvironment(directory, server),
            lines.Add,
            gitExecutable: shim.GitPath));

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            server.Url,
            "main",
            credential: null,
            CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.AuthenticationRejected, failure.Kind);
        Assert.DoesNotContain(token, failure.Message, StringComparison.Ordinal);

        // The server's own view: the worker's attempts were challenged and never accepted, so the
        // ambient helper answered nothing. (The control's accepted requests are the baseline.)
        Assert.Equal(authenticatedByTheControl, server.AuthenticatedRequests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work.RepositoryPath));

        // The hardened environment, as the git child itself saw it: the operator's config files are out
        // of reach, the helper list is reset, there is no agent socket and no ask-pass program - while the
        // isolated HOME (where the ambient credential lives) is still exactly what the child would have
        // read. That is the difference between "there was no ambient credential" and "the worker does not
        // use one".
        var observed = shim.ReadEntries();
        Assert.Contains(
            $"GLOBAL:{Path.Combine(work.HardeningPath, WorkerGit.GitEnvironment.EmptyGitConfigFileName)}",
            observed);
        Assert.Contains("NOSYSTEM:1", observed);
        Assert.Contains("CONFIGCOUNT:1", observed);
        Assert.Contains($"HELPERKEY:{WorkerGit.GitEnvironment.CredentialHelperKey}", observed);
        Assert.Contains("HELPERVALUE:", observed);
        Assert.Contains("AUTHSOCK:", observed);
        Assert.Contains("ASKPASS:", observed);
        Assert.Contains($"HOME:{directory.HomePath}", observed);
        Assert.DoesNotContain(
            observed,
            entry => entry.StartsWith("ASKPASS:", StringComparison.Ordinal)
                && entry.Length > "ASKPASS:".Length);
        // The ambient helper lives in that HOME's config, and the clone still did not authenticate - and,
        // decisively, the helper was never even consulted: its own call count did not move.
        Assert.True(File.Exists(Path.Combine(directory.HomePath, ".gitconfig")));
        Assert.Equal(helperCallsAfterControl, CountHelperCalls(directory.HomePath));
    }

    /// <summary>
    /// The control group's environment: the scratch HOME, the synthetic helper as the <b>only</b>
    /// credential source (<c>GIT_CONFIG_NOSYSTEM=1</c> plus a <c>GIT_CONFIG_GLOBAL</c> that names the
    /// scratch file), no ask-pass program, no agent, and the fixture's certificate authority. Nothing
    /// here can reach the machine's own credential stores, so the control touches no real credential.
    /// </summary>
    private static Dictionary<string, string?> SyntheticAmbientEnvironment(
        GitTestDirectory directory,
        AuthenticatedHttpsGitServer server) => new(StringComparer.Ordinal)
    {
        ["HOME"] = directory.HomePath,
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(directory.HomePath, ".gitconfig"),
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_ASKPASS"] = string.Empty,
        ["SSH_AUTH_SOCK"] = string.Empty,
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_SSL_CAINFO"] = server.CertificateAuthorityPath,
    };

    /// <summary>
    /// The environment for a worker-side clone in these tests: a scratch HOME (so nothing here reads the
    /// developer's real <c>~/.ssh</c> or <c>~/.gitconfig</c>) and the fixture's CA, injected the way a
    /// private certificate authority is installed - through <c>GIT_SSL_CAINFO</c>. The worker's own
    /// hardening is deliberately left in charge of <c>GIT_CONFIG_GLOBAL</c>/<c>GIT_CONFIG_NOSYSTEM</c>:
    /// overriding them here would hide the very property under test.
    /// </summary>
    private static Dictionary<string, string?> ServerEnvironment(
        GitTestDirectory directory,
        AuthenticatedHttpsGitServer server) => new(StringComparer.Ordinal)
    {
        ["HOME"] = directory.HomePath,
        ["GIT_SSL_CAINFO"] = server.CertificateAuthorityPath,
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    /// <summary>
    /// Writes the <b>synthetic</b> ambient credential into the scratch HOME's global config: a helper
    /// script that answers the challenge with <paramref name="token"/> (a value this test invented). It
    /// has the shape a worker host's <c>~/.gitconfig</c> has when someone ran
    /// <c>git config --global credential.helper store</c> - written here only for a scratch directory,
    /// never for the developer's HOME.
    /// </summary>
    private static void WriteAmbientCredentialHelper(string homePath, string token)
    {
        // git calls a helper as `<helper> get` and writes the request (protocol/host) on stdin; the
        // helper answers with `key=value` lines. That is what "store", "cache" and the desktop helpers
        // all do - this one answers every request with the token the fixture server accepts.
        // The helper announces every invocation. Card F-018's judgement is a *count*, not a message: after
        // the hardened clone the number of calls must be exactly what it was before.
        var callLog = HelperCallLogPath(homePath);
        var helper = GitCli.WriteScript(
            Directory.CreateDirectory(Path.Combine(homePath, "bin")).FullName,
            "ambient-helper.sh",
            // Composed so no absolute interpreter path (and no escape sequence) appears in the source.
            "#!" + "/" + "bin" + "/" + "sh" + "\n"
            + "[ \"$1\" = get ] || exit 0\n"
            + $"echo invoked >> '{callLog}'\n"
            + "echo 'username=ambient-worker'\n"
            + $"echo 'password={token}'\n");

        File.WriteAllText(
            Path.Combine(homePath, ".gitconfig"),
            "[credential]\n\thelper = " + helper + "\n");
    }

    /// <summary>The file the synthetic helper appends one line to per invocation.</summary>
    private static string HelperCallLogPath(string homePath) => Path.Combine(homePath, "helper-calls.log");

    /// <summary>How many times the synthetic helper was invoked so far.</summary>
    private static int CountHelperCalls(string homePath) =>
        File.Exists(HelperCallLogPath(homePath)) ? File.ReadAllLines(HelperCallLogPath(homePath)).Length : 0;

    private static string NewToken() => "zkpat_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
}
