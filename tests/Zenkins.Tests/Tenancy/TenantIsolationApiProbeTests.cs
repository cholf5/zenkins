using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Api;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card F-027's tenancy probes: the API never answers from another organization, and it never leaks
/// existence. Every probe signs in or presents a token issued by the real surfaces, and the assertion
/// side reads rows through the product's system scope.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationApiProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static HttpClient ApiClient(TenantProbeHostFixture host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> TriggerAsync(HttpClient client, Guid projectId) =>
        client.PostAsync(
            $"/api/v1/projects/{projectId}/runs",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Foreign_project_or_run_ids_are_a_bare_404_for_every_api_route()
    {
        using var host = new TenantProbeHostFixture();
        using var api = ApiClient(host, TwoOrgsFixture.ApiTokenAPlaintext);

        var runsBefore = await host.Data.CountAcrossTenantsAsync<BuildRun>(TwoOrgsFixture.OrgBId, CancellationToken);

        // Trigger: B's project is not A's, and nothing is queued for it.
        using (var trigger = await TriggerAsync(api, TwoOrgsFixture.ProjectBId))
        {
            Assert.Equal(HttpStatusCode.NotFound, trigger.StatusCode);
            Assert.Empty(await trigger.Content.ReadAsStringAsync(CancellationToken));
        }

        Assert.Equal(runsBefore, await host.Data.CountAcrossTenantsAsync<BuildRun>(TwoOrgsFixture.OrgBId, CancellationToken));

        foreach (var url in new[]
                 {
                     $"/api/v1/runs/{TwoOrgsFixture.BuildRunBId}",
                     $"/api/v1/projects/{TwoOrgsFixture.ProjectBId}/runs",
                     $"/api/v1/runs/{TwoOrgsFixture.BuildRunBId}/log",
                     $"/api/v1/runs/{Guid.NewGuid()}",
                     $"/api/v1/runs/{Guid.NewGuid()}/log",
                 })
        {
            using var response = await api.GetAsync(url, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.Empty(body);
        }

        // Cancel: refused without a byte, and B's run is untouched.
        using (var cancel = await api.PostAsync(
                   $"/api/v1/runs/{TwoOrgsFixture.BuildRunBId}/cancel", content: null, CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
            Assert.Empty(await cancel.Content.ReadAsStringAsync(CancellationToken));
        }

        var runB = Assert.Single(
            await host.Data.ReadAcrossTenantsAsync<BuildRun>(run => run.Id == TwoOrgsFixture.BuildRunBId, CancellationToken));
        Assert.Equal(BuildRunStatus.Queued, runB.Status);
        Assert.Null(runB.CancelRequestedAt);
    }

    [Fact]
    public async Task A_token_without_the_route_s_scope_is_a_403()
    {
        using var host = new TenantProbeHostFixture();
        using var owner = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        var readOnly = await MintAsync(host, owner, "read-only-probe", trigger: false, read: true, cancel: false);
        using (var api = ApiClient(host, readOnly))
        {
            using var trigger = await TriggerAsync(api, TwoOrgsFixture.ProjectAId);
            Assert.Equal(HttpStatusCode.Forbidden, trigger.StatusCode);
        }

        var triggerOnly = await MintAsync(host, owner, "trigger-only-probe", trigger: true, read: false, cancel: false);
        using (var api = ApiClient(host, triggerOnly))
        {
            using var read = await api.GetAsync("/api/v1/projects", CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

            using var cancel = await api.PostAsync(
                $"/api/v1/runs/{TwoOrgsFixture.BuildRunAId}/cancel", content: null, CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        }

        // The refused calls changed nothing: A's run is still queued and no run was added.
        var runA = Assert.Single(
            await host.Data.ReadAcrossTenantsAsync<BuildRun>(run => run.Id == TwoOrgsFixture.BuildRunAId, CancellationToken));
        Assert.Equal(BuildRunStatus.Queued, runA.Status);
        Assert.Equal(1, await host.Data.CountAcrossTenantsAsync<BuildRun>(TwoOrgsFixture.OrgAId, CancellationToken));
    }

    [Fact]
    public async Task A_revoked_or_unknown_token_is_a_bare_401()
    {
        using var host = new TenantProbeHostFixture();

        using (var unknown = ApiClient(host, "zapi_never-issued"))
        {
            using var response = await unknown.GetAsync("/api/v1/projects", CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync(CancellationToken));
        }

        // Revoke A's token through the real Owner form, then the same plaintext is refused.
        using var owner = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);
        using (var revoked = await OrganizationTestHost.PostFormAsync(
                   owner,
                   "/api-tokens",
                   $"/api-tokens/{TwoOrgsFixture.ApiTokenAId}/revoke",
                   new Dictionary<string, string>()))
        {
            Assert.True((int)revoked.StatusCode is 302 or 303);
        }

        using (var api = ApiClient(host, TwoOrgsFixture.ApiTokenAPlaintext))
        {
            using var response = await api.GetAsync("/api/v1/projects", CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync(CancellationToken));
        }
    }

    [Fact]
    public async Task The_management_forms_cannot_reach_another_tenant_s_token()
    {
        using var host = new TenantProbeHostFixture();
        using var owner = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        // Revoking B's token through A's session is B's id - a bare 404, row untouched.
        using (var response = await OrganizationTestHost.PostFormAsync(
                   owner,
                   "/api-tokens",
                   $"/api-tokens/{TwoOrgsFixture.ApiTokenBId}/revoke",
                   new Dictionary<string, string>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var tokenB = Assert.Single(
            await host.Data.ReadAcrossTenantsAsync<ApiToken>(token => token.Id == TwoOrgsFixture.ApiTokenBId, CancellationToken));
        Assert.Null(tokenB.RevokedAt);

        // Minting lands in A only: B still has exactly its one token.
        await MintAsync(host, owner, "minted-by-a", trigger: true, read: true, cancel: false);
        Assert.Equal(2, await host.Data.CountAcrossTenantsAsync<ApiToken>(TwoOrgsFixture.OrgAId, CancellationToken));
        Assert.Equal(1, await host.Data.CountAcrossTenantsAsync<ApiToken>(TwoOrgsFixture.OrgBId, CancellationToken));
    }

    [Fact]
    public async Task The_api_tokens_page_is_owner_only_and_never_prints_token_material()
    {
        using var host = new TenantProbeHostFixture();
        using var owner = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);

        var page = await Auth.AuthTestHost.GetStringAsync(owner, "/api-tokens");
        Assert.Contains(TwoOrgsFixture.ApiTokenAName, page, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ApiTokenAPlaintext, page, StringComparison.Ordinal);

        var tokenA = Assert.Single(
            await host.Data.ReadAcrossTenantsAsync<ApiToken>(token => token.Id == TwoOrgsFixture.ApiTokenAId, CancellationToken));
        Assert.DoesNotContain(tokenA.TokenHash, page, StringComparison.Ordinal);

        // A Member of the same organization never sees the labels, and cannot mint.
        using var member = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAMemberEmail);
        var memberPage = await Auth.AuthTestHost.GetStringAsync(member, "/api-tokens");
        Assert.Contains("Owner access required", memberPage, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ApiTokenAName, memberPage, StringComparison.Ordinal);

        using (var mint = await OrganizationTestHost.PostFormAsync(
                   member,
                   "/api-tokens",
                   "/api-tokens/create",
                   new Dictionary<string, string> { ["Name"] = "member-mint", ["Trigger"] = "true" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, mint.StatusCode);
        }

        Assert.Equal(1, await host.Data.CountAcrossTenantsAsync<ApiToken>(TwoOrgsFixture.OrgAId, CancellationToken));
    }

    /// <summary>Mints a token through the real Owner form and returns the one-time plaintext.</summary>
    private static async Task<string> MintAsync(
        TenantProbeHostFixture host,
        HttpClient ownerClient,
        string name,
        bool trigger,
        bool read,
        bool cancel)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = name };
        if (trigger)
        {
            fields["Trigger"] = "true";
        }

        if (read)
        {
            fields["Read"] = "true";
        }

        if (cancel)
        {
            fields["Cancel"] = "true";
        }

        using var response = await OrganizationTestHost.PostFormAsync(
            ownerClient, "/api-tokens", "/api-tokens/create", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(CancellationToken);
        return TenantIsolationAuditProbes.ExtractCodeContent(html, "api-token-plaintext");
    }
}
