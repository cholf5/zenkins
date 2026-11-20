using System.Text.Json;
using Xunit;
using Zenkins.Server.Projects;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Snippets;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// The steps editor's list + inspector layout ([data-split-editor], js/split-list.js): the left column is one
/// line per step, the right one is the selected step's fields.
/// <para>
/// The layout rests on one property that no compiler checks: the fields of a step are rendered once, inside
/// their row, and the pane is filled by <em>moving</em> that block. A re-render would be the obvious
/// implementation and would silently save every collapsed step as empty, because js/freestyle-steps.js
/// serializes the DOM rather than a model - so these facts pin the shape (one block per row, one pane per
/// list, a pointer the serializer reads through) and leave the pixels to a browser pass.
/// </para>
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectUiSplitEditorTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_steps_editor_renders_the_list_and_the_inspector_pane()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var projectId = await host.CreateFreestyleProjectAsync(owner, "split-editor");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");

        // One editor per list, each with its own pane (four lists share one form, so the ids must not collide).
        Assert.Contains("data-split-editor=\"steps\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"steps-step-pane\"", page, StringComparison.Ordinal);
        Assert.Contains("data-split-editor=\"always\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"always-step-pane\"", page, StringComparison.Ordinal);

        // The words the summary line is drawn with travel as data, so no string of either language lives in
        // the script, and the empty state is the server's copy.
        Assert.Contains("data-split-labels=", page, StringComparison.Ordinal);
        Assert.Contains("data-split-empty", page, StringComparison.Ordinal);

        // The row template carries the fields too - "add step" must produce a step whose fields exist even
        // before it is selected, because that is what the serializer reads.
        var templateStart = page.IndexOf("id=\"freestyle-step-template\"", StringComparison.Ordinal);
        Assert.True(templateStart > 0, "the editor's row template is not on the page.");
        var template = page[templateStart..];
        var templateEnd = template.IndexOf("</template>", StringComparison.Ordinal);
        Assert.True(templateEnd > 0, "the row template is not closed.");
        template = template[..templateEnd];
        Assert.Contains("data-split-detail", template, StringComparison.Ordinal);
        Assert.Contains("data-freestyle-field=\"script\"", template, StringComparison.Ordinal);
        Assert.Contains("data-freestyle-field=\"runs-on\"", template, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_serializer_reads_through_the_moved_block_and_the_frame_never_re_renders_it()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var projectId = await host.CreateFreestyleProjectAsync(owner, "split-serializer");

        // The page loads both scripts, frame first: the serializer is the one that reads the DOM.
        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");
        var frameAt = page.IndexOf("js/split-list.js", StringComparison.Ordinal);
        var stepsAt = page.IndexOf("js/freestyle-steps.js", StringComparison.Ordinal);
        Assert.True(frameAt > 0 && stepsAt > frameAt, "js/split-list.js must load before js/freestyle-steps.js.");

        var steps = await AuthTestHost.GetStringAsync(owner, "/js/freestyle-steps.js");
        var frame = await AuthTestHost.GetStringAsync(owner, "/js/split-list.js");

        // The fields leave the row when the row is selected; the row keeps a pointer, and the serializer
        // looks the field up there. Without this line a selected step saves everything as empty.
        Assert.Contains("row._splitDetail || row", steps, StringComparison.Ordinal);

        // The frame moves the block (appendChild) and never clones or rebuilds it: a cloned textarea is an
        // empty one, which is the whole failure this layout has to avoid.
        Assert.Contains("data-split-detail", frame, StringComparison.Ordinal);
        Assert.Contains("appendChild(detail)", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("cloneNode", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", frame, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_parameters_editor_uses_the_same_frame_and_its_serializer_reads_through_it()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var projectId = await host.CreateFreestyleProjectAsync(owner, "split-params");

        // Two saved parameters, posted the way the editor's own form posts them.
        var drafts = JsonSerializer.Serialize(
            new[]
            {
                new ProjectParameterDraft("NUGET_TOKEN", ProjectParameterTypes.String, "t0ken", null, "publishes the package"),
                new ProjectParameterDraft("CHANNEL", ProjectParameterTypes.Choice, "release", "debug, release", null),
            },
            ProjectParameters.DraftsOptions);
        using var saved = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = drafts });
        Assert.True(
            (int)saved.StatusCode is 302 or 303,
            $"saving the parameters answered {(int)saved.StatusCode}: {saved.Headers.Location}.");

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/parameters");

        // The same frame the steps list uses: one editor, one pane, the summary words as data, an empty state.
        Assert.Contains("data-split-editor=\"parameters\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"parameters-pane\"", page, StringComparison.Ordinal);
        Assert.Contains("data-split-labels=", page, StringComparison.Ordinal);
        Assert.Contains("data-split-empty", page, StringComparison.Ordinal);

        // One line per saved parameter, each carrying its own block of fields - the frame moves the block, so
        // the fields of an unselected row have to be inside that row already.
        var listStart = page.IndexOf("data-parameter-rows-list", StringComparison.Ordinal);
        Assert.True(listStart > 0, "the parameters editor's row list is not on the page.");
        var list = page[listStart..];
        var listEnd = list.IndexOf("</ol>", StringComparison.Ordinal);
        Assert.True(listEnd > 0, "the row list is not closed.");
        list = list[..listEnd];
        Assert.Equal(2, CountOf(list, "data-parameter-row data-split-step"));
        Assert.Equal(2, CountOf(list, "data-split-detail-slot"));
        Assert.Contains(">NUGET_TOKEN<", list, StringComparison.Ordinal);

        // The row template holds the same block, so a row added in the browser has real fields to serialize.
        var templateStart = page.IndexOf("id=\"parameter-row-template\"", StringComparison.Ordinal);
        Assert.True(templateStart > 0, "the parameters editor's row template is not on the page.");
        var template = page[templateStart..];
        var templateEnd = template.IndexOf("</template>", StringComparison.Ordinal);
        Assert.True(templateEnd > 0, "the row template is not closed.");
        template = template[..templateEnd];
        Assert.Contains("data-split-detail", template, StringComparison.Ordinal);
        Assert.Contains("data-parameter-field=\"name\"", template, StringComparison.Ordinal);
        Assert.Contains("data-parameter-field=\"choices\"", template, StringComparison.Ordinal);

        // The script reads a row's fields through the pointer and installs the row's summary; without both the
        // selected parameter saves empty and every line stays blank.
        var script = await AuthTestHost.GetStringAsync(owner, "/js/parameter-rows.js");
        Assert.Contains("row._splitDetail || row", script, StringComparison.Ordinal);
        Assert.Contains("summarize(EDITOR_KEY, parameterSummary)", script, StringComparison.Ordinal);
        Assert.Contains("template.content.firstElementChild || template.firstElementChild", script, StringComparison.Ordinal);

        var frameAt = page.IndexOf("js/split-list.js", StringComparison.Ordinal);
        var rowsAt = page.IndexOf("js/parameter-rows.js", StringComparison.Ordinal);
        Assert.True(frameAt > 0 && rowsAt > frameAt, "js/split-list.js must load before js/parameter-rows.js.");
    }

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
