using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Tenancy;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Two-organization isolation fixture (card Q-001): the shared shape every tenancy test needs.
/// <para>
/// Organization A (<c>Alpha</c>) owns one project (<c>alpha-app</c>) and has two members, its owner
/// <c>alice</c> and the ordinary member <c>bob</c>. Organization B (<c>Beta</c>) owns one project
/// (<c>beta-app</c>) and has one member, its owner <c>carol</c>. Everything is created by a single
/// <see cref="Seed"/> routine inside an <see cref="ISystemDataScope"/> scope, because the first
/// organizations and memberships exist before any tenant can be selected.
/// </para>
/// <para>
/// The fixture owns one temporary SQLite file (migrated with the product's real migration set,
/// never the repository's <c>App_Data</c>) plus the scoped tenant context the tests re-point. It is
/// disposable: <see cref="Dispose"/> drops the connection pools and deletes the file and its
/// journals. Construct it per test class (<c>IClassFixture&lt;TwoOrgsFixture&gt;</c>) for read-mostly
/// probes, or per test when a test mutates rows.
/// </para>
/// <para>
/// The ids are fixed constants rather than generated values so a test failure reads as prose
/// (<c>ProjectBId</c>) and so the HTTP probe can put a literal id in a URL. The fixture is the only
/// thing that writes them, and it does so under a system scope because inserting tenant-owned rows
/// requires an explicit organization there.
/// </para>
/// </summary>
public sealed class TwoOrgsFixture : IDisposable
{
    // ---------------------------------------------------------------------------------------------
    // The fixture shape. Tests reference these names instead of hard-coded strings.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Tenant root A.</summary>
    public static readonly Guid OrgAId = new("a0000000-0000-4000-8000-000000000001");

    /// <summary>Tenant root B.</summary>
    public static readonly Guid OrgBId = new("b0000000-0000-4000-8000-000000000002");

    /// <summary>A's only project, the positive control for every probe.</summary>
    public static readonly Guid ProjectAId = new("a0000000-0000-4000-8000-000000000011");

    /// <summary>B's only project, the id a cross-tenant probe substitutes.</summary>
    public static readonly Guid ProjectBId = new("b0000000-0000-4000-8000-000000000012");

    /// <summary>
    /// Card B-008: the payload hash the fixture's idempotency records carry. It is a real (lowercase hex
    /// SHA-256 shaped) value so a probe reads the same column the product writes.
    /// </summary>
    public const string FixtureRequestHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>A's owner membership row.</summary>
    public static readonly Guid OrgAOwnerMembershipId = new("a0000000-0000-4000-8000-000000000021");

    /// <summary>A's ordinary membership row.</summary>
    public static readonly Guid OrgAMemberMembershipId = new("a0000000-0000-4000-8000-000000000022");

    /// <summary>B's owner membership row.</summary>
    public static readonly Guid OrgBOwnerMembershipId = new("b0000000-0000-4000-8000-000000000023");

    /// <summary>A's only worker, the positive control for the F-004 probes.</summary>
    public static readonly Guid WorkerAId = new("a0000000-0000-4000-8000-000000000031");

    /// <summary>B's only worker.</summary>
    public static readonly Guid WorkerBId = new("b0000000-0000-4000-8000-000000000032");

    /// <summary>A worker token of A's worker (the id a cross-tenant probe substitutes).</summary>
    public static readonly Guid WorkerTokenAId = new("a0000000-0000-4000-8000-000000000041");

    /// <summary>A worker token of B's worker.</summary>
    public static readonly Guid WorkerTokenBId = new("b0000000-0000-4000-8000-000000000042");

    /// <summary>A's only build run, the positive control for the F-006 probes.</summary>
    public static readonly Guid BuildRunAId = new("a0000000-0000-4000-8000-000000000051");

    /// <summary>B's only build run.</summary>
    public static readonly Guid BuildRunBId = new("b0000000-0000-4000-8000-000000000052");

