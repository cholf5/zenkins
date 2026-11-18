using System.Net;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Organizations;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;
using Zenkins.Shared.Snippets;

namespace Zenkins.Tests.Snippets;

/// <summary>
/// Card F-035's HTTP test host: the real <c>Program</c> pipeline over a temporary SQLite database
/// (<see cref="RunTestHost"/>), an Owner, an ordinary Member and a way to read snippet rows through the
/// product's system scope. The assertions read the database, never the API under test.
/// </summary>
internal sealed class SnippetTestHost : IDisposable
{
    private const string MemberEmail = "snippet-bob@example.test";

    private SnippetTestHost(RunTestHost inner) => Inner = inner;

    public RunTestHost Inner { get; }

    public static async Task<SnippetTestHost> CreateAsync() => new(await RunTestHost.CreateAsync());

    public Task<HttpClient> SignInOwnerAsync() => Inner.Inner.SignInOwnerAsync();

    public Task<Guid> OrganizationIdAsync() => Inner.OrganizationIdAsync();

    /// <summary>An ordinary Member of the Owner's organization (card F-035 allows Members to manage snippets).</summary>
    public async Task<HttpClient> CreateMemberAsync()
    {
        var memberClient = Inner.Host.CreateClient();
        using (var registration = await AuthTestHost.RegisterAsync(memberClient, MemberEmail))
        {
            Assert.True(
                (int)registration.StatusCode is 302 or 303,
                $"registering the Member failed with {(int)registration.StatusCode}.");
        }

        using var ownerClient = await SignInOwnerAsync();
        using var invite = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = MemberEmail });
        Assert.True((int)invite.StatusCode is 302 or 303, $"inviting the Member failed with {(int)invite.StatusCode}.");

        // Resolve the Member's session once so the starter organization becomes its persisted selection.
        _ = await AuthTestHost.GetStringAsync(memberClient, "/");
        return memberClient;
    }

    /// <summary>Posts a snippet form with the antiforgery token the list page renders.</summary>
    public static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string postPath,
        IReadOnlyDictionary<string, string> fields) =>
        OrganizationTestHost.PostFormAsync(client, "/snippets", postPath, fields);

    public Task<HttpResponseMessage> CreateAsync(
        HttpClient client,
        string name,
        string kind,
        string body,
        string? description = null) =>
        PostFormAsync(
            client,
            "/snippets/create",
            new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Kind"] = kind,
                ["Body"] = body,
                ["Description"] = description ?? string.Empty,
            });

    public static Task<HttpResponseMessage> EditAsync(
        HttpClient client,
        Guid snippetId,
        string name,
        string kind,
        string body,
        string? description = null) =>
        PostFormAsync(
            client,
            $"/snippets/{snippetId}/edit",
            new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Kind"] = kind,
                ["Body"] = body,
                ["Description"] = description ?? string.Empty,
            });

    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid snippetId) =>
        PostFormAsync(client, $"/snippets/{snippetId}/delete", new Dictionary<string, string>());

    /// <summary>Creates a snippet through the real endpoint and returns its id.</summary>
    public async Task<Guid> CreateSnippetAsync(
        HttpClient client,
        string name,
        string kind,
        string body,
        string? description = null)
    {
        using var response = await CreateAsync(client, name, kind, body, description);
        Assert.True((int)response.StatusCode is 302 or 303, $"creating the snippet failed with {(int)response.StatusCode}.");

        var snippet = (await ReadAllAsync(await OrganizationIdAsync()))
            .Single(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return snippet.Id;
    }

    public Task<StepSnippet?> ReadSnippetAsync(Guid snippetId) =>
        Inner.InSystemScopeAsync(async context =>
            (await context.SystemQueryAsync<StepSnippet>(snippet => snippet.Id == snippetId)).SingleOrDefault());

    public Task<IReadOnlyList<StepSnippet>> ReadAllAsync(Guid organizationId) =>
        Inner.InSystemScopeAsync(async context =>
            (IReadOnlyList<StepSnippet>)await context.SystemQueryAsync<StepSnippet>(
                snippet => snippet.OrganizationId == organizationId));

    public Task<Project> ReadProjectAsync(Guid projectId) => Inner.ReadProjectAsync(projectId);

    /// <summary>Creates a Freestyle project through the real form (the product path), returning its id.</summary>
    public async Task<Guid> CreateFreestyleProjectAsync(HttpClient owner, string name)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Kind"] = Zenkins.Shared.Projects.ProjectKinds.Freestyle,
            });
        Assert.True((int)response.StatusCode is 302 or 303, $"creating the project failed with {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }

    /// <summary>Seeds an inline Pipeline project (no repository: its script is the server-side body).</summary>
    public async Task<Guid> SeedInlineProjectAsync(string name, string script)
    {
        var projectId = await Inner.SeedProjectAsync(name, repositoryUrl: "https://example.test/inline.git");
        await Inner.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.PipelineSource = Zenkins.Shared.Projects.ProjectSources.Inline;
            project.InlineScript = script;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        });
        return projectId;
    }

    public void Dispose() => Inner.Dispose();
}
