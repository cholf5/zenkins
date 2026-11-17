using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Projects;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Projects;

/// <summary>
/// Card F-003's project CRUD tests, driven through the real authenticated HTTP pipeline (cookies,
/// antiforgery, form endpoints) of <see cref="OrganizationTestHost"/>. UI controls are never the
/// assertion: every forbidden action is also a direct POST, and every rejection is verified against
/// the database.
/// </summary>
[Trait("Category", "Projects")]
public sealed class ProjectCrudTests
{
    private const string RepositoryUrl = "https://github.com/acme/game-client.git";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delete_without_an_antiforgery_token_is_rejected_and_changes_nothing()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var projectId = await CreateProjectAsync(client, "csrf-app");

        // No antiforgery token: UseAntiforgery rejects the POST before the endpoint runs.
        using var response = await client.PostAsync(
            $"/projects/{projectId}/delete",
            new FormUrlEncodedContent(new Dictionary<string, string>()),
            CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf-app", (await host.ReadProjectAsync(projectId)).Name);
    }

    [Fact]
    public async Task Owner_delete_requires_only_the_antiforgery_token()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var projectId = await CreateProjectAsync(client, "confirmation-app");

        // The confirmation page is the confirmation; there is no extra "Confirmed" field to forge.
        var page = await AuthTestHost.GetStringAsync(client, $"/projects/{projectId}");
        var token = OrganizationTestHost.ExtractAntiforgeryMetaToken(page);
        using var response = await client.PostAsync(
            $"/projects/{projectId}/delete",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }),
            CancellationToken);

        MembershipAuthorizationProbes.AssertRedirect(response, "/projects?notice=deleted");
        Assert.Empty(await host.ReadProjectsAsync(
            await host.ReadOrganizationIdAsync(Zenkins.Server.Auth.BootstrapService.StarterOrganizationName)));
    }

    [Fact]
    public async Task New_project_form_prefills_main_and_the_pipeline_default()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        var form = await AuthTestHost.GetStringAsync(client, "/projects/new");

        Assert.Contains("id=\"project-default-branch\"", form, StringComparison.Ordinal);
        Assert.Contains("value=\"main\"", form, StringComparison.Ordinal);
        Assert.Contains("id=\"project-pipeline-path\"", form, StringComparison.Ordinal);
        Assert.Contains("value=\"pipeline.lua\"", form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_shows_in_the_list_with_the_pipeline_path_default()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        var projectId = await CreateProjectAsync(client, "game-client", pipelinePath: string.Empty);

        var list = await AuthTestHost.GetStringAsync(client, "/projects");
        Assert.Contains("game-client", list, StringComparison.Ordinal);
        Assert.Contains(RepositoryUrl, list, StringComparison.Ordinal);
        Assert.Contains("pipeline.lua", list, StringComparison.Ordinal);
        Assert.Contains("main", list, StringComparison.Ordinal);

        var stored = await host.ReadProjectAsync(projectId);
        Assert.Equal(organizationId, stored.OrganizationId);
        Assert.Equal("game-client", stored.Name);
        Assert.Equal(RepositoryUrl, stored.RepositoryUrl);
        Assert.Equal("main", stored.DefaultBranch);
        Assert.Equal(ProjectValidation.DefaultPipelinePath, stored.PipelinePath);
        Assert.Null(stored.GitCredentialId);

        // The data layer is the one authority for the audit timestamps.
        Assert.NotEqual(default, stored.CreatedAt);
        Assert.NotEqual(default, stored.UpdatedAt);
        Assert.True(stored.UpdatedAt >= stored.CreatedAt);

        var detail = await AuthTestHost.GetStringAsync(client, $"/projects/{projectId}");
        Assert.Contains("game-client", detail, StringComparison.Ordinal);
        Assert.Contains("pipeline.lua", detail, StringComparison.Ordinal);
        Assert.Contains("Not configured", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_name_is_a_field_error_and_creates_no_second_row()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        await CreateProjectAsync(client, "game-client");

        var duplicate = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects/new",
            "/projects/create",
            ProjectForm("game-client", "https://github.com/acme/other.git"));

        Assert.True((int)duplicate.StatusCode is 302 or 303);
        var location = duplicate.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/new?", location, StringComparison.Ordinal);
        Assert.Contains("error=name-duplicate", location, StringComparison.Ordinal);

        // Field-level error: rendered against the Name input, with the submitted value preserved.
        var page = await AuthTestHost.GetStringAsync(client, location);
        Assert.Contains("id=\"project-name-error\"", page, StringComparison.Ordinal);
        Assert.Contains("A project with this name already exists in this organization.", page, StringComparison.Ordinal);
        Assert.Contains("value=\"game-client\"", page, StringComparison.Ordinal);

        var projects = await host.ReadProjectsAsync(organizationId);
        Assert.Single(projects);
        Assert.Equal(RepositoryUrl, projects[0].RepositoryUrl);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    public async Task Dangerous_repository_urls_are_rejected_and_store_nothing(string repositoryUrl)
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        var response = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects/new",
            "/projects/create",
            ProjectForm("dangerous", repositoryUrl));

        Assert.True((int)response.StatusCode is 302 or 303);
        var location = response.Headers.Location!.OriginalString;
        Assert.Contains("error=repository-url-invalid", location, StringComparison.Ordinal);

        var page = await AuthTestHost.GetStringAsync(client, location);
        Assert.Contains("Only HTTPS or SSH git URLs are supported", page, StringComparison.Ordinal);

        Assert.Empty(await host.ReadProjectsAsync(organizationId));
    }

    [Fact]
    public async Task Invalid_branch_and_pipeline_path_are_rejected_with_field_errors()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        var badBranch = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects/new",
            "/projects/create",
            ProjectForm("bad-branch", RepositoryUrl, defaultBranch: "../main"));
        Assert.Contains("error=default-branch-invalid", badBranch.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains(
            "not a valid branch name",
            await AuthTestHost.GetStringAsync(client, badBranch.Headers.Location!.OriginalString),
            StringComparison.Ordinal);

        foreach (var badPath in new[] { "../pipeline.lua", "/pipeline.lua", "ci/../../pipeline.lua" })
        {
            var badPipelinePath = await OrganizationTestHost.PostFormAsync(
                client,
                "/projects/new",
                "/projects/create",
                ProjectForm("bad-path", RepositoryUrl, pipelinePath: badPath));
            Assert.Contains(
                "error=pipeline-path-invalid",
                badPipelinePath.Headers.Location!.OriginalString,
                StringComparison.Ordinal);
            Assert.Contains(
                "Enter a relative path inside the repository",
                await AuthTestHost.GetStringAsync(client, badPipelinePath.Headers.Location!.OriginalString),
                StringComparison.Ordinal);
        }

        Assert.Empty(await host.ReadProjectsAsync(organizationId));
    }

    [Fact]
    public async Task Edit_updates_the_fields_but_not_the_creation_timestamp()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var projectId = await CreateProjectAsync(client, "game-client");
        var before = await host.ReadProjectAsync(projectId);

        var response = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}/edit",
            $"/projects/{projectId}/edit",
            ProjectForm(
                "game-client-renamed",
                "git@github.com:acme/renamed.git",
                defaultBranch: "develop",
                pipelinePath: "ci/pipeline.lua"));

        MembershipAuthorizationProbes.AssertRedirect(response, $"/projects/{projectId}?notice=updated");

        var after = await host.ReadProjectAsync(projectId);
        Assert.Equal("game-client-renamed", after.Name);
        Assert.Equal("git@github.com:acme/renamed.git", after.RepositoryUrl);
        Assert.Equal("develop", after.DefaultBranch);
        Assert.Equal("ci/pipeline.lua", after.PipelinePath);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.True(after.UpdatedAt >= before.UpdatedAt);

        var detail = await AuthTestHost.GetStringAsync(client, $"/projects/{projectId}");
        Assert.Contains("game-client-renamed", detail, StringComparison.Ordinal);
        Assert.Contains("ci/pipeline.lua", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_that_posts_a_different_kind_does_not_change_it()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        // A Freestyle project's kind is what makes its runs execute a frozen step list instead of a
        // repository definition (F-020), and a queued run already snapshotted that choice - so an edit
        // that posts a different kind is ignored, not honoured.
        var freestyleId = await CreateFreestyleProjectAsync(client, "stays-freestyle");

        // The premise first (an assertion about "unchanged" is vacuous if the row was never what it
        // claims): the project really is a repository-free Freestyle project before the edit.
        var freestyleBefore = await host.ReadProjectAsync(freestyleId);
        Assert.Equal(ProjectKinds.Freestyle, freestyleBefore.Kind);
        Assert.Null(freestyleBefore.RepositoryUrl);
        Assert.Null(freestyleBefore.PipelinePath);

        var toPipeline = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{freestyleId}/edit",
            $"/projects/{freestyleId}/edit",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "stays-freestyle-renamed",
                ["Kind"] = ProjectKinds.Pipeline,
                ["RepositoryUrl"] = "https://github.com/acme/turned-into-pipeline.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = ProjectValidation.DefaultPipelinePath,
            });

        // The edit itself succeeds - only the kind is immune, so the rest of the form still applies.
        MembershipAuthorizationProbes.AssertRedirect(toPipeline, $"/projects/{freestyleId}?notice=updated");
        var freestyle = await host.ReadProjectAsync(freestyleId);
        Assert.Equal(ProjectKinds.Freestyle, freestyle.Kind);
        Assert.Equal("stays-freestyle-renamed", freestyle.Name);
        Assert.Null(freestyle.RepositoryUrl);
        Assert.Null(freestyle.DefaultBranch);
        Assert.Null(freestyle.PipelinePath);

        // And the other direction: a Pipeline project's repository fields cannot be dropped by posting
        // the Freestyle kind over them (a Pipeline edit still has to carry them, so they are posted
        // unchanged - and only the kind is under test).
        var pipelineId = await CreateProjectAsync(client, "stays-pipeline");
        var pipelineBefore = await host.ReadProjectAsync(pipelineId);
        Assert.Equal(ProjectKinds.Pipeline, pipelineBefore.Kind);
        Assert.Equal(RepositoryUrl, pipelineBefore.RepositoryUrl);
        Assert.Equal(ProjectValidation.DefaultPipelinePath, pipelineBefore.PipelinePath);

        var toFreestyle = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{pipelineId}/edit",
            $"/projects/{pipelineId}/edit",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "stays-pipeline",
                ["Kind"] = ProjectKinds.Freestyle,
                ["RepositoryUrl"] = RepositoryUrl,
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = ProjectValidation.DefaultPipelinePath,
            });
        MembershipAuthorizationProbes.AssertRedirect(toFreestyle, $"/projects/{pipelineId}?notice=updated");
        var pipeline = await host.ReadProjectAsync(pipelineId);
        Assert.Equal(ProjectKinds.Pipeline, pipeline.Kind);
        Assert.Equal(RepositoryUrl, pipeline.RepositoryUrl);
        Assert.Equal("main", pipeline.DefaultBranch);
    }

    [Fact]
    public async Task Member_can_create_and_edit_but_only_an_owner_can_delete()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var ownerClient = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        const string memberEmail = "bob@example.test";
        using var memberClient = await host.RegisterUserAsync(memberEmail);
        var organizationId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);

        var invite = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            "/organizations/members",
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = memberEmail });
        MembershipAuthorizationProbes.AssertRedirect(invite, "/organizations/members?notice=invited");

        // A Member may create...
        var projectId = await CreateProjectAsync(memberClient, "member-app");

        // ...and edit.
        var edit = await OrganizationTestHost.PostFormAsync(
            memberClient,
            $"/projects/{projectId}/edit",
            $"/projects/{projectId}/edit",
            ProjectForm("member-app-edited", "https://github.com/acme/member.git"));
        MembershipAuthorizationProbes.AssertRedirect(edit, $"/projects/{projectId}?notice=updated");
        Assert.Equal("member-app-edited", (await host.ReadProjectAsync(projectId)).Name);

        // ...but a direct delete POST is 403 and the row survives.
        var memberDelete = await OrganizationTestHost.PostFormAsync(
            memberClient,
            $"/projects/{projectId}",
            $"/projects/{projectId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Forbidden, memberDelete.StatusCode);
        Assert.Equal("member-app-edited", (await host.ReadProjectAsync(projectId)).Name);

        // The confirmation page is not offered to a Member either, but the 403 above is the guard.
        var deletePage = await AuthTestHost.GetStringAsync(memberClient, $"/projects/{projectId}/delete/confirm");
        Assert.Contains("Owner access required", deletePage, StringComparison.Ordinal);
        Assert.DoesNotContain($"/projects/{projectId}/delete", deletePage, StringComparison.Ordinal);

        // The Owner deletes with the confirmation form's POST.
        var ownerDelete = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            $"/projects/{projectId}",
            $"/projects/{projectId}/delete",
            new Dictionary<string, string>());
        MembershipAuthorizationProbes.AssertRedirect(ownerDelete, "/projects?notice=deleted");
        Assert.Empty(await host.ReadProjectsAsync(organizationId));
    }

    [Fact]
    public async Task Cross_tenant_detail_is_404_for_an_html_navigation_too()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var bravoId = await host.AddOrganizationAsync(
            "Bravo",
            (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));
        var bravoProjectId = await host.AddProjectAsync(bravoId, "beta-app");

        // A browser sends Accept: text/html, which used to trigger the /not-found re-execute inside
        // the same request and fail with "RemoteNavigationManager already initialized". The page must
        // answer its own 404 body instead.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/projects/{bravoProjectId}");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain("beta-app", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_organizations_project_id_is_404_for_list_detail_edit_and_delete()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var client = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var starterId = await host.ReadOrganizationIdAsync(
            Zenkins.Server.Auth.BootstrapService.StarterOrganizationName);
        var starterProjectId = await CreateProjectAsync(client, "starter-app");

        // alice is also an Owner of Bravo, so this is not a role failure: the active organization is
        // Starter and Bravo's project must be invisible in it.
        var bravoId = await host.AddOrganizationAsync(
            "Bravo",
            (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));
        var bravoProjectId = await host.AddProjectAsync(
            bravoId,
            "beta-app",
            "https://example.test/beta.git",
            "develop");

        var list = await AuthTestHost.GetStringAsync(client, "/projects");
        Assert.Contains("starter-app", list, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-app", list, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.test/beta.git", list, StringComparison.Ordinal);

        foreach (var path in new[]
                 {
                     $"/projects/{bravoProjectId}",
                     $"/projects/{bravoProjectId}/edit",
                     $"/projects/{bravoProjectId}/delete/confirm",
                 })
        {
            using var detail = await client.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

            var body = await detail.Content.ReadAsStringAsync(CancellationToken);
            Assert.DoesNotContain("beta-app", body, StringComparison.Ordinal);
            Assert.DoesNotContain("https://example.test/beta.git", body, StringComparison.Ordinal);
        }

        // POSTs cannot reach it either, and Bravo's row is untouched.
        var edit = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{starterProjectId}",
            $"/projects/{bravoProjectId}/edit",
            ProjectForm("hijacked", "https://example.test/hijacked.git"));
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);

        var delete = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{starterProjectId}",
            $"/projects/{bravoProjectId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var bravoProject = await host.ReadProjectAsync(bravoProjectId);
        Assert.Equal("beta-app", bravoProject.Name);
        Assert.Equal("https://example.test/beta.git", bravoProject.RepositoryUrl);
        Assert.Equal("develop", bravoProject.DefaultBranch);
        Assert.Single(await host.ReadProjectsAsync(starterId));
        Assert.Single(await host.ReadProjectsAsync(bravoId));

        // The active organization decides: after switching to Bravo, A's project is the 404.
        var switchToBravo = await OrganizationTestHost.PostFormAsync(
            client,
            "/",
            "/organizations/switch",
            new Dictionary<string, string> { ["OrganizationId"] = bravoId.ToString() });
        MembershipAuthorizationProbes.AssertRedirect(switchToBravo, "/");

        using var foreign = await client.GetAsync($"/projects/{starterProjectId}", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static Task<Guid> CreateProjectAsync(
        HttpClient client,
        string name,
        string repositoryUrl = RepositoryUrl,
        string defaultBranch = "main",
        string pipelinePath = ProjectValidation.DefaultPipelinePath)
    {
        return CreateProjectCoreAsync(client, name, repositoryUrl, defaultBranch, pipelinePath);
    }

    /// <summary>Creates a Freestyle project through the real create form (card F-020's kind selector).</summary>
    private static async Task<Guid> CreateFreestyleProjectAsync(HttpClient client, string name)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = ProjectKinds.Freestyle,
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating the freestyle project '{name}' failed with {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }

    private static async Task<Guid> CreateProjectCoreAsync(
        HttpClient client,
        string name,
        string repositoryUrl,
        string defaultBranch,
        string pipelinePath)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects/new",
            "/projects/create",
            ProjectForm(name, repositoryUrl, defaultBranch, pipelinePath));

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

    private static Dictionary<string, string> ProjectForm(
        string name,
        string repositoryUrl,
        string defaultBranch = "main",
        string pipelinePath = ProjectValidation.DefaultPipelinePath) => new(StringComparer.Ordinal)
    {
        ["Name"] = name,
        ["RepositoryUrl"] = repositoryUrl,
        ["DefaultBranch"] = defaultBranch,
        ["PipelinePath"] = pipelinePath,
    };
}
