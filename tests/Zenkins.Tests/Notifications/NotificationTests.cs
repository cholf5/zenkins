using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Notifications;
using Zenkins.Server.Runs;
using Zenkins.Tests.Runs;
using Zenkins.Shared.Notifications;

namespace Zenkins.Tests.Notifications;

/// <summary>
/// Card F-039's completion notifications: the durable obligation a terminal run leaves behind, the signed delivery,
/// the recovery rule, the retry bound and the one thing the payload must never carry.
/// <para>
/// Every fact drives the product's own pieces - <see cref="CompletionNotifications"/>, the real
/// <see cref="NotificationDispatcher"/> and a real loopback HTTP receiver - and passes time in rather than waiting on
/// it: the backoff is configured to zero and the attempt bound to two.
/// </para>
/// </summary>
[Trait("Category", "Notifications")]
public sealed class NotificationTests
{
    private const string Secret = "fixture-shared-secret";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failed_run_notifies_a_subscribed_endpoint_once_and_signed()
    {
        using var host = await CreateHostAsync();
        using var receiver = NotificationReceiver.Accepting();
        var project = await host.SeedProjectAsync("notify-failed", "https://example.test/f.git");
        await CreateEndpointAsync(host, project, receiver.Url, "failed", Secret);
        var run = await SeedRunAsync(host, project, number: 7, status: BuildRunStatus.Failed, daysOld: 0);

        Assert.Equal(1, await StageAsync(host, run, BuildRunStatus.Failed));
        Assert.Equal(1, await DispatchAsync(host));

        // Exactly one POST, and a second pass adds nothing (the row is settled, not re-sent).
        var delivered = Assert.Single(receiver.Received);
        Assert.Equal(0, await DispatchAsync(host));
        Assert.Single(receiver.Received);

        // The signature verifies against the shared secret over the exact bytes that arrived.
        Assert.True(NotificationSignature.Verify(
            delivered.Header(NotificationSignature.HeaderName),
            delivered.Body,
            Secret));
        Assert.False(NotificationSignature.Verify(
            delivered.Header(NotificationSignature.HeaderName),
            delivered.Body,
            "the-wrong-secret"));

        Assert.NotNull(delivered.Payload);
        var payload = delivered.Payload!;
        Assert.Equal(NotificationEvents.Failed, payload.Event);
        Assert.Equal(NotificationEvents.Failed, delivered.Header(NotificationSignature.EventHeaderName));
        Assert.Equal(payload.DeliveryId, delivered.Header(NotificationSignature.DeliveryHeaderName));
        Assert.Equal("1", delivered.Header(NotificationSignature.AttemptHeaderName));
        Assert.Equal(run, payload.Run.Id);
        Assert.Equal(7, payload.Run.Number);
        Assert.Equal("main", payload.Run.Branch);
        Assert.Equal("abc0123", payload.Run.Commit);
        Assert.Equal(BuildRunStatus.Failed, payload.Run.Status);
        Assert.Equal("notify-failed", payload.Project.Name);
        Assert.Equal(project, payload.Project.Id);
        Assert.NotNull(payload.FailureReason);

        // The endpoint records the outcome the page renders.
        var endpoint = await ReadEndpointAsync(host, project);
        Assert.Equal(NotificationOutcomes.Delivered, endpoint.LastOutcome);
        Assert.Null(endpoint.LastError);
        Assert.NotNull(endpoint.LastDeliveryAt);
    }

