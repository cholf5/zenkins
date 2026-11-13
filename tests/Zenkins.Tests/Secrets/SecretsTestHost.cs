using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Organizations;
using Zenkins.Server.Secrets;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// F-012's HTTP test host: the real <c>Program</c> pipeline over a temporary SQLite file (through
/// <see cref="OrganizationTestHost"/>), with a temporary Data Protection key ring so the suite never
/// writes into the repository's <c>src/Server/App_Data</c>.
/// <para>
/// The shape is the one the card needs: an Owner (alice, bootstrap) of the starter organization, an
/// ordinary Member (bob) of it, and a second organization owned by carol so a foreign secret id can be
/// probed. Assertions read the database through the product's system scope, never through the API
/// under test.
/// </para>
/// </summary>
public sealed class SecretsTestHost : IDisposable
{
    public const string OwnerEmail = OrganizationTestHost.OwnerEmail;
    public const string MemberEmail = "bob@example.test";
    public const string ForeignOwnerEmail = "carol@example.test";
    public const string ForeignOrganizationName = "Bravo";

    private SecretsTestHost(OrganizationTestHost organization, string keysDirectory)
    {
        Organization = organization;
        KeysDirectory = keysDirectory;
    }

    public OrganizationTestHost Organization { get; }

    public AuthTestHost Host => Organization.Host;

    /// <summary>The temporary key ring this host persisted (asserted by the key-ring tests).</summary>
    public string KeysDirectory { get; }

