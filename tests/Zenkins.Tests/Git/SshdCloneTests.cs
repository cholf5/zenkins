using System.Text;
using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Milestone;
using Zenkins.Worker;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// Card Q-009's acceptance evidence, on real processes: a <b>private repository served by a real
/// OpenSSH server</b> (<c>sshd</c> running as a user process with the fixture's own keys), a real
/// Server, and a real Worker that clones <b>from the delivered SSH credential and nothing else</b> -
/// the F-018 standard, extended to the transport that card could only shape-pin.
/// <para>
/// <b>What a real sshd adds that the construction pins cannot see</b> (card F-018's bug #4):
/// </para>
/// <list type="bullet">
/// <item><b>The host-key pin does something.</b> The same delivered key, the same server, the same
/// project - and a known-hosts pin that names a different key - is refused before authentication, the
/// refusal surfaces as a named failure whose log carries <c>Host key verification failed</c>, and the
/// pinned file is not "repaired" by the run. Under the <em>correct</em> pin the identical setup
/// succeeds (fact 2), so the only variable is the pin.</item>
/// <item><b>Key format and permission reality.</b> The credential value is a real <c>ssh-keygen</c>
/// OpenSSH private key that a real sshd accepted, materialised by the product into the run's
/// <c>temp/</c> at 0600 - a key format no earlier test ever put on a wire.</item>
/// <item><b>The hardened <c>-F</c> interaction with the real binary.</b> The captured argv below is
/// what ssh <em>received</em> after the shell parsed <c>GIT_SSH_COMMAND</c> - on macOS, where ssh takes
/// <c>~</c> from the passwd entry rather than <c>$HOME</c>, the <c>-F</c> pin is the only thing that
/// keeps <c>~/.ssh/config</c> out of the exchange.</item>
/// </list>
/// <para>
/// <b>Why the counts, and why they are the server's.</b> "The clone used the delivered key" is not
/// asserted from a success message: the fixture's sshd logs at <c>VERBOSE</c> level, so the test reads
/// which key <em>fingerprints</em> the server accepted and refused. The no-credential run must leave
/// the accepted count exactly where the control clone left it (the server accepted nothing, whatever
/// the client offered), and the credentialed run's every accepted fingerprint must be the delivered
/// key's. The ambient side is synthetic here too: the worker's <c>HOME</c> is a scratch directory with
/// no <c>.ssh</c>, and <c>SSH_AUTH_SOCK</c> is emptied before the product empties it again - so nothing
/// on the host's <c>~/.ssh</c> is read or modified (the Q-008 precedent).
/// </para>
/// <para>
/// <b>Prerequisite and skip.</b> This suite needs a real OpenSSH server
/// (<see cref="SshdGitServer.UnavailabilityReason"/>); where the tools are absent the facts skip with
/// that named reason - recorded by the runner, never silent. The suite is excluded from the fast
/// filter on purpose (development.md §2), the same way the PublishAot suite is.
/// </para>
/// </summary>
[Trait("Category", "GitSshd")]
public sealed class SshdCloneTests
{
    // -------------------------------------------------------------------------------------------
    // TC-001: clone from the delivered credential, against a real sshd
    // -------------------------------------------------------------------------------------------

    [Fact(Timeout = 8 * 60 * 1000)]
    public async Task A_private_repository_clones_only_from_the_delivered_ssh_credential_against_a_real_sshd()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // The recorded skip: a machine without the OpenSSH tools never runs this suite silently -
        // the runner records a skip carrying the reason the fixture cannot start.
        var unavailable = SshdGitServer.UnavailabilityReason();
        if (unavailable is not null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(unavailable);
        }

        // ---- The private repository: a real one, served by a real sshd on loopback --------------
        using var repository = MilestoneRepository.Create(
            MilestonePipelines.Pipeline(
                "q009-ssh-private",
                env: null,
                // `git --version` is a real child process on every platform the harness supports, and
                // it needs no SDK, no shell and no asset from this test.
                MilestonePipelines.Step("Clone", "git", "--version")));
        using var sshd = SshdGitServer.Start(repository.BarePath);

        using var host = await MilestoneHost.StartAsync();

        // ---- The worker host: an isolated HOME with no key for anyone, and an ssh shim on PATH ----
        var workerHome = Path.Combine(host.RootPath, "worker-home");
        Directory.CreateDirectory(workerHome);
        Assert.False(Directory.Exists(Path.Combine(workerHome, ".ssh")));

