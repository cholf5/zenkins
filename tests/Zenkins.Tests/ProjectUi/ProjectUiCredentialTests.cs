using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// TC-002 (card D-003): a private repository becomes drivable from the browser. The Owner creates,
/// rotates and deletes a git credential through the project's settings tab forms (F-013's endpoints
/// with the tab's return address), the list answers name, kind and timestamps and never the
/// material - in the page, in the JSON list, or anywhere else - and the Member's writes are 403 and
/// change nothing. After a rotation the material a worker fetches for the next run is the new one,
/// which is exactly the "the second run still authenticates" property at the fetch step every git
/// clone passes through.
/// <para>
/// The project's <c>GitCredentialId</c> is seeded directly: no product surface assigns a credential
/// to a project in v0.9 (F-013 left the reference column and the settings card manages the store),
/// the same seam F-013's own endpoint tests use.
/// </para>
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectUiCredentialTests
{
    private const string TokenValue = "zkpat_0123456789abcdef0123456789abcdef";
    private const string RotatedValue = "zkpat_fedcba9876543210fedcba9876543210";
    private const string CredentialName = "PIPELINE_TOKEN";
    private const string MemberEmail = "bob@example.test";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_settings_tab_lifecycle_never_returns_a_value_and_member_writes_are_403()
    {
        var host = await RunTestHost.CreateAsync();
        using var ownerScope = host;
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("credential-app");
        var settingsPath = $"/projects/{projectId}/settings";

        // 1. Create through the settings tab: PRG back to the tab with the created notice.
        var created = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            "/git-credentials/create",
            new Dictionary<string, string>
            {
                ["Name"] = CredentialName,
                ["Kind"] = "HttpsToken",
                ["Value"] = TokenValue,
                ["UserName"] = "x-access-token",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)created.StatusCode is 302 or 303, $"The create answered {(int)created.StatusCode}.");
        Assert.Contains($"/projects/{projectId}/settings?notice=credential-created", created.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // The tab lists the credential's metadata - name, kind, "Never" rotated - and no value.
        var settings = await AuthTestHost.GetStringAsync(owner, settingsPath);
        Assert.Contains(CredentialName, settings, StringComparison.Ordinal);
        Assert.Contains("HttpsToken", settings, StringComparison.Ordinal);
        Assert.Contains("Never", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenValue, settings, StringComparison.Ordinal);

        // 2. The list endpoint: metadata only. Every item carries exactly the six safe fields.
        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        Assert.DoesNotContain(TokenValue, list, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(TokenValue), list, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(list))
        {
            var item = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(
                new[] { "createdAt", "id", "kind", "name", "rotatedAt", "updatedAt" },
                item.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
            Assert.Equal(CredentialName, item.GetProperty("name").GetString());
            Assert.Equal("HttpsToken", item.GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("rotatedAt").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, item.GetProperty("createdAt").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, item.GetProperty("updatedAt").ValueKind);
        }

        // 3. The Member's forged writes: 403 each, and the list is untouched afterwards.
        using var member = await CreateMemberAsync(host);
        var memberCreate = await OrganizationTestHost.PostFormAsync(
            member,
            settingsPath,
            "/git-credentials/create",
            new Dictionary<string, string>
            {
                ["Name"] = "MEMBER_TOKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = TokenValue,
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.Equal(HttpStatusCode.Forbidden, memberCreate.StatusCode);

        var credentialId = await ReadSingleCredentialIdAsync(owner, projectId);

        var memberRotate = await OrganizationTestHost.PostFormAsync(
            member,
            settingsPath,
            $"/git-credentials/{credentialId}/rotate",
            new Dictionary<string, string>
            {
                ["Kind"] = "HttpsToken",
                ["Value"] = RotatedValue,
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.Equal(HttpStatusCode.Forbidden, memberRotate.StatusCode);

        var memberDelete = await OrganizationTestHost.PostFormAsync(
            member,
            settingsPath,
            $"/git-credentials/{credentialId}/delete",
            new Dictionary<string, string> { ["ProjectId"] = projectId.ToString() });
        Assert.Equal(HttpStatusCode.Forbidden, memberDelete.StatusCode);

        // A Member may not even read the list (they must not learn which names exist).
        using var memberList = await member.GetAsync($"/projects/{projectId}/git-credentials", CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, memberList.StatusCode);

        Assert.Equal(CredentialName, await ReadSingleRotatedStateAsync(owner, projectId, expectRotated: false));

        // 4. Rotate through the settings tab: the previous value is retired, the new one never shows.
        var rotated = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"/git-credentials/{credentialId}/rotate",
            new Dictionary<string, string>
            {
                ["Kind"] = "HttpsToken",
                ["Value"] = RotatedValue,
                ["UserName"] = "git",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)rotated.StatusCode is 302 or 303, $"The rotate answered {(int)rotated.StatusCode}.");
        Assert.Contains("notice=credential-rotated", rotated.Headers.Location!.OriginalString, StringComparison.Ordinal);

        Assert.Equal(CredentialName, await ReadSingleRotatedStateAsync(owner, projectId, expectRotated: true));
        var rotatedSettings = await AuthTestHost.GetStringAsync(owner, settingsPath);
        Assert.DoesNotContain(TokenValue, rotatedSettings, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedValue, rotatedSettings, StringComparison.Ordinal);

        // 5. Delete through the settings tab, and the list is empty afterwards.
        var deleted = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"/git-credentials/{credentialId}/delete",
            new Dictionary<string, string> { ["ProjectId"] = projectId.ToString() });
        Assert.True((int)deleted.StatusCode is 302 or 303, $"The delete answered {(int)deleted.StatusCode}.");
        Assert.Contains("notice=credential-deleted", deleted.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var emptyList = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        Assert.Equal("[]", emptyList.Trim());
    }

    [Fact]
    public async Task After_a_rotation_the_next_runs_material_is_the_new_one()
    {
        var host = await RunTestHost.CreateAsync();
        using var ownerScope = host;
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("rotating-app");
        var settingsPath = $"/projects/{projectId}/settings";

        var created = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            "/git-credentials/create",
            new Dictionary<string, string>
            {
                ["Name"] = CredentialName,
                ["Kind"] = "HttpsToken",
                ["Value"] = TokenValue,
                ["UserName"] = "x-access-token",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)created.StatusCode is 302 or 303, $"The create answered {(int)created.StatusCode}.");
        var credentialId = await ReadSingleCredentialIdAsync(owner, projectId);

        // No product surface assigns a credential to a project in v0.9; seed the reference the way
        // F-013's own tests do.
        await host.InSystemScopeAsync(async context =>
        {
            var projects = await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId);
            Assert.NotEmpty(projects);
            projects[0].GitCredentialId = credentialId;
            await context.SaveChangesAsync();
            return true;
        });

        var (workerToken, _) = await host.RegisterWorkerAsync(name: "settings-agent", maxConcurrency: 8);
        using var workerClient = host.Host.CreateClient();

        // Run 1 executes with the created material...
        var firstRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        await ClaimAndAcceptAsync(host, workerClient, workerToken, firstRunId);
        using var first = await FetchMaterialAsync(workerClient, workerToken, firstRunId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        AssertMaterial(await first.Content.ReadAsStringAsync(CancellationToken), TokenValue);

        // ...the Owner rotates through the settings tab (a rotation replaces the whole material:
        // value, kind and user name, so the form re-states the user name)...
        var rotated = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"/git-credentials/{credentialId}/rotate",
            new Dictionary<string, string>
            {
                ["Kind"] = "HttpsToken",
                ["Value"] = RotatedValue,
                ["UserName"] = "x-access-token",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)rotated.StatusCode is 302 or 303, $"The rotate answered {(int)rotated.StatusCode}.");

        // ...and run 2 authenticates with the rotated material - the property a private repository
        // depends on. The old material is gone.
        var secondRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        await ClaimAndAcceptAsync(host, workerClient, workerToken, secondRunId);
        using var second = await FetchMaterialAsync(workerClient, workerToken, secondRunId);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertMaterial(await second.Content.ReadAsStringAsync(CancellationToken), RotatedValue);
        Assert.NotEqual(
            await first.Content.ReadAsStringAsync(CancellationToken),
            await second.Content.ReadAsStringAsync(CancellationToken));

        // Neither material is ever on the tab.
        var settings = await AuthTestHost.GetStringAsync(owner, settingsPath);
        Assert.DoesNotContain(TokenValue, settings, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedValue, settings, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Registers bob and has the Owner invite him into the starter organization.</summary>
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

    private static async Task<Guid> ReadSingleCredentialIdAsync(HttpClient owner, Guid projectId)
    {
        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        using var document = JsonDocument.Parse(list);
        var item = Assert.Single(document.RootElement.EnumerateArray());
        return item.GetProperty("id").GetGuid();
    }

    /// <summary>The one credential's name, after asserting its rotation state through the list.</summary>
    private static async Task<string> ReadSingleRotatedStateAsync(
        HttpClient owner,
        Guid projectId,
        bool expectRotated)
    {
        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        Assert.DoesNotContain(TokenValue, list, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedValue, list, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(list);
        var items = document.RootElement.EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal(
            expectRotated ? JsonValueKind.String : JsonValueKind.Null,
            item.GetProperty("rotatedAt").ValueKind);
        return item.GetProperty("name").GetString()!;
    }

    private static async Task ClaimAndAcceptAsync(
        RunTestHost host,
        HttpClient client,
        string workerToken,
        Guid runId)
    {
        using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        using var accepted = await RunTestHost.AcceptAsync(client, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    private static async Task<HttpResponseMessage> FetchMaterialAsync(
        HttpClient client,
        string workerToken,
        Guid runId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workers/runs/{runId}/git-credential");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);
        return await client.SendAsync(request, CancellationToken);
    }

    private static void AssertMaterial(string body, string expectedValue)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal("HttpsToken", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal("x-access-token", document.RootElement.GetProperty("userName").GetString());
        Assert.Equal(expectedValue, document.RootElement.GetProperty("value").GetString());
    }
}
