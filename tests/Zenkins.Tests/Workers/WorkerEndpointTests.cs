using System.Net;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Card F-004's worker tests, driven through the real pipeline: the Owner token forms (cookies +
/// antiforgery + PRG) and the token-authenticated <c>/api/workers/*</c> endpoints. Every rejection
/// is verified against the database, not against the absence of a message.
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkerEndpointTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registration_with_a_valid_token_creates_the_worker_and_burns_the_token()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        var (registrationToken, mintResponse) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        Assert.Equal(HttpStatusCode.OK, mintResponse.StatusCode);
        Assert.StartsWith("zreg_", registrationToken, StringComparison.Ordinal);

        // TC-001: register with the token.
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(name: "build-agent-1"));
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var workerToken = await WorkerTestHost.ReadStringPropertyAsync(registered, "workerToken");
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId"));
        Assert.StartsWith("zwrk_", workerToken, StringComparison.Ordinal);

        // The worker row carries the capabilities and the tenant it registered into.
        var stored = await host.ReadWorkerAsync(workerId);
        Assert.Equal(organizationId, stored.OrganizationId);
        Assert.Equal("build-agent-1", stored.Name);
        Assert.Equal("Linux", stored.Os);
        Assert.Equal("X64", stored.Architecture);
        Assert.Equal("1.0.0", stored.Version);
        Assert.Equal("dotnet,ci", stored.Tags);
        Assert.Equal(8, stored.CpuCount);
        Assert.Equal(2, stored.MaxConcurrency);
        Assert.Equal(WorkerStatus.Online, stored.Status);
        Assert.NotNull(stored.LastHeartbeatAt);

        // ...and it is hashed: exactly one Registration row (burned) + one Worker token row.
        var tokens = await host.ReadWorkerTokensAsync(organizationId);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(1, tokens.Count(token => token.Kind == WorkerTokenKind.Registration && token.UsedAt is not null));
        Assert.Equal(1, tokens.Count(token => token.Kind == WorkerTokenKind.Worker));
        Assert.DoesNotContain(tokens, token => token.TokenHash.Contains(registrationToken, StringComparison.Ordinal));
        Assert.DoesNotContain(tokens, token => token.TokenHash.Contains(workerToken, StringComparison.Ordinal));

        // The plaintext is gone; the stored hash verifies against it and only it.
        Assert.All(tokens, token => Assert.Equal(64, token.TokenHash.Length));

        // Visible on the /workers page with its capabilities.
        var page = await AuthTestHost.GetStringAsync(owner, "/workers");
        Assert.Contains("build-agent-1", page, StringComparison.Ordinal);
        Assert.Contains("Linux", page, StringComparison.Ordinal);
        Assert.Contains("dotnet,ci", page, StringComparison.Ordinal);

        // TC-001 second half: the registration token is burned; a replay is 401.
        using var replay = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(name: "build-agent-2"));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Single(await host.ReadWorkersAsync(organizationId));
    }

    [Fact]
    public async Task Registration_with_an_unknown_token_is_401_and_creates_nothing()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        using var response = await WorkerTestHost.RegisterAsync(
            owner,
            "zreg_totally-unknown-token-value",
            WorkerTestHost.RegistrationPayload(name: "ghost-agent"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await host.ReadWorkersAsync(organizationId));
        Assert.Empty(await host.ReadWorkerTokensAsync(organizationId));
    }

    [Fact]
    public async Task Registration_with_a_revoked_token_is_401_and_creates_nothing()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        var tokens = await host.ReadWorkerTokensAsync(organizationId);
        var tokenId = tokens.Single(token => token.Kind == WorkerTokenKind.Registration).Id;

        var revoked = await OrganizationTestHost.PostFormAsync(
            owner,
            "/workers",
            $"/workers/registration-tokens/{tokenId}/revoke",
            new Dictionary<string, string>());
        AssertRedirect(revoked, "/workers?notice=token-revoked");

        using var response = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await host.ReadWorkersAsync(organizationId));
    }

    [Fact]
    public async Task Revoking_a_registration_token_twice_gives_a_friendly_result_not_a_500()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);
        var tokenId = (await host.ReadWorkerTokensAsync(organizationId))
            .Single(token => token.Kind == WorkerTokenKind.Registration).Id;

        var first = await OrganizationTestHost.PostFormAsync(
            owner,
            "/workers",
            $"/workers/registration-tokens/{tokenId}/revoke",
            new Dictionary<string, string>());
        AssertRedirect(first, "/workers?notice=token-revoked");

        // B (second review fix): the second revoke - a double-click, or a request that raced the
        // first one behind the pre-check - must land on the same friendly outcome the pre-check
        // produces, never a 500 from a lost concurrency race.
        var second = await OrganizationTestHost.PostFormAsync(
            owner,
            "/workers",
            $"/workers/registration-tokens/{tokenId}/revoke",
            new Dictionary<string, string>());
        AssertRedirect(second, "/workers?error=token-already-revoked");
    }

    [Fact]
    public async Task Heartbeat_with_a_valid_worker_token_refreshes_state()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(name: "heartbeat-agent", cpuCount: 4));
        var workerToken = await WorkerTestHost.ReadStringPropertyAsync(registered, "workerToken");
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId"));

        // TC-004: two heartbeats with updated capability fields.
        using var first = await WorkerTestHost.HeartbeatAsync(
            owner,
            workerToken,
            WorkerTestHost.HeartbeatPayload(cpuCount: 12, tags: "dotnet"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await Task.Delay(5, CancellationToken);
        using var second = await WorkerTestHost.HeartbeatAsync(
            owner,
            workerToken,
            WorkerTestHost.HeartbeatPayload(cpuCount: 16, tags: "dotnet,unity", version: "1.1.0"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var stored = await host.ReadWorkerAsync(workerId);
        Assert.Equal(16, stored.CpuCount);
        Assert.Equal("dotnet,unity", stored.Tags);
        Assert.Equal("1.1.0", stored.Version);
        Assert.Equal(WorkerStatus.Online, stored.Status);
        Assert.NotNull(stored.LastHeartbeatAt);

        // The token's LastUsedAt tracked the successful call.
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);
        var workerTokenRow = (await host.ReadWorkerTokensAsync(organizationId))
            .Single(token => token.Kind == WorkerTokenKind.Worker);
        Assert.NotNull(workerTokenRow.LastUsedAt);
    }

    [Fact]
    public async Task Heartbeat_with_an_unknown_worker_token_is_401_and_changes_nothing()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        // A worker exists so "changes nothing" has something to lose.
        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(name: "target-agent"));
        var before = await host.ReadWorkerAsync(
            Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId")));

        using var response = await WorkerTestHost.HeartbeatAsync(
            owner,
            "zwrk_totally-unknown-worker-token",
            WorkerTestHost.HeartbeatPayload(cpuCount: 99));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = await host.ReadWorkerAsync(before.Id);
        Assert.Equal(before.CpuCount, after.CpuCount);
        Assert.Equal(before.LastHeartbeatAt, after.LastHeartbeatAt);
    }

    [Fact]
    public async Task Registration_without_tags_succeeds_and_stores_an_empty_capability()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        // B1 (review fix): PRD §3.4.4 makes tags optional - a worker started without tags must
        // register successfully, not get a 400 it cannot do anything about.
        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(name: "tagless-agent", tags: null));
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId"));

        // The capability field is stored empty (never null), the token is burned as usual.
        var stored = await host.ReadWorkerAsync(workerId);
        Assert.Equal(string.Empty, stored.Tags);
        var tokens = await host.ReadWorkerTokensAsync(organizationId);
        Assert.Single(tokens, token => token.Kind == WorkerTokenKind.Registration && token.UsedAt is not null);
        Assert.Single(await host.ReadWorkersAsync(organizationId));
    }

    [Fact]
    public async Task Concurrent_registrations_with_one_token_burn_it_exactly_once()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);

        // M1 (review fix): two registrations race for the same single-use token with two
        // different names, so the Workers unique index is out of the picture and only the token
        // burn decides. Exactly one wins; the loser must see a 401, and only one row may appear.
        var firstAttempt = WorkerTestHost.RegisterAsync(
            owner, registrationToken, WorkerTestHost.RegistrationPayload(name: "race-agent-a"));
        var secondAttempt = WorkerTestHost.RegisterAsync(
            owner, registrationToken, WorkerTestHost.RegistrationPayload(name: "race-agent-b"));
        await Task.WhenAll(firstAttempt, secondAttempt);

        var statuses = new[] { firstAttempt.Result.StatusCode, secondAttempt.Result.StatusCode };
        Assert.Single(statuses, HttpStatusCode.OK);
        Assert.Single(statuses, HttpStatusCode.Unauthorized);

        Assert.Single(await host.ReadWorkersAsync(organizationId));
        var tokens = await host.ReadWorkerTokensAsync(organizationId);
        Assert.Single(tokens, token => token.Kind == WorkerTokenKind.Registration);
        Assert.Single(tokens, token => token.Kind == WorkerTokenKind.Worker);
    }

    [Fact]
    public async Task Heartbeat_with_a_revoked_worker_token_is_401_and_changes_nothing()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner, registrationToken, WorkerTestHost.RegistrationPayload(name: "revoked-token-agent"));
        var workerToken = await WorkerTestHost.ReadStringPropertyAsync(registered, "workerToken");
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId"));

        // One accepted heartbeat so "changes nothing" has state to compare against.
        using var first = await WorkerTestHost.HeartbeatAsync(
            owner, workerToken, WorkerTestHost.HeartbeatPayload(cpuCount: 4));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var before = await host.ReadWorkerAsync(workerId);

        // Revoke the worker token (the server-side effect of a same-name re-registration).
        await host.RevokeWorkerTokenAsync(workerToken);

        // TC-002 shape: a revoked token is 401 and the worker row is untouched.
        using var response = await WorkerTestHost.HeartbeatAsync(
            owner, workerToken, WorkerTestHost.HeartbeatPayload(cpuCount: 99));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = await host.ReadWorkerAsync(workerId);
        Assert.Equal(before.CpuCount, after.CpuCount);
        Assert.Equal(before.LastHeartbeatAt, after.LastHeartbeatAt);
    }

    [Fact]
    public async Task An_unauthenticated_caller_gets_401_even_with_an_invalid_payload()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        // Review fix: authentication must come before payload validation, so a tokenless caller
        // cannot probe the contract through the 400-vs-401 difference. Both payloads would be
        // 400 on their own (empty name; cpuCount 0).
        using var register = await WorkerTestHost.RegisterWithoutTokenAsync(
            owner, WorkerTestHost.RegistrationPayload(name: ""));
        using var heartbeat = await WorkerTestHost.HeartbeatWithoutTokenAsync(
            owner, WorkerTestHost.HeartbeatPayload(cpuCount: 0));

        Assert.Equal(HttpStatusCode.Unauthorized, register.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, heartbeat.StatusCode);
    }

    [Fact]
    public async Task Missing_bearer_header_is_401_for_both_worker_endpoints()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        using var register = await WorkerTestHost.RegisterWithoutTokenAsync(
            owner, WorkerTestHost.RegistrationPayload());
        using var heartbeat = await WorkerTestHost.HeartbeatWithoutTokenAsync(
            owner, WorkerTestHost.HeartbeatPayload());

        Assert.Equal(HttpStatusCode.Unauthorized, register.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, heartbeat.StatusCode);
    }

    [Fact]
    public async Task A_session_cookie_cannot_authorize_worker_actions()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        // The same client is fully signed in (cookie works on /workers)...
        var page = await AuthTestHost.GetStringAsync(owner, "/workers");
        Assert.Contains("Registration tokens", page, StringComparison.Ordinal);

        // ...but the worker API only accepts a token: no header, no entry.
        using var withoutHeader = await WorkerTestHost.RegisterWithoutTokenAsync(
            owner, WorkerTestHost.RegistrationPayload(name: "cookie-agent"));
        Assert.Equal(HttpStatusCode.Unauthorized, withoutHeader.StatusCode);

        // ...and a bogus token is still 401 even with a valid Owner session.
        using var withBogusToken = await WorkerTestHost.RegisterAsync(
            owner, "zreg_bogus", WorkerTestHost.RegistrationPayload(name: "cookie-agent"));
        Assert.Equal(HttpStatusCode.Unauthorized, withBogusToken.StatusCode);

        // No cookie-authenticated worker-management bypass exists: registering needs a zreg_ token
        // minted by an Owner, and the API ignores who the cookie says the caller is.
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);
        Assert.Empty(await host.ReadWorkersAsync(organizationId));
    }

    [Fact]
    public async Task Re_registering_with_the_same_name_updates_the_row_and_revokes_the_old_token()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        var (firstRegistration, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var first = await WorkerTestHost.RegisterAsync(
            owner,
            firstRegistration,
            WorkerTestHost.RegistrationPayload(name: "restart-agent", cpuCount: 4, version: "1.0.0"));
        var firstWorkerToken = await WorkerTestHost.ReadStringPropertyAsync(first, "workerToken");
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(first, "workerId"));

        var (secondRegistration, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var second = await WorkerTestHost.RegisterAsync(
            owner,
            secondRegistration,
            WorkerTestHost.RegistrationPayload(name: "restart-agent", cpuCount: 32, version: "2.0.0"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondWorkerToken = await WorkerTestHost.ReadStringPropertyAsync(second, "workerToken");
        Assert.NotEqual(firstWorkerToken, secondWorkerToken);

        // Same row, updated capabilities - no duplicate worker.
        Assert.Single(await host.ReadWorkersAsync(organizationId));
        var stored = await host.ReadWorkerAsync(workerId);
        Assert.Equal(32, stored.CpuCount);
        Assert.Equal("2.0.0", stored.Version);

        // The old worker token is revoked and stops working immediately...
        using var oldHeartbeat = await WorkerTestHost.HeartbeatAsync(
            owner, firstWorkerToken, WorkerTestHost.HeartbeatPayload(cpuCount: 1));
        Assert.Equal(HttpStatusCode.Unauthorized, oldHeartbeat.StatusCode);

        // ...and the new one works.
        using var newHeartbeat = await WorkerTestHost.HeartbeatAsync(
            owner, secondWorkerToken, WorkerTestHost.HeartbeatPayload());
        Assert.Equal(HttpStatusCode.OK, newHeartbeat.StatusCode);

        var tokens = await host.ReadWorkerTokensAsync(organizationId);
        var workerTokenRows = tokens.Where(token => token.Kind == WorkerTokenKind.Worker).ToArray();
        Assert.Equal(2, workerTokenRows.Length);
        Assert.Single(workerTokenRows, token => token.RevokedAt is null);
        Assert.Single(workerTokenRows, token => token.RevokedAt is not null);
    }

    [Fact]
    public async Task Workers_page_shows_only_the_active_organizations_workers()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var starterOrganizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        // A second organization with its own worker. Minting always targets the active
        // organization, so alice switches to Bravo to mint a Bravo token.
        var bravoId = await host.Inner.AddOrganizationAsync(
            "Bravo", (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));
        var switchToBravo = await OrganizationTestHost.PostFormAsync(
            owner,
            "/organizations/settings",
            "/organizations/switch",
            new Dictionary<string, string> { ["OrganizationId"] = bravoId.ToString() });
        AssertRedirect(switchToBravo, "/");

        var (bravoRegistrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var bravoRegistered = await WorkerTestHost.RegisterAsync(
            owner,
            bravoRegistrationToken,
            WorkerTestHost.RegistrationPayload(name: "bravo-agent"));
        Assert.Equal(HttpStatusCode.OK, bravoRegistered.StatusCode);
        var bravoWorkerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(bravoRegistered, "workerId"));
        Assert.Equal(bravoId, (await host.ReadWorkerAsync(bravoWorkerId)).OrganizationId);

        // Switch back and register the Starter worker.
        var switchBack = await OrganizationTestHost.PostFormAsync(
            owner,
            "/organizations/settings",
            "/organizations/switch",
            new Dictionary<string, string> { ["OrganizationId"] = starterOrganizationId.ToString() });
        AssertRedirect(switchBack, "/");
        var (starterRegistrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var starterRegistered = await WorkerTestHost.RegisterAsync(
            owner,
            starterRegistrationToken,
            WorkerTestHost.RegistrationPayload(name: "starter-agent"));
        Assert.Equal(HttpStatusCode.OK, starterRegistered.StatusCode);

        // TC-003: Starter's view must not contain Bravo's worker and vice versa.
        var starterPage = await AuthTestHost.GetStringAsync(owner, "/workers");
        Assert.Contains("starter-agent", starterPage, StringComparison.Ordinal);
        Assert.DoesNotContain("bravo-agent", starterPage, StringComparison.Ordinal);

        var switchAgain = await OrganizationTestHost.PostFormAsync(
            owner,
            "/organizations/settings",
            "/organizations/switch",
            new Dictionary<string, string> { ["OrganizationId"] = bravoId.ToString() });
        AssertRedirect(switchAgain, "/");
        var bravoPage = await AuthTestHost.GetStringAsync(owner, "/workers");
        Assert.Contains("bravo-agent", bravoPage, StringComparison.Ordinal);
        Assert.DoesNotContain("starter-agent", bravoPage, StringComparison.Ordinal);

        // The rows carry OrganizationId under the global query filter (system-scope read above).
        Assert.All(await host.ReadWorkersAsync(bravoId), worker => Assert.Equal(bravoId, worker.OrganizationId));
        Assert.All(
            await host.ReadWorkersAsync(starterOrganizationId),
            worker => Assert.Equal(starterOrganizationId, worker.OrganizationId));
    }

    [Fact]
    public async Task A_member_can_view_workers_but_cannot_manage_registration_tokens()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        const string memberEmail = "carol@example.test";

        // Seed one worker so the Member has something to view.
        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(name: "member-visible-agent"));

        // F-002 invites existing accounts: register carol first, then invite her as a Member.
        var member = await host.Inner.RegisterUserAsync(memberEmail);
        var invite = await OrganizationTestHost.PostFormAsync(
            owner,
            "/organizations/members",
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = memberEmail });
        AssertRedirect(invite, "/organizations/members?notice=invited");
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);
        var tokenCountBefore = (await host.ReadWorkerTokensAsync(organizationId)).Count;

        // Member sees the list but none of the Owner's token management.
        var page = await AuthTestHost.GetStringAsync(member, "/workers");
        Assert.Contains("member-visible-agent", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Create registration token", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/workers/registration-tokens", page, StringComparison.Ordinal);

        // Direct POSTs are rejected regardless of the UI.
        using var mint = await OrganizationTestHost.PostFormAsync(
            member, "/workers", "/workers/registration-tokens", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Forbidden, mint.StatusCode);
        Assert.Equal(tokenCountBefore, (await host.ReadWorkerTokensAsync(organizationId)).Count);
    }

    [Fact]
    public async Task Worker_authentication_failures_leak_no_tenant_information()
    {
        using var host = await WorkerTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);
        var organizationName = (await host.Inner.ReadOrganizationsAsync())
            .Single(organization => organization.Id == organizationId).Name;

        var (registrationToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(name: "quiet-agent"));
        var workerToken = await WorkerTestHost.ReadStringPropertyAsync(registered, "workerToken");

        // Burn the registration token, then present every failure shape.
        using var replay = await WorkerTestHost.RegisterAsync(
            owner, registrationToken, WorkerTestHost.RegistrationPayload());
        using var unknownWorkerToken = await WorkerTestHost.HeartbeatAsync(
            owner, "zwrk_unknown", WorkerTestHost.HeartbeatPayload());
        using var missingHeader = await WorkerTestHost.HeartbeatWithoutTokenAsync(
            owner, WorkerTestHost.HeartbeatPayload());

        foreach (var response in new[] { replay, unknownWorkerToken, missingHeader })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.DoesNotContain(organizationName, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(organizationId.ToString(), body, StringComparison.Ordinal);
            Assert.DoesNotContain("organization", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertRedirect(HttpResponseMessage response, string expectedPathAndQuery)
    {
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Expected a redirect to '{expectedPathAndQuery}' but got {(int)response.StatusCode}.");
        var location = response.Headers.Location?.ToString();
        Assert.StartsWith(expectedPathAndQuery, location, StringComparison.Ordinal);
    }
}
