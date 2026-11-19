using System.Net;
using Xunit;

namespace Zenkins.Tests.Auth;

/// <summary>
/// F-001 end-to-end authentication over the real pipeline: static-SSR account pages, antiforgery,
/// cookie authentication, authorization and the first-run bootstrap. These are the same steps the
/// card's curl proof performs, pinned so a regression fails <c>dotnet test</c>.
/// </summary>
[Trait("Category", "Auth")]
public sealed class AuthenticationFlowTests : IDisposable
{
    private const string AliceEmail = "alice@example.test";

    private readonly AuthTestHost _host = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_account_posts_in_static_ssr_returns_an_auth_cookie_and_the_shell_shows_the_owner()
    {
        using var client = _host.CreateClient();

        // The wizard's Owner form must be static SSR: it posts back to the page (no circuit handler)
        // and carries the antiforgery token the middleware validates. A circuit has no HttpContext,
        // so a form that needed one could never set the authentication cookie.
        var ownerHtml = await AuthTestHost.GetStringAsync(client, "/setup/owner");
        Assert.Contains("action=\"/setup/owner\"", ownerHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", ownerHtml, StringComparison.Ordinal);

        using var response = await AuthTestHost.RegisterAsync(client, AliceEmail);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // F-044: the first account is created by the wizard, so the redirect continues the wizard.
        Assert.Equal("/setup/worker", response.Headers.Location?.PathAndQuery);

        var cookie = AuthTestHost.FindAuthCookie(response);
        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        // Plain HTTP request: SameAsRequest must not mark the cookie Secure (the browser would drop it).
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);

        var home = await AuthTestHost.GetStringAsync(client, "/");
        Assert.Contains("Starter Organization", home, StringComparison.Ordinal);
        Assert.Contains($"Open user menu for {AliceEmail}", home, StringComparison.Ordinal);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(1, snapshot.Memberships);
        Assert.Single(snapshot.MembershipUserIds, snapshot.UserIds.Contains);
        Assert.Equal(1, snapshot.OwnerMemberships);
        Assert.Equal(1, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task Second_registration_gets_no_organization_and_does_not_bootstrap_again()
    {
        using var alice = _host.CreateClient();
        using (var first = await AuthTestHost.RegisterAsync(alice, AliceEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        }

        using var bob = _host.CreateClient();
        using var second = await AuthTestHost.RegisterAsync(bob, "bob@example.test");

        // Not a first run any more: the account is created, but without an organization.
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal("/no-organization", second.Headers.Location?.PathAndQuery);
        Assert.NotNull(AuthTestHost.FindAuthCookie(second));

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(2, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(1, snapshot.Memberships);
        Assert.Equal(1, snapshot.BootstrapMarkers);

        var noOrganization = await AuthTestHost.GetStringAsync(bob, "/no-organization");
        Assert.Contains("What happens next", noOrganization, StringComparison.Ordinal);

        // Every app page carries the explicit no-organization banner instead of an empty dashboard.
        var projects = await AuthTestHost.GetStringAsync(bob, "/projects");
        Assert.Contains("No organization yet", projects, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_projects_request_redirects_to_login_with_return_url_and_returns_there()
    {
        using var owner = _host.CreateClient();
        using (var registration = await AuthTestHost.RegisterAsync(owner, AliceEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
        }

        using var anonymous = _host.CreateClient();
        using var blocked = await anonymous.GetAsync("/projects", CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        var loginLocation = blocked.Headers.Location?.OriginalString;
        Assert.NotNull(loginLocation);
        Assert.Contains("/account/login", loginLocation, StringComparison.Ordinal);
        Assert.Contains("ReturnUrl=%2Fprojects", loginLocation, StringComparison.OrdinalIgnoreCase);

        using var login = await AuthTestHost.LoginAsync(anonymous, AliceEmail, returnUrl: "/projects");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/projects", login.Headers.Location?.PathAndQuery);
        Assert.NotNull(AuthTestHost.FindAuthCookie(login));

        using var projects = await anonymous.GetAsync("/projects", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
        Assert.Contains("Projects", await projects.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_post_clears_the_authentication_cookie()
    {
        using var client = _host.CreateClient();
        using (var registration = await AuthTestHost.RegisterAsync(client, AliceEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
        }

        var logoutHtml = await AuthTestHost.GetStringAsync(client, "/account/logout");
        Assert.Contains("action=\"/account/logout\"", logoutHtml, StringComparison.Ordinal);

        using var signedOut = await AuthTestHost.PostFormAsync(
            client,
            "/account/logout",
            new Dictionary<string, string>
            {
                ["Input.Confirm"] = "true",
                ["__RequestVerificationToken"] = AuthTestHost.ExtractAntiforgeryToken(logoutHtml),
                ["_handler"] = "logout",
            });

        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/account/login?signedOut=true", signedOut.Headers.Location?.PathAndQuery);

        var cleared = AuthTestHost.FindAuthCookie(signedOut);
        Assert.NotNull(cleared);
        Assert.StartsWith("Zenkins.Auth=;", cleared, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);

        // The cookie is gone, so the protected page must redirect again.
        using var afterLogout = await client.GetAsync("/", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.Contains("/account/login", afterLogout.Headers.Location?.OriginalString!, StringComparison.Ordinal);

        var loginPage = await AuthTestHost.GetStringAsync(client, "/account/login?signedOut=true");
        Assert.Contains("You have been signed out.", loginPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_cookie_is_secure_only_for_https_requests()
    {
        using var httpsClient = _host.CreateHttpsClient();
        using var secureRegistration = await AuthTestHost.RegisterAsync(httpsClient, AliceEmail);

        var secureCookie = AuthTestHost.FindAuthCookie(secureRegistration);
        Assert.NotNull(secureCookie);
        Assert.Contains("secure", secureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", secureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", secureCookie, StringComparison.OrdinalIgnoreCase);

        using var httpClient = _host.CreateClient();
        using var plainLogin = await AuthTestHost.LoginAsync(httpClient, AliceEmail);

        var plainCookie = AuthTestHost.FindAuthCookie(plainLogin);
        Assert.NotNull(plainCookie);
        Assert.DoesNotContain("secure", plainCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_five_failures_lock_the_account()
    {
        using var owner = _host.CreateClient();
        using (var registration = await AuthTestHost.RegisterAsync(owner, AliceEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
        }

        using var attacker = _host.CreateClient();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var failed = await AuthTestHost.LoginAsync(attacker, AliceEmail, "WrongPassword9!X");
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            Assert.Null(AuthTestHost.FindAuthCookie(failed));
            var html = await failed.Content.ReadAsStringAsync(CancellationToken);
            Assert.True(
                html.Contains("Incorrect email or password.", StringComparison.Ordinal)
                || html.Contains("temporarily locked", StringComparison.Ordinal),
                $"Unexpected login failure page on attempt {attempt}.");
        }

        // Lockout is server-side state: the correct password is still refused while it lasts.
        using var locked = await AuthTestHost.LoginAsync(attacker, AliceEmail);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Null(AuthTestHost.FindAuthCookie(locked));
        Assert.Contains(
            "temporarily locked",
            await locked.Content.ReadAsStringAsync(CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_without_an_antiforgery_token_is_rejected_and_creates_nothing()
    {
        using var client = _host.CreateClient();

        using var response = await AuthTestHost.PostFormAsync(
            client,
            "/setup/owner",
            new Dictionary<string, string>
            {
                ["Input.OrganizationName"] = "Contoso CI",
                ["Input.Email"] = AliceEmail,
                ["Input.Password"] = AuthTestHost.ValidPassword,
                ["Input.ConfirmPassword"] = AuthTestHost.ValidPassword,
                ["_handler"] = "setup-owner",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(AuthTestHost.FindAuthCookie(response));

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(0, snapshot.Users);
        Assert.Equal(0, snapshot.Organizations);
    }

    [Fact]
    public async Task Password_policy_is_enforced_server_side()
    {
        using var client = _host.CreateClient();

        using (var tooShort = await AuthTestHost.RegisterAsync(client, "short@example.test", "Short9!x"))
        {
            Assert.Equal(HttpStatusCode.OK, tooShort.StatusCode);
            Assert.Contains("Use at least 12 characters.", await tooShort.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);
        }

        // Long enough, but missing an uppercase letter and a symbol: Identity's policy must refuse it,
        // proving the configured policy (not just the length attribute) is active.
        using (var tooSimple = await AuthTestHost.RegisterAsync(client, "simple@example.test", "alllowercase123"))
        {
            Assert.Equal(HttpStatusCode.OK, tooSimple.StatusCode);
            var html = await tooSimple.Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains("must have at least one", html, StringComparison.OrdinalIgnoreCase);
        }

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(0, snapshot.Users);
        Assert.Equal(0, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task Return_url_that_leaves_the_site_is_ignored()
    {
        using var client = _host.CreateClient();
        using (var registration = await AuthTestHost.RegisterAsync(client, AliceEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
        }

        using var login = await AuthTestHost.LoginAsync(client, AliceEmail, returnUrl: "//evil.example/phish");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.PathAndQuery);
    }

    public void Dispose() => _host.Dispose();
}