    /// <summary>A's only recorded step (of A's only run), the positive control for the F-015 probes.</summary>
    public static readonly Guid BuildStepAId = new("a0000000-0000-4000-8000-000000000053");

    /// <summary>B's only recorded step.</summary>
    public static readonly Guid BuildStepBId = new("b0000000-0000-4000-8000-000000000054");

    /// <summary>A's only secret, the positive control for the F-012 probes.</summary>
    public static readonly Guid SecretAId = new("a0000000-0000-4000-8000-000000000061");

    /// <summary>B's only secret.</summary>
    public static readonly Guid SecretBId = new("b0000000-0000-4000-8000-000000000062");

    /// <summary>A's only artifact (of A's only run), the positive control for the F-016 probes.</summary>
    public static readonly Guid ArtifactAId = new("a0000000-0000-4000-8000-000000000081");

    /// <summary>B's only artifact.</summary>
    public static readonly Guid ArtifactBId = new("b0000000-0000-4000-8000-000000000082");

    /// <summary>A's only Git credential, the positive control for the F-013 probes.</summary>
    public static readonly Guid GitCredentialAId = new("a0000000-0000-4000-8000-000000000071");

    /// <summary>B's only Git credential.</summary>
    public static readonly Guid GitCredentialBId = new("b0000000-0000-4000-8000-000000000072");

    /// <summary>A's only webhook, the positive control for the F-017 probes.</summary>
    public static readonly Guid WebhookAId = new("a0000000-0000-4000-8000-000000000091");

    /// <summary>B's only webhook.</summary>
    public static readonly Guid WebhookBId = new("b0000000-0000-4000-8000-000000000092");

    /// <summary>A's only delivery record (of A's only webhook).</summary>
    public static readonly Guid WebhookDeliveryAId = new("a0000000-0000-4000-8000-000000000093");

    /// <summary>B's only delivery record.</summary>
    public static readonly Guid WebhookDeliveryBId = new("b0000000-0000-4000-8000-000000000094");

    /// <summary>F-024: A's only cron schedule, the positive control for the schedule probes.</summary>
    public static readonly Guid ScheduleAId = new("a0000000-0000-4000-8000-0000000000b1");

    /// <summary>F-024: B's only cron schedule.</summary>
    public static readonly Guid ScheduleBId = new("b0000000-0000-4000-8000-0000000000b2");

    /// <summary>F-024: A's only due-time row (of A's only schedule).</summary>
    public static readonly Guid ScheduleOccurrenceAId = new("a0000000-0000-4000-8000-0000000000b3");

    /// <summary>F-024: B's only due-time row.</summary>
    public static readonly Guid ScheduleOccurrenceBId = new("b0000000-0000-4000-8000-0000000000b4");

    /// <summary>F-023: A's only branch job, the positive control for the branch probes.</summary>
    public static readonly Guid BranchAId = new("a0000000-0000-4000-8000-0000000000c1");

    /// <summary>F-023: B's only branch job (the id a cross-tenant probe substitutes).</summary>
    public static readonly Guid BranchBId = new("b0000000-0000-4000-8000-0000000000c2");

    /// <summary>F-034: A's only organization environment variable, the positive control for its probes.</summary>
    public static readonly Guid EnvironmentAId = new("a0000000-0000-4000-8000-0000000000d1");

    /// <summary>F-034: B's only organization environment variable (the id a cross-tenant probe substitutes).</summary>
    public static readonly Guid EnvironmentBId = new("b0000000-0000-4000-8000-0000000000d2");

    /// <summary>F-027: A's only API token, the positive control for the API probes.</summary>
    public static readonly Guid ApiTokenAId = new("a0000000-0000-4000-8000-0000000000a1");

    /// <summary>F-027: B's only API token (the id a cross-tenant probe substitutes).</summary>
    public static readonly Guid ApiTokenBId = new("b0000000-0000-4000-8000-0000000000a2");

    public const string OrgAName = "Alpha";
    public const string OrgBName = "Beta";

