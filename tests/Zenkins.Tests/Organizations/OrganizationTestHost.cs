using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Organizations;

/// <summary>A user/membership row as the tests assert on it.</summary>
public sealed record MemberRow(Guid MembershipId, string UserId, string Email, OrganizationRole Role);

/// <summary>
/// F-002's HTTP test host: the real <c>Program</c> pipeline over a temporary SQLite file (through
/// <see cref="AuthTestHost"/>), with helpers that seed a second organization, invite users, and read
/// the persisted server-side selection.
/// <para>
/// Every helper that writes tenant-owned rows goes through <see cref="ISystemDataScope"/> because
/// those rows exist before a tenant is selected - the same shape the product's bootstrap and fixture
/// use. Nothing here weakens the product's isolation guards.
/// </para>
/// </summary>
public sealed class OrganizationTestHost : IDisposable
{
    public const string OwnerEmail = "alice@example.test";

    private OrganizationTestHost(AuthTestHost host) => Host = host;

    public AuthTestHost Host { get; }

    /// <summary>Starts an empty host and registers the first account (Owner of the starter organization).</summary>
    public static async Task<OrganizationTestHost> CreateAsync(
        Action<IConfigurationBuilder>? configureAppConfiguration = null)
    {
        var host = new AuthTestHost(configureAppConfiguration: configureAppConfiguration);
        using var client = host.CreateClient();
        var response = await AuthTestHost.RegisterAsync(client, OwnerEmail);
        if ((int)response.StatusCode is not (302 or 303))
        {
            throw new InvalidOperationException(
                $"Registering the owner failed with {(int)response.StatusCode}.");
        }

        return new OrganizationTestHost(host);
    }

    /// <summary>Registers a second account through the real form. Returns a client that is signed in as it.</summary>
    public async Task<HttpClient> RegisterUserAsync(string email)
    {
        var client = Host.CreateClient();
        var response = await AuthTestHost.RegisterAsync(client, email);
        if ((int)response.StatusCode is not (302 or 303))
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"Registering '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }

    /// <summary>Signs in an existing account on a fresh cookie jar.</summary>
    public async Task<HttpClient> SignInAsync(string email)
    {
        var client = Host.CreateClient();
        var response = await AuthTestHost.LoginAsync(client, email);
        if ((int)response.StatusCode is not (302 or 303))
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"Signing in as '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }

    public async Task<IReadOnlyList<Organization>> ReadOrganizationsAsync() =>
        await Host.InScopeAsync(async services =>
        {
            await using var context = await CreateContextAsync(services);
            // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset column.
            return (IReadOnlyList<Organization>)(await context.Organizations.ToListAsync())
                .OrderBy(organization => organization.CreatedAt)
                .ThenBy(organization => organization.Id)
                .ToList();
        });

    public async Task<Guid> ReadOrganizationIdAsync(string name) =>
        (await ReadOrganizationsAsync()).Single(organization => organization.Name == name).Id;

    public async Task<User> ReadUserAsync(string email) =>
        await Host.InScopeAsync(async services =>
        {
            await using var context = await CreateContextAsync(services);
            return await context.Users.SingleAsync(user => user.Email == email);
        });

    public async Task<Guid?> ReadActiveOrganizationIdAsync(string email) =>
        (await ReadUserAsync(email)).ActiveOrganizationId;

    public async Task<IReadOnlyList<MemberRow>> ReadMembersAsync(Guid organizationId) =>
        await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var memberships = await context.SystemQueryAsync<OrganizationMember>(
                member => member.OrganizationId == organizationId);
            var userIds = memberships.Select(member => member.UserId).ToArray();
            var users = await context.Users
                .Where(user => userIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id);

            return (IReadOnlyList<MemberRow>)memberships
                .Select(member => new MemberRow(
                    member.Id,
                    member.UserId,
                    users[member.UserId].Email ?? "(no email)",
                    member.Role))
                .ToArray();
        });

    public async Task<Guid> AddOrganizationAsync(
        string name,
        params (string Email, OrganizationRole Role)[] members) =>
        await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var organization = new Organization { Name = name };
            context.Organizations.Add(organization);

            foreach (var (email, role) in members)
            {
                var user = await context.Users.SingleAsync(candidate => candidate.Email == email);
                context.OrganizationMembers.Add(new OrganizationMember
                {
                    OrganizationId = organization.Id,
                    UserId = user.Id,
                    Role = role,
                });
            }

            await context.SaveChangesAsync();
            return organization.Id;
        });

    public async Task<Guid> AddProjectAsync(
        Guid organizationId,
        string name,
        string repositoryUrl = "https://example.test/repo.git",
        string defaultBranch = "main") =>
        await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var project = new Project
            {
                OrganizationId = organizationId,
                Name = name,
                RepositoryUrl = repositoryUrl,
                DefaultBranch = defaultBranch,
            };

            context.Projects.Add(project);
            await context.SaveChangesAsync();
            return project.Id;
        });

    /// <summary>All projects of one organization, read across tenants for assertions.</summary>
    public async Task<IReadOnlyList<Project>> ReadProjectsAsync(Guid organizationId) =>
        await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            return (IReadOnlyList<Project>)await context.SystemQueryAsync<Project>(
                project => project.OrganizationId == organizationId);
        });

    /// <summary>One project read across tenants (the assertion side of a cross-tenant probe).</summary>
    public async Task<Project> ReadProjectAsync(Guid projectId) =>
        await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            return (await context.SystemQueryAsync<Project>(project => project.Id == projectId)).Single();
        });

    /// <summary>
    /// Overwrites the persisted selection directly, to simulate a stale or forged value that must be
    /// rejected during resolution. The product never does this; only the switch endpoint writes the
    /// selection, and only for a membership.
    /// </summary>
    public async Task SetPersistedSelectionAsync(string email, Guid? organizationId) =>
        await Host.InScopeAsync(async services =>
        {
            await using var context = await CreateContextAsync(services);
            var user = await context.Users.SingleAsync(candidate => candidate.Email == email);
            user.ActiveOrganizationId = organizationId;
            await context.SaveChangesAsync();
            return true;
        });

    public async Task RemoveMembershipAsync(Guid membershipId) =>
        await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var membership = await context.SystemQueryAsync<OrganizationMember>(member => member.Id == membershipId);
            context.OrganizationMembers.Remove(membership.Single());
            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>Posts a form with the antiforgery token from <paramref name="pageUrl"/>.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string pageUrl,
        string postUrl,
        IReadOnlyDictionary<string, string> fields)
    {
        var html = await AuthTestHost.GetStringAsync(client, pageUrl);
        var token = ExtractAntiforgeryMetaToken(html);

        var values = new Dictionary<string, string>(fields, StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = token,
        };

        return await client.PostAsync(postUrl, new FormUrlEncodedContent(values));
    }

    /// <summary>Reads the request token App.razor renders into the page for the plain HTML forms.</summary>
    public static string ExtractAntiforgeryMetaToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "<meta name=\"request-verification-token\" content=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("The page did not render an antiforgery meta token.");
        }

        return match.Groups["token"].Value;
    }

    private static async Task<ZenkinsDbContext> CreateContextAsync(IServiceProvider services) =>
        await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>().CreateDbContextAsync();

    public void Dispose() => Host.Dispose();
}
