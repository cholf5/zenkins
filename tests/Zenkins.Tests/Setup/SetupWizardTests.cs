using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Setup;

/// <summary>
/// Card F-044 over the real pipeline: the gate that makes the wizard the only door into an empty
/// deployment, the welcome page's deployment facts and schema answer, the Owner form's single
/// transaction, the post-setup lockout, and the optional Worker step.
/// </summary>
[Trait("Category", "Auth")]
public sealed class SetupWizardTests : IDisposable
{
    private const string OwnerEmail = "owner@example.test";
    private const string MemberEmail = "member@example.test";

    private readonly AuthTestHost _host = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_deployment_sends_both_account_pages_to_the_wizard()
    {
        using var client = _host.CreateClient();

        using var login = await client.GetAsync("/account/login", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/setup", PathOf(login));

        using var register = await client.GetAsync("/account/register", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        Assert.Equal("/setup", PathOf(register));

        var welcome = await AuthTestHost.GetStringAsync(client, "/setup");
        Assert.Contains("Welcome to Zenkins", welcome, StringComparison.Ordinal);
        Assert.Contains("This deployment has no accounts yet.", welcome, StringComparison.Ordinal);
        Assert.Contains("Start setup", welcome, StringComparison.Ordinal);

        // The facts are this deployment's, not a canned list: the provider and the real SQLite file
        // the host is using (never the connection string, which could carry a password).
        Assert.Contains("SQLite", welcome, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode(_host.DatabasePath), welcome, StringComparison.Ordinal);
        Assert.Contains("Connected, and the schema is up to date.", welcome, StringComparison.Ordinal);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(0, snapshot.Users);
        Assert.Equal(0, snapshot.Organizations);
        Assert.Equal(0, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task The_owner_form_creates_the_named_organization_and_signs_the_browser_in()
    {
        using var client = _host.CreateClient();

        var form = await AuthTestHost.GetStringAsync(client, "/setup/owner");
        Assert.Contains("action=\"/setup/owner\"", form, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", form, StringComparison.Ordinal);
        Assert.Contains("Create the Owner account", form, StringComparison.Ordinal);

        using var created = await PostOwnerAsync(client, "Contoso CI", OwnerEmail);

        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/setup/worker", PathOf(created));
        Assert.NotNull(AuthTestHost.FindAuthCookie(created));

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(1, snapshot.Memberships);
        Assert.Equal(1, snapshot.OwnerMemberships);
        Assert.Equal(1, snapshot.BootstrapMarkers);
        Assert.Equal(snapshot.UserIds, snapshot.MembershipUserIds);

        // The typed name is the stored name - not "Starter Organization".
        Assert.Equal("Contoso CI", await _host.InScopeAsync(async services =>
        {
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            return await context.Organizations.Select(organization => organization.Name).SingleAsync(CancellationToken);
        }));

        var home = await AuthTestHost.GetStringAsync(client, "/");
        Assert.Contains("Contoso CI", home, StringComparison.Ordinal);

        // The wizard is done: its first two pages are gone for a signed-in Owner.
        using var welcome = await client.GetAsync("/setup", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, welcome.StatusCode);
        Assert.Equal("/", PathOf(welcome));

        using var owner = await client.GetAsync("/setup/owner", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, owner.StatusCode);
        Assert.Equal("/", PathOf(owner));
    }

    [Fact]
    public async Task The_owner_form_refuses_a_blank_or_over_long_name_and_writes_nothing()
    {
        using var client = _host.CreateClient();

        using var blank = await PostOwnerAsync(client, "   ", OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        Assert.Contains(
            "Enter an organization name.",
            await blank.Content.ReadAsStringAsync(CancellationToken),
            StringComparison.Ordinal);

        using var tooLong = await PostOwnerAsync(client, new string('n', 201), OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, tooLong.StatusCode);
        Assert.Contains(
            "The organization name must be at most 200 characters.",
            await tooLong.Content.ReadAsStringAsync(CancellationToken),
            StringComparison.Ordinal);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(0, snapshot.Users);
        Assert.Equal(0, snapshot.Organizations);
        Assert.Equal(0, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task The_owner_form_leaves_the_password_policy_to_identity()
    {
        using var client = _host.CreateClient();

        using var weak = await PostOwnerAsync(client, "Contoso CI", OwnerEmail, password: "alllowercase123");

        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        var html = await weak.Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("must have at least one", html, StringComparison.OrdinalIgnoreCase);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(0, snapshot.Users);
        Assert.Equal(0, snapshot.Organizations);
        Assert.Equal(0, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task A_finished_setup_is_not_offered_again_and_the_service_refuses_late_submissions()
    {
        using var owner = _host.CreateClient();
        using (var created = await PostOwnerAsync(owner, "Contoso CI", OwnerEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        }

        // A browser that still holds the form and submits after someone else finished gets a named
        // refusal, not an account without an organization.
        var refusal = await _host.InScopeAsync(services => services
            .GetRequiredService<RegistrationService>()
            .RegisterOwnerAsync("late@example.test", AuthTestHost.ValidPassword, "Late Org"));

        Assert.False(refusal.Succeeded);
        Assert.Contains("Setup has already been completed. Sign in instead.", refusal.Errors);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(1, snapshot.BootstrapMarkers);

        // Signed out, the wizard points at sign-in; the ordinary registration form is open again for
        // later accounts (they join as Members through an Owner's invitation).
        using var anonymous = _host.CreateClient();
        using var closed = await anonymous.GetAsync("/setup", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        Assert.Equal("/account/login", PathOf(closed));

        using var register = await anonymous.GetAsync("/account/register", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
    }

    [Fact]
    public async Task The_worker_step_mints_a_token_once_and_shows_how_to_start_a_worker()
    {
        using var owner = _host.CreateClient();
        using (var created = await PostOwnerAsync(owner, "Contoso CI", OwnerEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        }

        var page = await AuthTestHost.GetStringAsync(owner, "/setup/worker");
        Assert.Contains("Register the first Worker", page, StringComparison.Ordinal);
        Assert.Contains("Create registration token", page, StringComparison.Ordinal);

        using var minted = await PostFormAsync(
            owner,
            "/setup/worker",
            new Dictionary<string, string>
            {
                ["Input.Confirm"] = "true",
                ["__RequestVerificationToken"] = AuthTestHost.ExtractAntiforgeryToken(page),
                ["_handler"] = "setup-worker",
            });

        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        var html = await minted.Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("zreg_", html, StringComparison.Ordinal);
        Assert.Contains("ZENKINS_SERVER_URL=http://localhost", html, StringComparison.Ordinal);
        Assert.Contains("ZENKINS_REGISTRATION_TOKEN=zreg_", html, StringComparison.Ordinal);
        Assert.Contains("shown exactly once", html, StringComparison.Ordinal);

        // The token is not stored for display: a fresh GET has only the button.
        var again = await AuthTestHost.GetStringAsync(owner, "/setup/worker");
        Assert.DoesNotContain("zreg_", again, StringComparison.Ordinal);
        Assert.Contains("Create registration token", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_worker_step_refuses_anonymous_visitors_and_members()
    {
        using var owner = _host.CreateClient();
        using (var created = await PostOwnerAsync(owner, "Contoso CI", OwnerEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        }

        using var anonymous = _host.CreateClient();
        using var blocked = await anonymous.GetAsync("/setup/worker", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        Assert.Contains("/account/login", blocked.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);

        // A second account joins the organization as a Member; the step is Owner-only, and the page
        // says so instead of rendering a mint button.
        using var memberClient = _host.CreateClient();
        using (var registered = await AuthTestHost.RegisterAsync(memberClient, MemberEmail))
        {
            Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        }

        await _host.InScopeAsync(async services =>
        {
            using var systemScope = services.GetRequiredService<ISystemDataScope>().BeginScope();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);

            var organization = await context.Organizations.SingleAsync(CancellationToken);
            var member = await context.Users.SingleAsync(user => user.UserName == MemberEmail, CancellationToken);
            context.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = organization.Id,
                UserId = member.Id,
                Role = OrganizationRole.Member,
            });
            await context.SaveChangesAsync(CancellationToken);
            return 0;
        });

        var memberPage = await AuthTestHost.GetStringAsync(memberClient, "/setup/worker");
        Assert.Contains("Only an organization Owner can register a Worker.", memberPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Create registration token", memberPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_schema_behind_the_binaries_is_reported_instead_of_a_crash()
    {
        // Production is the configuration that does not migrate on startup: the database exists but
        // has no schema, so even reading the first-run marker fails. The wizard must explain that,
        // not answer with an exception page.
        using var host = new AuthTestHost(environment: "Production");
        using var client = host.CreateClient();

        using var register = await client.GetAsync("/account/register", CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        Assert.Equal("/setup", PathOf(register));

        var welcome = await AuthTestHost.GetStringAsync(client, "/setup");
        Assert.Contains("migrations behind the server binaries", welcome, StringComparison.Ordinal);
        Assert.Contains("dotnet ef database update", welcome, StringComparison.Ordinal);
        Assert.DoesNotContain("Start setup", welcome, StringComparison.Ordinal);
    }

    public void Dispose() => _host.Dispose();

    private static Task<HttpResponseMessage> PostOwnerAsync(
        HttpClient client,
        string organizationName,
        string email,
        string password = AuthTestHost.ValidPassword) =>
        PostOwnerAsync(client, organizationName, email, password, password);

    private static async Task<HttpResponseMessage> PostOwnerAsync(
        HttpClient client,
        string organizationName,
        string email,
        string password,
        string confirmPassword)
    {
        var form = await AuthTestHost.GetStringAsync(client, "/setup/owner");
        return await PostFormAsync(client, "/setup/owner", new Dictionary<string, string>
        {
            ["Input.OrganizationName"] = organizationName,
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = confirmPassword,
            ["__RequestVerificationToken"] = AuthTestHost.ExtractAntiforgeryToken(form),
            ["_handler"] = "setup-owner",
        });
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string url,
        IReadOnlyDictionary<string, string> fields) =>
        AuthTestHost.PostFormAsync(client, url, fields);

    /// <summary>
    /// The redirect's path. TestServer answers with either shape - a relative <c>Location</c> (whose
    /// <c>PathAndQuery</c> throws) or an absolute one - so both are reduced to the path the browser
    /// would follow. Comparing the raw string instead pinned the host, not the redirect.
    /// </summary>
    private static string? PathOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        return location is null ? null : location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }
}