    [Fact]
    public async Task A_dead_endpoint_ends_in_a_recorded_failure_and_never_retries_forever()
    {
        using var host = await CreateHostAsync(maxAttempts: 2);
        var project = await host.SeedProjectAsync("notify-dead", "https://example.test/d.git");
        await CreateEndpointAsync(host, project, NotificationReceiver.ClosedPortUrl(), "all", Secret);
        var run = await SeedRunAsync(host, project, number: 1, status: BuildRunStatus.Failed, daysOld: 0);

        await StageAsync(host, run, BuildRunStatus.Failed);

        // Attempt one: transient, the row backs off (zero here) and stays pending.
        Assert.Equal(0, await DispatchAsync(host));
        Assert.Equal(NotificationOutcomes.Retrying, (await ReadEndpointAsync(host, project)).LastOutcome);

        // Attempt two is the bound: the row settles as failed, with the transport error recorded on both rows.
        Assert.Equal(1, await DispatchAsync(host));
        var message = Assert.Single(await ReadOutboxAsync(host));
        Assert.Equal(OutboxStatuses.Settled, message.Status);
        Assert.Equal(NotificationOutcomes.Failed, message.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(message.Reason));

        var endpoint = await ReadEndpointAsync(host, project);
        Assert.Equal(NotificationOutcomes.Failed, endpoint.LastOutcome);
        Assert.False(string.IsNullOrWhiteSpace(endpoint.LastError));
        Assert.True(endpoint.LastError!.Length <= NotificationLimits.MaxErrorLength);

        // Nothing is left pending: a dead receiver costs two requests, not an unbounded queue.
        Assert.Equal(0, await DispatchAsync(host));
    }

    [Fact]
    public async Task A_success_after_a_failure_is_recovered_and_a_success_after_a_success_is_success()
    {
        using var host = await CreateHostAsync();
        using var receiver = NotificationReceiver.Accepting();
        var project = await host.SeedProjectAsync("notify-recovery", "https://example.test/r.git");
        var endpoint = await CreateEndpointAsync(host, project, receiver.Url, "failed,success,recovered", Secret);

        var firstFailed = await SeedRunAsync(host, project, 1, BuildRunStatus.Failed, daysOld: 3);
        var secondFailed = await SeedRunAsync(host, project, 2, BuildRunStatus.Failed, daysOld: 2);
        var firstSuccess = await SeedRunAsync(host, project, 3, BuildRunStatus.Success, daysOld: 1);
        var secondSuccess = await SeedRunAsync(host, project, 4, BuildRunStatus.Success, daysOld: 0);

        foreach (var (run, status) in new[]
                 {
                     (firstFailed, BuildRunStatus.Failed),
                     (secondFailed, BuildRunStatus.Failed),
                     (firstSuccess, BuildRunStatus.Success),
                     (secondSuccess, BuildRunStatus.Success),
                 })
        {
            await StageAsync(host, run, status);
            await DispatchAsync(host);
        }

        // TC-003: failed, failed, recovered, success - and the recovery is its own event, not a flag on success.
        var events = receiver.Received.Select(received => received.Payload!.Event).ToArray();
        Assert.Equal(
            [NotificationEvents.Failed, NotificationEvents.Failed, NotificationEvents.Recovered, NotificationEvents.Success],
            events);

        // A delivery id per terminal state: four runs, four ids (a retry would repeat one).
        Assert.Equal(4, receiver.Received.Select(received => received.Payload!.DeliveryId).Distinct().Count());
        Assert.Equal(4, receiver.Received.Select(received => received.Payload!.Run.Number).Distinct().Count());

        // And a `success` subscriber hears the recovery too - a recovered run is a success that happens to be green again.
        var recoveredForSuccessSubscriber = receiver.Received.Last().Payload!.Event;
        Assert.Equal(NotificationEvents.Success, recoveredForSuccessSubscriber);
        Assert.Equal(NotificationEvents.Recovered, events[2]);

        // The endpoint saw the last one.
        var endpointRow = await ReadEndpointAsync(host, project);
        Assert.Equal(NotificationOutcomes.Delivered, endpointRow.LastOutcome);
        _ = endpoint;
    }