    public const string OrgAOwnerUserId = "alice";
    public const string OrgAMemberUserId = "bob";
    public const string OrgBOwnerUserId = "carol";

    /// <summary>
    /// Password of the three seeded accounts, so the authenticated HTTP probes can sign in through
    /// the real login form instead of a test-only tenant header. Same policy-compliant value the
    /// auth tests use.
    /// </summary>
    public const string UserPassword = Zenkins.Tests.Auth.AuthTestHost.ValidPassword;

    public const string ProjectAName = "alpha-app";
    public const string ProjectBName = "beta-app";
    public const string ProjectABranch = "main";
    public const string ProjectBBranch = "develop";
    public const string ProjectARepositoryUrl = "https://example.test/alpha.git";
    public const string ProjectBRepositoryUrl = "https://example.test/beta.git";

    // F-034: one organization environment variable per organization. Both the names and the values are
    // distinct per tenant, so a page or a claim that leaked one would be visible in the other's scan.
    public const string EnvironmentAName = "ALPHA_REGISTRY_HOST";
    public const string EnvironmentBName = "BETA_REGISTRY_HOST";
    public const string EnvironmentAValue = "registry.alpha.internal";
    public const string EnvironmentBValue = "registry.beta.internal";

    public const string WorkerAName = "alpha-agent";
    public const string WorkerBName = "beta-agent";

    // F-012: one secret per organization. The ciphertext is a well-shaped placeholder, never a real
    // Data Protection blob: the tenancy probes read rows and never decrypt them, so the fixture must
    // not depend on this process's key ring.
    public const string SecretAName = "ALPHA_TOKEN";
    public const string SecretBName = "BETA_TOKEN";
    public const string SecretACiphertext = "CfDJ8-fixture-alpha-ciphertext";
    public const string SecretBCiphertext = "CfDJ8-fixture-beta-ciphertext";

    // F-013: one Git credential per organization, same placeholder-ciphertext discipline as the
    // secrets above (the tenancy probes never decrypt).
    public const string GitCredentialAName = "ALPHA_DEPLOY_TOKEN";
    public const string GitCredentialBName = "BETA_DEPLOY_TOKEN";
    public const string GitCredentialACiphertext = "CfDJ8-fixture-alpha-git-ciphertext";
    public const string GitCredentialBCiphertext = "CfDJ8-fixture-beta-git-ciphertext";

    // F-016: one ready artifact per organization's run. The manifest is a well-formed (empty-file) record:
    // the tenancy probes never download, they only prove the row is invisible to the other tenant.
    public const string ArtifactAName = "bin/Release";
    public const string ArtifactBName = "out/web";
    public const string ArtifactManifestJson = "{\"version\":1,\"fileCount\":0,\"totalBytes\":0,\"files\":[]}";

    // F-017: one webhook per organization (and one delivery record each). The ciphertext is a placeholder
    // under the same discipline as the secrets above - the tenancy probes never decrypt - and the dedupe
    // key is a well-shaped "id:" hash.
    public const string WebhookAName = "alpha-hook";
    public const string WebhookBName = "beta-hook";
    public const string WebhookACiphertext = "CfDJ8-fixture-alpha-webhook-ciphertext";
    public const string WebhookBCiphertext = "CfDJ8-fixture-beta-webhook-ciphertext";
    public const string WebhookDedupeKey = "id:0000000000000000000000000000000000000000000000000000000000000000";

    // F-027: one API token per organization. Only the SHA-256 hash is stored, so the probes' plaintext is
    // a fixture constant whose hash the seed writes - the same discipline as the worker tokens above.
    public const string ApiTokenAName = "alpha-api";
    public const string ApiTokenBName = "beta-api";
    public const string ApiTokenAPlaintext = "zapi_alpha-fixture-token";
    public const string ApiTokenBPlaintext = "zapi_beta-fixture-token";
    public const string ApiTokenScopesAll = "trigger,read,cancel";

