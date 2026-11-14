using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's id-swap probes over the token-authenticated worker API. Every credential here was
/// minted through the real product path (Owner's registration token → <c>/api/workers/register</c>),
/// so each probe drives two honest workers: org A's asks for org B's run by id and vice versa. The
/// answer must be a bare 404 that leaves the foreign tenant untouched - the token row, never a URL
/// value, selects the tenant. Each test builds its own two-organization environment because the
/// probes claim (and therefore lease) the fixture's runs.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationWorkerClaimProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_claim_never_crosses_the_tenant_boundary()
    {
        using var host = new TenantProbeHostFixture();
        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var (_, betaToken) = await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host);

        using var alphaClient = host.CreateClient();
        alphaClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);
        using var betaClient = host.CreateClient();
        betaClient.DefaultRequestHeaders.Authorization = new("Bearer", betaToken);

        // Org A's worker claims first: its answer is org A's queued run, with org A's project and
        // repository - never anything from org B.
        var (alphaStatus, alphaRunId, alphaProjectId, alphaRepositoryUrl) =
            await TenantIsolationWorkerApiProbes.ClaimAsync(alphaClient);
        Assert.Equal(HttpStatusCode.OK, alphaStatus);
        Assert.Equal(TwoOrgsFixture.BuildRunAId, alphaRunId);
        Assert.Equal(TwoOrgsFixture.ProjectAId, alphaProjectId);
        Assert.Equal(TwoOrgsFixture.ProjectARepositoryUrl, alphaRepositoryUrl);

        // Org B's worker claims next: it gets org B's run - org A's is leased by now, and even
        // without that lease the tenant filter alone would make it invisible.
        var (betaStatus, betaRunId, betaProjectId, betaRepositoryUrl) =
            await TenantIsolationWorkerApiProbes.ClaimAsync(betaClient);
        Assert.Equal(HttpStatusCode.OK, betaStatus);
        Assert.Equal(TwoOrgsFixture.BuildRunBId, betaRunId);
        Assert.Equal(TwoOrgsFixture.ProjectBId, betaProjectId);
        Assert.Equal(TwoOrgsFixture.ProjectBRepositoryUrl, betaRepositoryUrl);

        // A third claim each way finds nothing foreign: the queue is per tenant and now empty.
        var (alphaEmpty, alphaEmptyRunId, _, _) = await TenantIsolationWorkerApiProbes.ClaimAsync(alphaClient);
        Assert.Equal(HttpStatusCode.NoContent, alphaEmpty);
        Assert.Null(alphaEmptyRunId);
        var (betaEmpty, betaEmptyRunId, _, _) = await TenantIsolationWorkerApiProbes.ClaimAsync(betaClient);
        Assert.Equal(HttpStatusCode.NoContent, betaEmpty);
        Assert.Null(betaEmptyRunId);

        // Positive control: each worker accepts its own run through the same token.
        await TenantIsolationWorkerApiProbes.AssertAcceptedAsync(alphaClient, TwoOrgsFixture.BuildRunAId);
        await TenantIsolationWorkerApiProbes.AssertAcceptedAsync(betaClient, TwoOrgsFixture.BuildRunBId);
    }
}