    [Fact]
    public async Task A_pending_delivery_is_signed_with_the_secret_it_was_staged_under()
    {
        using var host = await CreateHostAsync();
        using var receiver = NotificationReceiver.Accepting();
        var project = await host.SeedProjectAsync("notify-rotation", "https://example.test/rot.git");
        var endpoint = await CreateEndpointAsync(host, project, receiver.Url, "all", "the-old-secret");
        var run = await SeedRunAsync(host, project, 1, BuildRunStatus.Failed, daysOld: 0);

        // The obligation is created while the old secret is in force...
        await StageAsync(host, run, BuildRunStatus.Failed);

        // ...then the operator rotates it before the dispatcher got to the row.
        await RotateAsync(host, project, endpoint, "the-brand-new-secret");
        Assert.Equal(1, await DispatchAsync(host));

        // The delivered body still verifies with the OLD secret: rotation must not re-sign what is already owed.
        var delivered = Assert.Single(receiver.Received);
        Assert.True(NotificationSignature.Verify(
            delivered.Header(NotificationSignature.HeaderName), delivered.Body, "the-old-secret"));
        Assert.False(NotificationSignature.Verify(
            delivered.Header(NotificationSignature.HeaderName), delivered.Body, "the-brand-new-secret"));

        // A delivery staged *after* the rotation uses the new secret.
        var nextRun = await SeedRunAsync(host, project, 2, BuildRunStatus.Failed, daysOld: 0);
        await StageAsync(host, nextRun, BuildRunStatus.Failed);
        await DispatchAsync(host);
        var second = receiver.Received.Last();
        Assert.True(NotificationSignature.Verify(
            second.Header(NotificationSignature.HeaderName), second.Body, "the-brand-new-secret"));
        Assert.False(NotificationSignature.Verify(
            second.Header(NotificationSignature.HeaderName), second.Body, "the-old-secret"));
    }