    private readonly string _databasePath;
    private bool _migrated;

    public TwoOrgsFixture()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"zenkins-q001-{Guid.NewGuid():N}.db");

        TenantContext = new TenantContext();
        SystemDataScope = new SystemDataScope();
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ZenkinsDataOptions.ProviderConfigurationKey] = nameof(DatabaseProvider.Sqlite),
                [ZenkinsDataOptions.ConnectionStringConfigurationKey] = ConnectionString,
            })
            .Build();

        Seed();
    }

    /// <summary>Absolute path of this fixture's temporary SQLite file (never inside the repository).</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// This fixture's artifact store root (card F-016): a sibling of the temporary database, because a host that
    /// boots the real <c>Program</c> creates and asserts it at startup - it must never be
    /// <c>src/Server/App_Data/artifacts</c>, which <c>**/App_Data/</c> hides from <c>git status</c>.
    /// </summary>
    public string ArtifactsPath => _databasePath + "-artifacts";

    /// <summary>Connection string for the same file, for hosts and options built outside the fixture.</summary>
    public string ConnectionString => $"Data Source={_databasePath}";

    /// <summary>The scoped tenant context every fixture-created context uses.</summary>
    public TenantContext TenantContext { get; }

    /// <summary>The scoped escape hatch the seed and the system-wide assertions need.</summary>
    public ISystemDataScope SystemDataScope { get; }

    /// <summary>Configuration with only the data-layer keys, for <c>AddZenkinsData</c> style tests.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>Active organization, or <see langword="null"/> when no tenant is selected.</summary>
    public Guid? ActiveOrganizationId => TenantContext.OrganizationId;

    /// <summary>Re-points the active tenant mid-test. The data layer detects the switch through the revision.</summary>
    public void UseOrganization(Guid organizationId) => TenantContext.OrganizationId = organizationId;

    /// <summary>Activates organization A, keeping the full setup readable in tests.</summary>
    public void UseOrganizationA() => UseOrganization(OrgAId);

    /// <summary>Activates organization B.</summary>
    public void UseOrganizationB() => UseOrganization(OrgBId);

    /// <summary>Simulates an anonymous request: no tenant selected, so no tenant-owned row matches.</summary>
    public void ClearOrganizationSelection() => TenantContext.OrganizationId = null;

    /// <summary>
    /// Creates a migrated context on the fixture's file, bound to the fixture's tenant context.
    /// The caller owns (and disposes) it.
    /// </summary>
    public ZenkinsDbContext CreateContext() => CreateContext(TenantContext);

    /// <summary>
    /// Creates a context on the fixture's file with a caller-supplied <see cref="ITenantContext"/>
    /// (used to prove the data layer does not depend on the concrete type).
    /// </summary>
    public ZenkinsDbContext CreateContext(ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Sqlite, ConnectionString))
            .Options;

        var context = new ZenkinsDbContext(options, tenantContext, SystemDataScope);
        if (!_migrated)
        {
            context.Database.Migrate();
            _migrated = true;
        }

        return context;
    }

    /// <summary>
    /// Reads rows across every tenant through the product's escape hatch. This is how the harness
    /// derives its expectations and how a probe proves "nothing persisted" without trusting the
    /// filtered view it is testing.
    /// </summary>
    public async Task<List<TEntity>> ReadAcrossTenantsAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        using var scope = SystemDataScope.BeginScope();
        await using var context = CreateContext();
        return await context.SystemQueryAsync(predicate, cancellationToken);
    }

    /// <summary>
    /// Counts one tenant's rows across all tenants. Used by write probes to prove a rejected write
    /// left both the active and the foreign tenant untouched.
    /// </summary>
    public async Task<int> CountAcrossTenantsAsync<TEntity>(
        Guid organizationId,
        CancellationToken cancellationToken = default)
        where TEntity : class, ITenantOwned =>
        (await ReadAcrossTenantsAsync<TEntity>(
            entity => entity.OrganizationId == organizationId,
            cancellationToken)).Count;

    private void Seed()
    {
        // Bootstrap data exists before any tenant is selected, so it can only be written through
        // the system scope - the same shape F-001's first-run bootstrap uses.
        using var scope = SystemDataScope.BeginScope();
        using var context = CreateContext();

        // F-001 made OrganizationMember.UserId a real foreign key to AspNetUsers, so the fixture
        // seeds the three identity accounts its memberships refer to. Ids stay the readable strings
        // the existing probes assert on; emails exist because Identity stores one per account.
        context.Users.AddRange(
            CreateUser(OrgAOwnerUserId, "alice@example.test"),
            CreateUser(OrgAMemberUserId, "bob@example.test"),
            CreateUser(OrgBOwnerUserId, "carol@example.test"));
        context.SaveChanges();

        context.Organizations.AddRange(
            new Organization { Id = OrgAId, Name = OrgAName },
            new Organization { Id = OrgBId, Name = OrgBName });

        context.OrganizationMembers.AddRange(
            new OrganizationMember
            {
                Id = OrgAOwnerMembershipId,
                OrganizationId = OrgAId,
                UserId = OrgAOwnerUserId,
                Role = OrganizationRole.Owner,
            },
            new OrganizationMember
            {
                Id = OrgAMemberMembershipId,
                OrganizationId = OrgAId,
                UserId = OrgAMemberUserId,
                Role = OrganizationRole.Member,
            },
            new OrganizationMember
            {
                Id = OrgBOwnerMembershipId,
                OrganizationId = OrgBId,
                UserId = OrgBOwnerUserId,
                Role = OrganizationRole.Owner,
            });

        context.Projects.AddRange(
            new Project
            {
                Id = ProjectAId,
                OrganizationId = OrgAId,
                Name = ProjectAName,
                RepositoryUrl = ProjectARepositoryUrl,
                DefaultBranch = ProjectABranch,
            },
            new Project
            {
                Id = ProjectBId,
                OrganizationId = OrgBId,
                Name = ProjectBName,
                RepositoryUrl = ProjectBRepositoryUrl,
                DefaultBranch = ProjectBBranch,
            });

        // F-004: one worker per organization, each with one worker token (the token hash is the
        // SHA-256 of an arbitrary offline string - the plaintext never exists in tests).
        context.Workers.AddRange(
            new WorkerEntity
            {
                Id = WorkerAId,
                OrganizationId = OrgAId,
                Name = WorkerAName,
                Os = "Linux",
                Architecture = "X64",
                Version = "1.0.0",
                Tags = "dotnet",
                CpuCount = 4,
                MaxConcurrency = 1,
                Status = WorkerStatus.Offline,
            },
            new WorkerEntity
            {
                Id = WorkerBId,
                OrganizationId = OrgBId,
                Name = WorkerBName,
                Os = "macOS",
                Architecture = "Arm64",
                Version = "1.0.0",
                Tags = "dotnet",
                CpuCount = 8,
                MaxConcurrency = 1,
                Status = WorkerStatus.Online,
            });

        context.WorkerTokens.AddRange(
            new WorkerToken
            {
                Id = WorkerTokenAId,
                OrganizationId = OrgAId,
                WorkerId = WorkerAId,
                Kind = WorkerTokenKind.Worker,
                TokenHash = SeededTokenHash(WorkerAName),
            },
            new WorkerToken
            {
                Id = WorkerTokenBId,
                OrganizationId = OrgBId,
                WorkerId = WorkerBId,
                Kind = WorkerTokenKind.Worker,
                TokenHash = SeededTokenHash(WorkerBName),
            });

        // F-006: one queued run per organization, both unleased (the state every run starts in).
        context.BuildRuns.AddRange(
            new BuildRun
            {
                Id = BuildRunAId,
                OrganizationId = OrgAId,
                ProjectId = ProjectAId,
                TriggerType = BuildRunTriggerType.Manual,
                TriggerUserId = OrgAOwnerUserId,
                RequiredTags = string.Empty,
                GitBranch = "main",
                PipelinePath = "pipeline.lua",

                // F-036: each organization's first run is #1 (numbering is per project).
                Number = 1,
            },
            new BuildRun
            {
                Id = BuildRunBId,
                OrganizationId = OrgBId,
                ProjectId = ProjectBId,
                TriggerType = BuildRunTriggerType.Manual,
                TriggerUserId = OrgBOwnerUserId,
                RequiredTags = "dotnet",
                GitBranch = "main",
                PipelinePath = "pipeline.lua",

                // F-036: numbering is per project, so organization B's first run is #1 as well.
                Number = 1,
            });

        // F-027: one API token per organization, all three scopes, so an API probe has a working
        // credential on each side without minting through the form first.
        context.ApiTokens.AddRange(
            new ApiToken
            {
                Id = ApiTokenAId,
                OrganizationId = OrgAId,
                Name = ApiTokenAName,
                TokenHash = Zenkins.Server.Api.ApiTokenCrypto.Hash(ApiTokenAPlaintext),
                Scopes = ApiTokenScopesAll,
                CreatedByUserId = OrgAOwnerUserId,
            },
            new ApiToken
            {
                Id = ApiTokenBId,
                OrganizationId = OrgBId,
                Name = ApiTokenBName,
                TokenHash = Zenkins.Server.Api.ApiTokenCrypto.Hash(ApiTokenBPlaintext),
                Scopes = ApiTokenScopesAll,
                CreatedByUserId = OrgBOwnerUserId,
            });

        // F-027: one idempotency record per organization (the token + run it queued), so the entity's
        // tenant filter has a row on each side. Card B-008 added the project and the payload hash the
        // key is bound to.
        context.ApiIdempotencyRecords.AddRange(
            new ApiIdempotencyRecord
            {
                OrganizationId = OrgAId,
                ApiTokenId = ApiTokenAId,
                Key = "fixture-key",
                ProjectId = ProjectAId,
                RequestHash = FixtureRequestHash,
                RunId = BuildRunAId,
            },
            new ApiIdempotencyRecord
            {
                OrganizationId = OrgBId,
                ApiTokenId = ApiTokenBId,
                Key = "fixture-key",
                ProjectId = ProjectBId,
                RequestHash = FixtureRequestHash,
                RunId = BuildRunBId,
            });

        // F-008: one log line per organization's run, the row every log probe reads.
        context.BuildLogs.AddRange(
            new BuildLog
            {
                OrganizationId = OrgAId,
                RunId = BuildRunAId,
                Seq = 1,
                Stream = LogStream.StdOut,
                Message = "alpha-line",
                Timestamp = DateTimeOffset.UtcNow,
            },
            new BuildLog
            {
                OrganizationId = OrgBId,
                RunId = BuildRunBId,
                Seq = 1,
                Stream = LogStream.StdErr,
                Message = "beta-line",
                Timestamp = DateTimeOffset.UtcNow,
            });

        // F-015: one recorded step per organization's run, so the read/write/find probes are not vacuous
        // (and so the unique (RunId, Index) index has a row to collide with).
        context.BuildSteps.AddRange(
            new BuildStep
            {
                Id = BuildStepAId,
                OrganizationId = OrgAId,
                RunId = BuildRunAId,
                Index = 1,
                Name = "Restore",
                Status = PipelineStepStatus.Success,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow,
                ExitCode = 0,
            },
            new BuildStep
            {
                Id = BuildStepBId,
                OrganizationId = OrgBId,
                RunId = BuildRunBId,
                Index = 1,
                Name = "Build",
                Status = PipelineStepStatus.Failed,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow,
                ExitCode = 3,
            });

        // F-012: one secret per organization (the read/write/unique-index probes need a row on each
        // side). CreatedByUserId is the seeded Owner, which is what the product records.
        context.Secrets.AddRange(
            new Secret
            {
                Id = SecretAId,
                OrganizationId = OrgAId,
                Name = SecretAName,
                Ciphertext = SecretACiphertext,
                CreatedByUserId = OrgAOwnerUserId,
            },
            new Secret
            {
                Id = SecretBId,
                OrganizationId = OrgBId,
                Name = SecretBName,
                Ciphertext = SecretBCiphertext,
                CreatedByUserId = OrgBOwnerUserId,
            });

        // F-016: one artifact per organization, both Ready, both hanging off that organization's run - the
        // read/write/find probes need a row on each side, and the unique (RunId, Name) index needs one to
        // collide with.
        context.Set<Artifact>().AddRange(
            new Artifact
            {
                Id = ArtifactAId,
                OrganizationId = OrgAId,
                RunId = BuildRunAId,
                Name = ArtifactAName,
                Kind = "Directory",
                Status = "Ready",
                ManifestJson = ArtifactManifestJson,
            },
            new Artifact
            {
                Id = ArtifactBId,
                OrganizationId = OrgBId,
                RunId = BuildRunBId,
                Name = ArtifactBName,
                Kind = "File",
                Status = "Ready",
                ManifestJson = ArtifactManifestJson,
            });

        // F-013: one Git credential per organization, so the read/write/find probes are not vacuous.
        context.GitCredentials.AddRange(
            new GitCredential
            {
                Id = GitCredentialAId,
                OrganizationId = OrgAId,
                Name = GitCredentialAName,
                Kind = "HttpsToken",
                Ciphertext = GitCredentialACiphertext,
                UserName = "x-access-token",
                CreatedByUserId = OrgAOwnerUserId,
            },
            new GitCredential
            {
                Id = GitCredentialBId,
                OrganizationId = OrgBId,
                Name = GitCredentialBName,
                Kind = "SshKey",
                Ciphertext = GitCredentialBCiphertext,
                CreatedByUserId = OrgBOwnerUserId,
            });

        // F-017: one webhook per organization (and one delivery record hanging off each), so the
        // read/write/find probes are not vacuous and the two unique indexes - (ProjectId, Name) and
        // (WebhookId, DedupeKey) - each have a row to collide with.
        context.Set<Webhook>().AddRange(
            new Webhook
            {
                Id = WebhookAId,
                OrganizationId = OrgAId,
                ProjectId = ProjectAId,
                Name = WebhookAName,
                SecretCiphertext = WebhookACiphertext,
                BranchFilter = ProjectABranch,
                RequiredTags = string.Empty,
                CreatedByUserId = OrgAOwnerUserId,
            },
            new Webhook
            {
                Id = WebhookBId,
                OrganizationId = OrgBId,
                ProjectId = ProjectBId,
                Name = WebhookBName,
                SecretCiphertext = WebhookBCiphertext,
                BranchFilter = ProjectBBranch,
                RequiredTags = string.Empty,
                CreatedByUserId = OrgBOwnerUserId,
            });

        context.Set<WebhookDelivery>().AddRange(
            new WebhookDelivery
            {
                Id = WebhookDeliveryAId,
                OrganizationId = OrgAId,
                WebhookId = WebhookAId,
                DedupeKey = WebhookDedupeKey,
                Outcome = "queued",
                ReceivedAt = DateTimeOffset.UtcNow,
            },
            new WebhookDelivery
            {
                Id = WebhookDeliveryBId,
                OrganizationId = OrgBId,
                WebhookId = WebhookBId,
                DedupeKey = WebhookDedupeKey,
                Outcome = "queued",
                ReceivedAt = DateTimeOffset.UtcNow,
            });

        // F-024: one schedule per organization (and one due-time row hanging off each), so the
        // read/write/find probes are not vacuous and the two unique indexes - (ProjectId, Cron, TimeZone)
        // and (ScheduleId, DueAt) - each have a row to collide with.
        var dueAt = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);
        context.Set<ProjectSchedule>().AddRange(
            new ProjectSchedule
            {
                Id = ScheduleAId,
                OrganizationId = OrgAId,
                ProjectId = ProjectAId,
                Cron = "0 3 * * *",
                TimeZone = "UTC",
                CreatedByUserId = OrgAOwnerUserId,
                LastEvaluatedAt = dueAt,
            },
            new ProjectSchedule
            {
                Id = ScheduleBId,
                OrganizationId = OrgBId,
                ProjectId = ProjectBId,
                Cron = "0 3 * * *",
                TimeZone = "UTC",
                CreatedByUserId = OrgBOwnerUserId,
                LastEvaluatedAt = dueAt,
            });

        context.Set<ScheduleOccurrence>().AddRange(
            new ScheduleOccurrence
            {
                Id = ScheduleOccurrenceAId,
                OrganizationId = OrgAId,
                ScheduleId = ScheduleAId,
                DueAt = dueAt,
                Outcome = "queued",
                RecordedAt = dueAt,
            },
            new ScheduleOccurrence
            {
                Id = ScheduleOccurrenceBId,
                OrganizationId = OrgBId,
                ScheduleId = ScheduleBId,
                DueAt = dueAt,
                Outcome = "queued",
                RecordedAt = dueAt,
            });

        // F-023: one branch job per organization. Their branch names are the same on purpose - the
        // key is (ProjectId, Branch) inside one tenant, and two organizations legitimately track the
        // same branch name.
        context.Set<BranchJob>().AddRange(
            new BranchJob
            {
                Id = BranchAId,
                OrganizationId = OrgAId,
                ProjectId = ProjectAId,
                Branch = "main",
                PipelineState = BranchPipelineStates.HasPipeline,
                ObservedCommit = new string('a', 40),
            },
            new BranchJob
            {
                Id = BranchBId,
                OrganizationId = OrgBId,
                ProjectId = ProjectBId,
                Branch = "main",
                PipelineState = BranchPipelineStates.NoPipeline,
                ObservedCommit = new string('b', 40),
            });

        // F-034: one plain-configuration variable per organization - no ciphertext, no masking: the value
        // is exactly what a step of that tenant would read.
        context.Set<OrganizationEnvironmentVariable>().AddRange(
            new OrganizationEnvironmentVariable
            {
                Id = EnvironmentAId,
                OrganizationId = OrgAId,
                Name = EnvironmentAName,
                Value = EnvironmentAValue,
                CreatedByUserId = OrgAOwnerUserId,
            },
            new OrganizationEnvironmentVariable
            {
                Id = EnvironmentBId,
                OrganizationId = OrgBId,
                Name = EnvironmentBName,
                Value = EnvironmentBValue,
                CreatedByUserId = OrgBOwnerUserId,
            });

        context.SaveChanges();
    }

    /// <summary>A fake but well-shaped 64-character hash for fixture tokens (no plaintext exists).</summary>
    private static string SeededTokenHash(string workerName) =>
        $"seed:{workerName}".PadRight(64, '-');

    /// <summary>
    /// A seeded account with a non-null security stamp (Identity's claims factory requires one) and
    /// a real password hash, so the HTTP probes sign in through the product's login form. The user
    /// name is the email because that is what <c>SignInManager.PasswordSignInAsync</c> looks up.
    /// </summary>
    private static User CreateUser(string userId, string email)
    {
        var user = new User
        {
            Id = userId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = $"seed-security-stamp-{userId}",
            ConcurrencyStamp = $"seed-concurrency-stamp-{userId}",
        };

        // F-003 replaces the test-only X-Test-Organization probe with the real authenticated
        // endpoints, so the seeded accounts need a real password hash for the login form.
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, UserPassword);
        return user;
    }

    public void Dispose()
    {
        // SQLite pools connections; on Windows the file stays locked until the pool is drained.
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(ArtifactsPath))
            {
                Directory.Delete(ArtifactsPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        foreach (var path in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A leaked reader in a failing test must not mask the real failure; the file lives
                // in the OS temp directory and is never committed.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
