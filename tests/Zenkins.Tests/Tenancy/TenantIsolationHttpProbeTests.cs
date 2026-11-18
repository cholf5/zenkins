using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-001's HTTP isolation probe, replacing the old test-only <c>/__probe/projects</c> +
/// <c>X-Test-Organization</c> seam with F-003's real, authenticated project pages and endpoints.
/// <para>
/// The assertion is unchanged in substance: a request made as organization A's user, carrying
/// organization B's project id anywhere a client can put it (route, query string, form body), must
/// never return B's data or mutate B's row. Because the real login form is used, the tenant is the
/// membership-derived active organization - not a header the test can choose.
/// </para>
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationHttpProbeTests : IClassFixture<TenantProbeHostFixture>
{
    private readonly TenantProbeHostFixture _host;

    public TenantIsolationHttpProbeTests(TenantProbeHostFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_shows_only_the_active_organizations_projects()
    {
        using var client = await SignInAsync("alice@example.test");

        var list = await AuthTestHost.GetStringAsync(client, "/projects");

        Assert.Contains(TwoOrgsFixture.ProjectAName, list, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, list, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBRepositoryUrl, list, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBId.ToString(), list, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_route_as_a_with_bs_project_id_returns_not_found()
    {
        using var client = await SignInAsync("alice@example.test");

        foreach (var path in ForeignProjectPaths(TwoOrgsFixture.ProjectBId))
        {
            using var response = await client.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectBRepositoryUrl, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.OrgBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Detail_route_as_b_with_as_project_id_returns_not_found()
    {
        using var client = await SignInAsync("carol@example.test");

        foreach (var path in ForeignProjectPaths(TwoOrgsFixture.ProjectAId))
        {
            using var response = await client.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectAName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectARepositoryUrl, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TwoOrgsFixture.OrgAId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Query_string_cannot_select_another_tenants_project()
    {
        using var client = await SignInAsync("alice@example.test");

        // A's id in the route plus B's id in the query: the route decides, so A's row is shown and
        // B's fields never appear.
        var positive = await AuthTestHost.GetStringAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}?id={TwoOrgsFixture.ProjectBId}");
        Assert.Contains(TwoOrgsFixture.ProjectAName, positive, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, positive, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBRepositoryUrl, positive, StringComparison.Ordinal);

        // B's id in the route plus A's id in the query: still a 404.
        using var negative = await client.GetAsync(
            $"/projects/{TwoOrgsFixture.ProjectBId}?id={TwoOrgsFixture.ProjectAId}",
            CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, negative.StatusCode);

        var body = await negative.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectAName, body, StringComparison.Ordinal);

        // The list does not accept a query-selected project either.
        var list = await AuthTestHost.GetStringAsync(client, $"/projects?id={TwoOrgsFixture.ProjectBId}");
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Form_body_cannot_select_another_tenants_project()
    {
        using var client = await SignInAsync("alice@example.test");

        // A project created for this probe, so the test never mutates the seeded rows other probes read.
        var projectId = await CreateProjectAsync(client, "form-probe");

        // The route names A's project; a forged ProjectId field names B's. The route wins.
        var edit = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}",
            $"/projects/{projectId}/edit",
            new Dictionary<string, string>
            {
                ["ProjectId"] = TwoOrgsFixture.ProjectBId.ToString(),
                ["Name"] = "form-probe-edited",
                ["RepositoryUrl"] = "https://example.test/form-probe.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
            });
        MembershipAuthorizationProbes.AssertRedirect(edit, $"/projects/{projectId}?notice=updated");

        var edited = (await _host.Data.ReadAcrossTenantsAsync<Project>(project => project.Id == projectId, CancellationToken)).Single();
        Assert.Equal("form-probe-edited", edited.Name);

        // F-025: the polling switch is a project mutation like the others, resolved through the tenant
        // filter - a foreign id is the same 404, and B's polling state is untouched by it.
        var polling = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/polling",
            new Dictionary<string, string>
            {
                ["PollingEnabled"] = "true",
                ["PollIntervalMinutes"] = "1",
            });
        Assert.Equal(HttpStatusCode.NotFound, polling.StatusCode);

        var beta = (await _host.Data.ReadAcrossTenantsAsync<Project>(
            project => project.Id == TwoOrgsFixture.ProjectBId,
            CancellationToken)).Single();
        Assert.Equal(TwoOrgsFixture.ProjectBName, beta.Name);
        Assert.Equal(TwoOrgsFixture.ProjectBRepositoryUrl, beta.RepositoryUrl);
        Assert.Null(beta.PollIntervalMinutes);
        Assert.False(beta.PollingEnabled);

        // A POST that names B in the route is a 404 even with A's id in the body, and B is untouched.
        var foreignEdit = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/edit",
            new Dictionary<string, string>
            {
                ["ProjectId"] = projectId.ToString(),
                ["Name"] = "hijacked",
                ["RepositoryUrl"] = "https://example.test/hijacked.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
            });
        Assert.Equal(HttpStatusCode.NotFound, foreignEdit.StatusCode);

        var foreignDelete = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/delete",
            new Dictionary<string, string> { ["ProjectId"] = projectId.ToString() });
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);

        beta = (await _host.Data.ReadAcrossTenantsAsync<Project>(
            project => project.Id == TwoOrgsFixture.ProjectBId,
            CancellationToken)).Single();
        Assert.Equal(TwoOrgsFixture.ProjectBName, beta.Name);

        // Deleting A's project while naming B in the body deletes A, never B.
        var delete = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}",
            $"/projects/{projectId}/delete",
            new Dictionary<string, string> { ["ProjectId"] = TwoOrgsFixture.ProjectBId.ToString() });
        MembershipAuthorizationProbes.AssertRedirect(delete, "/projects?notice=deleted");

        Assert.DoesNotContain(
            await _host.Data.ReadAcrossTenantsAsync<Project>(project => project.Id == projectId, CancellationToken),
            project => project.Id == projectId);
        Assert.Contains(
            await _host.Data.ReadAcrossTenantsAsync<Project>(
                project => project.Id == TwoOrgsFixture.ProjectBId,
                CancellationToken),
            project => project.Id == TwoOrgsFixture.ProjectBId);
    }

    [Fact]
    public async Task Member_of_a_cannot_read_or_mutate_bs_project()
    {
        using var client = await SignInAsync("bob@example.test");

        // bob is an ordinary Member of A: A's project is readable...
        var own = await AuthTestHost.GetStringAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}");
        Assert.Contains(TwoOrgsFixture.ProjectAName, own, StringComparison.Ordinal);

        // ...and B's is indistinguishable from a missing project in every shape.
        foreach (var path in ForeignProjectPaths(TwoOrgsFixture.ProjectBId))
        {
            using var response = await client.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, body, StringComparison.Ordinal);
        }

        var edit = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/edit",
            new Dictionary<string, string>
            {
                ["Name"] = "member-hijack",
                ["RepositoryUrl"] = "https://example.test/member-hijack.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
            });
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);

        var delete = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // F-020/F-021: the two definition editors resolve the project through the tenant filter,
        // so a foreign id is the same 404 the read paths answer - never a write to B's row.
        var steps = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/freestyle/steps",
            new Dictionary<string, string> { ["StepsJson"] = "[]" });
        Assert.Equal(HttpStatusCode.NotFound, steps.StatusCode);

        var parameters = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/parameters",
            new Dictionary<string, string> { ["ParametersJson"] = "[]" });
        Assert.Equal(HttpStatusCode.NotFound, parameters.StatusCode);

        // F-028/F-029: the trigger brake and the queue controls resolve the project through the tenant
        // filter as well - a foreign id is the same 404 the other mutations answer, and B's row keeps its
        // state (enabled, unlimited).
        var disable = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/disable",
            new Dictionary<string, string> { ["Reason"] = "member-hijack" });
        Assert.Equal(HttpStatusCode.NotFound, disable.StatusCode);

        var enable = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/enable",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, enable.StatusCode);

        var limits = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            $"/projects/{TwoOrgsFixture.ProjectBId}/limits",
            new Dictionary<string, string> { ["MaxConcurrentRuns"] = "1" });
        Assert.Equal(HttpStatusCode.NotFound, limits.StatusCode);

        var beta = (await _host.Data.ReadAcrossTenantsAsync<Project>(
            project => project.Id == TwoOrgsFixture.ProjectBId,
            CancellationToken)).Single();
        Assert.Equal(TwoOrgsFixture.ProjectBName, beta.Name);
        Assert.Equal(TwoOrgsFixture.ProjectBRepositoryUrl, beta.RepositoryUrl);
        Assert.False(beta.Disabled);
        Assert.Null(beta.MaxConcurrentRuns);
        Assert.Null(beta.MaxQueuedRuns);
        Assert.Equal(0, beta.QuietPeriodSeconds);
    }

    [Fact]
    public async Task Positive_control_each_tenant_reads_its_own_project()
    {
        using var aliceClient = await SignInAsync("alice@example.test");
        var alpha = await AuthTestHost.GetStringAsync(
            aliceClient,
            $"/projects/{TwoOrgsFixture.ProjectAId}");
        Assert.Contains(TwoOrgsFixture.ProjectAName, alpha, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, alpha, StringComparison.Ordinal);

        using var carolClient = await SignInAsync("carol@example.test");
        var beta = await AuthTestHost.GetStringAsync(
            carolClient,
            $"/projects/{TwoOrgsFixture.ProjectBId}");
        Assert.Contains(TwoOrgsFixture.ProjectBName, beta, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectAName, beta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_without_a_selected_tenant_sees_no_tenant_data()
    {
        // Anonymous requests are redirected to the login page by [Authorize]; they never reach a
        // tenant-filtered read, and no project data is returned.
        using var client = _host.CreateClient();
        using var response = await client.GetAsync(
            $"/projects/{TwoOrgsFixture.ProjectAId}",
            CancellationToken);

        Assert.True((int)response.StatusCode is 302 or 303);
        Assert.Contains(
            "/account/login",
            response.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectAName, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_with_a_malformed_project_id_is_not_found()
    {
        // The real route has a :guid constraint, so a malformed id does not match any endpoint and
        // is a 404. (The old test-only probe parsed the id itself and answered 400.)
        using var client = await SignInAsync("alice@example.test");
        using var response = await client.GetAsync("/projects/not-a-guid", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static IEnumerable<string> ForeignProjectPaths(Guid projectId)
    {
        yield return $"/projects/{projectId}";
        yield return $"/projects/{projectId}/edit";
        yield return $"/projects/{projectId}/delete/confirm";
        yield return $"/projects/{projectId}/parameters";
        yield return $"/projects/{projectId}/branches";
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _host.CreateClient();
        var response = await AuthTestHost.LoginAsync(
            client,
            email,
            TwoOrgsFixture.UserPassword,
            returnUrl: "/projects");
        if ((int)response.StatusCode is not (302 or 303))
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"Signing in as '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>
            {
                ["Name"] = name,
                ["RepositoryUrl"] = "https://example.test/probe.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
            });

        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating '{name}' failed with {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.Contains("?notice=created", location, StringComparison.Ordinal);

        var idText = location["/projects/".Length..];
        var queryStart = idText.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
        {
            idText = idText[..queryStart];
        }

        Assert.True(Guid.TryParse(idText, out var projectId), $"Unexpected redirect '{location}'.");
        return projectId;
    }
}

/// <summary>
/// One class fixture for the probes: a seeded two-organization database and an in-process host that
/// serves F-003's real project pages. Sign-in goes through the real login form; the fixture's users
/// have password hashes for exactly that reason.
/// </summary>
public sealed class TenantProbeHostFixture : IDisposable
{
    public TenantProbeHostFixture()
    {
        Data = new TwoOrgsFixture();
        Factory = new AuthenticatedTenantApplicationFactory(Data);
    }

    public TwoOrgsFixture Data { get; }

    public AuthenticatedTenantApplicationFactory Factory { get; }

    public HttpClient CreateClient() =>
        Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost"),
        });

    public void Dispose()
    {
        Factory.Dispose();
        Data.Dispose();
    }
}
