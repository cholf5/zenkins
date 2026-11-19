using Xunit;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Snippets;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// Card B-011: the two row editors (Freestyle steps, project parameters) clone each new row from a
/// <c>&lt;template&gt;</c>, and after the Blazor circuit activates they copy it from nowhere.
/// <para>
/// The defect: a template's children live in <c>template.content</c> only when the HTML parser put them
/// there. Blazor's renderer inserts a component's children with <c>appendChild</c>, which lands them
/// directly on the template element - so as soon as the interactive circuit replaced the prerendered
/// page, <c>template.content</c> was empty while the row sat in <c>template.childNodes</c>. Both scripts
/// read <c>template.content.firstElementChild</c> and nothing else, so "Add step" and "Add parameter"
/// threw <c>TypeError: Cannot read properties of null (reading 'cloneNode')</c> and did nothing at all -
/// no row, no message, no console-free way for a reader to tell why.
/// </para>
/// <para>
/// These facts pin both halves of the repair: the server still renders the row inside the template (so
/// the browser's own parse fills <c>content</c> before the circuit arrives), and the scripts resolve the
/// row from <c>content</c> or from the element, which is the only place it can be after activation. A
/// browser pass is what proved the behaviour (B-011's Verification); these facts are the fail-closed net
/// the gate can carry - a script that goes back to reading <c>content</c> alone turns them red.
/// </para>
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectUiRowTemplateTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_steps_editor_renders_its_row_template_and_the_script_reads_the_row_wherever_it_is()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var projectId = await host.CreateFreestyleProjectAsync(owner, "row-template-steps");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");

        // The server puts the row inside the template, so a browser's parse of this HTML fills content.
        Assert.Contains(
            "<template id=\"freestyle-step-template\"><li data-freestyle-step",
            page,
            StringComparison.Ordinal);

        var script = ReadScript("freestyle-steps.js");

        // ...and the row is read from either place, because after the circuit activates it is only a child.
        Assert.Contains("template.content.firstElementChild || template.firstElementChild", script, StringComparison.Ordinal);

        // No path clones content's first child directly: that is the null dereference this card fixed.
        Assert.DoesNotContain(".content.firstElementChild.cloneNode", script, StringComparison.Ordinal);

        // Both add paths (an empty row, and a row filled from a snippet) go through the resolution.
        Assert.Equal(1, CountOf(script, "template.content.firstElementChild"));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_parameters_editor_renders_its_row_template_and_the_script_reads_the_row_wherever_it_is()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var projectId = await host.CreateFreestyleProjectAsync(owner, "row-template-params");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/parameters");

        Assert.Contains(
            "<template id=\"parameter-row-template\"><li data-parameter-row",
            page,
            StringComparison.Ordinal);

        var script = ReadScript("parameter-rows.js");

        Assert.Contains("template.content.firstElementChild || template.firstElementChild", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".content.firstElementChild.cloneNode", script, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(script, "template.content.firstElementChild"));
    }

    private static string ReadScript(string file) =>
        File.ReadAllText(Path.Combine(MilestoneHost.FindRepositoryRoot(), "src", "Server", "wwwroot", "js", file));

    private static int CountOf(string source, string needle)
    {
        var count = 0;
        var index = source.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
