using System.Net;
using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Snippets;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Snippets;

/// <summary>
/// Card F-035's snippet library through the real endpoints: create/edit/delete, the field rules, the unique
/// name, and what the page promises (the body, the resulting step preview, the secret references it carries,
/// and the freeze-on-insert rule). Member and Owner may both manage snippets.
/// </summary>
[Trait("Category", "Snippets")]
public sealed class SnippetEndpointsTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_snippet_is_created_listed_and_previewed_with_its_secret_references()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var body = "deploy()\necho ${secret:DEPLOY_TOKEN}\n";

        var id = await host.CreateSnippetAsync(
            owner,
            "deploy-to-registry",
            StepSnippetKinds.ScriptStep,
            body,
            description: "Push the image and tell the registry.");

        var row = await host.ReadSnippetAsync(id);
        Assert.NotNull(row);
        Assert.Equal("deploy-to-registry", row!.Name);
        Assert.Equal(StepSnippetKinds.ScriptStep, row.Kind);
        Assert.Equal(body, row.Body);
        Assert.Equal("Push the image and tell the registry.", row.Description);
        Assert.False(string.IsNullOrEmpty(row.CreatedByUserId));

        var page = await AuthTestHost.GetStringAsync(owner, "/snippets");
        Assert.Contains($"id=\"snippet-body-{id}\"", page, StringComparison.Ordinal);
        Assert.Contains($"id=\"snippet-preview-{id}\"", page, StringComparison.Ordinal);
        Assert.Contains("Resulting step", page, StringComparison.Ordinal);
        // The preview names what the inserted step will record, read by the scanner D-006 already uses. The
        // assertion is scoped to the preview block: the body above and the edit form below both carry the
        // token too, so a page-wide containment would pass even if the preview listed no reference at all.
        var previewStart = page.IndexOf($"id=\"snippet-preview-{id}\"", StringComparison.Ordinal);
        var previewEnd = page.IndexOf("<details", previewStart, StringComparison.Ordinal);
        Assert.True(previewStart >= 0 && previewEnd > previewStart);
        var preview = page[previewStart..previewEnd];
        Assert.Contains("Secret references:", preview, StringComparison.Ordinal);
        Assert.Contains("DEPLOY_TOKEN", preview, StringComparison.Ordinal);
        // The rule the card is built on.
        Assert.Contains("never changes a saved project", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_duplicate_name_is_a_field_error_and_nothing_is_written()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        await host.CreateSnippetAsync(owner, "same-name", StepSnippetKinds.ScriptStep, "echo one");

        using var duplicate = await host.CreateAsync(owner, "same-name", StepSnippetKinds.ScriptStep, "echo two");
        Assert.True((int)duplicate.StatusCode is 302 or 303);
        Assert.Contains("error=name-duplicate", duplicate.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var rows = await host.ReadAllAsync(await host.OrganizationIdAsync());
        Assert.Single(rows);
        Assert.Equal("echo one", rows[0].Body);
    }

    [Theory(Timeout = 2 * 60 * 1000)]
    [InlineData("", StepSnippetKinds.ScriptStep, "echo x", "name-required")]
    [InlineData("n", "not-a-kind", "echo x", "kind-invalid")]
    [InlineData("n", StepSnippetKinds.ScriptStep, "   ", "body-required")]
    public async Task An_invalid_form_redirects_with_its_stable_code(
        string name,
        string kind,
        string body,
        string expectedCode)
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();

        using var response = await host.CreateAsync(owner, name, kind, body);

        Assert.True((int)response.StatusCode is 302 or 303);
        Assert.Contains($"error={expectedCode}", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Empty(await host.ReadAllAsync(await host.OrganizationIdAsync()));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_body_longer_than_the_target_it_feeds_is_refused()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();

        // A script step is bounded like a Freestyle script (32 KiB), a Lua fragment like an inline script
        // (64 KiB): one over each limit, and the code is the same field code.
        using (var scriptStep = await host.CreateAsync(
                   owner,
                   "too-long-step",
                   StepSnippetKinds.ScriptStep,
                   new string('x', PipelineSpecSchema.MaxScriptChars + 1)))
        {
            Assert.Contains("error=body-too-long", scriptStep.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        using (var fragment = await host.CreateAsync(
                   owner,
                   "too-long-fragment",
                   StepSnippetKinds.LuaFragment,
                   new string('x', PipelineScriptLimits.MaxScriptBytes + 1)))
        {
            Assert.Contains("error=body-too-long", fragment.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        Assert.Empty(await host.ReadAllAsync(await host.OrganizationIdAsync()));

        // ... while a fragment at the inline cap is accepted (the bound belongs to the kind, not to the page).
        var accepted = await host.CreateSnippetAsync(
            owner,
            "at-the-cap",
            StepSnippetKinds.LuaFragment,
            new string('y', PipelineScriptLimits.MaxScriptBytes));
        Assert.NotNull(await host.ReadSnippetAsync(accepted));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task An_ordinary_member_can_create_edit_and_delete_snippets()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var member = await host.CreateMemberAsync();

        var id = await host.CreateSnippetAsync(member, "member-snippet", StepSnippetKinds.ScriptStep, "echo one");

        using (var edit = await SnippetTestHost.EditAsync(member, id, "member-snippet", StepSnippetKinds.ScriptStep, "echo two"))
        {
            Assert.True((int)edit.StatusCode is 302 or 303);
        }

        Assert.Equal("echo two", (await host.ReadSnippetAsync(id))!.Body);

        using (var delete = await SnippetTestHost.DeleteAsync(member, id))
        {
            Assert.True((int)delete.StatusCode is 302 or 303);
        }

        Assert.Null(await host.ReadSnippetAsync(id));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Edit_keeps_the_name_unique_and_attributes_the_change()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await SnippetTestHost.CreateAsync();
        var owner = await host.SignInOwnerAsync();
        var first = await host.CreateSnippetAsync(owner, "first", StepSnippetKinds.ScriptStep, "echo first");
        var second = await host.CreateSnippetAsync(owner, "second", StepSnippetKinds.ScriptStep, "echo second");

        using (var conflict = await SnippetTestHost.EditAsync(owner, second, "first", StepSnippetKinds.ScriptStep, "echo x"))
        {
            Assert.Contains("error=name-duplicate", conflict.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        using (var rename = await SnippetTestHost.EditAsync(
                   owner,
                   first,
                   "first-renamed",
                   StepSnippetKinds.LuaFragment,
                   "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } } }"))
        {
            Assert.Contains("notice=updated", rename.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        var row = await host.ReadSnippetAsync(first);
        Assert.Equal("first-renamed", row!.Name);
        Assert.Equal(StepSnippetKinds.LuaFragment, row.Kind);
        Assert.False(string.IsNullOrEmpty(row.UpdatedByUserId));
        Assert.Equal(row.CreatedByUserId, row.UpdatedByUserId);
    }
}