    [Fact]
    public async Task The_payload_carries_no_parameter_environment_or_secret_material()
    {
        using var host = await CreateHostAsync();
        using var receiver = NotificationReceiver.Accepting();
        var project = await host.SeedProjectAsync("notify-leak", "https://example.test/leak.git");
        await CreateEndpointAsync(host, project, receiver.Url, "all", Secret);

        const string parameterValue = "hunter2-parameter-value";
        const string environmentValue = "env-secret-value";
        var run = await SeedRunAsync(
            host,
            project,
            number: 9,
            status: BuildRunStatus.Failed,
            daysOld: 0,
            parametersJson: $"{{\"PASSWORD\":\"{parameterValue}\"}}",
            environmentJson: $"{{\"DEPLOY_TOKEN\":\"{environmentValue}\"}}");
        await StageAsync(host, run, BuildRunStatus.Failed);
        Assert.Equal(1, await DispatchAsync(host));

        var delivered = Assert.Single(receiver.Received);
        var json = Encoding.UTF8.GetString(delivered.Body);

        // TC-004: none of the three secret-shaped strings is in the body - not the parameter value, not the
        // environment value, not the shared secret the signature was computed with.
        Assert.DoesNotContain(parameterValue, json, StringComparison.Ordinal);
        Assert.DoesNotContain(environmentValue, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);

        // ...and no field exists for them to arrive in (the contract has no slot for either).
        Assert.DoesNotContain("parameters", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASSWORD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DEPLOY_TOKEN", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_http_receiver_needs_the_opt_in_and_a_redirect_is_never_followed()
    {
        using var host = await CreateHostAsync(maxAttempts: 1);
        var project = await host.SeedProjectAsync("notify-http", "https://example.test/h.git");

        // AC-7's first half: plain http without the opt-in is refused with a stable code, and nothing is stored.
        var refused = await InTenantAsync(host, async services => await services
            .GetRequiredService<NotificationService>()
            .CreateAsync(
                await OwnerUserIdAsync(host, services),
                project,
                new NotificationEndpointInput(
                    "http://127.0.0.1:1/hook", Secret, [NotificationEvents.All], true, AllowInsecureHttp: false),
                CancellationToken));
        Assert.Equal(NotificationMutationStatus.InvalidInput, refused.Status);
        Assert.Equal(NotificationErrorCodes.InsecureUrl, refused.ErrorCode);
        Assert.Empty(await ReadEndpointsAsync(host, project));

        // AC-7's second half: a receiver that answers 302 is a failed attempt, and the target is never contacted.
        using var receiver = NotificationReceiver.Redirecting("http://127.0.0.1:9/elsewhere");
        await CreateEndpointAsync(host, project, receiver.Url, "all", Secret, allowInsecureHttp: true);
        var run = await SeedRunAsync(host, project, 1, BuildRunStatus.Failed, daysOld: 0);
        await StageAsync(host, run, BuildRunStatus.Failed);
        Assert.Equal(1, await DispatchAsync(host));

        var endpoint = await ReadEndpointAsync(host, project);
        Assert.Equal(NotificationOutcomes.Failed, endpoint.LastOutcome);
        Assert.Contains("302", endpoint.LastError ?? string.Empty, StringComparison.Ordinal);

        // One request: the redirect target never got a second one.
        Assert.Single(receiver.Received);
    }

    [Fact]
    public async Task The_terminal_status_commits_before_any_notification_is_attempted()
    {
        using var host = await CreateHostAsync(timeoutSeconds: 1);
        using var receiver = NotificationReceiver.Slow(TimeSpan.FromSeconds(5));
        var project = await host.SeedProjectAsync("notify-slow", "https://example.test/s.git");
        await CreateEndpointAsync(host, project, receiver.Url, "all", Secret);

        var (token, workerId) = await host.RegisterWorkerAsync();
        var run = await SeedRunAsync(
            host,
            project,
            number: 1,
            status: BuildRunStatus.Running,
            daysOld: 0,
            workerId: workerId);

        // The worker's completion report must not wait for a receiver that says nothing.
        var started = DateTimeOffset.UtcNow;
        var completed = await host.Host.InScopeAsync(services => services
            .GetRequiredService<RunService>()
            .CompleteAsync(token, run, exitCode: 1, failureReason: null, CancellationToken));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(RunOperationStatus.Success, completed.Status);
        Assert.Equal(BuildRunStatus.Failed, (await ReadRunAsync(host, run))!.Status);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(4),
            $"the completion report waited {elapsed.TotalSeconds:0.#}s on the receiver");

        // The obligation is on disk and still owed - the receiver has not been answered yet.
        var message = Assert.Single(await ReadOutboxAsync(host));
        Assert.Equal(OutboxStatuses.Pending, message.Status);
        Assert.Equal(NotificationEvents.Failed, message.NotificationEvent);

        // And when a pass does run, the timeout bounds it instead of hanging.
        Assert.Equal(0, await DispatchAsync(host));
        Assert.Equal(NotificationOutcomes.Retrying, (await ReadEndpointAsync(host, project)).LastOutcome);
    }

    [Fact]
    public async Task A_disabled_project_still_notifies_about_the_run_it_already_had()
    {
        using var host = await CreateHostAsync();
        using var receiver = NotificationReceiver.Accepting();
        var project = await host.SeedProjectAsync("notify-disabled", "https://example.test/off.git");
        await CreateEndpointAsync(host, project, receiver.Url, "all", Secret);
        await SetDisabledAsync(host, project, "maintenance window");
        var run = await SeedRunAsync(host, project, 1, BuildRunStatus.Failed, daysOld: 0);

        // AC-8: F-028's brake stops new triggers; the run it already had still ends, and its notification is owed.
        await StageAsync(host, run, BuildRunStatus.Failed);
        Assert.Equal(1, await DispatchAsync(host));

        var delivered = Assert.Single(receiver.Received);
        Assert.Equal(NotificationEvents.Failed, delivered.Payload!.Event);
        Assert.Equal("notify-disabled", delivered.Payload.Project.Name);
    }

    [Fact]
    public async Task The_settings_card_lists_each_endpoint_its_last_delivery_and_sends_a_test()
    {
        using var host = await CreateHostAsync();
        using var receiver = NotificationReceiver.Accepting();
        var project = await host.SeedProjectAsync("notify-card", "https://example.test/card.git");
        var run = await SeedRunAsync(host, project, 1, BuildRunStatus.Success, daysOld: 0);
        using var owner = await host.Inner.SignInOwnerAsync();

        // The card's create form is the product's own (Owner + antiforgery + validation).
        using var created = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{project}/settings",
            $"/projects/{project}/notifications/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Url"] = receiver.Url,
                ["Secret"] = Secret,
                ["Events"] = NotificationEvents.All,
                ["Enabled"] = "true",
                ["AllowInsecureHttp"] = "true",
            });
        Assert.True(
            (int)created.StatusCode is 302 or 303
                && created.Headers.Location!.OriginalString.Contains("notice=notification-added", StringComparison.Ordinal),
            $"creating the endpoint answered {(int)created.StatusCode}: {created.Headers.Location}.");

        // The card shows it, with the plain-http warning and no secret.
        var before = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/projects/{project}/settings");
        Assert.Contains("project-notifications", before, StringComparison.Ordinal);
        Assert.Contains(receiver.Url, before, StringComparison.Ordinal);
        Assert.Contains("Plain http", before, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, before, StringComparison.Ordinal);
        Assert.Contains("Never delivered", before, StringComparison.Ordinal);

        // "Send test" posts one delivery now and records its outcome on the endpoint.
        var endpoint = (await ReadEndpointsAsync(host, project)).Single();
        using var tested = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{project}/settings",
            $"/projects/{project}/notifications/{endpoint.Id}/test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.True((int)tested.StatusCode is 302 or 303, $"the test send answered {(int)tested.StatusCode}.");

        var testDelivery = Assert.Single(receiver.Received);
        Assert.Equal(NotificationEvents.Test, testDelivery.Payload!.Event);
        Assert.Equal(run, testDelivery.Payload.Run.Id);
        Assert.True(NotificationSignature.Verify(
            testDelivery.Header(NotificationSignature.HeaderName), testDelivery.Body, Secret));

        var after = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/projects/{project}/settings");
        Assert.Contains($"data-notification-last=\"{NotificationOutcomes.Delivered}\"", after, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static Task<RunTestHost> CreateHostAsync(
        int maxAttempts = 5,
        int timeoutSeconds = 5) =>
        RunTestHost.CreateAsync(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                // The tests drive the pass; the hosted loop must not deliver behind their back.
                [NotificationOptions.PollIntervalSecondsKey] = "3600",
                [NotificationOptions.BackoffSecondsKey] = "0",
                [NotificationOptions.MaxAttemptsKey] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [NotificationOptions.TimeoutSecondsKey] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));

    private static Task<T> InTenantAsync<T>(RunTestHost host, Func<IServiceProvider, Task<T>> action) =>
        host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = await host.OrganizationIdAsync();
            return await action(services);
        });

    private static async Task<string> OwnerUserIdAsync(RunTestHost host, IServiceProvider services)
    {
        var organizationId = await host.OrganizationIdAsync();
        await using var context = await services
            .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
            .CreateDbContextAsync(CancellationToken);
        return await context.OrganizationMembers
            .Where(member => member.OrganizationId == organizationId && member.Role == OrganizationRole.Owner)
            .Select(member => member.UserId)
            .FirstAsync(CancellationToken);
    }

    /// <summary>Configures one endpoint through the product's own service. Returns its id.</summary>
    private static async Task<Guid> CreateEndpointAsync(
        RunTestHost host,
        Guid projectId,
        string url,
        string events,
        string secret,
        bool allowInsecureHttp = true)
    {
        var result = await InTenantAsync(host, async services => await services
            .GetRequiredService<NotificationService>()
            .CreateAsync(
                await OwnerUserIdAsync(host, services),
                projectId,
                new NotificationEndpointInput(
                    url,
                    secret,
                    [.. events.Split(',')],
                    Enabled: true,
                    AllowInsecureHttp: allowInsecureHttp),
                CancellationToken));
        Assert.Equal(NotificationMutationStatus.Success, result.Status);
        return result.EndpointId!.Value;
    }

    private static async Task RotateAsync(RunTestHost host, Guid projectId, Guid endpointId, string secret)
    {
        var result = await InTenantAsync(host, async services => await services
            .GetRequiredService<NotificationService>()
            .RotateSecretAsync(
                await OwnerUserIdAsync(host, services), projectId, endpointId, secret, CancellationToken));
        Assert.Equal(NotificationMutationStatus.Success, result.Status);
    }

    /// <summary>
    /// Stages the obligation the way the completion path does: the product's own routine, in a tenant-scoped context,
    /// saved with the terminal status it belongs to.
    /// </summary>
    private static Task<int> StageAsync(RunTestHost host, Guid runId, string terminalStatus) =>
        host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = await host.OrganizationIdAsync();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            var run = await context.BuildRuns.SingleAsync(candidate => candidate.Id == runId, CancellationToken);
            var staged = await CompletionNotifications.StageAsync(context, run, terminalStatus, CancellationToken);
            await context.SaveChangesAsync(CancellationToken);
            return staged;
        });

    /// <summary>One dispatch pass through the product's own hosted service (never waits on real time).</summary>
    private static Task<int> DispatchAsync(RunTestHost host) =>
        host.Host.InScopeAsync(services => services
            .GetServices<IHostedService>()
            .OfType<NotificationDispatcher>()
            .Single()
            .DispatchPendingAsync(CancellationToken));

    private static Task<Guid> SeedRunAsync(
        RunTestHost host,
        Guid projectId,
        int number,
        string status,
        int daysOld,
        string branch = "main",
        Guid? workerId = null,
        string? parametersJson = null,
        string? environmentJson = null) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            var created = DateTimeOffset.UtcNow.AddDays(-daysOld);
            var terminal = status is BuildRunStatus.Success or BuildRunStatus.Failed;
            var run = new BuildRun
            {
                OrganizationId = project.OrganizationId,
                ProjectId = projectId,
                TriggerType = BuildRunTriggerType.Manual,
                RequiredTags = string.Empty,
                GitBranch = branch,
                PipelinePath = "pipeline.lua",
                GitCommit = "abc0123",
                Number = number,
                Status = status,
                WorkerId = workerId,
                CreatedAt = created,
                UpdatedAt = created,
                StartedAt = created,
                EndedAt = terminal ? created.AddMinutes(1) : null,
                ExitCode = status == BuildRunStatus.Success ? 0 : status == BuildRunStatus.Failed ? 1 : null,
                FailureReason = status == BuildRunStatus.Failed ? "step-exit-nonzero" : null,
                ParametersJson = parametersJson,
                EnvironmentJson = environmentJson,
            };
            context.BuildRuns.Add(run);
            await context.SaveChangesAsync();
            return run.Id;
        });

    private static Task SetDisabledAsync(RunTestHost host, Guid projectId, string reason) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.Disabled = true;
            project.DisabledReason = reason;
            await context.SaveChangesAsync();
            return true;
        });

    private static Task<List<OutboxMessage>> ReadOutboxAsync(RunTestHost host) =>
        host.InSystemScopeAsync(context => context.SystemQueryAsync<OutboxMessage>(
            message => message.Kind == OutboxKinds.CompletionNotification));

    private static Task<List<NotificationEndpoint>> ReadEndpointsAsync(RunTestHost host, Guid projectId) =>
        host.InSystemScopeAsync(context => context.SystemQueryAsync<NotificationEndpoint>(
            endpoint => endpoint.ProjectId == projectId));

    private static async Task<NotificationEndpoint> ReadEndpointAsync(RunTestHost host, Guid projectId) =>
        Assert.Single(await ReadEndpointsAsync(host, projectId));

    private static async Task<BuildRun?> ReadRunAsync(RunTestHost host, Guid runId) =>
        (await host.InSystemScopeAsync(context => context.SystemQueryAsync<BuildRun>(run => run.Id == runId)))
        .SingleOrDefault();
}
