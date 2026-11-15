using Xunit;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// The SSH half of F-013's credential contract, verified at the construction level (the card's own
/// decision: no sshd in the suite). Three properties are pinned: the exact <c>GIT_SSH_COMMAND</c> git is
/// given, the key file's <c>0600</c> mode, and its deletion after the operation - the last one through
/// a real (immediately failing) ssh invocation, so the cleanup is exercised on a failure path.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitSshConstructionTests
{
    private const string PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nzenkins-test-key-material\n-----END OPENSSH PRIVATE KEY-----\n";

    [Fact]
    public void The_ssh_command_line_is_pinned()
    {
        var command = WorkerGit.GitEnvironment.BuildSshCommand(
            "/tmp/run/temp/id",
            "/tmp/run/temp/known_hosts",
            WorkerGit.GitEnvironment.DefaultHostKeyPolicy,
            "/tmp/run/temp/ssh_config.hardened");

        // Card F-018 added two options to the F-013 shape: -F names an ssh config of the run's own (so
        // ~/.ssh/config cannot contribute an IdentityFile or a ProxyCommand) and IdentityAgent=none
        // keeps the host's agent out of the exchange even when an -i key is supplied.
        Assert.Equal(
            "ssh -F \"/tmp/run/temp/ssh_config.hardened\" -i \"/tmp/run/temp/id\" -o IdentitiesOnly=yes "
            + "-o IdentityAgent=none -o StrictHostKeyChecking=accept-new "
            + "-o UserKnownHostsFile=\"/tmp/run/temp/known_hosts\"",
            command);

        // A pinned known-hosts file switches the policy to strict.
        var pinned = WorkerGit.GitEnvironment.BuildSshCommand(
            "/tmp/run/temp/id",
            "/etc/zenkins/known_hosts",
            WorkerGit.GitEnvironment.PinnedHostKeyPolicy,
            "/tmp/run/temp/ssh_config.hardened");
        Assert.Contains("StrictHostKeyChecking=yes", pinned, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_credential_ssh_offers_no_identity_at_all()
    {
        // Card F-018: an SSH project with no assigned credential must not reach for the host's keys.
        var command = WorkerGit.GitEnvironment.BuildNoIdentitySshCommand(
            "/tmp/run/temp/known_hosts",
            WorkerGit.GitEnvironment.DefaultHostKeyPolicy,
            "/tmp/run/temp/ssh_config.hardened");

        Assert.Equal(
            "ssh -F \"/tmp/run/temp/ssh_config.hardened\" -o IdentitiesOnly=yes -o IdentityAgent=none "
            + "-o BatchMode=yes -o StrictHostKeyChecking=accept-new "
            + "-o UserKnownHostsFile=\"/tmp/run/temp/known_hosts\"",
            command);
        Assert.DoesNotContain("-i ", command, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_spaces_and_quotes_is_quoted_for_gits_parser()
    {
        var command = WorkerGit.GitEnvironment.BuildSshCommand(
            "/tmp/a b/id",
            "/tmp/a \"b\"/known_hosts",
            WorkerGit.GitEnvironment.DefaultHostKeyPolicy,
            "/tmp/a b/ssh_config.hardened");

        Assert.Contains("-i \"/tmp/a b/id\"", command, StringComparison.Ordinal);
        Assert.Contains("UserKnownHostsFile=\"/tmp/a \\\"b\\\"/known_hosts\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_file_is_0600_and_is_deleted_on_dispose()
    {
        using var directory = new GitTestDirectory("ssh-mode");
        var temp = directory.Combine("temp");
        var material = new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.SshKey, PrivateKey);

        var path = string.Empty;
        using (var carrier = WorkerGit.GitCredentialFiles.Materialise(temp, material))
        {
            Assert.NotNull(carrier.SshKeyPath);
            Assert.Null(carrier.AskPassPath);
            path = carrier.SshKeyPath!;
            Assert.True(File.Exists(path));
            Assert.Equal(PrivateKey, File.ReadAllText(path));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));

                // The directory itself is 0700: the file is only reachable by this user.
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(temp));
            }
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_askpass_helper_is_0700_and_answers_both_prompts()
    {
        using var directory = new GitTestDirectory("askpass-mode");
        var temp = directory.Combine("temp");
        var material = new WorkerGit.GitCredentialMaterial(
            WorkerGit.GitCredentialKind.HttpsToken,
            "zkpat_token_with_'quotes'");

        using var carrier = WorkerGit.GitCredentialFiles.Materialise(temp, material);
        Assert.NotNull(carrier.AskPassPath);
        Assert.Null(carrier.SshKeyPath);

        var script = File.ReadAllText(carrier.AskPassPath!);
        if (OperatingSystem.IsWindows())
        {
            // The Windows helper is a cmd batch (git-askpass.cmd), the one form Process.Start can
            // run there: the username prompt is told apart by a "sername" match, and the value is
            // written cmd-escaped - for a token without % that is the value verbatim.
            Assert.StartsWith("@echo off", script, StringComparison.Ordinal);
            Assert.Contains("sername", script, StringComparison.Ordinal);
            Assert.Contains(material.Secret, script, StringComparison.Ordinal);
            Assert.Contains(
                WorkerGit.GitCredentialMaterial.DefaultTokenUserName, script, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("#!" + "/" + "bin" + "/" + "sh", script, StringComparison.Ordinal);
            Assert.Contains("[Uu]sername*", script, StringComparison.Ordinal);

            // The value is a single quoted shell word, and the quote character is escaped - a token
            // is never allowed to break out of its literal.
            Assert.Contains("'zkpat_token_with_'\\''quotes'\\'''", script, StringComparison.Ordinal);

            // 0700: the helper holds the value, so only the worker's own user may read or run it.
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(carrier.AskPassPath!));
        }
    }

    [Fact]
    public void A_token_with_a_line_break_is_refused_rather_than_injected()
    {
        using var directory = new GitTestDirectory("askpass-inject");
        var temp = directory.Combine("temp");
        var material = new WorkerGit.GitCredentialMaterial(
            WorkerGit.GitCredentialKind.HttpsToken,
            "zkpat_one\nrm -rf /");

        var failure = Assert.Throws<WorkerGit.GitException>(
            () => WorkerGit.GitCredentialFiles.Materialise(temp, material));

        Assert.Equal(WorkerGit.GitFailureKind.Credential, failure.Kind);
        Assert.DoesNotContain("rm -rf", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ssh_clone_uses_the_key_file_and_deletes_it_afterwards()
    {
        using var directory = new GitTestDirectory("ssh-clone");
        // The recorded skip (Q-009's contract): Windows cannot exec the shim this fact observes
        // through, so the fact is named as skipped there rather than silently red or green.
        if (GitCli.RecordingShimUnavailableReason() is { } reason)
        {{
            throw Xunit.Sdk.SkipException.ForSkip(reason);
        }}

        using var shim = RecordingGitShim.Create(directory, GitCli.ResolveGitPath());
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var material = new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.SshKey, PrivateKey);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work,
                GitCli.Environment(directory),
                lines.Add,
                gitExecutable: shim.GitPath,
                operationTimeout: TimeSpan.FromSeconds(30)));

        // Port 1 is closed: ssh fails immediately, which is all the construction pin needs. The key
        // file is still written, wired into GIT_SSH_COMMAND and cleaned up on the failure path.
        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            "ssh://git@127.0.0.1:1/repo.git",
            "main",
            material,
            TestContext.Current.CancellationToken));

        Assert.Contains(failure.Kind, new[]
        {
            WorkerGit.GitFailureKind.Unreachable,
            WorkerGit.GitFailureKind.AuthenticationRejected,
            WorkerGit.GitFailureKind.Failed,
        });
        Assert.DoesNotContain("PRIVATE KEY", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "zenkins-test-key-material",
            failure.Message,
            StringComparison.Ordinal);

        // The shim recorded the environment git was given: the pinned ssh command, with the key file
        // inside this run's temp directory.
        var sshLines = shim.ReadEntries()
            .Where(entry => entry.StartsWith("SSH:", StringComparison.Ordinal))
            .Select(entry => entry["SSH:".Length..])
            .Where(value => value.Length > 0)
            .ToArray();
        Assert.NotEmpty(sshLines);
        var sshCommand = sshLines[0];

        // Card F-018's hardening is part of the command: -F pins an ssh config of the run's own (so
        // ~/.ssh/config cannot add an IdentityFile) and IdentityAgent=none keeps any host agent out,
        // while -i still names the delivered key.
        Assert.StartsWith("ssh -F \"", sshCommand, StringComparison.Ordinal);
        Assert.Contains("-i \"", sshCommand, StringComparison.Ordinal);
        Assert.Contains("IdentityAgent=none", sshCommand, StringComparison.Ordinal);
        Assert.Contains("IdentitiesOnly=yes", sshCommand, StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking=accept-new", sshCommand, StringComparison.Ordinal);
        Assert.Contains(work.HardeningPath, sshCommand, StringComparison.Ordinal);
        Assert.Contains(work.TempPath, sshCommand, StringComparison.Ordinal);
        Assert.Contains(WorkerGit.GitCredentialFiles.SshKeyFileName, sshCommand, StringComparison.Ordinal);

        // After the operation: the key file is gone and no file holds the key material.
        Assert.False(File.Exists(Path.Combine(work.TempPath, WorkerGit.GitCredentialFiles.SshKeyFileName)));
        foreach (var file in Directory.EnumerateFiles(work.RootPath, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("zenkins-test-key-material", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_known_hosts_pin_that_does_not_exist_is_refused_before_git_runs()
    {
        using var directory = new GitTestDirectory("ssh-pin");
        using var shim = RecordingGitShim.Create(directory, GitCli.ResolveGitPath());
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work,
                GitCli.Environment(directory),
                gitExecutable: shim.GitPath,
                knownHostsPath: directory.Combine("missing_known_hosts")));

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            "ssh://git@127.0.0.1:1/repo.git",
            "main",
            new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.SshKey, PrivateKey),
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.Credential, failure.Kind);
        Assert.Contains("ZENKINS_GIT_KNOWN_HOSTS", failure.Message, StringComparison.Ordinal);
        Assert.Empty(shim.ReadEntries());
    }
}
