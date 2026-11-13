using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Git;
using Zenkins.Server.Organizations;
using Zenkins.Server.Secrets;
using Zenkins.Server.Workers;
using Zenkins.Shared.Tenancy;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Git;

/// <summary>
/// TC-003 plus the management contract (card F-013, AC-4): Owner-only create/rotate/delete, and a
/// worker fetch that answers material only for a run currently assigned to the calling worker.
/// Cross-tenant, foreign-run and unknown-run attempts are the <b>same</b> bodyless 404, and the server
/// log carries one neutral line for all of them - the endpoint is not an existence oracle.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitCredentialEndpointsTests
{
    private const string TokenValue = "zkpat_0123456789abcdef0123456789abcdef";
    private const string RotatedValue = "zkpat_fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task The_owner_creates_rotates_and_deletes_a_credential()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var organizationId = await host.OrganizationIdAsync();

        var credentialId = await CreateCredentialAsync(host, owner, "DEPLOY_TOKEN", TokenValue);

        var stored = await ReadCredentialAsync(host, credentialId);
        Assert.Equal("DEPLOY_TOKEN", stored.Name);
        Assert.Equal("HttpsToken", stored.Kind);
        Assert.Equal("x-access-token", stored.UserName);
        Assert.NotEqual(TokenValue, stored.Ciphertext);
        Assert.DoesNotContain(TokenValue, stored.Ciphertext, StringComparison.Ordinal);
        Assert.Equal(organizationId, stored.OrganizationId);

        // Rotate: the kind and the value may both change; the name may not.
        using var rotated = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/rotate",
            new Dictionary<string, string>
            {
                ["Kind"] = "HttpsToken",
                ["Value"] = RotatedValue,
                ["UserName"] = "git",
            });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        var afterRotate = await ReadCredentialAsync(host, credentialId);
        Assert.Equal("git", afterRotate.UserName);
        Assert.NotEqual(stored.Ciphertext, afterRotate.Ciphertext);
        Assert.NotNull(afterRotate.RotatedAt);

        // Delete: the row is gone, and a repeat is the same 404 a foreign id gets.
        using var deleted = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Null(await FindCredentialAsync(host, credentialId));

        using var secondDelete = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, secondDelete.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_manage_git_credentials()
    {
        using var host = await RunTestHost.CreateAsync();
        using var member = await CreateMemberAsync(host);

        using var response = await OrganizationTestHost.PostFormAsync(
            member,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>
            {
                ["Name"] = "MEMBER_TOKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = TokenValue,
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadCredentialsAsync(host, await host.OrganizationIdAsync()));
    }

    [Fact]
    public async Task A_token_only_create_or_rotate_is_a_field_error_not_a_500()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();

        // Only the antiforgery token travels, so the form model binds null (the D-003-B3 shape):
        // the endpoint substitutes an empty input and validation - never a null reference - answers.
        using var create = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        using (var document = JsonDocument.Parse(await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(GitCredentialFields.Name, document.RootElement.GetProperty("field").GetString());
            Assert.Equal(GitCredentialErrorCodes.NameRequired, document.RootElement.GetProperty("code").GetString());
        }

        var credentialId = await CreateCredentialAsync(host, owner, "NEEDS_ROTATION", TokenValue);
        using var rotate = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/rotate",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.BadRequest, rotate.StatusCode);

        // The refused rotation replaced nothing: the stored row keeps its first material.
        var stored = await ReadCredentialAsync(host, credentialId);
        Assert.Null(stored.RotatedAt);
        Assert.Equal("x-access-token", stored.UserName);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_with_a_stable_field_code()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();

        // A token with a line break can never be delivered through ask-pass, so it is refused at the
        // door rather than on a worker.
        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>
            {
                ["Name"] = "BROKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = "zkpat_line1\nzkpat_line2",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(GitCredentialFields.Value, document.RootElement.GetProperty("field").GetString());
        Assert.Equal(GitCredentialErrorCodes.ValueShape, document.RootElement.GetProperty("code").GetString());

        // An unknown kind is refused too, and nothing is stored.
        using var kindResponse = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>
            {
                ["Name"] = "KINDLESS",
                ["Kind"] = "Bearer",
                ["Value"] = TokenValue,
            });
        Assert.Equal(HttpStatusCode.BadRequest, kindResponse.StatusCode);
        Assert.Empty(await ReadCredentialsAsync(host, await host.OrganizationIdAsync()));
    }

    [Fact]
    public async Task A_foreign_credential_id_is_the_same_404_as_a_missing_one()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var credentialId = await CreateCredentialAsync(host, owner, "OWN_TOKEN", TokenValue);

        var foreignOrganizationId = await CreateForeignOrgAsync(host);
        using var foreignOwner = await host.Inner.Inner.SignInAsync(ForeignOwnerEmail);
        Assert.NotEqual(await host.OrganizationIdAsync(), foreignOrganizationId);

        using var foreignRotate = await OrganizationTestHost.PostFormAsync(
            foreignOwner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/{credentialId}/rotate",
            new Dictionary<string, string> { ["Kind"] = "HttpsToken", ["Value"] = RotatedValue });
        using var missingRotate = await OrganizationTestHost.PostFormAsync(
            foreignOwner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/{Guid.NewGuid()}/rotate",
            new Dictionary<string, string> { ["Kind"] = "HttpsToken", ["Value"] = RotatedValue });

        Assert.Equal(HttpStatusCode.NotFound, foreignRotate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRotate.StatusCode);
        Assert.Equal(
            await missingRotate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await foreignRotate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The row survived the foreign attempt unchanged.
        var stored = await ReadCredentialAsync(host, credentialId);
        Assert.Equal("OWN_TOKEN", stored.Name);
    }

    [Fact]
    public async Task The_worker_fetch_serves_material_only_for_its_own_run()
    {
        using var setup = await CreateAssignedRunAsync();

        // (1) The worker that owns the run gets the material.
        using var own = await FetchAsync(setup.Host, setup.WorkerToken, setup.RunId);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var ownBody = JsonDocument.Parse(await own.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("HttpsToken", ownBody.RootElement.GetProperty("kind").GetString());
        Assert.Equal(TokenValue, ownBody.RootElement.GetProperty("value").GetString());
        Assert.Equal("x-access-token", ownBody.RootElement.GetProperty("userName").GetString());

        // (2) A different worker of the same organization: the same 404, no body.
        using var otherWorker = await FetchAsync(setup.Host, setup.OtherWorkerToken, setup.RunId);
        Assert.Equal(HttpStatusCode.NotFound, otherWorker.StatusCode);
        Assert.Equal(string.Empty, await otherWorker.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // (3) A worker of another organization asking for this run id: byte-identical to (2).
        using var foreignWorker = await FetchAsync(setup.Host, setup.ForeignWorkerToken, setup.RunId);
        Assert.Equal(HttpStatusCode.NotFound, foreignWorker.StatusCode);
        Assert.Equal(string.Empty, await foreignWorker.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // (4) An unknown run id for the owning worker: the same answer again.
        using var unknownRun = await FetchAsync(setup.Host, setup.WorkerToken, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);
        Assert.Equal(string.Empty, await unknownRun.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // (5) Not just the status and the bytes: the Content-Type is identical too, so no miss is
        // distinguishable by any part of the response (review M1).
        // A miss carries no body at all, hence no Content-Type either; the three are byte- and
        // header-identical. The success answer, by contrast, is JSON - asserted so the equality above
        // is not a vacuous null == null.
        Assert.Null(otherWorker.Content.Headers.ContentType);
        Assert.Equal(otherWorker.Content.Headers.ContentType?.ToString(), foreignWorker.Content.Headers.ContentType?.ToString());
        Assert.Equal(otherWorker.Content.Headers.ContentType?.ToString(), unknownRun.Content.Headers.ContentType?.ToString());
        Assert.StartsWith(
            "application/json",
            own.Content.Headers.ContentType?.ToString() ?? string.Empty,
            StringComparison.Ordinal);

        // (6) No token at all: the one distinguishable answer, and it is authentication, not existence.
        using var anonymous = await FetchAsync(setup.Host, null, setup.RunId);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Every_miss_produces_one_neutral_log_line()
    {
        using var setup = await CreateAssignedRunAsync();
        var collector = new CollectingLogger<GitCredentialService>();

        var results = await setup.Host.Host.InScopeAsync(async services =>
        {
            // The real service, with the real worker authentication path - only the logger is captured,
            // so the assertion is about what the product would write.
            var service = new GitCredentialService(
                services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>(),
                services.GetRequiredService<TenantContext>(),
                services.GetRequiredService<GitCredentialProtector>(),
                services.GetRequiredService<WorkerService>(),
                collector);

            var misses = new List<GitCredentialFetchStatus>
            {
                (await service.FetchMaterialForRunAsync(setup.OtherWorkerToken, setup.RunId)).Status,
                (await service.FetchMaterialForRunAsync(setup.ForeignWorkerToken, setup.RunId)).Status,
                (await service.FetchMaterialForRunAsync(setup.WorkerToken, Guid.NewGuid())).Status,
            };

            // The owning worker's own fetch (logged last, so it does not interleave with the misses).
            var success = await service.FetchMaterialForRunAsync(setup.WorkerToken, setup.RunId);
            return (Misses: misses, Success: success);
        });

        Assert.All(results.Misses, status => Assert.Equal(GitCredentialFetchStatus.NotFound, status));
        Assert.Equal(GitCredentialFetchStatus.Success, results.Success.Status);
        Assert.Equal(TokenValue, results.Success.Material!.Value);

        var missLines = collector.Messages
            .Where(message => message.Contains("resolved no material", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, missLines.Length);
        Assert.All(missLines, line => Assert.StartsWith(
            "Information:Git credential request resolved no material. RunId=",
            line,
            StringComparison.Ordinal));

        // No distinguishable authorization vocabulary: the line carries the ids and nothing else.
        foreach (var forbidden in new[] { "foreign", "forbidden", "denied", "not assigned", "another tenant", "owner", "invalid" })
        {
            Assert.All(missLines, line => Assert.DoesNotContain(forbidden, line, StringComparison.OrdinalIgnoreCase));
        }

        // The success line names the material's metadata, never its value.
        var deliveredLine = Assert.Single(
            collector.Messages,
            message => message.Contains("Git credential delivered", StringComparison.Ordinal));
        Assert.Contains("Kind=HttpsToken", deliveredLine, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenValue, deliveredLine, StringComparison.Ordinal);
        Assert.All(collector.Messages, message => Assert.DoesNotContain(TokenValue, message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_git_credential_ciphertext_is_bound_to_its_own_purpose()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"zenkins-f013-keys-{Guid.NewGuid():N}");
        try
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keysDirectory));
            var secrets = new SecretProtector(provider);
            var credentials = new GitCredentialProtector(provider);

            Assert.NotEqual(SecretProtector.ProtectionPurpose, GitCredentialProtector.ProtectionPurpose);

            var credentialBlob = credentials.Protect(TokenValue);
            var secretBlob = secrets.Protect(TokenValue);

            // Neither blob is accepted by the other store: the two ciphertext classes are not
            // interchangeable, which is the whole reason the purpose is separate.
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => secrets.Unprotect(credentialBlob));
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => credentials.Unprotect(secretBlob));
            Assert.Equal(TokenValue, credentials.Unprotect(credentialBlob));
        }
        finally
        {
            try
            {
                if (Directory.Exists(keysDirectory))
                {
                    Directory.Delete(keysDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // Setup and helpers
    // ------------------------------------------------------------------

    private const string ForeignOwnerEmail = "carol@example.test";

    private sealed record AssignedRun(
        RunTestHost Host,
        Guid CredentialId,
        Guid ProjectId,
        Guid RunId,
        string WorkerToken,
        string OtherWorkerToken,
        string ForeignWorkerToken) : IDisposable
    {
        public void Dispose() => Host.Dispose();
    }

    private static async Task<AssignedRun> CreateAssignedRunAsync()
    {
        var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var credentialId = await CreateCredentialAsync(host, owner, "PROJECT_TOKEN", TokenValue, "x-access-token");

        var (projectId, projectOwner) = await host.CreateProjectAsync();
        projectOwner.Dispose();
        await host.InSystemScopeAsync(async context =>
        {
            var projects = await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId);
            if (projects.Count == 0)
            {
                throw new InvalidOperationException("The fixture project was not found.");
            }

            projects[0].GitCredentialId = credentialId;
            await context.SaveChangesAsync();
            return true;
        });

        // A queued run of the project that references the credential, then a worker claims and accepts
        // it - the state the fetch is scoped to.
        var queuedRunId = await host.AddRunAsync(projectId);

        var (workerToken, _) = await host.RegisterWorkerAsync(name: "git-agent-owner");
        var (otherWorkerToken, _) = await host.RegisterWorkerAsync(name: "git-agent-other");

        using var client = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        var runId = await RunTestHost.ReadClaimedRunIdAsync(claimed);
        Assert.Equal(queuedRunId, runId);
        using var accepted = await RunTestHost.AcceptAsync(client, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var foreignOrganizationId = await CreateForeignOrgAsync(host);
        var foreignWorkerToken = await host.CreateWorkerTokenDirectlyAsync(
            foreignOrganizationId,
            name: "foreign-git-agent");

        return new AssignedRun(host, credentialId, projectId, runId, workerToken, otherWorkerToken, foreignWorkerToken);
    }

    /// <summary>Registers bob and has the Owner invite him into the starter organization.</summary>
    private static async Task<HttpClient> CreateMemberAsync(RunTestHost host)
    {
        var member = await host.Inner.Inner.RegisterUserAsync("bob@example.test");
        try
        {
            using var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
            using var invite = await OrganizationTestHost.PostFormAsync(
                owner,
                OrganizationEndpoints.MembersPath,
                "/organizations/members/invite",
                new Dictionary<string, string> { ["Email"] = "bob@example.test" });
            Assert.True(
                (int)invite.StatusCode is 302 or 303,
                $"Inviting the Member failed with {(int)invite.StatusCode}.");

            // Persist the starter organization as Bob's selection (the deterministic single-membership
            // fallback the product applies on the first request).
            _ = await AuthTestHost.GetStringAsync(member, "/");
            return member;
        }
        catch
        {
            member.Dispose();
            throw;
        }
    }

    /// <summary>Registers carol and creates a second organization she owns; returns its id.</summary>
    private static async Task<Guid> CreateForeignOrgAsync(RunTestHost host)
    {
        using var foreignOwner = await host.Inner.Inner.RegisterUserAsync(ForeignOwnerEmail);
        return await host.Inner.Inner.AddOrganizationAsync(
            "Bravo",
            (ForeignOwnerEmail, OrganizationRole.Owner));
    }

    private static async Task<Guid> CreateCredentialAsync(
        RunTestHost host,
        HttpClient owner,
        string name,
        string value,
        string? userName = "x-access-token")
    {
        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Kind"] = "HttpsToken",
                ["Value"] = value,
                ["UserName"] = userName ?? string.Empty,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<GitCredential> ReadCredentialAsync(RunTestHost host, Guid credentialId) =>
        await FindCredentialAsync(host, credentialId)
        ?? throw new InvalidOperationException($"The credential '{credentialId}' was not found.");

    private static async Task<GitCredential?> FindCredentialAsync(RunTestHost host, Guid credentialId) =>
        await host.InSystemScopeAsync(async context =>
        {
            var matches = await context.SystemQueryAsync<GitCredential>(credential => credential.Id == credentialId);
            return matches.Count == 0 ? null : matches[0];
        });

    private static async Task<IReadOnlyList<GitCredential>> ReadCredentialsAsync(
        RunTestHost host,
        Guid organizationId) =>
        await host.InSystemScopeAsync(
            context => context.SystemQueryAsync<GitCredential>(
                credential => credential.OrganizationId == organizationId));

    private static async Task<HttpResponseMessage> FetchAsync(
        RunTestHost host,
        string? workerToken,
        Guid runId)
    {
        using var client = host.Host.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/workers/runs/{runId}/git-credential");
        if (workerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}

/// <summary>A capturing <see cref="ILogger{T}"/>: what the product would write, without a log backend.</summary>
internal sealed class CollectingLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_messages)
        {
            _messages.Add($"{logLevel}:{formatter(state, exception)}");
        }
    }
}