        using var shim = RecordingSshShim.Create(host.RootPath, SshdGitServer.SshClientPath);

        // Control: outside the product, the fixture's sshd really serves the private repository with
        // the fixture's own key - which is what makes the product's refusals below meaningful. The
        // control reads the host's ~/.ssh exactly as little as the product does: -F at its own empty
        // config, its own scratch known-hosts file, IdentitiesOnly, no agent.
        var control = GitCli.Run(
            host.RootPath,
            ControlEnvironment(host.RootPath, sshd),
            "clone", "--", sshd.Url, Path.Combine(host.RootPath, "control-clone"));
        Assert.True(
            control.ExitCode == 0,
            $"the control clone must succeed with the fixture's own key: {control.StdErr}");
        var acceptedAfterControl = sshd.AcceptedPublicKeyCount;
        Assert.True(
            acceptedAfterControl > 0,
            "the control clone never authenticated against the fixture's sshd");

        await host.StartWorkerAsync(
            "q009-agent",
            WorkerEnvironment(shim, workerHome, pinnedKnownHostsPath: null));

        // ---- The project: created through the real form, pointing at the private SSH repository ---
        var projectId = await host.CreateProjectAsync("q009-ssh-private-app", "main", sshd.Url);

        // ---- Run 1: no credential assigned. The sshd accepted nothing; the run says why. ----------
        var failedRunId = await host.TriggerRunAsync(projectId);
        var failedRun = await host.WaitForTerminalAsync(failedRunId);

        Assert.Equal("Failed", failedRun.Status);
        Assert.Equal(PipelineFailureReasons.GitCredentialNotAssigned, failedRun.FailureReason);
        Assert.Null(failedRun.GitCommit);

        var failedLog = await host.WaitForLogLinesAsync(
            failedRunId,
            lines => lines.Any(line => line.Message.Contains(
                "no Git credential is assigned to this project", StringComparison.Ordinal)));