    public static async Task<SecretsTestHost> CreateAsync()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"zenkins-f012-keys-{Guid.NewGuid():N}");
        var organization = await OrganizationTestHost.CreateAsync(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SecretDataProtection.KeysDirectoryConfigurationKey] = keysDirectory,
            }));

        return new SecretsTestHost(organization, keysDirectory);
    }

    /// <summary>The starter organization the bootstrap Owner controls.</summary>
    public Task<Guid> ReadStarterOrganizationIdAsync() =>
        Organization.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

    public Task<HttpClient> SignInAsync(string email) => Organization.SignInAsync(email);

    /// <summary>The bootstrap Owner's client (a fresh cookie jar).</summary>
    public Task<HttpClient> SignInOwnerAsync() => Organization.SignInAsync(OwnerEmail);

    /// <summary>
    /// Registers the Member account and has the Owner invite it into the starter organization, so bob
    /// is an ordinary Member of the same organization the Owner manages.
    /// </summary>
    public async Task<HttpClient> CreateMemberAsync()
    {
        var memberClient = await Organization.RegisterUserAsync(MemberEmail);
        try
        {
            using var ownerClient = await Organization.SignInAsync(OwnerEmail);
            var invite = await OrganizationTestHost.PostFormAsync(
                ownerClient,
                OrganizationEndpoints.MembersPath,
                "/organizations/members/invite",
                new Dictionary<string, string> { ["Email"] = MemberEmail });
            if ((int)invite.StatusCode is not (302 or 303))
            {
                throw new InvalidOperationException(
                    $"Inviting the Member failed with {(int)invite.StatusCode}.");
            }

            // Resolve the Member's session once so the starter organization is persisted as its
            // selection (the deterministic fallback for a single membership).
            _ = await AuthTestHost.GetStringAsync(memberClient, "/");
            return memberClient;
        }
        catch
        {
            memberClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Registers carol and creates a second organization she owns. Returns the foreign organization id;
    /// the caller signs in with <see cref="ForeignOwnerEmail"/> to act as its Owner.
    /// </summary>
    public async Task<Guid> CreateForeignOrganizationAsync()
    {
        using var foreignOwner = await Organization.RegisterUserAsync(ForeignOwnerEmail);
        return await Organization.AddOrganizationAsync(
            ForeignOrganizationName,
            (ForeignOwnerEmail, OrganizationRole.Owner));
    }

    /// <summary>
    /// Posts a secrets form with the antiforgery token rendered on <paramref name="tokenPagePath"/>.
    /// The page is parameterised because a Member's <c>/secrets</c> is a 403 (no token to read), while
    /// the shell renders the token on every page they can load.
    /// </summary>
    public Task<HttpResponseMessage> PostSecretFormAsync(
        HttpClient client,
        string postPath,
        IReadOnlyDictionary<string, string> fields,
        string tokenPagePath = SecretEndpoints.ListPath) =>
        OrganizationTestHost.PostFormAsync(client, tokenPagePath, postPath, fields);

    /// <summary>Creates a secret through the real endpoint and returns its persisted row.</summary>
    public async Task<Secret> CreateSecretAsync(
        HttpClient ownerClient,
        Guid organizationId,
        string name,
        string value)
    {
        using var response = await PostSecretFormAsync(
            ownerClient,
            "/secrets/create",
            new Dictionary<string, string> { ["Name"] = name, ["Value"] = value });

        if ((int)response.StatusCode is not (302 or 303))
        {
            throw new InvalidOperationException(
                $"Creating secret '{name}' failed with {(int)response.StatusCode}.");
        }

        return (await ReadSecretByNameAsync(organizationId, name))
            ?? throw new InvalidOperationException($"Secret '{name}' was not persisted.");
    }

    public async Task<Secret?> ReadSecretByNameAsync(Guid organizationId, string name) =>
        await Host.InScopeAsync(async services =>
        {
            var secrets = await ReadAcrossTenantsAsync(services, organizationId);
            return secrets.SingleOrDefault(secret => string.Equals(secret.Name, name, StringComparison.Ordinal));
        });

    public async Task<Secret?> ReadSecretByIdAsync(Guid secretId) =>
        await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return (await context.SystemQueryAsync<Secret>(secret => secret.Id == secretId))
                .SingleOrDefault();
        });

    public async Task<IReadOnlyList<Secret>> ReadSecretsAsync(Guid organizationId) =>
        await Host.InScopeAsync(services => ReadAcrossTenantsAsync(services, organizationId));

    /// <summary>
    /// Decrypts one secret through the internal run-execution seam (F-013/F-015) with the given tenant
    /// selected. This is the only read path in the product; no endpoint or page calls it.
    /// </summary>
    public async Task<string?> ReadDecryptedValueAsync(Guid organizationId, string name) =>
        await Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = organizationId;
            return await services.GetRequiredService<SecretService>().TryGetValueAsync(name);
        });

    /// <summary>
    /// Reads the secret list through the service as <paramref name="userId"/> with the given tenant
    /// selected - the second gate behind the page's session check.
    /// </summary>
    public async Task<SecretListResult> ReadSecretsAsAsync(Guid organizationId, string userId) =>
        await Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = organizationId;
            return await services.GetRequiredService<SecretService>().GetSecretsAsync(userId);
        });

    /// <summary>The Identity user id of a seeded account.</summary>
    public async Task<string> ReadUserIdAsync(string email) =>
        (await Organization.ReadUserAsync(email)).Id;

    /// <summary>The Data Protection key files persisted in this host's temporary ring.</summary>
    public string[] KeyFiles() =>
        Directory.Exists(KeysDirectory) ? Directory.GetFiles(KeysDirectory, "key-*.xml") : [];

    private static async Task<IReadOnlyList<Secret>> ReadAcrossTenantsAsync(
        IServiceProvider services,
        Guid organizationId)
    {
        var systemDataScope = services.GetRequiredService<ISystemDataScope>();
        await using var context = await CreateContextAsync(services);
        using var scope = systemDataScope.BeginScope();
        return await context.SystemQueryAsync<Secret>(secret => secret.OrganizationId == organizationId);
    }

    private static Task<ZenkinsDbContext> CreateContextAsync(IServiceProvider services) =>
        services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>().CreateDbContextAsync();

    public void Dispose()
    {
        Organization.Dispose();

        try
        {
            if (Directory.Exists(KeysDirectory))
            {
                Directory.Delete(KeysDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked read handle in a failing test must not mask the real failure; the directory
            // lives in the OS temp folder and is never committed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