/// <summary>
/// The F-009 fallback cancel channel rides the heartbeat response; the probe proves it can only
/// ever carry the calling worker's own runs - a pending cancel on org A's run never shows up in
/// org B's worker's heartbeat.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationWorkerHeartbeatProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_heartbeat_cancel_channel_never_carries_another_organizations_run()
    {
        using var host = new TenantProbeHostFixture();
        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var (_, betaToken) = await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host);

        // Org A's run is claimed, accepted and cancel-requested by org A's own session.
        using var alphaClient = host.CreateClient();
        alphaClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(alphaClient, TwoOrgsFixture.BuildRunAId);

        using var alice = await TenantIsolationAuditProbes.SignInAsync(
            host, TenantIsolationProbeAssertions.OrgAOwnerEmail);
        var cancel = await OrganizationTestHost.PostFormAsync(
            alice,
            $"/runs/{TwoOrgsFixture.BuildRunAId}",
            $"/runs/{TwoOrgsFixture.BuildRunAId}/cancel",
            new Dictionary<string, string>());
        MembershipAuthorizationProbes.AssertRedirect(
            cancel, $"/runs/{TwoOrgsFixture.BuildRunAId}?notice=cancel-requested");

        // Org A's worker sees the cancel request on its own run (positive control)...
        var (alphaStatus, alphaCancels) = await TenantIsolationWorkerApiProbes.HeartbeatAsync(alphaClient);
        Assert.Equal(HttpStatusCode.OK, alphaStatus);
        Assert.Contains(alphaCancels, entry => entry.runId == TwoOrgsFixture.BuildRunAId);

        // ...and org B's worker - claiming and accepting org B's own run, and having org B's own
        // cancel request pending on it, so its heartbeat is not vacuously empty - never sees
        // org A's run in the channel.
        using var betaClient = host.CreateClient();
        betaClient.DefaultRequestHeaders.Authorization = new("Bearer", betaToken);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(betaClient, TwoOrgsFixture.BuildRunBId);
        using var carol = await TenantIsolationAuditProbes.SignInAsync(
            host, TenantIsolationProbeAssertions.OrgBOwnerEmail);
        var betaCancel = await OrganizationTestHost.PostFormAsync(
            carol,
            $"/runs/{TwoOrgsFixture.BuildRunBId}",
            $"/runs/{TwoOrgsFixture.BuildRunBId}/cancel",
            new Dictionary<string, string>());
        MembershipAuthorizationProbes.AssertRedirect(
            betaCancel, $"/runs/{TwoOrgsFixture.BuildRunBId}?notice=cancel-requested");

        var (betaStatus, betaCancels) = await TenantIsolationWorkerApiProbes.HeartbeatAsync(betaClient);
        Assert.Equal(HttpStatusCode.OK, betaStatus);
        Assert.Contains(betaCancels, entry => entry.runId == TwoOrgsFixture.BuildRunBId);
        Assert.DoesNotContain(betaCancels, entry => entry.runId == TwoOrgsFixture.BuildRunAId);
    }
}

/// <summary>Every run report verb: a valid foreign token is a bare 404, and the foreign run stays untouched.</summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationWorkerReportProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_report_endpoints_refuse_a_foreign_token_with_a_bare_404()
    {
        using var host = new TenantProbeHostFixture();
        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var (_, betaToken) = await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host);

        // Org B's worker claims and accepts org B's run first: the report refusals below are the
        // tenant boundary, not a state conflict, and org B's run stays Running and untouched.
        using var betaClient = host.CreateClient();
        betaClient.DefaultRequestHeaders.Authorization = new("Bearer", betaToken);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(betaClient, TwoOrgsFixture.BuildRunBId);

        using var alphaClient = host.CreateClient();
        alphaClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);

        // The definition report validates its payload before the tenant-filtered run lookup
        // (deliberate: payload shape is the worker's own bug, independent of run existence), so the
        // probe reports a VALID spec - the 404 below is then purely the tenant boundary.
        var validSpec = """{"version":1,"name":"build","steps":[{"name":"Build","run":["dotnet","build"]}]}""";
        var definitionBody = $$"""{"gitCommit":null,"pipelineSpecJson":{{System.Text.Json.JsonSerializer.Serialize(validSpec)}}}""";

        foreach (var (path, body) in new[]
                 {
                     ($"/api/workers/runs/{TwoOrgsFixture.BuildRunBId}/accepted", "{}"),
                     ($"/api/workers/runs/{TwoOrgsFixture.BuildRunBId}/definition", definitionBody),
                     ($"/api/workers/runs/{TwoOrgsFixture.BuildRunBId}/steps",
                         """{"index":1,"name":"Build","status":"Running"}"""),
                     ($"/api/workers/runs/{TwoOrgsFixture.BuildRunBId}/completed",
                         """{"exitCode":0}"""),
                     ($"/api/workers/runs/{TwoOrgsFixture.BuildRunBId}/canceled",
                         """{"outcome":"canceled"}"""),
                 })
        {
            using var response = await alphaClient.PostAsync(
                path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        // The reverse direction: org B's token cannot report org A's run either.
        using var reverse = await betaClient.PostAsync(
            $"/api/workers/runs/{TwoOrgsFixture.BuildRunAId}/accepted",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, reverse.StatusCode);

        // Nothing changed in either tenant: org B's run is still Running, and its only step row is
        // the fixture's seeded one - the refused reports recorded nothing.
        var betaRun = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<BuildRun>(
            host, run => run.Id == TwoOrgsFixture.BuildRunBId)).Single();
        Assert.Equal(BuildRunStatus.Running, betaRun.Status);
        var betaSteps = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<BuildStep>(
            host, step => step.RunId == TwoOrgsFixture.BuildRunBId);
        Assert.Single(betaSteps);
        Assert.Equal(TwoOrgsFixture.BuildStepBId, betaSteps[0].Id);
        Assert.Equal("Failed", betaSteps[0].Status);
    }
}

