using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Worker;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Identity recovery (card B-012): a worker token the Server no longer accepts must not leave the
/// worker dead behind a 401 loop.
/// <para>
/// The Server authenticates a worker by the <c>zwrk_…</c> token's hash - the worker's name only
/// selects the row and the local directory. A token the Server does not know (its data was replaced
/// or restored, or an Owner revoked it) can never be "waited out", so the worker has exactly two
/// honest options: recover by registering again with a registration token the operator supplied, or
/// say precisely what is wrong and what to do. These tests drive the real <see cref="WorkerRunner"/>
/// against the real pipeline, so the assertions are about the credential's server-side effect, not
/// about a log line alone.
/// </para>
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkerIdentityRecoveryTests
{
    private const string AgentName = "recovering-agent";

    /// <summary>The heartbeat loop's first-success line; counted, never merely present.</summary>
    private const string OnlineLine = "online as worker";

    [Fact]
    public async Task A_persisted_token_the_server_rejects_is_replaced_by_a_configured_registration_token()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        using var work = new WorkerTestWork();
        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);

        // Generation 1: an ordinary first registration.
        await using (var run = work.Start(host, registrationToken,
            configureConnection: work.ConnectUpstreamThrough(host)))
        {
            await WaitUntilAsync(
                async () => (await host.ReadWorkersAsync(organizationId)).Count == 1,
                "the worker registered",
                work.Describe);
            await run.StopAsync();
        }

        var before = (await host.ReadWorkersAsync(organizationId)).Single();
        var staleToken = work.Store.Load();
        Assert.NotNull(staleToken);

        // The server no longer knows that credential: the effect of a replaced/restored database, or
        // of an Owner revoking the worker. Only the token stops working - the row stays, which is
        // exactly the situation that used to require deleting a file on the worker's disk.
        await host.RevokeWorkerTokenAsync(staleToken!);

        // Generation 2: same worker name, same store, and a fresh registration token in the
        // environment - the operator's standard recovery instruction.
        var (freshRegistrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        await using var second = work.Start(host, freshRegistrationToken,
            configureConnection: work.ConnectUpstreamThrough(host));

        await WaitUntilAsync(
            () => Task.FromResult(work.OutputContains("the server no longer accepts the persisted worker token")),
            "the worker noticed the rejected credential",
            work.Describe);
        await WaitUntilAsync(
            () => Task.FromResult(work.Store.Load() != staleToken),
            "the replacement credential was persisted",
            work.Describe);

        // The replacement is a working credential for the same worker row (same name -> same row,
        // so history, queue and the page do not see a second machine).
        var renewed = work.Store.Load();
        Assert.NotNull(renewed);
        Assert.NotEqual(staleToken, renewed);

        var after = (await host.ReadWorkersAsync(organizationId)).Single();
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(WorkerStatus.Online, after.Status);

        using var probe = host.Inner.Host.CreateClient();
        using var accepted = await WorkerTestHost.HeartbeatAsync(
            probe, renewed!, WorkerTestHost.HeartbeatPayload(cpuCount: 8));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var refused = await WorkerTestHost.HeartbeatAsync(
            probe, staleToken!, WorkerTestHost.HeartbeatPayload(cpuCount: 8));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        await second.StopAsync();
    }

    [Fact]
    public async Task A_rejected_persisted_token_without_a_registration_token_names_the_file_and_registers_nothing()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        using var work = new WorkerTestWork();
        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);

        await using (var run = work.Start(host, registrationToken,
            configureConnection: work.ConnectUpstreamThrough(host)))
        {
            await WaitUntilAsync(
                async () => (await host.ReadWorkersAsync(organizationId)).Count == 1,
                "the worker registered",
                work.Describe);
            await run.StopAsync();
        }

        var staleToken = work.Store.Load();
        Assert.NotNull(staleToken);
        await host.RevokeWorkerTokenAsync(staleToken!);
        var tokenRowsBefore = (await host.ReadWorkerTokensAsync(organizationId)).Count;

        // Nothing to heal with: the message has to be actionable, and no registration may be
        // attempted (a worker that cannot authenticate must never invent an identity).
        await using var second = work.Start(host, registrationToken: null,
            configureConnection: work.ConnectUpstreamThrough(host));

        await WaitUntilAsync(
            () => Task.FromResult(work.ErrorContains($"Delete '{work.Store.StorePath}'")),
            "the guidance names the credential file",
            work.Describe);
        await WaitUntilAsync(
            () => Task.FromResult(work.ErrorContains("rejected the worker token")),
            "the rejection is reported",
            work.Describe);

        Assert.Equal(staleToken, work.Store.Load());
        Assert.Equal(tokenRowsBefore, (await host.ReadWorkerTokensAsync(organizationId)).Count);
        Assert.Single(await host.ReadWorkersAsync(organizationId));

        await second.StopAsync();
    }

    [Fact]
    public async Task A_healthy_persisted_token_is_not_replaced_and_the_staged_registration_token_stays_unused()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        using var work = new WorkerTestWork();
        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);

        await using (var run = work.Start(host, registrationToken,
            configureConnection: work.ConnectUpstreamThrough(host)))
        {
            await WaitUntilAsync(
                async () => (await host.ReadWorkersAsync(organizationId)).Count == 1,
                "the worker registered",
                work.Describe);
            await run.StopAsync();
        }

        var persisted = work.Store.Load();
        Assert.NotNull(persisted);

        // A restart that also carries a staged registration token must not burn it: the probe finds
        // the persisted credential acceptable, and a needless re-registration would revoke a token
        // that other loops are already using.
        var (staged, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        var generationsOnline = work.OutputCount(OnlineLine);
        await using var second = work.Start(host, staged,
            configureConnection: work.ConnectUpstreamThrough(host));

        await WaitUntilAsync(
            () => Task.FromResult(work.OutputCount(OnlineLine) > generationsOnline),
            "the worker came back online",
            work.Describe);

        Assert.Equal(persisted, work.Store.Load());
        Assert.DoesNotContain("registering again", work.Output, StringComparison.Ordinal);

        var tokens = await host.ReadWorkerTokensAsync(organizationId);
        var unusedRegistrations = tokens
            .Where(token => token.Kind == WorkerTokenKind.Registration && token.UsedAt is null)
            .ToList();
        Assert.Single(unusedRegistrations);
        Assert.Equal(1, tokens.Count(token => token.Kind == WorkerTokenKind.Worker && token.RevokedAt is null));

        await second.StopAsync();
    }

    [Fact]
    public async Task A_token_revoked_while_running_is_replaced_without_a_restart()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        using var work = new WorkerTestWork();
        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);

        await using (var run = work.Start(host, registrationToken,
            configureConnection: work.ConnectUpstreamThrough(host)))
        {
            await WaitUntilAsync(
                async () => (await host.ReadWorkersAsync(organizationId)).Count == 1,
                "the worker registered",
                work.Describe);
            await run.StopAsync();
        }

        var persisted = work.Store.Load();
        Assert.NotNull(persisted);
        var workerId = (await host.ReadWorkersAsync(organizationId)).Single().Id;

        // An operator staged a fresh registration token for the next credential loss (the documented
        // way to keep a supervised worker recoverable without inventing a second identity).
        var (staged, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        var generationsOnline = work.OutputCount(OnlineLine);
        await using var second = work.Start(host, staged,
            configureConnection: work.ConnectUpstreamThrough(host));

        // This generation's own first beat, not the previous generation's line still in the buffer:
        // revoking before this generation's startup probe runs would exercise the restart path
        // instead of the running one.
        await WaitUntilAsync(
            () => Task.FromResult(work.OutputCount(OnlineLine) > generationsOnline),
            "the worker came back online with the persisted token",
            work.Describe);

        // Revoked mid-flight: the next heartbeat is the only thing that notices.
        await host.RevokeWorkerTokenAsync(persisted!);

        await WaitUntilAsync(
            () => Task.FromResult(work.OutputContains("re-registered; this worker now presents the new worker token.")),
            "the running worker healed its credential",
            work.Describe);
        await WaitUntilAsync(
            () => Task.FromResult(work.Store.Load() != persisted),
            "the healed credential was persisted",
            work.Describe);

        var healed = work.Store.Load();
        Assert.NotNull(healed);
        Assert.Equal(workerId, (await host.ReadWorkersAsync(organizationId)).Single().Id);

        using var probe = host.Inner.Host.CreateClient();
        using var accepted = await WorkerTestHost.HeartbeatAsync(
            probe, healed!, WorkerTestHost.HeartbeatPayload(cpuCount: 8));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var tokens = await host.ReadWorkerTokensAsync(organizationId);
        Assert.DoesNotContain(
            tokens,
            token => token.Kind == WorkerTokenKind.Registration && token.UsedAt is null);

        await second.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, Func<string>? diagnostics = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for: {what}.{Environment.NewLine}{diagnostics?.Invoke()}");
    }

    /// <summary>
    /// One worker's temp directories plus the log capture, shared across the generations of a test
    /// (the point of every case here is that the same store survives a restart).
    /// </summary>
    private sealed class WorkerTestWork : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"zenkins-b012-identity-{Guid.NewGuid():N}");

        private readonly System.Text.StringBuilder _output = new();
        private readonly System.Text.StringBuilder _error = new();

        public WorkerTestWork() => Directory.CreateDirectory(_root);

        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        public string Error
        {
            get
            {
                lock (_error)
                {
                    return _error.ToString();
                }
            }
        }

        public WorkerTokenStore Store => new(
            Path.Combine(_root, "data"),
            new Uri("http://localhost"),
            AgentName);

        public bool OutputContains(string text) => Output.Contains(text, StringComparison.Ordinal);

        /// <summary>
        /// How many times a line appeared. The streams outlive the generations on purpose (the store
        /// must survive a restart), so "the worker is online" has to mean *this* generation, not the
        /// previous one's line still sitting in the buffer.
        /// </summary>
        public int OutputCount(string text)
        {
            var output = Output;
            var count = 0;
            var index = 0;
            while ((index = output.IndexOf(text, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += text.Length;
            }

            return count;
        }

        /// <summary>Both streams, for a failed wait's message: the runner's own account of what it did.</summary>
        public string Describe() => $"-- worker stdout --{Environment.NewLine}{Output}"
            + $"{Environment.NewLine}-- worker stderr --{Environment.NewLine}{Error}";

        public bool ErrorContains(string text) => Error.Contains(text, StringComparison.Ordinal);

        /// <summary>
        /// Long polling through the in-memory TestServer, the F-008 hub-test pattern: the log stream
        /// is part of the runner and must not sit in its own reconnect loop against port 80 for the
        /// length of a test.
        /// </summary>
        public Action<HttpConnectionOptions> ConnectUpstreamThrough(WorkerTestHost host)
        {
            var handler = host.Inner.Host.Server.CreateHandler();
            return options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => handler;
            };
        }

        public Generation Start(
            WorkerTestHost host,
            string? registrationToken,
            Action<HttpConnectionOptions>? configureConnection)
        {
            var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
            {
                [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
                [WorkerConfiguration.WorkerNameVariable] = AgentName,
                [WorkerConfiguration.DataDirectoryVariable] = Path.Combine(_root, "data"),
                [WorkerConfiguration.WorkRootVariable] = Path.Combine(_root, "work"),
                [WorkerConfiguration.RegistrationTokenVariable] = registrationToken,
                [WorkerConfiguration.HeartbeatSecondsVariable] = "1",
                [WorkerConfiguration.ClaimSecondsVariable] = "0.2",
                [WorkerConfiguration.ProbesEnabledVariable] = "false",
            }).Configuration ?? throw new InvalidOperationException("The identity-recovery configuration did not parse.");

            var runner = new WorkerRunner(
                configuration,
                new WorkerApiClient(host.Inner.Host.CreateClient()),
                new WorkerTokenStore(
                    configuration.DataDirectory,
                    configuration.ServerUrl,
                    configuration.WorkerName),
                new WorkDirectoryService(configuration.ResolveWorkRoot()),
                BackoffScheduler.Default(),
                TextWriter.Synchronized(new CallbackWriter(_output)),
                TextWriter.Synchronized(new CallbackWriter(_error)),
                configureConnection);

            var stop = new CancellationTokenSource();
            var task = Task.Run(() => runner.RunAsync(stop.Token));
            return new Generation(task, stop);
        }

        public void Dispose() => TestCleanup.DeleteTree(_root);
    }

    private sealed class Generation(Task<int> task, CancellationTokenSource stop) : IAsyncDisposable
    {
        public async Task StopAsync()
        {
            await stop.CancelAsync();
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            }
            catch (Exception)
            {
                // The runner's shutdown is best effort here; assertions have already read the state.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            stop.Dispose();
        }
    }

    /// <summary>A <see cref="TextWriter"/> that appends to one locked buffer (the runner's loops write concurrently).</summary>
    private sealed class CallbackWriter(System.Text.StringBuilder buffer) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            lock (buffer)
            {
                buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (buffer)
            {
                buffer.Append(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (buffer)
            {
                buffer.Append(value).Append('\n');
            }
        }
    }
}
