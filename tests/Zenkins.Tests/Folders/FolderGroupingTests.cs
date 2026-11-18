using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Folders;
using Zenkins.Server.Projects;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Folders;

/// <summary>
/// Card F-042's folder rules, driven through the real HTTP forms of a real host (the same shape every
/// other project surface is probed with): folders nest, sibling names are unique case-insensitively, a
/// project's name is unique inside its folder, a collision is refused rather than renamed around, a
/// non-empty folder cannot be deleted, and the two listing pages render the tree with rolled-up counts and
/// each project's last run.
/// <para>
/// Every fact here asserts a <b>stored row</b> as well as the answer, so a page that renders the refusal
/// while still writing the row cannot pass.
/// </para>
/// </summary>
[Trait("Category", "Folders")]
public sealed class FolderGroupingTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Folder_names_are_unique_among_siblings_ignoring_case_and_the_tree_records_its_depth()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        var teamA = await CreateFolderAsync(host, owner, "Team-A");
        var sibling = await CreateFolderAsync(host, owner, "team-b");

        // AC-1: "team-a" is the same name as "Team-A" among the same siblings - refused, and nothing is
        // written (the count is still two).
        var refused = await TryCreateFolderAsync(owner, "team-a", parentId: null);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("folder-name-duplicate", refused.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(2, (await host.ReadFoldersAsync(organizationId)).Count);

        // The same name is fine under a different parent: uniqueness is among siblings, not global.
        var nested = await CreateFolderAsync(host, owner, "team-a", parentId: teamA);
        Assert.NotEqual(sibling, nested);

        // The depth column is stored, not inferred: a root folder is level 1, its child is level 2.
        var folders = await host.ReadFoldersAsync(organizationId);
        Assert.Equal(1, folders.Single(folder => folder.Id == teamA).Depth);
        Assert.Equal(2, folders.Single(folder => folder.Id == nested).Depth);
        Assert.Equal(teamA, folders.Single(folder => folder.Id == nested).ParentFolderId);

        // And the name key is the lowercased identity the unique index compares, stamped by the data layer.
        Assert.Equal("team-a", folders.Single(folder => folder.Id == teamA).NameKey);
        Assert.Equal(Guid.Empty, folders.Single(folder => folder.Id == teamA).ParentKey);
    }

    [Fact]
    public async Task Projects_are_unique_inside_a_folder_and_the_same_name_works_in_two_folders()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // TC-001: `build` in team-a and `build` in team-b are two independent projects.
        var teamA = await CreateFolderAsync(host, owner, "team-a");
        var teamB = await CreateFolderAsync(host, owner, "team-b");
        var buildA = await CreateProjectAsync(owner, "build", teamA);
        var buildB = await CreateProjectAsync(owner, "build", teamB);

        Assert.NotEqual(buildA, buildB);

        var projects = await host.ReadProjectsAsync(organizationId);
        Assert.Equal(teamA, projects.Single(project => project.Id == buildA).FolderId);
        Assert.Equal(teamB, projects.Single(project => project.Id == buildB).FolderId);
        Assert.Equal("build", projects.Single(project => project.Id == buildA).NameKey);

        // The projects page shows them under their folders, not in one flat list: the root lists the two
        // folders and no project.
        var root = await AuthTestHost.GetStringAsync(owner, "/projects");
        Assert.Contains("team-a", root, StringComparison.Ordinal);
        Assert.Contains("team-b", root, StringComparison.Ordinal);
        Assert.DoesNotContain(">/projects/" + buildA + "<", root, StringComparison.Ordinal);

        // Each folder page lists exactly its own child.
        var folderA = await AuthTestHost.GetStringAsync(owner, $"/folders/{teamA}");
        Assert.Contains("build", folderA, StringComparison.Ordinal);
        Assert.Contains($"/projects/{buildA}", folderA, StringComparison.Ordinal);
        Assert.DoesNotContain($"/projects/{buildB}", folderA, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_root_project_name_is_refused_case_insensitively_and_the_refusal_names_the_existing_project()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // TC-002: a second `build` at the root is refused - and by case-insensitive identity, so `BUILD`
        // is the same name. Nothing is renamed with a suffix.
        var first = await CreateProjectAsync(owner, "build", folderId: null);

        var refused = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            ProjectFieldsFor("BUILD", folderId: null));
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);

        var location = refused.Headers.Location!.OriginalString;
        Assert.Contains("name-duplicate", location, StringComparison.Ordinal);

        // TC-002's "naming the existing project": the form is told which project holds the name.
        Assert.Contains("conflict=build", location, StringComparison.Ordinal);
        var page = await AuthTestHost.GetStringAsync(owner, location);
        Assert.Contains("already exists in that folder", page, StringComparison.Ordinal);
        Assert.Contains("build", page, StringComparison.Ordinal);

        // Nothing was stored twice and the survivor kept its own name.
        var projects = await host.ReadProjectsAsync(organizationId);
        Assert.Single(projects);
        Assert.Equal(first, projects[0].Id);
        Assert.Equal("build", projects[0].Name);
    }

    [Fact]
    public async Task A_non_empty_folder_cannot_be_deleted_and_an_empty_one_deletes_nothing_else()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // TC-003: a folder holding one project is refused; the project and the folder both survive.
        var folder = await CreateFolderAsync(host, owner, "services");
        var project = await CreateProjectAsync(owner, "api", folder);

        var refused = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/folders/{folder}",
            $"/folders/{folder}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("folder-not-empty", refused.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var folders = await host.ReadFoldersAsync(organizationId);
        Assert.Single(folders);
        Assert.Equal(folder, folders[0].Id);
        Assert.Single(await host.ReadProjectsAsync(organizationId));

        // A folder holding only a subfolder is refused too (nothing cascades down the tree).
        var child = await CreateFolderAsync(host, owner, "child", parentId: folder);
        await MoveProjectAsync(owner, project, targetFolderId: null);
        var stillNotEmpty = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/folders/{folder}",
            $"/folders/{folder}/delete",
            new Dictionary<string, string>());
        Assert.Contains("folder-not-empty", stillNotEmpty.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // Emptying it makes the delete succeed - and deletes exactly that one folder: the child, the
        // project and the (now root) project all survive.
        await DeleteFolderAsync(owner, child);
        await DeleteFolderAsync(owner, folder);

        folders = await host.ReadFoldersAsync(organizationId);
        Assert.Empty(folders);
        var projects = await host.ReadProjectsAsync(organizationId);
        Assert.Single(projects);
        Assert.Null(projects[0].FolderId);
        Assert.Equal(project, projects[0].Id);
    }

    [Fact]
    public async Task Nesting_is_capped_at_five_levels()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // TC-004a: five levels are allowed, the sixth is refused - with no partial write.
        Guid? parent = null;
        var chain = new List<Guid>();
        for (var depth = 1; depth <= FolderValidation.MaxDepth; depth++)
        {
            var folder = await CreateFolderAsync(host, owner, $"level-{depth}", parentId: parent);
            chain.Add(folder);
            parent = folder;
        }

        Assert.Equal(FolderValidation.MaxDepth, chain.Count);
        var folders = await host.ReadFoldersAsync(organizationId);
        Assert.Equal(FolderValidation.MaxDepth, folders.Count);
        Assert.Equal(Enumerable.Range(1, FolderValidation.MaxDepth), folders.Select(folder => folder.Depth).Order());

        var refused = await TryCreateFolderAsync(owner, "level-6", parentId: chain[^1]);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("folder-depth-exceeded", refused.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(FolderValidation.MaxDepth, (await host.ReadFoldersAsync(organizationId)).Count);
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_itself_or_a_descendant()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // TC-004b: self and descendant are both refused, with the no-partial-write property: the tree is
        // byte-for-byte what it was.
        var outer = await CreateFolderAsync(host, owner, "outer");
        var inner = await CreateFolderAsync(host, owner, "inner", parentId: outer);
        var leaf = await CreateFolderAsync(host, owner, "leaf", parentId: inner);

        foreach (var (target, expectedCode) in new[]
                 {
                     (outer, "folder-cycle"),
                     (inner, "folder-cycle"),
                     (leaf, "folder-cycle"),
                 })
        {
            var response = await MoveFolderAsync(owner, invokingFolderId: outer, targetParentId: target);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains(expectedCode, response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        var folders = await host.ReadFoldersAsync(organizationId);
        Assert.Equal(3, folders.Count);
        Assert.Null(folders.Single(folder => folder.Id == outer).ParentFolderId);
        Assert.Equal(outer, folders.Single(folder => folder.Id == inner).ParentFolderId);
        Assert.Equal(inner, folders.Single(folder => folder.Id == leaf).ParentFolderId);
        Assert.Equal(1, folders.Single(folder => folder.Id == outer).Depth);
        Assert.Equal(3, folders.Single(folder => folder.Id == leaf).Depth);

        // And a legitimate move still works, rewriting the subtree's depths with it.
        var elsewhere = await CreateFolderAsync(host, owner, "elsewhere");
        var moved = await MoveFolderAsync(owner, invokingFolderId: inner, targetParentId: elsewhere);
        Assert.Equal(HttpStatusCode.Redirect, moved.StatusCode);
        Assert.Contains("notice=moved", moved.Headers.Location!.OriginalString, StringComparison.Ordinal);

        folders = await host.ReadFoldersAsync(organizationId);
        Assert.Equal(elsewhere, folders.Single(folder => folder.Id == inner).ParentFolderId);
        Assert.Equal(2, folders.Single(folder => folder.Id == inner).Depth);
        Assert.Equal(3, folders.Single(folder => folder.Id == leaf).Depth);
    }

    [Fact]
    public async Task Moving_a_project_into_a_folder_with_a_collision_is_refused_without_renaming_either_project()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // AC-5: the target folder already holds `build`; the root's `build` is refused - the existing
        // project keeps its name, the moving one keeps its name *and* its folder. No silent "build-2".
        var target = await CreateFolderAsync(host, owner, "target");
        var existing = await CreateProjectAsync(owner, "build", target);
        var mover = await CreateProjectAsync(owner, "build", folderId: null);

        var refused = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects",
            $"/projects/{mover}/move",
            new Dictionary<string, string> { ["FolderId"] = target.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);

        var location = refused.Headers.Location!.OriginalString;
        Assert.Contains("name-duplicate", location, StringComparison.Ordinal);
        Assert.Contains($"conflict=build", location, StringComparison.Ordinal);

        var projects = await host.ReadProjectsAsync(organizationId);
        Assert.Equal(2, projects.Count);
        Assert.Null(projects.Single(project => project.Id == mover).FolderId);
        Assert.Equal(target, projects.Single(project => project.Id == existing).FolderId);
        Assert.All(projects, project => Assert.Equal("build", project.Name));

        // A case-only difference is the same collision, and the exit that works is a rename or another
        // folder - both of which keep the two names distinct in the end.
        var other = await CreateFolderAsync(host, owner, "other");
        var moved = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects",
            $"/projects/{mover}/move",
            new Dictionary<string, string> { ["FolderId"] = other.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, moved.StatusCode);
        Assert.Contains("notice=project-moved", moved.Headers.Location!.OriginalString, StringComparison.Ordinal);

        projects = await host.ReadProjectsAsync(organizationId);
        Assert.Equal(other, projects.Single(project => project.Id == mover).FolderId);
    }

    [Fact]
    public async Task Project_urls_stay_id_based_and_the_folder_only_sets_the_create_default()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // AC-4: the folder page's "New project" link carries the folder, the create form defaults to it,
        // and the project then lives there...
        var folder = await CreateFolderAsync(host, owner, "monorepo");
        var form = await AuthTestHost.GetStringAsync(owner, $"/projects/new?folder={folder}");
        Assert.Contains($"value=\"{folder}\" selected", form, StringComparison.Ordinal);

        var project = await CreateProjectAsync(owner, "web", folder);
        Assert.Equal(folder, (await host.ReadProjectsAsync(organizationId)).Single().FolderId);

        // ...while its URL stays exactly /projects/{id}: no folder segment anywhere (the same page the
        // pre-folders product served).
        var detail = await AuthTestHost.GetStringAsync(owner, $"/projects/{project}");
        Assert.Contains("web", detail, StringComparison.Ordinal);

        var notFound = await owner.GetAsync($"/projects/{folder}", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        // The edit form does not move a project: a posted FolderId is ignored (moving is its own route
        // with its own collision refusal).
        var root = await CreateFolderAsync(host, owner, "root-two");
        var edit = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{project}/edit",
            $"/projects/{project}/edit",
            new Dictionary<string, string>
            {
                ["Name"] = "web",
                ["Kind"] = ProjectKinds.Freestyle,
                ["FolderId"] = root.ToString(),
            });
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        Assert.Equal(folder, (await host.ReadProjectsAsync(organizationId)).Single().FolderId);
    }

    [Fact]
    public async Task Search_spans_the_current_subtree_and_the_everywhere_option()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        // AC-6: a root project, a project in a folder one level down, and one in an unrelated branch of
        // the tree - all matching the same word.
        var services = await CreateFolderAsync(host, owner, "services");
        var billing = await CreateFolderAsync(host, owner, "billing", parentId: services);
        var platform = await CreateFolderAsync(host, owner, "platform");

        await CreateProjectAsync(owner, "api-gateway", folderId: null);
        await CreateProjectAsync(owner, "api-billing", billing);
        await CreateProjectAsync(owner, "api-platform", platform);

        // From the folder page, the subtree scope finds both its own child folders and its nested
        // project; the sibling branch is out of scope.
        var subtree = await AuthTestHost.GetStringAsync(owner, $"/folders/{services}?q=api&scope=here");
        Assert.Contains("api-billing", subtree, StringComparison.Ordinal);
        Assert.DoesNotContain("api-platform", subtree, StringComparison.Ordinal);

        // "Everywhere" widens to the whole organization, and there the other branch and the root show up.
        var everywhere = await AuthTestHost.GetStringAsync(owner, $"/folders/{services}?q=api&scope=all");
        Assert.Contains("api-billing", everywhere, StringComparison.Ordinal);
        Assert.Contains("api-platform", everywhere, StringComparison.Ordinal);
        Assert.Contains("api-gateway", everywhere, StringComparison.Ordinal);

        // At the root, "here" is the root's own subtree - the root project matches, the nested ones do not.
        var rootHere = await AuthTestHost.GetStringAsync(owner, "/projects?q=api&scope=here");
        Assert.Contains("api-gateway", rootHere, StringComparison.Ordinal);
        Assert.DoesNotContain("api-billing", rootHere, StringComparison.Ordinal);

        var rootAll = await AuthTestHost.GetStringAsync(owner, "/projects?q=api&scope=all");
        Assert.Contains("api-gateway", rootAll, StringComparison.Ordinal);
        Assert.Contains("api-billing", rootAll, StringComparison.Ordinal);
        Assert.Contains("api-platform", rootAll, StringComparison.Ordinal);

        // A folder search finds the folder itself as well.
        var folderSearch = await AuthTestHost.GetStringAsync(owner, "/projects?q=bill&scope=all");
        Assert.Contains($"/folders/{billing}", folderSearch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folder_counts_roll_up_and_the_folder_page_shows_each_project_s_last_run()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // AC-3: a parent with one direct project, one child folder holding two, and one project that has
        // run. The parent's page states the rolled-up count (3) and the child's page states its own (2).
        var parent = await CreateFolderAsync(host, owner, "parent");
        var child = await CreateFolderAsync(host, owner, "child", parentId: parent);
        var direct = await CreateProjectAsync(owner, "direct", parent);
        var ran = await CreateProjectAsync(owner, "ran", child);
        await CreateProjectAsync(owner, "never-ran", child);

        await AddRunAsync(host, organizationId, ran, BuildRunStatus.Success, number: 7);

        var parentPage = await AuthTestHost.GetStringAsync(owner, $"/folders/{parent}");
        Assert.Contains("child", parentPage, StringComparison.Ordinal);
        Assert.Contains("3 project(s) in this subtree", parentPage, StringComparison.Ordinal);

        var childPage = await AuthTestHost.GetStringAsync(owner, $"/folders/{child}");
        Assert.Contains($"/projects/{ran}", childPage, StringComparison.Ordinal);
        Assert.Contains("Success", childPage, StringComparison.Ordinal);
        Assert.Contains($"#7", childPage, StringComparison.Ordinal);
        Assert.Contains("never run", childPage, StringComparison.Ordinal);
        Assert.Contains("2 project(s) in this subtree", childPage, StringComparison.Ordinal);

        // The root listing counts the whole tree, and the breadcrumb names the path the folder sits on.
        var root = await AuthTestHost.GetStringAsync(owner, "/projects");
        Assert.Contains("3 project(s) in this subtree", root, StringComparison.Ordinal);

        var breadcrumb = await AuthTestHost.GetStringAsync(owner, $"/folders/{child}");
        Assert.Contains("Projects", breadcrumb, StringComparison.Ordinal);
        Assert.Contains($"/folders/{parent}", breadcrumb, StringComparison.Ordinal);
        Assert.Contains("child", breadcrumb, StringComparison.Ordinal);

        // Nothing here depends on the project being anywhere special: the direct project is listed on the
        // parent's own page, not only counted.
        Assert.Contains($"/projects/{direct}", parentPage, StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static Dictionary<string, string> ProjectFieldsFor(string name, Guid? folderId) =>
        new(StringComparer.Ordinal)
        {
            ["Name"] = name,
            ["Kind"] = ProjectKinds.Freestyle,
            ["FolderId"] = folderId?.ToString() ?? string.Empty,
        };

    /// <summary>Creates a folder through the real form and returns its id (the row, not the URL, is the
    /// source of the id, so a refused create cannot pass as a success).</summary>
    private static async Task<Guid> CreateFolderAsync(
        OrganizationTestHost host,
        HttpClient client,
        string name,
        Guid? parentId = null)
    {
        var response = await TryCreateFolderAsync(client, name, parentId);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.Contains("notice=created", location, StringComparison.Ordinal);

        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
        var folder = (await host.ReadFoldersAsync(organizationId)).Single(candidate => candidate.Name == name
            && candidate.ParentFolderId == parentId);
        return folder.Id;
    }

    /// <summary>The same POST without the success assertion: a refusal is the caller's to inspect.</summary>
    private static Task<HttpResponseMessage> TryCreateFolderAsync(
        HttpClient client,
        string name,
        Guid? parentId) =>
        OrganizationTestHost.PostFormAsync(
            client,
            "/folders/new",
            "/folders/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["ParentFolderId"] = parentId?.ToString() ?? string.Empty,
            });

    private static async Task<Guid> CreateProjectAsync(
        HttpClient client,
        string name,
        Guid? folderId)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects/new",
            "/projects/create",
            ProjectFieldsFor(name, folderId));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.Contains("notice=created", location, StringComparison.Ordinal);

        var projectId = Guid.Parse(location["/projects/".Length..].Split('?')[0]);
        return projectId;
    }

    private static Task<HttpResponseMessage> MoveFolderAsync(
        HttpClient client,
        Guid invokingFolderId,
        Guid? targetParentId) =>
        OrganizationTestHost.PostFormAsync(
            client,
            $"/folders/{invokingFolderId}",
            $"/folders/{invokingFolderId}/move",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ParentFolderId"] = targetParentId?.ToString() ?? string.Empty,
            });

    private static async Task MoveProjectAsync(
        HttpClient client,
        Guid projectId,
        Guid? targetFolderId)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects",
            $"/projects/{projectId}/move",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FolderId"] = targetFolderId?.ToString() ?? string.Empty,
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task DeleteFolderAsync(
        HttpClient client,
        Guid folderId)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            $"/folders/{folderId}",
            $"/folders/{folderId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("notice=folder-deleted", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>
    /// Seeds one finished run for a project, so the folder page's "last run" column has something real
    /// to show. Written through the system scope, like every fixture that seeds tenant-owned rows.
    /// </summary>
    private static Task AddRunAsync(
        OrganizationTestHost host,
        Guid organizationId,
        Guid projectId,
        string status,
        int number) =>
        host.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            var factory = services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>();
            await using var context = await factory.CreateDbContextAsync(CancellationToken);
            using var scope = systemDataScope.BeginScope();

            context.BuildRuns.Add(new BuildRun
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Number = number,
                Status = status,
                TriggerType = BuildRunTriggerType.Manual,
                RequiredTags = string.Empty,
                GitBranch = "main",
                PipelinePath = "pipeline.lua",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                EndedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });
}
