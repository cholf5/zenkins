using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Projects;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// Card B-010: the project form's field groups are rendered by the server and only <em>switched</em> by
/// <c>js/project-kind.js</c>.
/// <para>
/// The defect: the create form decided which halves <b>exist</b> from the posted/query state (always
/// the repository half on a fresh page) while the script decided which are <b>visible</b>. Two truths,
/// and whenever the script had not run on the form's own DOM - the script is a plain
/// <c>&lt;script&gt;</c> loaded once per document, so a Blazor navigation replaces the form without
/// re-running it - the reader saw a Freestyle choice over repository fields whose <c>required</c>
/// attribute then blocked the submission. The mirror image was worse: the inline script box was not
/// rendered at all until a refused create echoed <c>pipelineSource=inline</c>, so "Pipeline script"
/// showed no box to type in.
/// </para>
/// <para>
/// These facts assert the product surface, not the script: every group is present in every state and
/// <c>hidden</c>/<c>disabled</c> say whether it applies, so a page whose script never ran is still
/// honest, and the script's own listeners are delegated to the document so a replaced form keeps
/// working.
/// </para>
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectFormFieldGroupsTests
{
    private const string RepositoryFields = "<div data-project-repository-fields";
    private const string InlineFields = "<div data-project-inline-fields";
    private const string SourceFields = "<div data-project-source-fields";

    [Fact]
    public async Task The_create_form_renders_every_group_and_starts_on_the_repository_half()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        var page = await AuthTestHost.GetStringAsync(owner, ProjectEndpoints.NewPath);

        // All three groups exist, so the script never has to resurrect one the server omitted.
        Assert.Contains(RepositoryFields, page, StringComparison.Ordinal);
        Assert.Contains(InlineFields, page, StringComparison.Ordinal);
        Assert.Contains(SourceFields, page, StringComparison.Ordinal);

        // The default state is the repository half: visible and enabled, its required fields clean.
        Assert.False(IsHidden(page, RepositoryFields), "the repository fields start hidden");
        Assert.False(IsDisabled(Tag(page, "input", "project-repository-url")), "the repository URL starts disabled");
        Assert.True(IsHidden(page, InlineFields), "the inline script box starts visible");
        Assert.True(IsDisabled(Tag(page, "textarea", "project-inline-script")), "the inline script box starts enabled");
        Assert.False(IsHidden(page, SourceFields), "the definition-source choice starts hidden");
    }

    /// <summary>
    /// A refused Freestyle create re-renders the form with <c>kind=freestyle</c>. The repository half
    /// must still be in the DOM - disabled, so the browser cannot demand its URL - and the reader can
    /// switch back to Pipeline and find the fields there.
    /// </summary>
    [Fact]
    public async Task A_refused_freestyle_create_disables_the_repository_half_but_still_renders_it()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        var page = await AuthTestHost.GetStringAsync(
            owner,
            $"{ProjectEndpoints.NewPath}?kind={ProjectKinds.Freestyle}");

        Assert.Contains(RepositoryFields, page, StringComparison.Ordinal);
        Assert.True(IsHidden(page, RepositoryFields), "the repository fields stayed visible for a Freestyle project");
        Assert.True(IsDisabled(Tag(page, "input", "project-repository-url")), "the repository URL stayed enabled");
        Assert.True(IsDisabled(Tag(page, "input", "project-default-branch")), "the default branch stayed enabled");
        Assert.True(IsDisabled(Tag(page, "input", "project-pipeline-path")), "the pipeline path stayed enabled");
        Assert.True(IsDisabled(Tag(page, "select", "project-branch-discovery")), "branch discovery stayed enabled");

        // A Freestyle project has no definition source and no script box either: both are rendered (so
        // the reader can switch back to Pipeline) but hidden and disabled, which is what makes them
        // submit nothing.
        Assert.True(IsHidden(page, SourceFields), "the definition-source choice stayed visible");
        Assert.True(IsHidden(page, InlineFields), "the inline script box stayed visible");
        Assert.True(IsDisabled(Tag(page, "textarea", "project-inline-script")), "the inline script box stayed enabled");
    }

    /// <summary>
    /// The inline half the script used to have nothing to reveal: a fresh create form carries the
    /// script box, hidden and disabled, and a refused inline create switches it on.
    /// </summary>
    [Fact]
    public async Task A_refused_inline_create_enables_the_script_box_and_disables_the_repository_half()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        var page = await AuthTestHost.GetStringAsync(
            owner,
            $"{ProjectEndpoints.NewPath}?pipelineSource={ProjectSources.Inline}");

        Assert.Contains(InlineFields, page, StringComparison.Ordinal);
        Assert.False(IsHidden(page, InlineFields), "the inline script box stayed hidden");
        Assert.False(IsDisabled(Tag(page, "textarea", "project-inline-script")), "the inline script box stayed disabled");
        Assert.True(IsHidden(page, RepositoryFields), "the repository fields stayed visible for an inline pipeline");
        Assert.True(IsDisabled(Tag(page, "input", "project-repository-url")), "the repository URL stayed enabled");
        Assert.False(IsHidden(page, SourceFields), "the definition-source choice was hidden for a Pipeline project");
    }

    /// <summary>
    /// The other side of the same coin: a kind-locked edit form still renders nothing for a half the
    /// project does not own. A Freestyle project cannot grow a repository field by editing it.
    /// </summary>
    [Fact]
    public async Task A_freestyle_edit_form_renders_no_repository_or_inline_fields_at_all()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var projectId = await CreateFreestyleProjectAsync(owner, "b010-freestyle-app");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/edit");

        Assert.DoesNotContain(RepositoryFields, page, StringComparison.Ordinal);
        Assert.DoesNotContain(InlineFields, page, StringComparison.Ordinal);
        Assert.DoesNotContain("project-repository-url", page, StringComparison.Ordinal);
        Assert.DoesNotContain("project-inline-script", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The script half, mechanically: the files that manage a page's form install every listener on
    /// <c>document</c>. A plain <c>&lt;script&gt;</c> runs once per document while a Blazor navigation
    /// replaces the form's nodes, so a listener bound to a control at load time is bound to a node the
    /// reader may never touch - which is exactly how the Freestyle choice used to do nothing.
    /// <para>
    /// A browser pass would be the stronger probe and is rejected on cost, like B-007's stylesheet
    /// probe: the gate is <c>dotnet test</c> with no Node.js and no browser download
    /// (docs/development.md §1). This fact fails closed instead - a handler bound to a form, a row or
    /// a button makes the two counts disagree.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("project-kind.js", "change")]
    [InlineData("freestyle-steps.js", "submit")]
    [InlineData("freestyle-steps.js", "click")]
    [InlineData("parameter-rows.js", "submit")]
    [InlineData("parameter-rows.js", "click")]
    [InlineData("snippets.js", "click")]
    public void Every_form_script_listens_on_the_document(string file, string eventName)
    {
        var source = File.ReadAllText(Path.Combine(MilestoneHost.FindRepositoryRoot(), "src", "Server", "wwwroot", "js", file));

        Assert.Contains($"document.addEventListener(\"{eventName}\"", source, StringComparison.Ordinal);

        var total = Regex.Matches(source, "\\.addEventListener\\(").Count;
        var delegated = Regex.Matches(source, "document\\.addEventListener\\(").Count;
        Assert.True(
            total == delegated,
            $"{file} binds {total - delegated} listener(s) to something other than the document: " +
            "a replaced form would keep a dead handler.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>The opening tag of the element carrying <paramref name="id"/>, attributes and all.</summary>
    private static string Tag(string html, string element, string id)
    {
        var match = Regex.Match(html, $"<{element}[^>]*id=\"{Regex.Escape(id)}\"[^>]*>");
        Assert.True(match.Success, $"the page has no <{element} id=\"{id}\">");
        return match.Value;
    }

    /// <summary>Whether the group's own opening tag carries <c>hidden</c>.</summary>
    private static bool IsHidden(string html, string openingTag)
    {
        var match = Regex.Match(html, Regex.Escape(openingTag) + "[^>]*>");
        Assert.True(match.Success, $"the page has no group opened by {openingTag}");
        return Regex.IsMatch(match.Value, "\\shidden(=\"[^\"]*\")?[\\s>]", RegexOptions.CultureInvariant);
    }

    private static bool IsDisabled(string tag) =>
        Regex.IsMatch(tag, "\\sdisabled(=\"[^\"]*\")?[\\s>/]", RegexOptions.CultureInvariant);

    /// <summary>Creates a Freestyle project through the real create form, as the product does.</summary>
    private static async Task<Guid> CreateFreestyleProjectAsync(HttpClient client, string projectName)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            ProjectEndpoints.NewPath,
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectFields.Name] = projectName,
                [ProjectFields.Kind] = ProjectKinds.Freestyle,
            });

        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating the Freestyle project failed with {(int)response.StatusCode}.");

        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }
}