        var failedPage = await host.GetPageAsync($"/runs/{failedRunId}");
        Assert.Contains("No credential is assigned to this project", failedPage, StringComparison.Ordinal);
        Assert.Contains("id=\"run-git-credential\"", failedPage, StringComparison.Ordinal);
        Assert.Contains("id=\"run-failure-explanation\"", failedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", failedPage, StringComparison.Ordinal);

        // The server-side ambient counter: whatever the keyless ssh offered, the server accepted
        // nothing - the count is exactly where the control clone left it. The failure is the product's
        // hardening, not a broken fixture (the control above).
        Assert.Equal(acceptedAfterControl, sshd.AcceptedPublicKeyCount);

        // The ssh invocation the real run used: the no-identity shape (no -i, BatchMode=yes), the
        // per-run known-hosts file, accept-new.
        var (_, failedKnownHosts, failedHardenedConfig) = RunPaths(host, failedRun.Id);
        AssertCapturedSshIsThePinnedShape(
            shim,
            entryIndex: 0,
            WorkerGit.GitEnvironment.BuildNoIdentitySshCommand(
                failedKnownHosts,
                WorkerGit.GitEnvironment.DefaultHostKeyPolicy,
                failedHardenedConfig),
            sshd);

        // ---- The assignment: the Owner attaches the organization's SSH credential -----------------
        var credentialId = await host.CreateSshGitCredentialAsync(
            projectId, "DEPLOY_KEY", sshd.DeliveredPrivateKey);
        await host.AttachGitCredentialAsync(projectId, credentialId);

        var settingsPage = await host.GetPageAsync($"/projects/{projectId}/settings");
        Assert.Contains("credential-assignment-current", settingsPage, StringComparison.Ordinal);
        Assert.Contains("DEPLOY_KEY", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", settingsPage, StringComparison.Ordinal);

        // ---- Run 2: the same private repository, cloned with the delivered SSH key ---------------
        var successRunId = await host.TriggerRunAsync(projectId);
        var successRun = await host.WaitForTerminalAsync(successRunId);

        Assert.Equal("Success", successRun.Status);
        Assert.Null(successRun.FailureReason);
        Assert.Equal(0, successRun.ExitCode);

        // The committed tip really came out of the private repository, read independently from the
        // bare origin the fixture pushed to.
        Assert.Equal(repository.MainTip, successRun.GitCommit);
        Assert.Equal(repository.OriginMainTip, successRun.GitCommit);

        var successLog = await host.WaitForLogLinesAsync(
            successRunId,
            lines => lines.Any(line => line.Message.Contains(
                "cloning with the project's assigned SshKey Git credential", StringComparison.Ordinal)));

        // The step really ran, and the run's own record names it.
        var steps = await host.ReadStepsAsync(successRunId);
        var step = Assert.Single(steps);
        Assert.Equal("Clone", step.Name);
        Assert.Equal("Success", step.Status);

        var successPage = await host.GetPageAsync($"/runs/{successRunId}");
        Assert.Contains("assigned credential; fetched by the worker", successPage, StringComparison.Ordinal);

        // The server's own log records the delivery - which credential, for which run, for which
        // worker - and never the material.
        Assert.Contains(host.ServerOutput, line => line.Contains("Git credential delivered", StringComparison.Ordinal));
        Assert.DoesNotContain(host.ServerOutput, line => line.Contains("PRIVATE KEY", StringComparison.Ordinal));

        // The server-side counters: at least one new accepted authentication, and every accepted
        // authentication so far (the control included) used the delivered key's fingerprint. The
        // server's own observation of what authenticated, not an inference from a success message.
        Assert.True(
            sshd.AcceptedPublicKeyCount > acceptedAfterControl,
            $"the credentialed run accepted no authentication on the server "
            + $"(before: {acceptedAfterControl}, after: {sshd.AcceptedPublicKeyCount})");
        Assert.All(
            sshd.AcceptedFingerprints,
            fingerprint => Assert.Equal(sshd.DeliveredKeyFingerprint, fingerprint));

        // The ssh invocation the credentialed run used: -i into the run's own temp key file (0600,
        // materialised by the product and deleted after the operation), IdentitiesOnly, IdentityAgent
        // =none, -F hardening config, per-run UserKnownHostsFile, accept-new.
        var (successKeyPath, successKnownHosts, successHardenedConfig) = RunPaths(host, successRun.Id);
        AssertCapturedSshIsThePinnedShape(
            shim,
            entryIndex: 1,
            WorkerGit.GitEnvironment.BuildSshCommand(
                successKeyPath,
                successKnownHosts,
                WorkerGit.GitEnvironment.DefaultHostKeyPolicy,
                successHardenedConfig),
            sshd);
        Assert.EndsWith("temp" + Path.DirectorySeparatorChar + "id", successKeyPath, StringComparison.Ordinal);

        // The key material never reached a persisted line on either run.
        Assert.DoesNotContain(failedLog, line => line.Message.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(successLog, line => line.Message.Contains("PRIVATE KEY", StringComparison.Ordinal));

        // Nothing key-shaped ever appeared on the worker host: the scratch HOME still holds no .ssh.
        Assert.False(Directory.Exists(Path.Combine(workerHome, ".ssh")));

        // The worker is still its own live process (nothing about the credential path ran in-host) ...
        Assert.NotEqual(Environment.ProcessId, host.WorkerProcessId);

        // ... and every tracked process really died at cleanup: the harness's, the sshd's, the keys.
        host.Dispose();
        Assert.True(host.ChildProcessesExited);
        Assert.True(host.TemporaryRootRemoved);
        sshd.Dispose();
        Assert.True(sshd.ExitedOnDispose);
        Assert.True(sshd.RootRemoved);
        Assert.False(cancellationToken.IsCancellationRequested);
    }

    // -------------------------------------------------------------------------------------------
    // TC-002a: the honest failures that involve the credential - under a CORRECT host-key pin, so a
    // refusal is about the key, never about the host
    // -------------------------------------------------------------------------------------------

    [Fact(Timeout = 8 * 60 * 1000)]
    public async Task A_rejected_ssh_key_fails_with_a_named_authentication_reason_under_a_correct_host_key_pin()
    {
        // The recorded skip: a machine without the OpenSSH tools never runs this suite silently -
        // the runner records a skip carrying the reason the fixture cannot start.
        var unavailable = SshdGitServer.UnavailabilityReason();
        if (unavailable is not null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(unavailable);
        }

        using var repository = MilestoneRepository.Create(
            MilestonePipelines.Pipeline(
                "q009-ssh-reject",
                env: null,
                MilestonePipelines.Step("Clone", "git", "--version")));
        using var sshd = SshdGitServer.Start(repository.BarePath);
        using var host = await MilestoneHost.StartAsync();

        var workerHome = Path.Combine(host.RootPath, "worker-home");
        Directory.CreateDirectory(workerHome);
        Assert.False(Directory.Exists(Path.Combine(workerHome, ".ssh")));

        using var shim = RecordingSshShim.Create(host.RootPath, SshdGitServer.SshClientPath);

        // The worker pins the CORRECT host key: strict checking (StrictHostKeyChecking=yes) against a
        // file that really names this server's key, so every refusal below is about the credential.
        await host.StartWorkerAsync(
            "q009-reject-agent",
            WorkerEnvironment(shim, workerHome, sshd.PinnedKnownHostsPath));

        var projectId = await host.CreateProjectAsync("q009-ssh-reject-app", "main", sshd.Url);

        // Run 1: no credential assigned, under the strict pin. Still the named not-assigned reason.
        var failedRunId = await host.TriggerRunAsync(projectId);
        var failedRun = await host.WaitForTerminalAsync(failedRunId);
        Assert.Equal("Failed", failedRun.Status);
        Assert.Equal(PipelineFailureReasons.GitCredentialNotAssigned, failedRun.FailureReason);
        Assert.Null(failedRun.GitCommit);

        // The captured invocation: the no-identity shape under StrictHostKeyChecking=yes - BatchMode,
        // no -i, and the pinned file (not a per-run scratch file) as UserKnownHostsFile.
        var (_, _, failedHardenedConfig) = RunPaths(host, failedRun.Id);
        AssertCapturedSshIsThePinnedShape(
            shim,
            entryIndex: 0,
            WorkerGit.GitEnvironment.BuildNoIdentitySshCommand(
                sshd.PinnedKnownHostsPath,
                WorkerGit.GitEnvironment.PinnedHostKeyPolicy,
                failedHardenedConfig),
            sshd);

        // Run 2: a credential the fixture does not recognize - a real, valid key this fixture
        // generated but never installed. The run names the authentication rejection.
        var badCredentialId = await host.CreateSshGitCredentialAsync(
            projectId, "REJECTED_KEY", sshd.RejectedPrivateKey);
        await host.AttachGitCredentialAsync(projectId, badCredentialId);

        var rejectedRunId = await host.TriggerRunAsync(projectId);
        var rejectedRun = await host.WaitForTerminalAsync(rejectedRunId);
        Assert.Equal("Failed", rejectedRun.Status);
        Assert.Equal(PipelineFailureReasons.GitAuthRejected, rejectedRun.FailureReason);
        Assert.Null(rejectedRun.GitCommit);

        var rejectedLog = await host.WaitForLogLinesAsync(
            rejectedRunId,
            lines => lines.Any(line => line.Message.Contains(
                "Permission denied (publickey)", StringComparison.Ordinal)));

        // The server's own record: it accepted nothing (the delivered-but-unrecognized key included),
        // and it refused exactly the key the credential carried.
        Assert.Equal(0, sshd.AcceptedPublicKeyCount);
        Assert.Contains(sshd.RejectedKeyFingerprint, sshd.RefusedFingerprints);

        // The captured invocation: -i names the run's own temp key file, the strict policy, the pinned
        // file. The construction pins, runtime-verified on the rejection path too.
        var (rejectedKeyPath, _, rejectedHardenedConfig) = RunPaths(host, rejectedRun.Id);
        AssertCapturedSshIsThePinnedShape(
            shim,
            entryIndex: 1,
            WorkerGit.GitEnvironment.BuildSshCommand(
                rejectedKeyPath,
                sshd.PinnedKnownHostsPath,
                WorkerGit.GitEnvironment.PinnedHostKeyPolicy,
                rejectedHardenedConfig),
            sshd);

        // The run page says the remote rejected the project's credential, and no material leaked.
        // (The page HTML-encodes the apostrophe, so the assertion stops before it.)
        var rejectedPage = await host.GetPageAsync($"/runs/{rejectedRunId}");
        Assert.Contains(
            "The remote rejected the project",
            rejectedPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", rejectedPage, StringComparison.Ordinal);
        Assert.DoesNotContain(rejectedLog, line => line.Message.Contains("PRIVATE KEY", StringComparison.Ordinal));

        // Run 3: the DELIVERED key under the same strict pin - Success. This is the contrast that
        // makes the wrong-pin refusal of the next fact exact: same delivered key, same server, same
        // strict policy, and the only variable there is one key line in the pinned file.
        var goodCredentialId = await host.CreateSshGitCredentialAsync(
            projectId, "DEPLOY_KEY", sshd.DeliveredPrivateKey);
        await host.AttachGitCredentialAsync(projectId, goodCredentialId);

        var successRunId = await host.TriggerRunAsync(projectId);
        var successRun = await host.WaitForTerminalAsync(successRunId);
        Assert.Equal("Success", successRun.Status);
        Assert.Null(successRun.FailureReason);
        Assert.Equal(repository.MainTip, successRun.GitCommit);
        Assert.Equal(repository.OriginMainTip, successRun.GitCommit);

        // The server-side counters, end to end: exactly one accepted authentication (this run - the
        // two refusals before it accepted nothing), and it used the delivered key's fingerprint.
        Assert.Equal(1, sshd.AcceptedPublicKeyCount);
        Assert.Equal(new[] { sshd.DeliveredKeyFingerprint }, sshd.AcceptedFingerprints);

        // The captured invocation of the pinned success run: -i, IdentitiesOnly, IdentityAgent=none,
        // StrictHostKeyChecking=yes against the pinned file - the strict shape, runtime-verified.
        var (successKeyPath, _, successHardenedConfig) = RunPaths(host, successRun.Id);
        AssertCapturedSshIsThePinnedShape(
            shim,
            entryIndex: 2,
            WorkerGit.GitEnvironment.BuildSshCommand(
                successKeyPath,
                sshd.PinnedKnownHostsPath,
                WorkerGit.GitEnvironment.PinnedHostKeyPolicy,
                successHardenedConfig),
            sshd);

        var successLog = await host.WaitForLogLinesAsync(
            successRunId,
            lines => lines.Any(line => line.Message.Contains(
                "cloning with the project's assigned SshKey Git credential", StringComparison.Ordinal)));
        Assert.DoesNotContain(successLog, line => line.Message.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(workerHome, ".ssh")));
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
    }

    // -------------------------------------------------------------------------------------------
    // TC-002b: the host-key refusal - the pin doing its job, not merely being present
    // -------------------------------------------------------------------------------------------

    [Fact(Timeout = 8 * 60 * 1000)]
    public async Task A_wrong_host_key_is_refused_by_the_known_hosts_pin_and_surfaces_as_a_named_failure()
    {
        // The recorded skip: a machine without the OpenSSH tools never runs this suite silently -
        // the runner records a skip carrying the reason the fixture cannot start.
        var unavailable = SshdGitServer.UnavailabilityReason();
        if (unavailable is not null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(unavailable);
        }

        using var repository = MilestoneRepository.Create(
            MilestonePipelines.Pipeline(
                "q009-ssh-hostkey",
                env: null,
                MilestonePipelines.Step("Clone", "git", "--version")));
        using var sshd = SshdGitServer.Start(repository.BarePath);
        using var host = await MilestoneHost.StartAsync();

        // The credential is the known-good one (fact 1 proved it clones this server); the ONLY
        // variable here is the pin: it names a different key for the same [127.0.0.1]:port host.
        var projectId = await host.CreateProjectAsync("q009-ssh-hostkey-app", "main", sshd.Url);
        var credentialId = await host.CreateSshGitCredentialAsync(
            projectId, "DEPLOY_KEY", sshd.DeliveredPrivateKey);
        await host.AttachGitCredentialAsync(projectId, credentialId);

        var workerHome = Path.Combine(host.RootPath, "worker-home");
        Directory.CreateDirectory(workerHome);
        Assert.False(Directory.Exists(Path.Combine(workerHome, ".ssh")));

        using var shim = RecordingSshShim.Create(host.RootPath, SshdGitServer.SshClientPath);

        // The pin differs from the correct one in exactly one field: the key body.
        var pinnedFields = File.ReadAllText(sshd.PinnedKnownHostsPath).Trim().Split(' ');
        var wrongFields = File.ReadAllText(sshd.WrongKnownHostsPath).Trim().Split(' ');
        Assert.Equal(pinnedFields[0], wrongFields[0]);
        Assert.Equal(pinnedFields[1], wrongFields[1]);
        Assert.NotEqual(pinnedFields[2], wrongFields[2]);
        var pinnedBytesBefore = File.ReadAllBytes(sshd.WrongKnownHostsPath);

        await host.StartWorkerAsync(
            "q009-hostkey-agent",
            WorkerEnvironment(shim, workerHome, sshd.WrongKnownHostsPath));

        var failedRunId = await host.TriggerRunAsync(projectId);
        var failedRun = await host.WaitForTerminalAsync(failedRunId);

        // The refusal surfaces as a failure with a reason - the closed vocabulary's git-failed, whose
        // page sentence points at the log, and the log carries ssh's own words.
        Assert.Equal("Failed", failedRun.Status);
        Assert.Equal(PipelineFailureReasons.GitFailed, failedRun.FailureReason);
        Assert.Null(failedRun.GitCommit);

        var failedLog = await host.WaitForLogLinesAsync(
            failedRunId,
            lines => lines.Any(line => line.Message.Contains(
                "Host key verification failed", StringComparison.Ordinal)));
        Assert.Contains(
            failedLog,
            line => line.Message.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal));

        // The pin refused the host before any authentication happened: the server accepted nothing.
        Assert.Equal(0, sshd.AcceptedPublicKeyCount);

        // ... and the pin is not "repaired" by the run: the file the operator pinned is untouched.
        Assert.Equal(pinnedBytesBefore, File.ReadAllBytes(sshd.WrongKnownHostsPath));

        // The captured invocation: the strict policy against the wrong pin - the per-run
        // UserKnownHostsFile pin, runtime-verified on the path where it actually refuses.
        var (failedKeyPath, _, hardenedConfig) = RunPaths(host, failedRun.Id);
        AssertCapturedSshIsThePinnedShape(
            shim,
            entryIndex: 0,
            WorkerGit.GitEnvironment.BuildSshCommand(
                failedKeyPath,
                sshd.WrongKnownHostsPath,
                WorkerGit.GitEnvironment.PinnedHostKeyPolicy,
                hardenedConfig),
            sshd);

        // The page keeps the credential answer (the credential was delivered; the host is what
        // refused) and carries the failure explanation row.
        var failedPage = await host.GetPageAsync($"/runs/{failedRunId}");
        Assert.Contains("assigned credential; fetched by the worker", failedPage, StringComparison.Ordinal);
        Assert.Contains("id=\"run-failure-explanation\"", failedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", failedPage, StringComparison.Ordinal);
        Assert.DoesNotContain(failedLog, line => line.Message.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(workerHome, ".ssh")));
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The worker process's extra environment: an isolated <c>HOME</c> with no key for anyone, the
    /// agent socket emptied (the product empties it again for every git child), and the recording shim
    /// first on <c>PATH</c> so every ssh invocation is captured. An optional
    /// <c>ZENKINS_GIT_KNOWN_HOSTS</c> pin selects the strict host-key policy.
    /// </summary>
    private static Dictionary<string, string?> WorkerEnvironment(
        RecordingSshShim shim,
        string workerHome,
        string? pinnedKnownHostsPath)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = workerHome,
            ["SSH_AUTH_SOCK"] = string.Empty,
            ["PATH"] = shim.BinDirectory
                + Path.PathSeparator
                + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty),
        };
        if (pinnedKnownHostsPath is not null)
        {
            environment[WorkerConfiguration.GitKnownHostsVariable] = pinnedKnownHostsPath;
        }

