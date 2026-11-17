using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Git;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Git;

/// <summary>
/// Card F-022 (AC-5) on the Git-credential surface: an inline Pipeline has no repository, so it can
/// never clone and must refuse an assignment on the <b>server</b>. Until this card only the settings UI
/// hid the section, which protected nothing against a direct POST; this fact drives that POST and reads
/// the row afterwards, so the guard is real rather than a rendered absence. The same shared predicate
/// hides the UI section, which the page assertion covers too.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitCredentialInlineGuardTests
{
    private const string Script = """
        pipeline {
          name = "inline-credential",
          steps = { { name = "Only", run = { "echo", "hi" } } },
        }
        """;

    [Fact]
    public async Task An_inline_project_refuses_a_git_credential_assignment_on_the_server()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await InlinePipelineRunTests.CreateInlineProjectAsync(owner, "inline-credential", Script);
        var credentialId = await CreateCredentialAsync(owner, projectId);

        // The UI half: the whole webhook/credential section is hidden for a project with no repository.
        var settings = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        Assert.DoesNotContain("credential-assignment-none", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Git credentials", settings, StringComparison.Ordinal);

        // The server half: a direct POST that skips the UI is refused (Forbidden maps to 403) and the
        // project's column stays null.
        using var attached = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/git-credential/attach",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GitCredentialId"] = credentialId.ToString(),
            });

        Assert.Equal(HttpStatusCode.Forbidden, attached.StatusCode);
        Assert.Null((await host.ReadProjectAsync(projectId)).GitCredentialId);
    }

    [Fact]
    public async Task A_freestyle_project_also_refuses_a_git_credential_assignment_on_the_server()
    {
        // F-022 closed the same hole for a project with no repository of any source. Freestyle had the
        // hidden UI and the same missing server guard; this fact is its half of the shared predicate.
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();

        using var created = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "freestyle-credential",
                ["Kind"] = ProjectKinds.Freestyle,
            });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var projectId = Guid.Parse(
            created.Headers.Location!.OriginalString["/projects/".Length..].Split('?')[0]);
        var credentialId = await CreateCredentialAsync(owner, projectId);

        using var attached = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/git-credential/attach",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GitCredentialId"] = credentialId.ToString(),
            });

        Assert.Equal(HttpStatusCode.Forbidden, attached.StatusCode);
        Assert.Null((await host.ReadProjectAsync(projectId)).GitCredentialId);
    }

    private static async Task<Guid> CreateCredentialAsync(HttpClient owner, Guid projectId)
    {
        using var created = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "INLINE_TOKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = "zkpat_0123456789abcdef0123456789abcdef",
                ["UserName"] = "x-access-token",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)created.StatusCode is 302 or 303, $"The create answered {(int)created.StatusCode}.");

        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        using var document = JsonDocument.Parse(list);
        return Assert.Single(document.RootElement.EnumerateArray()).GetProperty("id").GetGuid();
    }
}