/// <summary>The F-015 secret delivery: the value travels only to the executing worker of a tenant-owned run.</summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationSecretDeliveryProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public const string AlphaSecretName = "ALPHA_LIVE";
    public const string BetaSecretName = "BETA_LIVE";
    public const string AlphaSecretValue = "alpha-secret-plaintext-4f1c9d";
    public const string BetaSecretValue = "beta-secret-plaintext-7b2e60";

    [Fact]
    public async Task The_run_secret_delivery_never_crosses_the_tenant_boundary()
    {
        using var host = new TenantProbeHostFixture();
        // Real secrets on both sides, so every miss below is a refusal of REAL material - not a
        // probe against names that never had values.
        using var alice = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);
        using var carol = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgBOwnerEmail);
        await TenantIsolationAuditProbes.CreateSecretAsync(alice, AlphaSecretName, AlphaSecretValue);
        await TenantIsolationAuditProbes.CreateSecretAsync(carol, BetaSecretName, BetaSecretValue);

        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var (_, betaToken) = await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host);
        using var alphaClient = host.CreateClient();
        alphaClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);
        using var betaClient = host.CreateClient();
        betaClient.DefaultRequestHeaders.Authorization = new("Bearer", betaToken);

        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(alphaClient, TwoOrgsFixture.BuildRunAId);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(betaClient, TwoOrgsFixture.BuildRunBId);

        // Positive control: org B's worker fetches org B's secret while executing org B's run.
        var (betaStatus, betaValue) = await TenantIsolationWorkerApiProbes.FetchRunSecretAsync(
            betaClient, TwoOrgsFixture.BuildRunBId, BetaSecretName);
        Assert.Equal(HttpStatusCode.OK, betaStatus);
        Assert.Equal(BetaSecretValue, betaValue);

        // Org A's token, org B's run, org B's secret name: one bodyless 404, no value in it.
        var (foreignStatus, foreignValue) = await TenantIsolationWorkerApiProbes.FetchRunSecretAsync(
            alphaClient, TwoOrgsFixture.BuildRunBId, BetaSecretName);
        Assert.Equal(HttpStatusCode.NotFound, foreignStatus);
        Assert.Empty(foreignValue);

        // Org A's token, org B's run, org A's secret NAME: the run is foreign, so the answer is
        // the same 404 - another tenant's name is never resolved.
        var (foreignNameStatus, foreignNameValue) = await TenantIsolationWorkerApiProbes.FetchRunSecretAsync(
            alphaClient, TwoOrgsFixture.BuildRunBId, AlphaSecretName);
        Assert.Equal(HttpStatusCode.NotFound, foreignNameStatus);
        Assert.Empty(foreignNameValue);

        // The reverse direction: org B's token against org A's run and org A's secret.
        var (reverseStatus, reverseValue) = await TenantIsolationWorkerApiProbes.FetchRunSecretAsync(
            betaClient, TwoOrgsFixture.BuildRunAId, AlphaSecretName);
        Assert.Equal(HttpStatusCode.NotFound, reverseStatus);
        Assert.Empty(reverseValue);

        // Org A's own fetch still works (the delivery is a feature, not a bug).
        var (alphaStatus, alphaValue) = await TenantIsolationWorkerApiProbes.FetchRunSecretAsync(
            alphaClient, TwoOrgsFixture.BuildRunAId, AlphaSecretName);
        Assert.Equal(HttpStatusCode.OK, alphaStatus);
        Assert.Equal(AlphaSecretValue, alphaValue);
    }
}

