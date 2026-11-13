using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// Card D-003's unchanged-delete guarantees: deleting a project still requires the existing
/// confirmation route (<c>/projects/&#123;id&#125;/delete/confirm</c>, linked from every tab's
/// header actions), a foreign id is still a 404 there, and the delete still cascades exactly as it
/// did before the tabs - the project's runs and webhooks go with it, while the organization-scoped
/// credential store survives (a credential belongs to the organization, not to one project).
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectUiDeleteTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_confirmation_route_still_works_and_the_delete_still_cascades()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var projectId = host.ProjectId;
        var confirmPath = $"/projects/{projectId}/delete/confirm";

        // The confirmation page is the confirmation: it renders the POST the Owner submits.
        var confirmPage = await AuthTestHost.GetStringAsync(owner, confirmPath);
        Assert.Contains($"action=\"/projects/{projectId}/delete\"", confirmPage, StringComparison.Ordinal);
        Assert.Contains(WebhooksTestHost.ProjectName, confirmPage, StringComparison.Ordinal);

        // Dependent rows for the cascade assertions: a webhook (through the product's seeder) and a
        // queued run (a project delete removes the runs that belong to it).
        await host.SeedWebhookAsync("cascade-hook");
        var runId = await host.InSystemScopeAsync(async context =>
        {
            var run = new BuildRun
            {
                OrganizationId = host.OrganizationId,
                ProjectId = projectId,
                TriggerType = BuildRunTriggerType.Manual,
                TriggerUserId = host.OwnerUserId,
                RequiredTags = string.Empty,
                GitBranch = WebhooksTestHost.DefaultBranch,
                PipelinePath = "pipeline.lua",
            };
            context.BuildRuns.Add(run);
            await context.SaveChangesAsync();
            return run.Id;
        });

        // ...and an organization-scoped credential, which is NOT the project's row to delete.
        var credentialId = await host.InSystemScopeAsync(async context =>
        {
            var credential = new GitCredential
            {
                OrganizationId = host.OrganizationId,
                Name = "CASCADE_TOKEN",
                Kind = "HttpsToken",
                Ciphertext = "CfDJ8-fixture-cascade-ciphertext",
                CreatedByUserId = host.OwnerUserId,
            };
            context.GitCredentials.Add(credential);
            await context.SaveChangesAsync();
            return credential.Id;
        });

        Assert.Single(await host.ReadRunsAsync());
        Assert.Single(await host.ReadWebhooksAsync());

        var deleted = await OrganizationTestHost.PostFormAsync(
            owner,
            confirmPath,
            $"/projects/{projectId}/delete",
            new Dictionary<string, string>());
        MembershipAuthorizationProbes.AssertRedirect(deleted, "/projects?notice=deleted");

        // The project, its runs and its webhooks are gone.
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Empty(await host.ReadWebhooksAsync());
        var rows = await host.InSystemScopeAsync(async context =>
        {
            var projects = await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId);
            return (Projects: projects, Runs: await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId));
        });
        Assert.Empty(rows.Projects);
        Assert.Empty(rows.Runs);

        // The credential belongs to the organization: the delete cascades the project's own rows,
        // not the store another project may still reference.
        var credential = await host.InSystemScopeAsync(async context =>
            (await context.SystemQueryAsync<GitCredential>(candidate => candidate.Id == credentialId)).Single());
        Assert.Equal("CASCADE_TOKEN", credential.Name);
    }

    [Fact]
    public async Task The_confirmation_route_is_a_404_for_a_foreign_project()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var projectId = host.ProjectId;

        await host.CreateForeignOrganizationAsync();
        using var foreign = await host.SignInMemberAsync(WebhooksTestHost.ForeignOwnerEmail);

        using var response = await foreign.GetAsync($"/projects/{projectId}/delete/confirm", CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.DoesNotContain(WebhooksTestHost.ProjectName, body, StringComparison.Ordinal);
    }
}
