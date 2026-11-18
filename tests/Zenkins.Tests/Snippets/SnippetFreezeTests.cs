using System.Text.Json;
using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Snippets;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Snippets;

/// <summary>
/// Card F-035's core promise: an inserted snippet is <b>copied</b>, never referenced. The facts below insert a
/// snippet's text into a Freestyle project the way the editor does (a draft whose script is the snippet body),
/// then edit and delete the snippet and prove the project, its frozen run spec and the run page never move.
/// <para>
/// The "insertion" is the browser's job in production (js/freestyle-steps.js copies the body into a row); the
/// server sees the same draft a hand-typed body produces, which is exactly why a snippet cannot add execution
/// semantics or bypass the secret capture.
/// </para>
/// </summary>
[Trait("Category", "Snippets")]
public sealed class SnippetFreezeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task An_inserted_snippet_is_copied_and_editing_or_deleting_it_never_touches_the_project_or_a_run()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var body = "echo one\npublish --to registry\n";

        // A snippet whose body references a secret by the same syntax a hand-written step uses.
        var snippetId = await host.CreateSnippetAsync(
            owner,
            "publish",
            StepSnippetKinds.ScriptStep,
            body.Replace("publish", "publish ${secret:DEPLOY_TOKEN}\n#", StringComparison.Ordinal));

        var projectId = await host.CreateFreestyleProjectAsync(owner, "snippet-freeze");
        var insertedBody = body.Replace("publish", "publish ${secret:DEPLOY_TOKEN}\n#", StringComparison.Ordinal);
        Assert.True(await SaveStepsAsync(owner, host, projectId, "publish", insertedBody));

        var saved = await host.ReadProjectAsync(projectId);
        Assert.True(PipelineSpecJson.TryRead(saved.FreestyleSpecJson, out var spec, out var error), error);
        Assert.Equal(insertedBody, spec!.Steps[0].Script);

        // The same scanner as a typed body (card D-006): the snippet was not a bypass.
        Assert.NotNull(spec.SecretReferences);
        Assert.Contains("DEPLOY_TOKEN", spec.SecretReferences!);

        // Nothing references the snippet: no id, no name-keyed link - only the copied text.
        var frozenJson = saved.FreestyleSpecJson!;
        Assert.DoesNotContain(snippetId.ToString(), frozenJson, StringComparison.OrdinalIgnoreCase);

        // Editing the snippet after the save changes nothing the project holds or a run will execute.
        using (var edit = await SnippetTestHost.EditAsync(
                   owner, snippetId, "publish", StepSnippetKinds.ScriptStep, "echo edited-after-save"))
        {
            Assert.True((int)edit.StatusCode is 302 or 303);
        }

        var afterEdit = await host.ReadProjectAsync(projectId);
        Assert.Equal(frozenJson, afterEdit.FreestyleSpecJson);

        // A run snapshots that frozen text, and the old run keeps rendering after the snippet is gone.
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var run = await host.Inner.ReadRunAsync(runId);
        Assert.Equal(frozenJson, run.PipelineSpecJson);

        using (var delete = await SnippetTestHost.DeleteAsync(owner, snippetId))
        {
            Assert.True((int)delete.StatusCode is 302 or 303);
        }

        Assert.Null(await host.ReadSnippetAsync(snippetId));

        // Nothing dangles: the project still holds its text, the run page still renders it, and a new run
        // carries the same frozen body.
        Assert.Equal(frozenJson, (await host.ReadProjectAsync(projectId)).FreestyleSpecJson);
        var runPage = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains("publish", runPage, StringComparison.Ordinal);

        var secondRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var secondRun = await host.Inner.ReadRunAsync(secondRunId);
        Assert.True(PipelineSpecJson.TryRead(secondRun.PipelineSpecJson, out var secondSpec, out var secondError), secondError);
        Assert.Equal(insertedBody, secondSpec!.Steps[0].Script);

        // ... and the snippet library no longer lists it.
        var libraryPage = await AuthTestHost.GetStringAsync(owner, "/snippets");
        Assert.DoesNotContain(">publish<", libraryPage, StringComparison.Ordinal);
    }

    [Fact]
    public void No_worker_or_shared_code_knows_about_snippets_the_library_is_server_side_text()
    {
        // The card's hard constraint, pinned at the source level: there is no runtime consumer of a snippet.
        // The Worker never sees the entity or the service (a snippet cannot take part in execution or
        // resolution), and the Shared contract is deliberately only kinds + bounds.
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var directory in new[] { "Worker", "Shared" })
        {
            var path = Path.Combine(root, "src", directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                // The Shared contract's own file is the one allowed mention of the kinds/limits.
                var isContract = file.EndsWith("StepSnippetContract.cs", StringComparison.Ordinal);
                if (!isContract && (text.Contains("StepSnippetService", StringComparison.Ordinal)
                    || text.Contains("StepSnippetView", StringComparison.Ordinal)
                    || text.Contains("StepSnippet ", StringComparison.Ordinal)
                    || text.Contains("StepSnippet?", StringComparison.Ordinal)))
                {
                    offenders.Add(file);
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (File.Exists(Path.Combine(directory, "Zenkins.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory)!;
        }

        Assert.Fail("the repository root (Zenkins.slnx) was not found above the test output.");
        return string.Empty;
    }

    private static async Task<bool> SaveStepsAsync(
        HttpClient owner,
        SnippetTestHost host,
        Guid projectId,
        string stepName,
        string script)
    {
        var steps = JsonSerializer.Serialize(new object[]
        {
            new { name = stepName, script, envLines = string.Empty, continueOnError = false, artifactsText = string.Empty },
        });

        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/pipelines",
            $"/projects/{projectId}/freestyle/steps",
            new Dictionary<string, string> { ["StepsJson"] = steps });
        return (int)response.StatusCode is 302 or 303;
    }
}
