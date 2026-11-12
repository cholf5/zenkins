using System.Net;
using Xunit;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-001's HTTP id-swap probe: a request authenticated as one tenant, with another tenant's
/// project id substituted into the route, the query string or the form body, must come back as
/// "not found" and never carry the other tenant's data. The positive control on the same pipeline
/// proves the probe is not vacuous - the tenant's own id returns its own row.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationHttpProbeTests : IClassFixture<TenantProbeHostFixture>
{
    private readonly TenantProbeHostFixture _host;

    public TenantIsolationHttpProbeTests(TenantProbeHostFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("route")]
    [InlineData("query")]
    [InlineData("form")]
    public async Task Request_as_organization_a_with_organization_bs_project_id_returns_not_found(string shape)
    {
        using var client = _host.CreateClient();
        using var request = CreateProbeRequest(shape, TwoOrgsFixture.ProjectBId, TwoOrgsFixture.OrgAId);
        using var response = await client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Not just the status code: no field of organization B's project may appear in the body.
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBRepositoryUrl, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.OrgBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("query")]
    [InlineData("form")]
    public async Task Request_as_organization_b_with_organization_as_project_id_returns_not_found(string shape)
    {
        using var client = _host.CreateClient();
        using var request = CreateProbeRequest(shape, TwoOrgsFixture.ProjectAId, TwoOrgsFixture.OrgBId);
        using var response = await client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectAName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.OrgAId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("route", TwoOrgsFixture.ProjectAName)]
    [InlineData("query", TwoOrgsFixture.ProjectAName)]
    [InlineData("form", TwoOrgsFixture.ProjectAName)]
    public async Task Positive_control_the_active_tenants_own_id_returns_its_own_row(string shape, string expectedName)
    {
        using var client = _host.CreateClient();
        using var request = CreateProbeRequest(shape, TwoOrgsFixture.ProjectAId, TwoOrgsFixture.OrgAId);
        using var response = await client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains(expectedName, body, StringComparison.Ordinal);
        Assert.Contains(TwoOrgsFixture.OrgAId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TwoOrgsFixture.OrgBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Request_without_a_selected_tenant_sees_no_tenant_data()
    {
        // The anonymous case: no tenant header, so the active organization stays unset and the
        // query filter matches nothing. An unset tenant must never mean "all tenants".
        using var client = _host.CreateClient();
        using var request = CreateProbeRequest("route", TwoOrgsFixture.ProjectAId, organizationId: null);
        using var response = await client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectAName, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_with_a_malformed_project_id_is_rejected()
    {
        // A probe that silently fell back to "list everything" on a bad id would defeat its purpose.
        using var client = _host.CreateClient();
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/__probe/projects/not-a-guid"))
        {
            request.Headers.Add(
                TenantProbeApplicationFactory.OrganizationHeaderName,
                TwoOrgsFixture.OrgAId.ToString());

            using var response = await client.SendAsync(request, CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private static HttpRequestMessage CreateProbeRequest(string shape, Guid projectId, Guid? organizationId)
    {
        HttpRequestMessage request = shape switch
        {
            "route" => new HttpRequestMessage(
                HttpMethod.Get,
                $"{TenantProbeApplicationFactory.ProbePath}/{projectId}"),
            "query" => new HttpRequestMessage(
                HttpMethod.Get,
                $"{TenantProbeApplicationFactory.ProbePath}?id={projectId}"),
            "form" => new HttpRequestMessage(HttpMethod.Post, TenantProbeApplicationFactory.ProbePath)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["id"] = projectId.ToString(),
                }),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unsupported probe shape."),
        };

        if (organizationId is not null)
        {
            request.Headers.Add(
                TenantProbeApplicationFactory.OrganizationHeaderName,
                organizationId.Value.ToString());
        }

        return request;
    }
}

/// <summary>
/// One class fixture for the probe class: a single seeded database and a single in-process host, so
/// the expensive part (migrate, seed, start the app) happens once and every probe shares the exact
/// same data.
/// </summary>
public sealed class TenantProbeHostFixture : IDisposable
{
    public TenantProbeHostFixture()
    {
        Data = new TwoOrgsFixture();
        Factory = new TenantProbeApplicationFactory(Data);
    }

    public TwoOrgsFixture Data { get; }

    public TenantProbeApplicationFactory Factory { get; }

    public HttpClient CreateClient() =>
        Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    public void Dispose()
    {
        Factory.Dispose();
        Data.Dispose();
    }
}