        return environment;
    }

    /// <summary>
    /// The control clone's environment: the fixture's own proof, outside the product, hermetic the way
    /// every fixture command is - and reading the host's <c>~/.ssh</c> exactly as little as the product
    /// does (<c>-F</c> at an empty config of its own, a scratch known-hosts file, no agent).
    /// </summary>
    private static Dictionary<string, string?> ControlEnvironment(string rootDirectory, SshdGitServer sshd)
    {
        var controlHome = Directory.CreateDirectory(Path.Combine(rootDirectory, "control-home")).FullName;
        var emptySshConfig = Path.Combine(rootDirectory, "control-ssh-config");
        File.WriteAllText(emptySshConfig, string.Empty);
        var knownHosts = Path.Combine(rootDirectory, "control-known-hosts");
        File.WriteAllText(knownHosts, string.Empty);

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = controlHome,
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(controlHome, ".gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["SSH_AUTH_SOCK"] = string.Empty,
            ["GIT_SSH_COMMAND"] = string.Join(
                ' ',
                "ssh",
                "-F", $"\"{emptySshConfig}\"",
                "-i", $"\"{sshd.DeliveredKeyPath}\"",
                "-o", "IdentitiesOnly=yes",
                "-o", "IdentityAgent=none",
                "-o", $"StrictHostKeyChecking={WorkerGit.GitEnvironment.DefaultHostKeyPolicy}",
                "-o", $"UserKnownHostsFile=\"{knownHosts}\""),
        };
    }

    /// <summary>
    /// The per-run paths the product's own hardening names, computed from the run id the same way
    /// <c>WorkDirectoryService.Prepare</c> renders it (the canonical <c>D</c> form): the key file the
    /// credential is materialised into, the per-run known-hosts file, and the empty ssh config
    /// <c>-F</c> points at.
    /// </summary>
    private static (string KeyPath, string KnownHostsPath, string HardenedSshConfigPath) RunPaths(
        MilestoneHost host, Guid runId)
    {
        var runRoot = Path.Combine(host.WorkRoot, runId.ToString("D"));
        return (
            Path.Combine(runRoot, "temp", WorkerGit.GitCredentialFiles.SshKeyFileName),
            Path.Combine(runRoot, "temp", "known_hosts"),
            Path.Combine(runRoot, "hardening", WorkerGit.GitEnvironment.EmptySshConfigFileName));
    }

    /// <summary>
    /// The recorded argv of one real ssh invocation must carry the product's pinned shape: the exact
    /// words <see cref="WorkerGit.GitEnvironment.BuildSshCommand"/> (or
    /// <see cref="WorkerGit.GitEnvironment.BuildNoIdentitySshCommand"/>) builds, in order, followed by
    /// what git itself appends - the fixture's port, the fixture's host, and the transport command
    /// naming the fixture's repository. This is the bridge between the construction-level pins
    /// (GitSshConstructionTests) and what ssh actually received after the shell parsed
    /// <c>GIT_SSH_COMMAND</c>.
    /// </summary>
    private static void AssertCapturedSshIsThePinnedShape(
        RecordingSshShim shim,
        int entryIndex,
        string expectedGitSshCommand,
        SshdGitServer sshd)
    {
        var entries = shim.ReadEntries();
        Assert.True(
            entries.Length > entryIndex,
            $"the worker made {entries.Length} ssh invocation(s); expected at least {entryIndex + 1}. "
            + "Recorded: " + string.Join(" | ", entries.Select(RecordingSshShim.Describe)));
        var words = entries[entryIndex];
        var expected = ParseGitSshCommand(expectedGitSshCommand);

        // The pinned command names its program ("ssh"); the shim records the ARGUMENTS the shell
        // handed that program - the program word itself is argv[0], which "$@" never repeats.
        Assert.Equal("ssh", expected[0]);
        var expectedArguments = expected.Skip(1).ToArray();

        Assert.True(
            words.Length > expectedArguments.Length,
            $"ssh was invoked as [{RecordingSshShim.Describe(words)}]; the pinned shape is "
            + $"[{string.Join(' ', expected)}] followed by git's own transport words.");

        // The product's tokens, in order, exactly.
        Assert.Equal(expectedArguments, words.Take(expectedArguments.Length).ToArray());

        // git's own additions: the fixture's port and host, and a transport command that really names
        // the fixture's repository (the path single-quoted by git for the remote shell).
        var rest = words.Skip(expectedArguments.Length).ToArray();
        var destination = new Uri(sshd.Url);
        Assert.Contains("-p", rest);
        Assert.Contains(destination.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), rest);
        Assert.Contains($"{destination.UserInfo}@{destination.Host}", rest);
        Assert.Contains($"git-upload-pack '{sshd.RepositoryPath}'", rest);
    }

    /// <summary>
    /// Splits <c>GIT_SSH_COMMAND</c> into the words a shell would hand the ssh program: git parses the
    /// variable with shell rules, and <see cref="WorkerGit.GitEnvironment.Quote"/> produces the only
    /// quoting the product ever emits (double quotes, with <c>\"</c> and <c>\\</c> escapes). The paths
    /// in this suite hold neither quotes nor shell metacharacters, so the parsed words are exact.
    /// </summary>
    private static string[] ParseGitSshCommand(string command)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (inQuotes && character == '\\'
                && index + 1 < command.Length
                && (command[index + 1] == '"' || command[index + 1] == '\\'))
            {
                current.Append(command[++index]);
            }
            else if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (character == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return [.. words];
    }
}
