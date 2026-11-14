using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's id-swap probes over the cookie-authenticated surfaces (pages, form endpoints and
/// the two download endpoints). Org A's session asks for org B's resource by id in every place a
/// client can put one - route, form body or query - and the answer must be 404/403 without
/// confirming that the resource exists. Every negative answer is also checked against the fixture's
/// system-scope rows, so a refusal that mutated the foreign tenant fails the probe.
/// <para>
/// Every test constructs its own two-organization environment: several probes mutate rows
/// (renames, invitations), and the audit must never depend on a shared class fixture's state.
/// </para>
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationHttpSurfaceProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------
    // Read pages
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Tenant_pages_render_only_the_active_organizations_rows()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        foreach (var path in new[]
                 {
                     "/",
                     "/projects",
                     "/secrets",
                     "/workers",
                     "/organizations/members",
                     $"/projects/{TwoOrgsFixture.ProjectAId}/settings",
                     $"/projects/{TwoOrgsFixture.ProjectAId}/runs",
                     $"/projects/{TwoOrgsFixture.ProjectAId}/pipelines",
                     $"/runs/{TwoOrgsFixture.BuildRunAId}",
                 })
        {
            var body = await AuthTestHost.GetStringAsync(client, path);

            Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectBRepositoryUrl, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.WorkerBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.SecretBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.SecretBCiphertext, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.GitCredentialBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.WebhookBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.OrgBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.OrgBOwnerUserId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TwoOrgsFixture.BuildRunBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("beta-line", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Foreign_tenant_pages_answer_404_without_existence_leaks()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        foreach (var path in new[]
                 {
                     $"/projects/{TwoOrgsFixture.ProjectBId}",
                     $"/projects/{TwoOrgsFixture.ProjectBId}/runs",
                     $"/projects/{TwoOrgsFixture.ProjectBId}/pipelines",
                     $"/projects/{TwoOrgsFixture.ProjectBId}/settings",
                     $"/projects/{TwoOrgsFixture.ProjectBId}/edit",
                     $"/projects/{TwoOrgsFixture.ProjectBId}/delete/confirm",
                     $"/runs/{TwoOrgsFixture.BuildRunBId}",
                     $"/workers/{TwoOrgsFixture.WorkerBId}",
                 })
        {
            using var response = await client.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.WorkerBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectBRepositoryUrl, body, StringComparison.Ordinal);
            Assert.DoesNotContain("beta-line", body, StringComparison.Ordinal);
        }

        // A Member (not Owner) sees exactly the same answers - the page is not the enforcement point.
        using var memberClient = await TenantIsolationAuditProbes.SignInAsync(
            host, TenantIsolationProbeAssertions.OrgAMemberEmail);
        using var memberResponse = await memberClient.GetAsync(
            $"/projects/{TwoOrgsFixture.ProjectBId}/settings", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, memberResponse.StatusCode);
    }

    // ---------------------------------------------------------------------
    // Log and artifact downloads
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Log_download_refuses_a_foreign_run_without_bytes_or_oracle()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        // Foreign run: bare 404, no body, no bytes of the foreign log.
        using var foreign = await client.GetAsync(
            $"/runs/{TwoOrgsFixture.BuildRunBId}/log/download", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var foreignBody = await foreign.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain("beta-line", foreignBody, StringComparison.Ordinal);

        // Positive control: the same endpoint serves org A's own run's lines - and org B's line
        // never rides along.
        var own = await AuthTestHost.GetStringAsync(
            client, $"/runs/{TwoOrgsFixture.BuildRunAId}/log/download");
        Assert.Contains("alpha-line", own, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-line", own, StringComparison.Ordinal);

        // A Member downloads org A's log as well; org B's run stays unreachable.
        using var memberClient = await TenantIsolationAuditProbes.SignInAsync(
            host, TenantIsolationProbeAssertions.OrgAMemberEmail);
        using var memberForeign = await memberClient.GetAsync(
            $"/runs/{TwoOrgsFixture.BuildRunBId}/log/download", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, memberForeign.StatusCode);
    }

    [Fact]
    public async Task Artifact_download_refuses_a_foreign_artifact_without_oracle()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        // Both id-swap spellings: B's run id, and B's artifact id under A's run id.
        using var foreignRun = await client.GetAsync(
            $"/runs/{TwoOrgsFixture.BuildRunBId}/artifacts/{TwoOrgsFixture.ArtifactBId}/download", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreignRun.StatusCode);

        using var foreignArtifact = await client.GetAsync(
            $"/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{TwoOrgsFixture.ArtifactBId}/download", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreignArtifact.StatusCode);

        // No bytes in either refusal, so nothing of org B's artifact can leak.
        Assert.Empty(await foreignRun.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Empty(await foreignArtifact.Content.ReadAsByteArrayAsync(CancellationToken));
    }

    // ---------------------------------------------------------------------
    // Mutating form endpoints
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_run_on_another_tenants_project_is_a_bare_404()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        var response = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects",
            $"/projects/{TwoOrgsFixture.ProjectBId}/runs/start",
            new Dictionary<string, string> { ["Branch"] = "main" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Org B's queue is untouched: the trigger never landed in the foreign tenant.
        var betaRuns = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<BuildRun>(
            host, run => run.OrganizationId == TwoOrgsFixture.OrgBId);
        Assert.Single(betaRuns);
    }

    [Fact]
    public async Task Canceling_another_tenants_run_is_a_bare_404_and_touches_nothing()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        var response = await OrganizationTestHost.PostFormAsync(
            client,
            $"/runs/{TwoOrgsFixture.BuildRunAId}",
            $"/runs/{TwoOrgsFixture.BuildRunBId}/cancel",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var beta = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<BuildRun>(
            host, run => run.Id == TwoOrgsFixture.BuildRunBId)).Single();
        Assert.Equal(BuildRunStatus.Queued, beta.Status);
        Assert.Null(beta.EndedAt);
    }

    [Fact]
    public async Task Secret_mutations_cannot_reach_another_tenants_secret()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        var rotate = await OrganizationTestHost.PostFormAsync(
            client,
            "/secrets",
            $"/secrets/{TwoOrgsFixture.SecretBId}/rotate",
            new Dictionary<string, string> { ["Value"] = "hijacked-secret-value" });
        Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);

        var delete = await OrganizationTestHost.PostFormAsync(
            client,
            "/secrets",
            $"/secrets/{TwoOrgsFixture.SecretBId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var beta = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<Secret>(
            host, secret => secret.Id == TwoOrgsFixture.SecretBId)).Single();
        Assert.Equal(TwoOrgsFixture.SecretBCiphertext, beta.Ciphertext);
        Assert.Equal(TwoOrgsFixture.SecretBName, beta.Name);
    }

    [Fact]
    public async Task Git_credential_mutations_cannot_reach_another_tenants_credential()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        var rotate = await OrganizationTestHost.PostFormAsync(
            client,
            "/secrets",
            $"/git-credentials/{TwoOrgsFixture.GitCredentialBId}/rotate",
            new Dictionary<string, string>
            {
                ["Kind"] = "HttpsToken",
                ["Value"] = "hijacked-pat-value",
                ["UserName"] = "attacker",
            });
        Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);

        var delete = await OrganizationTestHost.PostFormAsync(
            client,
            "/secrets",
            $"/git-credentials/{TwoOrgsFixture.GitCredentialBId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var beta = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<GitCredential>(
            host, credential => credential.Id == TwoOrgsFixture.GitCredentialBId)).Single();
        Assert.Equal(TwoOrgsFixture.GitCredentialBCiphertext, beta.Ciphertext);
        Assert.Equal("SshKey", beta.Kind);
    }

    [Fact]
    public async Task The_settings_credential_list_of_another_tenants_project_is_a_bare_404()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        using var response = await client.GetAsync(
            $"/projects/{TwoOrgsFixture.ProjectBId}/git-credentials", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain(TwoOrgsFixture.GitCredentialBName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.GitCredentialBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Worker_management_cannot_reach_another_tenants_worker_or_token()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        foreach (var action in new[] { "disable", "enable", "delete" })
        {
            var response = await OrganizationTestHost.PostFormAsync(
                client,
                "/workers",
                $"/workers/{TwoOrgsFixture.WorkerBId}/{action}",
                new Dictionary<string, string>());
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var revoke = await OrganizationTestHost.PostFormAsync(
            client,
            "/workers",
            $"/workers/registration-tokens/{TwoOrgsFixture.WorkerTokenBId}/revoke",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        // Org B's worker and token rows are exactly as the fixture seeded them.
        var worker = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<WorkerEntity>(
            host, candidate => candidate.Id == TwoOrgsFixture.WorkerBId)).Single();
        Assert.Equal(WorkerStatus.Online, worker.Status);
        Assert.False(worker.IsDisabled);
        var token = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<WorkerToken>(
            host, candidate => candidate.Id == TwoOrgsFixture.WorkerTokenBId)).Single();
        Assert.Null(token.RevokedAt);
    }

    [Fact]
    public async Task Webhook_management_cannot_reach_another_tenants_webhook()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        var create = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects",
            $"/projects/{TwoOrgsFixture.ProjectBId}/webhooks/create",
            new Dictionary<string, string>
            {
                ["Name"] = "smuggled-hook",
                ["BranchFilter"] = "main",
                ["RequiredTags"] = string.Empty,
            });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);

        foreach (var action in new[] { "rotate", "enable", "disable", "delete" })
        {
            var response = await OrganizationTestHost.PostFormAsync(
                client,
                "/projects",
                $"/projects/{TwoOrgsFixture.ProjectBId}/webhooks/{TwoOrgsFixture.WebhookBId}/{action}",
                new Dictionary<string, string>());
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // Org B's webhook row and its delivery record are untouched, and no webhook was created there.
        var betaHooks = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<Webhook>(
            host, webhook => webhook.OrganizationId == TwoOrgsFixture.OrgBId);
        Assert.Single(betaHooks);
        Assert.Equal(TwoOrgsFixture.WebhookBName, betaHooks[0].Name);
        Assert.Equal(TwoOrgsFixture.WebhookBCiphertext, betaHooks[0].SecretCiphertext);
    }

    // ---------------------------------------------------------------------
    // Organization scope
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Switching_to_an_organization_without_membership_is_refused_and_keeps_the_selection()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        var response = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects",
            "/organizations/switch",
            new Dictionary<string, string>
            {
                ["OrganizationId"] = TwoOrgsFixture.OrgBId.ToString(),
                ["ReturnUrl"] = "/projects",
            });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The selection is untouched: the session still sees organization A's rows only.
        var list = await AuthTestHost.GetStringAsync(client, "/projects");
        Assert.Contains(TwoOrgsFixture.ProjectAName, list, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, list, StringComparison.Ordinal);

        // A forged (nonexistent) target is the same refusal - the membership check decides, never
        // the id's existence.
        var forged = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects",
            "/organizations/switch",
            new Dictionary<string, string>
            {
                ["OrganizationId"] = Guid.NewGuid().ToString(),
                ["ReturnUrl"] = "/projects",
            });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
    }

    [Fact]
    public async Task Member_management_cannot_touch_another_organizations_membership()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        // Org A's Owner presents org B's owner membership: the tenant filter makes it a 404 in both
        // forms (role change and removal) - never an unauthorized demotion of a foreign member.
        var role = await OrganizationTestHost.PostFormAsync(
            client,
            "/organizations/members",
            "/organizations/members/role",
            new Dictionary<string, string>
            {
                ["MembershipId"] = TwoOrgsFixture.OrgBOwnerMembershipId.ToString(),
                ["Role"] = "Member",
            });
        Assert.Equal(HttpStatusCode.NotFound, role.StatusCode);

        var remove = await OrganizationTestHost.PostFormAsync(
            client,
            "/organizations/members",
            "/organizations/members/remove",
            new Dictionary<string, string>
            {
                ["MembershipId"] = TwoOrgsFixture.OrgBOwnerMembershipId.ToString(),
            });
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);

        var betaMembership = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<OrganizationMember>(
            host, membership => membership.Id == TwoOrgsFixture.OrgBOwnerMembershipId)).Single();
        Assert.Equal(OrganizationRole.Owner, betaMembership.Role);
        Assert.Equal(TwoOrgsFixture.OrgBId, betaMembership.OrganizationId);
    }

    [Fact]
    public async Task Active_organization_forms_target_only_the_active_tenant()
    {
        using var host = new TenantProbeHostFixture();
        using var client = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        // Rename: the form carries no organization id, so the active org is the only target. The
        // foreign org's row cannot be addressed at all - and stays untouched.
        var rename = await OrganizationTestHost.PostFormAsync(
            client,
            "/organizations/settings",
            "/organizations/settings",
            new Dictionary<string, string> { ["Name"] = "Alpha-Renamed" });
        MembershipAuthorizationProbes.AssertRedirect(rename, "/organizations/settings?notice=renamed");

        var beta = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<Organization>(
            host, organization => organization.Id == TwoOrgsFixture.OrgBId)).Single();
        Assert.Equal(TwoOrgsFixture.OrgBName, beta.Name);

        // Invite: attaches an existing account to the ACTIVE organization only. Carol keeps her
        // organization B membership exactly as it was - the invite cannot write there.
        var invite = await OrganizationTestHost.PostFormAsync(
            client,
            "/organizations/members",
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = "carol@example.test" });
        MembershipAuthorizationProbes.AssertRedirect(invite, "/organizations/members?notice=invited");

        var carolMemberships = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<OrganizationMember>(
            host, membership => membership.UserId == TwoOrgsFixture.OrgBOwnerUserId);
        Assert.Contains(carolMemberships, membership =>
            membership.OrganizationId == TwoOrgsFixture.OrgBId
            && membership.Role == OrganizationRole.Owner);
        var alphaMembership = carolMemberships.Single(membership =>
            membership.OrganizationId == TwoOrgsFixture.OrgAId);
        Assert.Equal(OrganizationRole.Member, alphaMembership.Role);
    }

    [Fact]
    public async Task A_session_without_a_selected_tenant_never_reaches_tenant_rows()
    {
        using var host = new TenantProbeHostFixture();

        // Anonymous requests: tenant-bearing pages redirect to the login page, and the download
        // endpoints answer 302 as well - no tenant row is read, no bytes leave.
        using var client = host.CreateClient();
        foreach (var path in new[]
                 {
                     $"/runs/{TwoOrgsFixture.BuildRunBId}/log/download",
                     $"/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{TwoOrgsFixture.ArtifactAId}/download",
                     $"/projects/{TwoOrgsFixture.ProjectAId}",
                 })
        {
            using var response = await client.GetAsync(path, CancellationToken);
            Assert.True((int)response.StatusCode is 302 or 303);
            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.DoesNotContain("alpha-line", body, StringComparison.Ordinal);
            Assert.DoesNotContain("beta-line", body, StringComparison.Ordinal);
        }

        // The worker API is not cookie-authenticated: a cookie session without a token is a bare
        // 401 - a session cookie must never authorize a worker action.
        using var claim = await client.PostAsync("/api/workers/claim", null, CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, claim.StatusCode);
    }
}

/// <summary>Sign-in emails of the Q-001 fixture's seeded accounts, kept as named constants.</summary>
public static class TenantIsolationProbeAssertions
{
    /// <summary>Org A's owner email.</summary>
    public const string OrgAOwnerEmail = "alice@example.test";

    /// <summary>Org A's ordinary member email.</summary>
    public const string OrgAMemberEmail = "bob@example.test";

    /// <summary>Org B's owner email.</summary>
    public const string OrgBOwnerEmail = "carol@example.test";
}
