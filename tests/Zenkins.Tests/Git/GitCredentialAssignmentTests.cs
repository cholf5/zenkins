using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Git;
using Zenkins.Shared.Tenancy;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Git;

/// <summary>
/// Card F-018's write path: the assignment that <c>Project.GitCredentialId</c> never had. An Owner
/// attaches one of the organization's credentials through the project settings tab's form, the change
/// is persisted with its attribution (which credential, by whom, when), detaching clears it, and both
/// operations are refused for a Member and for anything outside the active organization - with a 404,
/// so the surface is not an existence oracle.
/// <para>
/// The tests drive the same forms the page renders and then read the state through the product's own
/// surfaces (the settings page and the worker's credential fetch), so "the assignment works" means the
/// run would really clone with it - not that a column was written.
/// </para>
/// </summary>
[Trait("Category", "Git")]
public sealed class GitCredentialAssignmentTests
{
    private const string TokenValue = "zkpat_0123456789abcdef0123456789abcdef";
    private const string CredentialName = "PROJECT_TOKEN";
    private const string MemberEmail = "bob@example.test";
    private const string ForeignOwnerEmail = "carol@example.test";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_owner_attaches_and_detaches_a_credential_through_the_settings_forms()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("attach-app");
        var credentialId = await CreateCredentialAsync(host, owner, projectId);

        // 1. Before anything is attached: the column is null and the page says so, in words, instead
        //    of leaving an anonymous clone unexplained.
        var beforePage = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        Assert.Contains("credential-assignment-none", beforePage, StringComparison.Ordinal);
        Assert.Contains("No credential is attached to this project", beforePage, StringComparison.Ordinal);
        Assert.Null(await ReadAssignmentAsync(host, projectId));

