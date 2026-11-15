using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Git;
using Zenkins.Tests.Recovery;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Card F-018's acceptance evidence, on real processes: a <b>private repository behind HTTPS with a
/// Basic-auth challenge</b>, a real Server, and a real Worker whose host holds <b>no</b> usable ambient
/// credential - and which is nevertheless handed one the ambient credential it <em>does</em> hold must
/// not be used.
/// <para>
/// <b>The two runs are the card.</b>
/// </para>
/// <list type="number">
/// <item>With nothing attached, the run <b>fails</b> with
/// <see cref="PipelineFailureReasons.GitCredentialNotAssigned"/>, the run's log says no credential is
/// assigned, and the run page says the same in words. This is the important half: an ambient credential
/// helper <em>is</em> present in the worker's HOME and <em>is</em> proven to work (the control clone), and
/// the worker still refuses to use it.</item>
/// <item>After the Owner attaches the organization's credential on the project's settings tab, a second
/// run clones the same private repository, builds it, and reports the pushed commit.</item>
/// </list>
/// <para>
/// <b>Why the control clone, and why it is synthetic.</b> "The clone failed" alone would prove nothing
/// about ambient credentials, so the test plants one and first proves, outside the product, that a plain
/// <c>git clone</c> authenticates with it. That credential is written by this test (a helper script in a
/// scratch HOME, holding an invented value) and the control runs with the machine's own git configuration
/// disabled, so no real credential store - login keychain, ssh agent, <c>~/.ssh</c> - is read or
/// reachable. The worker's failure is therefore the product's hardening, not the absence of a credential
/// on the machine.
/// </para>
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneGitCredentialTests
{
    [Fact(Timeout = 8 * 60 * 1000)]
    public async Task A_private_repository_clones_only_from_the_credential_the_server_delivers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // ---- The private repository: a real one, served over TLS, demanding a token -------------
        using var repository = MilestoneRepository.Create(
            MilestonePipelines.Pipeline(
                "f018-private",
                env: null,
                // `git --version` is a real child process on every platform the harness supports, and it
                // needs no SDK, no shell and no asset from this test.
                MilestonePipelines.Step("Clone", "git", "--version")));
        GitCli.MustRun(repository.BarePath, repository.Environment, "update-server-info");

        var token = "zkpat_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var gitServer = AuthenticatedHttpsGitServer.Start(repository.BarePath, token);

        using var host = await MilestoneHost.StartAsync();

        // ---- The worker host: an isolated HOME with no key for anyone, plus one planted ambient
        //      credential so "the ambient credential was not used" is measured, not assumed ----------
        var workerHome = Path.Combine(host.RootPath, "worker-home");
        Directory.CreateDirectory(workerHome);

        // The "ambient" credential is entirely synthetic - a helper script this test wrote, holding a value
        // this test invented, in a scratch HOME - and the control group below runs with the machine's own
        // git configuration switched off. Nothing here touches a real credential store, and the worker's
        // HOME holds no key material at all, which is what "a host with no key for that repository" means.
        var ambientHelper = WriteAmbientCredentialHelper(workerHome, token);
        Assert.False(Directory.Exists(Path.Combine(workerHome, ".ssh")));

        // Control: the synthetic helper really authenticates when the worker's hardening is not in play.
        var controlEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = workerHome,
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(workerHome, ".gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_ASKPASS"] = string.Empty,
            ["SSH_AUTH_SOCK"] = string.Empty,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_SSL_CAINFO"] = gitServer.CertificateAuthorityPath,
        };
        var controlClonePath = Path.Combine(host.RootPath, "control-clone");
        var control = GitCli.Run(
            host.RootPath,
            controlEnvironment,
            "clone", "--", gitServer.Url, controlClonePath);
        Assert.True(
            control.ExitCode == 0,
            $"the control clone must succeed with the synthetic helper (it is what makes the worker's "
            + $"refusal meaningful): {control.StdErr}");
        Assert.True(gitServer.AuthenticatedRequests > 0);
        Assert.True(File.Exists(ambientHelper));
        var helperCallsAfterControl = CountHelperCalls(workerHome);
        Assert.True(helperCallsAfterControl > 0, "the synthetic helper was never invoked by the control clone");

        await host.StartWorkerAsync(
            "f018-agent",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HOME"] = workerHome,
                ["GIT_SSL_CAINFO"] = gitServer.CertificateAuthorityPath,
                ["SSH_AUTH_SOCK"] = string.Empty,
            });

        // ---- The project: created through the real form, pointing at the private HTTPS repository ---
        var projectId = await host.CreateProjectAsync("f018-private-app", "main", gitServer.Url);

        // ---- Run 1: no credential assigned. The ambient credential exists and would work; the run must
        //      fail and say why. ------------------------------------------------------------------------
        var failedRunId = await host.TriggerRunAsync(projectId);
        var failedRun = await host.WaitForTerminalAsync(failedRunId);

        Assert.Equal("Failed", failedRun.Status);
        Assert.Equal(PipelineFailureReasons.GitCredentialNotAssigned, failedRun.FailureReason);
        Assert.Null(failedRun.GitCommit);

        // The log lines are persisted in flush-sized batches, so the assertion waits for the line the run
        // is expected to carry instead of reading once and racing the ingest.
        var failedLog = await host.WaitForLogLinesAsync(
            failedRunId,
            lines => lines.Any(line => line.Message.Contains(
                "no Git credential is assigned to this project", StringComparison.Ordinal)));

        var failedPage = await host.GetPageAsync($"/runs/{failedRunId}");
        Assert.Contains("No credential is assigned to this project", failedPage, StringComparison.Ordinal);
        Assert.Contains("id=\"run-git-credential\"", failedPage, StringComparison.Ordinal);
        Assert.Contains("id=\"run-failure-explanation\"", failedPage, StringComparison.Ordinal);
        Assert.DoesNotContain(token, failedPage, StringComparison.Ordinal);

        // The server's own record agrees, and nothing ever delivered material for that run. The decisive
        // half is the count: the synthetic helper in the worker's HOME was never consulted, so the failure
        // is the product's hardening and not an empty host.
        Assert.DoesNotContain(host.ServerOutput, line => line.Contains(token, StringComparison.Ordinal));
        Assert.Equal(helperCallsAfterControl, CountHelperCalls(workerHome));

        // ---- The assignment: the Owner attaches the organization's credential on the settings tab -----
        var credentialId = await host.CreateGitCredentialAsync(projectId, "PRIVATE_REPO_TOKEN", token);
        await host.AttachGitCredentialAsync(projectId, credentialId);

        var settingsPage = await host.GetPageAsync($"/projects/{projectId}/settings");
        Assert.Contains("credential-assignment-current", settingsPage, StringComparison.Ordinal);
        Assert.Contains("PRIVATE_REPO_TOKEN", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain(token, settingsPage, StringComparison.Ordinal);

        // ---- Run 2: the same private repository, cloned with the delivered credential ---------------
        var successRunId = await host.TriggerRunAsync(projectId);
        var successRun = await host.WaitForTerminalAsync(successRunId);

        Assert.Equal("Success", successRun.Status);
        Assert.Null(successRun.FailureReason);
        Assert.Equal(0, successRun.ExitCode);

        // The committed tip really came out of the private repository, read independently from the bare
        // origin the fixture pushed to.
        Assert.Equal(repository.MainTip, successRun.GitCommit);
        Assert.Equal(repository.OriginMainTip, successRun.GitCommit);

        var successLog = await host.WaitForLogLinesAsync(
            successRunId,
            lines => lines.Any(line => line.Message.Contains(
                "cloning with the project's assigned HttpsToken Git credential", StringComparison.Ordinal)));

        // The step really ran, and the run's own record names it.
        var steps = await host.ReadStepsAsync(successRunId);
        var step = Assert.Single(steps);
        Assert.Equal("Clone", step.Name);
        Assert.Equal("Success", step.Status);

        var successPage = await host.GetPageAsync($"/runs/{successRunId}");
        Assert.Contains("assigned credential; fetched by the worker", successPage, StringComparison.Ordinal);

        // The server's own log records the delivery - which credential, for which run, for which worker -
        // and never the material.
        Assert.Contains(host.ServerOutput, line => line.Contains("Git credential delivered", StringComparison.Ordinal));
        Assert.DoesNotContain(host.ServerOutput, line => line.Contains(token, StringComparison.Ordinal));

        // Neither run's log carries the token: the material never reaches a persisted line.
        Assert.DoesNotContain(failedLog, line => line.Message.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(successLog, line => line.Message.Contains(token, StringComparison.Ordinal));

        // Nothing key-shaped ever appeared on the worker host: the scratch HOME still holds only the
        // synthetic configuration this test wrote, and no .ssh directory was created by either run.
        Assert.False(Directory.Exists(Path.Combine(workerHome, ".ssh")));
        Assert.True(File.Exists(Path.Combine(workerHome, ".gitconfig")));

        // The worker is still its own live process (nothing about the credential path ran in-host).
        Assert.NotEqual(Environment.ProcessId, host.WorkerProcessId);
        Assert.False(cancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// AC-2's "survives a restart", proven against a restarted <b>process</b> rather than a fresh
    /// <c>DbContext</c>: the assignment is made through the settings tab, the real server is stopped and
    /// started again over the same database (the harness's own restart path: same binaries, same options,
    /// same SQLite file, a new process and port), and the settings tab then still states which credential,
    /// by whom and when.
    /// </summary>
    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task An_assignment_and_its_attribution_survive_a_server_restart()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await MilestoneHost.StartAsync();

        var projectId = await host.CreateProjectAsync("restart-app");
        var credentialId = await host.CreateGitCredentialAsync(
            projectId,
            "RESTART_TOKEN",
            "zkpat_" + Guid.NewGuid().ToString("N"));
        await host.AttachGitCredentialAsync(projectId, credentialId);

        var before = await host.GetPageAsync($"/projects/{projectId}/settings");
        Assert.Contains("credential-assignment-current", before, StringComparison.Ordinal);
        Assert.Contains("RESTART_TOKEN", before, StringComparison.Ordinal);

        // The restart: the process really goes away, the database does not. The process id is the direct
        // evidence - a new port and a still-valid cookie could in principle describe the same process.
        var originalServerProcessId = host.ServerProcessId;
        host.StopServer();
        await RecoveryHost.WaitForProcessGoneAsync(originalServerProcessId, TimeSpan.FromSeconds(15));

        await host.StartServerAgainAsync();
        Assert.NotEqual(originalServerProcessId, host.ServerProcessId);

        var after = await host.GetPageAsync($"/projects/{projectId}/settings");
        Assert.Contains("credential-assignment-current", after, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-assignment-none", after, StringComparison.Ordinal);
        Assert.Contains("RESTART_TOKEN", after, StringComparison.Ordinal);
        // Attribution: when (a UTC instant), and by whom - resolved below from the row the restarted
        // server holds, so the assertion cannot assume an e-mail address.
        Assert.Contains("UTC", after, StringComparison.Ordinal);
        Assert.DoesNotContain("Not recorded", after, StringComparison.Ordinal);

        // ...and the row the restarted server reads, straight from its own database.
        var rows = await host.ReadAsync(
            "SELECT \"GitCredentialId\", \"GitCredentialAssignedByUserId\", \"GitCredentialAssignedAt\" "
            + $"FROM \"Projects\" WHERE \"Id\" = '{projectId.ToString("D").ToUpperInvariant()}'",
            reader => new
            {
                CredentialId = reader.IsDBNull(0) ? null : reader.GetString(0),
                AssignedBy = reader.IsDBNull(1) ? null : reader.GetString(1),
                AssignedAt = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
            });

        var row = Assert.Single(rows);
        Assert.Equal(credentialId.ToString("D").ToUpperInvariant(), row.CredentialId);
        Assert.False(string.IsNullOrWhiteSpace(row.AssignedBy));
        Assert.NotNull(row.AssignedAt);

        // ...and the page names that operator's e-mail, which is what "by whom" means to a reader.
        var emails = await host.ReadAsync(
            $"SELECT \"Email\" FROM \"AspNetUsers\" WHERE \"Id\" = '{row.AssignedBy}'",
            reader => reader.GetString(0));
        Assert.Contains(Assert.Single(emails), after, StringComparison.Ordinal);
    }

    /// <summary>How many times the synthetic helper was invoked so far.</summary>
    private static int CountHelperCalls(string homePath)
    {
        var path = Path.Combine(homePath, "helper-calls.log");
        return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
    }

    /// <summary>
    /// The ambient credential a worker host has when someone has run
    /// <c>git config --global credential.helper …</c>: a helper in <c>$HOME/.gitconfig</c> that answers
    /// every challenge with the token this fixture server accepts. Written for a scratch host, never for
    /// the developer's real HOME.
    /// </summary>
    private static string WriteAmbientCredentialHelper(string homePath, string token)
    {
        // The helper announces each invocation, so "the worker did not use it" is a count that did not move
        // rather than a message that was not printed.
        var callLog = Path.Combine(homePath, "helper-calls.log");
        var helper = GitCli.WriteScript(
            Directory.CreateDirectory(Path.Combine(homePath, "bin")).FullName,
            "ambient-helper.sh",
            "#!" + "/" + "bin" + "/" + "sh" + "\n"
            + "[ \"$1\" = get ] || exit 0\n"
            + $"echo invoked >> '{callLog}'\n"
            + "echo 'username=ambient-worker'\n"
            + $"echo 'password={token}'\n");

        // Git's config parser treats a backslash as an escape, so a windows path written verbatim
        // is a "bad config line"; git itself accepts forward slashes on every platform, which is
        // what the helper value uses.
        File.WriteAllText(
            Path.Combine(homePath, ".gitconfig"),
            "[credential]\n\thelper = " + helper.Replace('\\', '/') + "\n");
        return helper;
    }
}