/// <summary>The F-013 credential delivery: the material travels only to the executing worker of a tenant-owned run.</summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationGitCredentialDeliveryProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public const string AlphaCredentialValue = "alpha-pat-plaintext-9d41c7";
    public const string BetaCredentialValue = "beta-pat-plaintext-2e87ba";

    [Fact]
    public async Task The_git_credential_delivery_never_crosses_the_tenant_boundary()
    {
        using var host = new TenantProbeHostFixture();
        // Real credentials on both sides (the fixture's placeholder ciphertext never decrypts, so
        // the positive controls need material the real protector wrote).
        using var alice = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);
        using var carol = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgBOwnerEmail);
        var alphaCredentialId = await TenantIsolationAuditProbes.CreateGitCredentialAsync(
            alice, "ALPHA_LIVE_TOKEN", "HttpsToken", AlphaCredentialValue);
        var betaCredentialId = await TenantIsolationAuditProbes.CreateGitCredentialAsync(
            carol, "BETA_LIVE_TOKEN", "HttpsToken", BetaCredentialValue);

        // Card F-018 gave the product the surface that attaches a credential, so the audit uses it:
        // each organization's Owner attaches their own organization's credential through the settings
        // tab's form (the delivery then resolves the run's project's credential).
        await TenantIsolationAuditProbes.AttachProjectCredentialAsync(
            alice, TwoOrgsFixture.ProjectAId, alphaCredentialId);
        await TenantIsolationAuditProbes.AttachProjectCredentialAsync(
            carol, TwoOrgsFixture.ProjectBId, betaCredentialId);

        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var (_, betaToken) = await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host);
        using var alphaClient = host.CreateClient();
        alphaClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);
        using var betaClient = host.CreateClient();
        betaClient.DefaultRequestHeaders.Authorization = new("Bearer", betaToken);

        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(alphaClient, TwoOrgsFixture.BuildRunAId);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(betaClient, TwoOrgsFixture.BuildRunBId);

        // Positive control: org B's worker, executing org B's run, receives the decrypted material.
        var (betaStatus, betaMaterial) = await TenantIsolationWorkerApiProbes.FetchGitCredentialAsync(
            betaClient, TwoOrgsFixture.BuildRunBId);
        Assert.Equal(HttpStatusCode.OK, betaStatus);
        Assert.Equal(BetaCredentialValue, betaMaterial.GetProperty("value").GetString());

        // Org A's token against org B's run: one bodyless 404, no material in it.
        var (foreignStatus, foreignMaterial) = await TenantIsolationWorkerApiProbes.FetchGitCredentialAsync(
            alphaClient, TwoOrgsFixture.BuildRunBId);
        Assert.Equal(HttpStatusCode.NotFound, foreignStatus);
        Assert.Equal(JsonValueKind.Undefined, foreignMaterial.ValueKind);

        // The reverse direction: org B's token against org A's run.
        var (reverseStatus, reverseMaterial) = await TenantIsolationWorkerApiProbes.FetchGitCredentialAsync(
            betaClient, TwoOrgsFixture.BuildRunAId);
        Assert.Equal(HttpStatusCode.NotFound, reverseStatus);
        Assert.Equal(JsonValueKind.Undefined, reverseMaterial.ValueKind);

        // Org A's own delivery still works (the feature path is alive).
        var (alphaStatus, alphaMaterial) = await TenantIsolationWorkerApiProbes.FetchGitCredentialAsync(
            alphaClient, TwoOrgsFixture.BuildRunAId);
        Assert.Equal(HttpStatusCode.OK, alphaStatus);
        Assert.Equal(AlphaCredentialValue, alphaMaterial.GetProperty("value").GetString());
    }
}
