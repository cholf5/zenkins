using Xunit;
using Zenkins.Shared.Snippets;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Snippets;

/// <summary>
/// Card F-035's editor affordances: the Freestyle steps editor offers <c>script-step</c> snippets (a click
/// appends a fully formed row), the inline pipeline editor offers <c>lua-fragment</c> snippets at the caret, and
/// a repository pipeline's Pipelines tab offers a clipboard copy - the product never rewrites the repository.
/// The bodies travel in one <c>#snippets-data</c> JSON; these facts read the rendered page and the served JS,
/// which is what a browser actually gets.
/// </summary>
[Trait("Category", "Snippets")]
public sealed class SnippetAffordanceTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_freestyle_editor_offers_script_step_snippets_and_carries_their_bodies()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var scriptStep = await host.CreateSnippetAsync(
            owner, "publish-step", StepSnippetKinds.ScriptStep, "echo published\n");
        var fragment = await host.CreateSnippetAsync(
            owner, "fragment-only", StepSnippetKinds.LuaFragment, "pipeline { name = \"x\" }");
        var projectId = await host.CreateFreestyleProjectAsync(owner, "snippet-affordance");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");

        Assert.Contains("id=\"freestyle-snippets\"", page, StringComparison.Ordinal);
        var picker = Between(page, "id=\"freestyle-snippet-select\"", "</select>");
        Assert.Contains(scriptStep.ToString(), picker, StringComparison.Ordinal);
        // A Lua fragment is offered where it belongs (the pipeline editor), not to a step list.
        Assert.DoesNotContain(fragment.ToString(), picker, StringComparison.Ordinal);

        // The bodies travel once as JSON - the insert is a client-side copy, never a server fetch per run.
        var data = Between(page, "id=\"snippets-data\"", "</script>");
        Assert.Contains("scriptStep", data, StringComparison.Ordinal);
        Assert.Contains("publish-step", data, StringComparison.Ordinal);
        Assert.Contains("echo published", data, StringComparison.Ordinal);

        var script = await AuthTestHost.GetStringAsync(owner, "/js/freestyle-steps.js");
        Assert.Contains("insertSnippetStep", script, StringComparison.Ordinal);
        Assert.Contains("insert-snippet", script, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_inline_editor_inserts_a_lua_fragment_at_the_caret()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var fragment = await host.CreateSnippetAsync(
            owner, "stage-snippet", StepSnippetKinds.LuaFragment, "  stages = {},\n");
        var projectId = await host.SeedInlineProjectAsync(
            "snippet-inline",
            "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } } }");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");

        Assert.Contains("id=\"inline-snippets\"", page, StringComparison.Ordinal);
        var picker = Between(page, "id=\"inline-snippet-select\"", "</select>");
        Assert.Contains(fragment.ToString(), picker, StringComparison.Ordinal);
        Assert.Contains("data-snippet-insert", page, StringComparison.Ordinal);
        Assert.Contains("data-snippet-target=\"#inline-script\"", page, StringComparison.Ordinal);

        var script = await AuthTestHost.GetStringAsync(owner, "/js/snippets.js");
        Assert.Contains("insertAtCaret", script, StringComparison.Ordinal);
        Assert.Contains("selectionStart", script, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_repository_pipeline_offers_a_copy_and_never_rewrites_the_repository()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var fragment = await host.CreateSnippetAsync(
            owner, "deploy-fragment", StepSnippetKinds.LuaFragment, "  deploy(),\n");
        var projectId = await host.Inner.SeedProjectAsync(
            "snippet-repository",
            repositoryUrl: "https://example.test/repo.git");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");

        Assert.Contains("id=\"pipelines-snippets\"", page, StringComparison.Ordinal);
        Assert.Contains($"data-snippet-id=\"{fragment}\"", page, StringComparison.Ordinal);
        Assert.Contains("data-snippet-copy=\"true\"", page, StringComparison.Ordinal);
        Assert.Contains("never rewrites your repository", page, StringComparison.Ordinal);
        // The file remains the source of truth: the page offers text, not an edit form for it.
        Assert.DoesNotContain("inline-script", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_project_without_snippets_offers_no_picker()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var projectId = await host.CreateFreestyleProjectAsync(owner, "snippet-empty");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");

        Assert.DoesNotContain("id=\"freestyle-snippets\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"snippets-data\"", page, StringComparison.Ordinal);
    }

    /// <summary>The slice from a marker through the next closing tag (a small, literal page read).</summary>
    private static string Between(string page, string startMarker, string endMarker)
    {
        var start = page.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the page does not contain '{startMarker}'.");
        var end = page.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"the page does not close '{startMarker}' with '{endMarker}'.");
        return page[start..end];
    }
}
