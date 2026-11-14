using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Organizations;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Server.Secrets;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Tenancy;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Secrets;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Forms;

/// <summary>
/// Card F-019: a form POST whose body binds to nothing is a readable client error, never a
/// <c>500</c> with a <see cref="NullReferenceException"/>.
/// <para>
/// Minimal-API <c>[FromForm]</c> binding hands a handler <see langword="null"/> - not an empty
/// instance - when the request carries no field the model knows: a renamed field, an empty form, a
/// proxy that drops the body, a fuzzer. The fix is deliberately the product's existing error answer:
/// the endpoint hands the service an empty input, the service names the field, and the page gets its
/// usual <c>302 ?error=&lt;stable-code&gt;</c> - the shape every form page already renders. No new
/// status code, no new response shape. The delete/toggle/revoke/cancel/mint forms, whose model has no
/// field at all (so <see langword="null"/> is their normal request), are untouched and are pinned
/// here as well.
/// </para>
/// <para>
/// Every test posts a <em>real</em> form: the token is read from a rendered page, the request goes
/// through the real endpoint and the real service. A token-only body therefore reaches the handler
/// (that is what the guard is for), which is why each test asserts both a stable error code and that
/// the refused request left no trace. Remove a guard and the handler dereferences null, so the
/// redirect assertion sees a 500.
/// </para>
/// </summary>
[Trait("Category", "Forms")]
public sealed class FormBindingGuardTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // Secrets (F-012)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_secret_with_no_bound_field_is_the_page_error_and_stores_nothing()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>());

        var code = AssertErrorRedirect(response, SecretEndpoints.ListPath, SecretErrorCodes.NameRequired);
        Assert.Equal(SecretFields.Name, SecretErrorCodes.FieldFor(code));
        Assert.Empty(await host.ReadSecretsAsync(organizationId));
    }

    /// <summary>
    /// The reported reproduction: the browser-shaped field names (<c>CreateSecretForm.Name</c>) match no
    /// property of the model, so binding yields null exactly like an empty form.
    /// </summary>
    [Fact]
    public async Task Create_secret_with_field_names_that_match_no_property_is_the_page_error_too()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>
            {
                [$"{nameof(SecretEndpoints.CreateSecretForm)}.{SecretFields.Name}"] = "MISPLACED_NAME",
                [$"{nameof(SecretEndpoints.CreateSecretForm)}.{SecretFields.Value}"] = "misplaced-value",
            });

        var code = AssertErrorRedirect(response, SecretEndpoints.ListPath, SecretErrorCodes.NameRequired);
        Assert.Equal(SecretFields.Name, SecretErrorCodes.FieldFor(code));
        Assert.Empty(await host.ReadSecretsAsync(organizationId));
    }

    [Fact]
    public async Task Rotate_secret_with_no_bound_field_names_the_value_and_leaves_the_ciphertext_alone()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        var secret = await host.CreateSecretAsync(owner, organizationId, "ROTATE_GUARD", "original-value");

        using var response = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{secret.Id}/rotate",
            new Dictionary<string, string>());

        var code = AssertErrorRedirect(response, SecretEndpoints.ListPath, SecretErrorCodes.ValueRequired);
        Assert.Equal(SecretFields.Value, SecretErrorCodes.FieldFor(code));

        var unchanged = Assert.IsType<Secret>(await host.ReadSecretByNameAsync(organizationId, "ROTATE_GUARD"));
        Assert.Equal(secret.Ciphertext, unchanged.Ciphertext);
        Assert.Null(unchanged.RotatedAt);
    }

    /// <summary>The other half of the card's trigger: the field name is right, the value is empty.</summary>
    [Fact]
    public async Task Create_secret_with_an_empty_field_names_that_field()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();

        using (var emptyName = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>
            {
                [SecretFields.Name] = string.Empty,
                [SecretFields.Value] = "a-value",
            }))
        {
            var code = AssertErrorRedirect(emptyName, SecretEndpoints.ListPath, SecretErrorCodes.NameRequired);
            Assert.Equal(SecretFields.Name, SecretErrorCodes.FieldFor(code));
        }

        using (var emptyValue = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>
            {
                [SecretFields.Name] = "EMPTY_VALUE_GUARD",
                [SecretFields.Value] = string.Empty,
            }))
        {
            var code = AssertErrorRedirect(emptyValue, SecretEndpoints.ListPath, SecretErrorCodes.ValueRequired);
            Assert.Equal(SecretFields.Value, SecretErrorCodes.FieldFor(code));
        }

        Assert.Empty(await host.ReadSecretsAsync(organizationId));
    }

    /// <summary>The tolerant shape must not touch a well-formed body.</summary>
    [Fact]
    public async Task A_well_formed_create_still_stores_the_secret_and_redirects()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var organizationId = await host.ReadStarterOrganizationIdAsync();

        using var response = await host.PostSecretFormAsync(
            owner,
            "/secrets/create",
            new Dictionary<string, string>
            {
                [SecretFields.Name] = "FORM_GUARD_OK",
                [SecretFields.Value] = "a-real-secret-value",
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{SecretEndpoints.ListPath}?notice=created", response.Headers.Location?.OriginalString);
        Assert.NotNull(await host.ReadSecretByNameAsync(organizationId, "FORM_GUARD_OK"));
    }

    // ------------------------------------------------------------------
    // Projects (F-003)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_project_with_no_bound_field_is_the_form_error_and_creates_nothing()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        using var response = await PostAsync(owner, "/", "/projects/create", new Dictionary<string, string>());

        var code = AssertErrorRedirect(response, ProjectEndpoints.NewPath, ProjectErrorCodes.NameRequired);
        Assert.Equal(ProjectFields.Name, ProjectErrorCodes.FieldFor(code));
        Assert.Empty(await host.ReadProjectsAsync(organizationId));
    }

    [Fact]
    public async Task Edit_project_with_no_bound_field_is_the_form_error_and_changes_nothing()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
        var projectId = await host.AddProjectAsync(organizationId, "form-guard-app");

        using (var tokenOnly = await PostAsync(
            owner,
            "/",
            $"/projects/{projectId}/edit",
            new Dictionary<string, string>()))
        {
            var code = AssertErrorRedirect(
                tokenOnly,
                $"/projects/{projectId}/edit",
                ProjectErrorCodes.NameRequired);
            Assert.Equal(ProjectFields.Name, ProjectErrorCodes.FieldFor(code));
        }

        // The model's field names, prefixed by the type name: still nothing binds.
        using (var misplaced = await PostAsync(
            owner,
            "/",
            $"/projects/{projectId}/edit",
            new Dictionary<string, string>
            {
                [$"{nameof(ProjectEndpoints.ProjectForm)}.{ProjectFields.Name}"] = "hijacked-name",
            }))
        {
            AssertErrorRedirect(misplaced, $"/projects/{projectId}/edit", ProjectErrorCodes.NameRequired);
        }

        var project = await host.ReadProjectAsync(projectId);
        Assert.Equal("form-guard-app", project.Name);
        Assert.NotNull(project.RepositoryUrl);
    }

    /// <summary>
    /// The forms with no bindable field are deliberately left alone: for them a null form is the normal
    /// request (the antiforgery token is the whole payload), so the delete must still succeed.
    /// </summary>
    [Fact]
    public async Task Delete_project_still_accepts_its_empty_form()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
        var projectId = await host.AddProjectAsync(organizationId, "empty-form-app");

        using var response = await PostAsync(
            owner,
            "/",
            $"/projects/{projectId}/delete",
            new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{ProjectEndpoints.ListPath}?notice=deleted", response.Headers.Location?.OriginalString);
        Assert.Empty(await host.ReadProjectsAsync(organizationId));
    }

    // ------------------------------------------------------------------
    // Organizations (F-002)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Organization_forms_with_no_bound_field_answer_with_their_page_errors()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        using var member = await host.CreateMemberAsync();

        var organization = host.Organization;
        var organizationId = await host.ReadStarterOrganizationIdAsync();
        var membershipId = (await organization.ReadMembersAsync(organizationId))
            .Single(row => row.Email == SecretsTestHost.MemberEmail)
            .MembershipId;

        // Rename: the service names the name field.
        using (var rename = await PostAsync(owner, "/", "/organizations/settings", new Dictionary<string, string>()))
        {
            AssertErrorRedirect(rename, OrganizationEndpoints.SettingsPath, "invalid-name");
        }

        var renamed = (await organization.ReadOrganizationsAsync()).Single(row => row.Id == organizationId);
        Assert.Equal(BootstrapService.StarterOrganizationName, renamed.Name);

        // Rename with the type-prefixed field name: the same answer, still no rename.
        using (var misplaced = await PostAsync(
            owner,
            "/",
            "/organizations/settings",
            new Dictionary<string, string>
            {
                [$"{nameof(OrganizationEndpoints.RenameOrganizationForm)}.{nameof(OrganizationEndpoints.RenameOrganizationForm.Name)}"] =
                    "hijacked-organization",
            }))
        {
            AssertErrorRedirect(misplaced, OrganizationEndpoints.SettingsPath, "invalid-name");
        }

        // Invite: no email to validate, so the service's own email error.
        using (var invite = await PostAsync(
            owner,
            "/",
            "/organizations/members/invite",
            new Dictionary<string, string>()))
        {
            AssertErrorRedirect(invite, OrganizationEndpoints.MembersPath, "invalid-email");
        }

        // Role and remove: no membership id to address, which the service answers as a missing member.
        using (var role = await PostAsync(
            owner,
            "/",
            "/organizations/members/role",
            new Dictionary<string, string>
            {
                [nameof(OrganizationEndpoints.ChangeRoleForm.MembershipId)] = string.Empty,
                [nameof(OrganizationEndpoints.ChangeRoleForm.Role)] = string.Empty,
            }))
        {
            AssertErrorRedirect(role, OrganizationEndpoints.MembersPath, "member-not-found");
        }

        using (var remove = await PostAsync(
            owner,
            "/",
            "/organizations/members/remove",
            new Dictionary<string, string>()))
        {
            AssertErrorRedirect(remove, OrganizationEndpoints.MembersPath, "member-not-found");
        }

        // Nothing changed: the member is present and still a Member, and no invitation was created.
        var members = await organization.ReadMembersAsync(organizationId);
        Assert.Contains(members, row => row.MembershipId == membershipId && row.Role == OrganizationRole.Member);
        Assert.Equal(2, members.Count);

        // Switch: a switch with no target keeps the answer a malformed id already got - a readable 400 -
        // and the stored selection is unchanged.
        var before = await organization.ReadActiveOrganizationIdAsync(OrganizationTestHost.OwnerEmail);
        using (var switchResponse = await PostAsync(
            owner,
            "/",
            "/organizations/switch",
            new Dictionary<string, string>()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, switchResponse.StatusCode);
            Assert.Contains(
                "organization id",
                await switchResponse.Content.ReadAsStringAsync(CancellationToken),
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, await organization.ReadActiveOrganizationIdAsync(OrganizationTestHost.OwnerEmail));
    }

    // ------------------------------------------------------------------
    // Webhooks (F-017)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_webhook_with_no_bound_field_is_the_settings_error_and_creates_nothing()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        using var response = await PostAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/webhooks/create",
            new Dictionary<string, string>());

        var code = AssertErrorRedirect(
            response,
            $"/projects/{host.ProjectId}/settings",
            WebhookErrorCodes.NameRequired,
            queryKey: "webhookError");
        Assert.Equal(WebhookFields.Name, WebhookErrorCodes.FieldFor(code));

        Assert.Empty(await host.ReadWebhooksAsync());
    }

    // ------------------------------------------------------------------
    // Runs (F-006/F-014)
    // ------------------------------------------------------------------

    /// <summary>
    /// Every field of the trigger form is optional, so an empty input would validate and start a real
    /// run from a body the server could not read. A lost body is refused instead - and the project page
    /// says so, because "no run started and nothing visible happened" is not a readable error.
    /// </summary>
    [Fact]
    public async Task Trigger_with_no_bound_field_starts_no_run_and_the_page_says_so()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        using var ownerClient = owner;

        using (var tokenOnly = await PostAsync(
            ownerClient,
            $"/projects/{projectId}",
            $"/projects/{projectId}/runs/start",
            new Dictionary<string, string>()))
        {
            AssertErrorRedirect(
                tokenOnly,
                $"/projects/{projectId}",
                RunErrorCodes.FieldInvalid,
                queryKey: "runError");
        }

        // The refusal the server sends is a PRG: follow it and read what the operator would see.
        var page = await AuthTestHost.GetStringAsync(
            ownerClient,
            $"/projects/{projectId}?runError={RunErrorCodes.FieldInvalid}");
        Assert.Contains("id=\"run-form-error\"", page, StringComparison.Ordinal);
        Assert.Contains("could not be read", page, StringComparison.Ordinal);

        // A body that names the fields with the type prefix binds to nothing as well: still no run.
        using (var misplaced = await PostAsync(
            ownerClient,
            $"/projects/{projectId}",
            $"/projects/{projectId}/runs/start",
            new Dictionary<string, string>
            {
                [$"{nameof(RunEndpoints.StartRunForm)}.{nameof(RunEndpoints.StartRunForm.Branch)}"] = "main",
            }))
        {
            AssertErrorRedirect(
                misplaced,
                $"/projects/{projectId}",
                RunErrorCodes.FieldInvalid,
                queryKey: "runError");
        }

        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
    }

    // ------------------------------------------------------------------
    // The sweep
    // ------------------------------------------------------------------

    /// <summary>
    /// Every POST the pipeline antiforgery-validates - i.e. every endpoint that binds a form - read
    /// from the running host's endpoint table, so a form endpoint added later joins the sweep by
    /// itself. A token-only body must never produce a server fault from any of them, including the
    /// ones whose model has no field (where null is the normal request).
    /// </summary>
    [Fact]
    public async Task Every_form_endpoint_survives_a_body_that_binds_to_nothing()
    {
        using var host = await SecretsTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var endpoints = FormEndpoints(host);

        // A form endpoint the enumeration loses must fail loudly, not silently shrink the sweep.
        Assert.Contains("/secrets/create", endpoints, StringComparer.Ordinal);
        Assert.Contains("/projects/create", endpoints, StringComparer.Ordinal);
        Assert.Contains("/organizations/settings", endpoints, StringComparer.Ordinal);
        Assert.Contains("/workers/registration-tokens", endpoints, StringComparer.Ordinal);
        Assert.Contains(endpoints, endpoint => endpoint.EndsWith("/webhooks/create", StringComparison.Ordinal));
        Assert.Contains(endpoints, endpoint => endpoint.EndsWith("/runs/start", StringComparison.Ordinal));

        var faults = new List<string>();
        foreach (var endpoint in endpoints)
        {
            using var response = await PostAsync(owner, "/", endpoint, new Dictionary<string, string>());
            if ((int)response.StatusCode >= 500)
            {
                faults.Add($"{endpoint} answered {(int)response.StatusCode}");
            }
        }

        Assert.Empty(faults);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Posts through the real form path: the antiforgery token is read from a rendered page, the body
    /// is form-encoded, and redirects are asserted rather than followed.
    /// </summary>
    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string tokenPagePath,
        string postPath,
        IReadOnlyDictionary<string, string> fields) =>
        OrganizationTestHost.PostFormAsync(client, tokenPagePath, postPath, fields);

    /// <summary>
    /// Asserts the product's PRG validation answer: a redirect to the page that owns the form, carrying
    /// a stable error code - proof the handler ran and the service validated, rather than a framework
    /// rejection that never reached the endpoint. Returns the code so the caller can assert that it
    /// belongs to the field this form could not read.
    /// </summary>
    private static string AssertErrorRedirect(
        HttpResponseMessage response,
        string expectedPath,
        string expectedCode,
        string queryKey = "error")
    {
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Expected the form's PRG error redirect, got {(int)response.StatusCode}.");

        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.StartsWith(expectedPath, location, StringComparison.Ordinal);

        var match = Regex.Match(location, $"[?&]{Regex.Escape(queryKey)}=(?<code>[^&]+)");
        Assert.True(match.Success, $"The redirect carried no '{queryKey}' code: {location}");

        var code = Uri.UnescapeDataString(match.Groups["code"].Value);
        Assert.Equal(expectedCode, code);
        return code;
    }

    /// <summary>
    /// Every POST route the pipeline antiforgery-validates - i.e. every endpoint that binds a form -
    /// read from the running host's endpoint table.
    /// </summary>
    private static IReadOnlyList<string> FormEndpoints(SecretsTestHost host) =>
        host.Organization.Host.Server.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: true })
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                HttpMethods.Post,
                StringComparer.OrdinalIgnoreCase) == true)
            // The login/register/logout pages are Blazor SSR forms, not [FromForm] handlers: their
            // antiforgery state flows through the component provider, and posting an empty body to one
            // of them is a different (already tested) question.
            .Where(endpoint => !endpoint.RoutePattern.RawText!.StartsWith("/account", StringComparison.Ordinal))
            .Select(endpoint => Materialize(endpoint.RoutePattern.RawText!))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Fills a route template's parameters with values that cannot exist.</summary>
    private static string Materialize(string pathTemplate) =>
        Regex.Replace(
            pathTemplate,
            "\\{[^}]+\\}",
            match => match.Value.Contains(":guid", StringComparison.Ordinal)
                ? Guid.NewGuid().ToString("D")
                : "1",
            RegexOptions.CultureInvariant);
}