        // 2. Attach through the page's form: PRG back to the tab with the notice.
        var attached = await AttachAsync(host, owner, projectId, credentialId);
        Assert.True((int)attached.StatusCode is 302 or 303, $"The attach answered {(int)attached.StatusCode}.");
        Assert.Contains(
            $"/projects/{projectId}/settings?notice=credential-attached",
            attached.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        var assignment = await ReadAssignmentAsync(host, projectId);
        Assert.NotNull(assignment);
        Assert.Equal(credentialId, assignment!.Value.CredentialId);
        Assert.NotNull(assignment.Value.AssignedAt);
        Assert.False(string.IsNullOrWhiteSpace(assignment.Value.AssignedByUserId));

        // 3. The page states the assignment with its provenance: which credential, by whom, when.
        var afterPage = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        Assert.Contains("credential-assignment-current", afterPage, StringComparison.Ordinal);
        Assert.Contains(CredentialName, afterPage, StringComparison.Ordinal);
        Assert.Contains(OrganizationTestHost.OwnerEmail, afterPage, StringComparison.Ordinal);
        Assert.Contains("UTC", afterPage, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenValue, afterPage, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-assignment-none", afterPage, StringComparison.Ordinal);

        // 4. The assignment is what the worker fetches for a run of this project - the end of the chain
        //    the card is about (no seeded column anywhere in this test).
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "attach-agent", maxConcurrency: 2);
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        await ClaimAndAcceptAsync(workerClient, workerToken, runId);
        using var material = await FetchMaterialAsync(workerClient, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, material.StatusCode);
        using (var document = JsonDocument.Parse(await material.Content.ReadAsStringAsync(CancellationToken)))
        {
            Assert.Equal(TokenValue, document.RootElement.GetProperty("value").GetString());
        }

        // 5. Re-attaching the credential that is already assigned changes nothing - not even the
        //    attribution, which records who made the assignment rather than who last pressed the button.
        var reattached = await AttachAsync(host, owner, projectId, credentialId);
        Assert.True((int)reattached.StatusCode is 302 or 303);
        var afterReattach = await ReadAssignmentAsync(host, projectId);
        Assert.Equal(assignment.Value.AssignedAt, afterReattach!.Value.AssignedAt);
        Assert.Equal(assignment.Value.AssignedByUserId, afterReattach.Value.AssignedByUserId);

        // 6. Detach clears the column and its attribution, and a second detach is still a success.
        var detached = await DetachAsync(host, owner, projectId);
        Assert.True((int)detached.StatusCode is 302 or 303, $"The detach answered {(int)detached.StatusCode}.");
        Assert.Contains(
            $"/projects/{projectId}/settings?notice=credential-detached",
            detached.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Null(await ReadAssignmentAsync(host, projectId));

        var secondDetach = await DetachAsync(host, owner, projectId);
        Assert.True((int)secondDetach.StatusCode is 302 or 303);
        Assert.Null(await ReadAssignmentAsync(host, projectId));

        var detachedPage = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        Assert.Contains("credential-assignment-none", detachedPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_foreign_missing_or_inaccessible_credential_is_a_404_that_changes_nothing()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("boundary-app");
        var credentialId = await CreateCredentialAsync(host, owner, projectId);

        // Another organization, with its own Owner and its own credential.
        var (foreignCredentialId, foreignOrganizationId) = await CreateForeignCredentialAsync(host);
        var foreignProjectId = await host.Inner.Inner.AddProjectAsync(foreignOrganizationId, "bravo-app");

        // 1. A credential that exists in another organization is a 404 - the same answer a random id gets.
        using (var foreignCredential = await AttachAsync(host, owner, projectId, foreignCredentialId))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignCredential.StatusCode);
        }

        using (var unknownCredential = await AttachAsync(host, owner, projectId, Guid.NewGuid()))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownCredential.StatusCode);
        }

        // 2. A project of another organization is a 404 as well, for attach and for detach. The
        //    antiforgery token comes from a page this Owner can read; only the action names the foreign
        //    project.
        using (var foreignProject = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"/projects/{foreignProjectId}/git-credential/attach",
            new Dictionary<string, string> { ["GitCredentialId"] = credentialId.ToString() }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignProject.StatusCode);
        }

        using (var foreignDetach = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"/projects/{foreignProjectId}/git-credential/detach",
            new Dictionary<string, string>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignDetach.StatusCode);
        }

        // 3. Nothing moved on either side.
        Assert.Null(await ReadAssignmentAsync(host, projectId));
        var foreignAssignment = await ReadAssignmentAsync(host, foreignProjectId);
        Assert.Null(foreignAssignment);

        // ...and the foreign project's settings page is a 404 for this Owner (the D-003 stance).
        using var foreignPage = await owner.GetAsync(
            $"/projects/{foreignProjectId}/settings",
            CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreignPage.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_attach_or_detach()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("member-attach-app");
        var credentialId = await CreateCredentialAsync(host, owner, projectId);
        Assert.True((int)(await AttachAsync(host, owner, projectId, credentialId)).StatusCode is 302 or 303);

        using var member = await CreateMemberAsync(host);

        using (var attach = await AttachAsync(host, member, projectId, credentialId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, attach.StatusCode);
        }

        using (var detach = await DetachAsync(host, member, projectId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, detach.StatusCode);
        }

        // The Owner's assignment survived both attempts untouched.
        var assignment = await ReadAssignmentAsync(host, projectId);
        Assert.Equal(credentialId, assignment!.Value.CredentialId);

        // And a Member never sees which credentials exist, so the page cannot show the assignment's name.
        var memberPage = await AuthTestHost.GetStringAsync(member, $"/projects/{projectId}/settings");
        Assert.DoesNotContain(CredentialName, memberPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_an_assigned_credential_is_refused_until_it_is_confirmed_and_names_the_projects()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("delete-guard-app");
        var credentialId = await CreateCredentialAsync(host, owner, projectId);
        Assert.True((int)(await AttachAsync(host, owner, projectId, credentialId)).StatusCode is 302 or 303);

        // 1. The settings page names the project that would lose its credential.
        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        Assert.Contains("Still used by delete-guard-app", page, StringComparison.Ordinal);
        Assert.Contains("name=\"ConfirmReferenced\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"ConfirmCredentialId\"", page, StringComparison.Ordinal);

        // 2. A plain delete is refused: PRG back with the reference code, the row and the assignment intact.
        using (var plainDelete = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/delete",
            new Dictionary<string, string> { ["ProjectId"] = projectId.ToString() }))
        {
            Assert.True((int)plainDelete.StatusCode is 302 or 303);
            Assert.Contains(
                $"credentialError={GitCredentialErrorCodes.CredentialInUse}",
                plainDelete.Headers.Location!.OriginalString,
                StringComparison.Ordinal);
        }

        Assert.NotNull(await FindCredentialAsync(host, credentialId));
        Assert.NotNull(await ReadAssignmentAsync(host, projectId));

        // 3. A confirmation that does not name the credential is still refused: the checkbox alone is not
        //    a confirmation of anything (P0-E2's rule).
        using (var checkboxOnly = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/delete",
            new Dictionary<string, string>
            {
                ["ProjectId"] = projectId.ToString(),
                ["ConfirmReferenced"] = "true",
            }))
        {
            Assert.True((int)checkboxOnly.StatusCode is 302 or 303);
            Assert.Contains("credentialError=credential-in-use", checkboxOnly.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        Assert.NotNull(await FindCredentialAsync(host, credentialId));

        // 4. The JSON path (no project return address) is a 409 naming the projects.
        using (var jsonPath = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/delete",
            new Dictionary<string, string>()))
        {
            Assert.Equal(HttpStatusCode.Conflict, jsonPath.StatusCode);
            var body = await jsonPath.Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains("delete-guard-app", body, StringComparison.Ordinal);
            Assert.Contains(GitCredentialErrorCodes.CredentialInUse, body, StringComparison.Ordinal);
            Assert.DoesNotContain(TokenValue, body, StringComparison.Ordinal);
        }

        // 5. The confirmed delete goes through and clears the assignment in the same transaction - the
        //    column never points at a row that is gone.
        using (var confirmed = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/delete",
            new Dictionary<string, string>
            {
                ["ProjectId"] = projectId.ToString(),
                ["ConfirmReferenced"] = "true",
                ["ConfirmCredentialId"] = credentialId.ToString(),
            }))
        {
            Assert.True((int)confirmed.StatusCode is 302 or 303);
            Assert.Contains("notice=credential-deleted", confirmed.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        Assert.Null(await FindCredentialAsync(host, credentialId));

        // The reference went away because the database's ON DELETE SET NULL said so - the service no
        // longer writes the NULL itself, which is what makes a concurrent attach unable to leave a
        // project pointing at the deleted row.
        var afterDelete = await ReadAssignmentAsync(host, projectId);
        Assert.Null(afterDelete);

        // Accepted limitation (card F-018, PM ruling): the constraint nulls the *reference* only, so the
        // attribution columns keep their last values. No page renders them (the assignment block needs a
        // credential id, and the project overview reads "Not configured"), but a direct read of the row
        // shows "an attribution with no credential". This assertion pins that semantic on purpose: adding
        // a trigger or a service-side write-back would reintroduce the read-then-write window that the
        // foreign key exists to close, so a future change must make that trade-off explicitly.
        var staleAttribution = await host.InSystemScopeAsync(async context =>
        {
            var projects = await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId);
            return (projects.Single().GitCredentialAssignedAt, projects.Single().GitCredentialAssignedByUserId);
        });
        Assert.NotNull(staleAttribution.GitCredentialAssignedAt);
        Assert.False(string.IsNullOrWhiteSpace(staleAttribution.GitCredentialAssignedByUserId));
    }

    [Fact]
    public async Task The_database_refuses_a_project_that_points_at_a_missing_credential()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync("fk-app");

        // The constraint, not a pre-flight query: a write naming a credential row that does not exist is
        // refused by the database itself. That is what closes the delete/attach race - a project can never
        // end up pointing at a deleted credential and degrade to a silent anonymous clone.
        await Assert.ThrowsAsync<DbUpdateException>(() => host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.GitCredentialId = Guid.NewGuid();
            await context.SaveChangesAsync();
            return true;
        }));

        Assert.Null(await ReadAssignmentAsync(host, projectId));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Task<HttpResponseMessage> AttachAsync(
        RunTestHost host,
        HttpClient client,
        Guid projectId,
        Guid credentialId) =>
        OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/git-credential/attach",
            new Dictionary<string, string> { ["GitCredentialId"] = credentialId.ToString() });

    private static Task<HttpResponseMessage> DetachAsync(
        RunTestHost host,
        HttpClient client,
        Guid projectId) =>
        OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/git-credential/detach",
            new Dictionary<string, string>());

    /// <summary>Creates a credential through the settings tab's own form and returns its id.</summary>
    private static async Task<Guid> CreateCredentialAsync(
        RunTestHost host,
        HttpClient owner,
        Guid projectId,
        string name = CredentialName)
    {
        using var created = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Kind"] = "HttpsToken",
                ["Value"] = TokenValue,
                ["UserName"] = "x-access-token",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)created.StatusCode is 302 or 303, $"The create answered {(int)created.StatusCode}.");

        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        using var document = JsonDocument.Parse(list);
        return Assert.Single(document.RootElement.EnumerateArray()).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// A second organization with its own Owner and one credential; returns the credential's id and the
    /// organization's id. The credential's ciphertext is produced by the product's own protector, so it
    /// is a real row of another tenant - the thing the attach surface must refuse without confirming that
    /// it exists.
    /// </summary>
    private static async Task<(Guid CredentialId, Guid OrganizationId)> CreateForeignCredentialAsync(RunTestHost host)
    {
        using var foreignOwner = await host.Inner.Inner.RegisterUserAsync(ForeignOwnerEmail);
        var organizationId = await host.Inner.Inner.AddOrganizationAsync(
            "Bravo",
            (ForeignOwnerEmail, OrganizationRole.Owner));

        var ownerUserId = await host.InSystemScopeAsync(async context =>
            (await context.SystemQueryAsync<User>(_ => true)).Single(user => user.Email == ForeignOwnerEmail).Id);

        var protector = await host.Host.InScopeAsync(
            services => Task.FromResult(services.GetRequiredService<GitCredentialProtector>()));

        var credentialId = await host.InSystemScopeAsync(async context =>
        {
            var credential = new GitCredential
            {
                OrganizationId = organizationId,
                Name = "FOREIGN_TOKEN",
                Kind = "HttpsToken",
                Ciphertext = protector.Protect(TokenValue),
                UserName = "x-access-token",
                CreatedByUserId = ownerUserId,
            };
            context.GitCredentials.Add(credential);
            await context.SaveChangesAsync();
            return credential.Id;
        });

        return (credentialId, organizationId);
    }

    private static async Task<(Guid CredentialId, DateTimeOffset? AssignedAt, string? AssignedByUserId)?> ReadAssignmentAsync(
        RunTestHost host,
        Guid projectId) =>
        await host.InSystemScopeAsync(async context =>
        {
            var projects = await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId);
            var project = projects.SingleOrDefault();
            return project?.GitCredentialId is { } credentialId
                ? (credentialId, project.GitCredentialAssignedAt, project.GitCredentialAssignedByUserId)
                : ((Guid, DateTimeOffset?, string?)?)null;
        });

    private static Task<GitCredential?> FindCredentialAsync(RunTestHost host, Guid credentialId) =>
        host.InSystemScopeAsync(async context =>
        {
            var matches = await context.SystemQueryAsync<GitCredential>(credential => credential.Id == credentialId);
            return matches.Count == 0 ? null : matches[0];
        });

    /// <summary>Registers bob and has the starter organization's Owner invite him.</summary>
    private static async Task<HttpClient> CreateMemberAsync(RunTestHost host)
    {
        var member = await host.Inner.Inner.RegisterUserAsync(MemberEmail);
        try
        {
            using var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
            using var invite = await OrganizationTestHost.PostFormAsync(
                owner,
                "/organizations/members",
                "/organizations/members/invite",
                new Dictionary<string, string> { ["Email"] = MemberEmail });
            Assert.True((int)invite.StatusCode is 302 or 303, "Inviting the Member failed.");

            _ = await AuthTestHost.GetStringAsync(member, "/");
            return member;
        }
        catch
        {
            member.Dispose();
            throw;
        }
    }

    private static async Task ClaimAndAcceptAsync(HttpClient client, string workerToken, Guid runId)
    {
        using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        using var accepted = await RunTestHost.AcceptAsync(client, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    private static Task<HttpResponseMessage> FetchMaterialAsync(
        HttpClient client,
        string workerToken,
        Guid runId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workers/runs/{runId}/git-credential");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);
        return client.SendAsync(request, CancellationToken);
    }
}
