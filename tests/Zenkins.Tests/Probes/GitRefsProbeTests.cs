using System.Net;
using System.Net.Sockets;
using Xunit;
using Zenkins.Shared.Probes;
using Zenkins.Tests.Git;
using Zenkins.Worker;
using Zenkins.Worker.Git;

namespace Zenkins.Tests.Probes;

/// <summary>
/// Card I-005's git half: <see cref="GitRefsProbe"/> against real repositories and real remotes. Every fact
/// here is about the <b>typed answer</b> - the property branch discovery depends on is that a remote the
/// worker could not reach never looks like a remote with no branches.
/// <para>
/// <b>What is real.</b> The heads and HEAD facts run against a local bare repository built by the F-013
/// fixture; the auth fact runs against the fixture's dumb-HTTP server with a deliberate wrong token; the
/// timeout fact runs against a socket that accepts and never answers. Only the SSH transport is absent, and
/// that is card Q-009's harness (it needs a real sshd).
/// </para>
/// </summary>
public sealed class GitRefsProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_probe_lists_the_remote_heads_and_head_in_one_git_invocation()
    {
        using var fixture = GitFixtureRepository.Create("probe-heads");
        var (probe, work, root) = CreateProbe(fixture.Directory);
        using var _ = root;

        var result = await probe.ProbeAsync(fixture.BarePath, credential: null, CancellationToken);

        Assert.Equal(GitProbeOutcomes.Refs, result.Outcome);
        Assert.Equal(
            new[] { "refs/heads/feature", "refs/heads/main" },
            result.Heads.Select(head => head.Name).ToArray());
        Assert.Equal(fixture.FeatureTip, result.Heads.Single(head => head.Name == "refs/heads/feature").Sha);
        Assert.Equal(fixture.MainTip, result.Heads.Single(head => head.Name == "refs/heads/main").Sha);

        // The remote's HEAD: what it points at, and which branch that is. Both come from the same
        // invocation (the --symref line plus the HEAD line), which is the card's "one git request".
        Assert.Equal(fixture.MainTip, result.HeadSha);
        Assert.Equal("refs/heads/main", result.HeadBranch);
        Assert.Null(result.Message);

        var invocation = Assert.Single(probe.Trace);
        Assert.StartsWith("ls-remote ", invocation.Arguments, StringComparison.Ordinal);
        Assert.Equal(0, invocation.ExitCode);

        // The operation's own carrier files are gone on the success path too (the loop deletes the whole
        // scratch tree; this is the part the probe itself owns).
        Assert.False(File.Exists(Path.Combine(work.TempPath, GitCredentialFiles.AskPassFileName)));
        Assert.False(File.Exists(Path.Combine(work.TempPath, GitCredentialFiles.SshKeyFileName)));
    }

    [Fact]
    public async Task An_empty_repository_is_a_success_with_no_heads()
    {
        // "Which heads exist?" has a legitimate answer of "none": a freshly created repository. It must be
        // the success shape with zero refs, never one of the failure values - otherwise branch discovery
        // could not tell an empty repository from an unreachable one.
        using var fixture = GitFixtureRepository.Create("probe-empty");
        var bare = fixture.Directory.Combine("empty.git");
        GitCli.MustRun(fixture.Directory.RootPath, fixture.Environment, "init", "--bare", bare);

        var (probe, _, root) = CreateProbe(fixture.Directory);
        using var _ = root;
        var result = await probe.ProbeAsync(bare, credential: null, CancellationToken);

        Assert.Equal(GitProbeOutcomes.Refs, result.Outcome);
        Assert.Empty(result.Heads);
        Assert.Null(result.HeadSha);
        Assert.Null(result.HeadBranch);
    }

    [Fact]
    public async Task A_wrong_token_is_git_auth_rejected_and_never_an_empty_ref_list()
    {
        // A real HTTP conversation with a real 401: the fixture's server demands a token, the probe presents
        // a different one. The rejection must be the typed auth outcome - not "refs" with no heads.
        using var fixture = GitFixtureRepository.Create("probe-auth");
        using var server = LocalHttpGitServer.Start(fixture.BarePath, requiredToken: "the-right-token");

        var (probe, _, root) = CreateProbe(fixture.Directory);
        using var _ = root;
        var credential = new GitCredentialMaterial(
            GitCredentialKind.HttpsToken,
            "the-wrong-token",
            "x-access-token");
        var result = await probe.ProbeAsync(server.Url, credential, CancellationToken);

        Assert.Equal(GitProbeOutcomes.AuthRejected, result.Outcome);
        Assert.Empty(result.Heads);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.True(server.RejectedRequests > 0, "The server never challenged the probe's credential.");
        Assert.DoesNotContain("the-wrong-token", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_remote_is_git_unreachable()
    {
        using var fixture = GitFixtureRepository.Create("probe-unreachable");

        // A port nothing listens on (bound and released, so the OS will not hand it to a service mid-test).
        int port;
        using (var probeSocket = new TcpListener(IPAddress.Loopback, 0))
        {
            probeSocket.Start();
            port = ((IPEndPoint)probeSocket.LocalEndpoint).Port;
            probeSocket.Stop();
        }

        var (probe, _, root) = CreateProbe(fixture.Directory);
        using var _ = root;
        var result = await probe.ProbeAsync($"http://127.0.0.1:{port}/repo.git", credential: null, CancellationToken);

        Assert.Equal(GitProbeOutcomes.Unreachable, result.Outcome);
        Assert.Empty(result.Heads);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task A_remote_that_never_answers_is_git_timeout()
    {
        // A listening socket whose backlog absorbs the connection and never answers: the TCP handshake
        // completes (so this is not "unreachable"), and git waits for an HTTP response that never comes
        // until the probe's own budget fires and the process tree is killed. The budget is deliberately
        // short - this fact measures the kill path, not patience.
        using var blackHole = new TcpListener(IPAddress.Loopback, 0);
        blackHole.Start();
        var port = ((IPEndPoint)blackHole.LocalEndpoint).Port;

        using var fixture = GitFixtureRepository.Create("probe-timeout");
        var (probe, _, root) = CreateProbe(fixture.Directory, TimeSpan.FromSeconds(1.5));
        using var _ = root;

        var started = DateTimeOffset.UtcNow;
        var result = await probe.ProbeAsync(
            $"http://127.0.0.1:{port}/repo.git",
            credential: null,
            CancellationToken);

        Assert.Equal(GitProbeOutcomes.Timeout, result.Outcome);
        Assert.Empty(result.Heads);
        Assert.True(
            DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30),
            "The probe did not honour its own git budget.");
    }

    [Fact]
    public async Task An_unsupported_credential_kind_is_a_generic_failure_with_a_message()
    {
        // An SSH key against an https URL is a deployment mistake. It must be a classified failure (the
        // credential kind cannot work here), not an exception that escapes the probe loop.
        using var fixture = GitFixtureRepository.Create("probe-kind");
        using var server = LocalHttpGitServer.Start(fixture.BarePath, requiredToken: null);

        var (probe, _, root) = CreateProbe(fixture.Directory);
        using var _ = root;
        var result = await probe.ProbeAsync(
            server.Url,
            new GitCredentialMaterial(GitCredentialKind.SshKey, "-----BEGIN KEY-----"),
            CancellationToken);

        Assert.Equal(GitProbeOutcomes.Failed, result.Outcome);
        Assert.Contains("ssh key", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One probe over a prepared scratch tree (the product's own <c>work/probes/{id}</c> shape, so the
    /// cleanup assertion is about the real directory the loop deletes).
    /// </summary>
    private static (GitRefsProbe Probe, ProbeWorkDirectory Work, IDisposable Root) CreateProbe(
        GitTestDirectory directory,
        TimeSpan? timeout = null)
    {
        var service = new WorkDirectoryService(directory.Combine("probe-work"));
        var work = service.PrepareProbe(Guid.NewGuid());
        var probe = new GitRefsProbe(new GitRefsProbeOptions
        {
            RootPath = work.RootPath,
            TempPath = work.TempPath,
            HardeningPath = work.HardeningPath,
            OperationTimeout = timeout ?? TimeSpan.FromSeconds(60),
            EnvironmentOverrides = GitCli.Environment(directory),
        });

        // The probe itself deletes only its credential carrier; the loop deletes the tree, so the facts
        // that assert "nothing survives" call the same method the loop calls.
        return (probe, work, new ProbeTreeCleanup(service, work));
    }

    /// <summary>Deletes the probe's whole scratch tree at the end of a fact (the worker loop's own call).</summary>
    private sealed class ProbeTreeCleanup(WorkDirectoryService service, ProbeWorkDirectory work) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                service.CleanupProbe(work);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}
