using Xunit;
using Zenkins.Server.Data;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Pipelines;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Tenancy;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-001's data-layer probes, driven entirely by <see cref="TwoOrgsFixture"/> and the generic
/// helpers in <see cref="TenantIsolationProbes"/>. One class fixture serves the whole class: every
/// probe here reads, or attempts a write that must be rejected, so no test mutates the seeded rows.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationProbeTests : IClassFixture<TwoOrgsFixture>
{
    private readonly TwoOrgsFixture _fixture;

    public TenantIsolationProbeTests(TwoOrgsFixture fixture) => _fixture = fixture;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public Task Organization_memberships_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<OrganizationMember>(_fixture, CancellationToken);

    [Fact]
    public Task Projects_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<Project>(_fixture, CancellationToken);

    [Fact]
    public Task Workers_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<WorkerEntity>(_fixture, CancellationToken);

    [Fact]
    public Task Worker_tokens_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<WorkerToken>(_fixture, CancellationToken);

    [Fact]
    public Task Build_runs_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<BuildRun>(_fixture, CancellationToken);

    [Fact]
    public Task Build_logs_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<BuildLog>(_fixture, CancellationToken);

    [Fact]
    public Task Build_steps_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<BuildStep>(_fixture, CancellationToken);

    [Fact]
    public Task Cross_tenant_build_step_write_is_rejected_and_persists_nothing()
    {
        // (RunId, Index) is unique, so the smuggled row uses A's run with a fresh position per direction:
        // the rejection must be the tenant guard's, never a key clash.
        var index = 900;
        return TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<BuildStep>(
            _fixture,
            () => new BuildStep
            {
                RunId = TwoOrgsFixture.BuildRunAId,
                Index = index++,
                Name = "Smuggled",
                Status = PipelineStepStatus.Running,
            },
            CancellationToken);
    }

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_build_steps() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<BuildStep>(
            _fixture,
            TwoOrgsFixture.BuildStepAId,
            TwoOrgsFixture.BuildStepBId,
            CancellationToken);

    [Fact]
    public Task Secrets_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<Secret>(_fixture, CancellationToken);

    [Fact]
    public Task Cross_tenant_secret_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<Secret>(
            _fixture,
            () => new Secret
            {
                // The fixture seeds one secret per organization, so this insert is refused by the
                // tenant guard - never persisted, never a unique-index accident.
                Name = "SMUGGLED_TOKEN",
                Ciphertext = "CfDJ8-smuggled-ciphertext",
                CreatedByUserId = TwoOrgsFixture.OrgAOwnerUserId,
            },
            CancellationToken);

    [Fact]
    public Task Git_credentials_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<GitCredential>(_fixture, CancellationToken);

    [Fact]
    public Task Cross_tenant_git_credential_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<GitCredential>(
            _fixture,
            () => new GitCredential
            {
                // The fixture seeds one credential per organization, so a cross-tenant insert is refused
                // by the tenant guard - never persisted, never a unique-index accident.
                Name = "SMUGGLED_DEPLOY_TOKEN",
                Kind = "HttpsToken",
                Ciphertext = "CfDJ8-smuggled-ciphertext",
                CreatedByUserId = TwoOrgsFixture.OrgAOwnerUserId,
            },
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_git_credentials() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<GitCredential>(
            _fixture,
            TwoOrgsFixture.GitCredentialAId,
            TwoOrgsFixture.GitCredentialBId,
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_secrets() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<Secret>(
            _fixture,
            TwoOrgsFixture.SecretAId,
            TwoOrgsFixture.SecretBId,
            CancellationToken);

    [Fact]
    public Task Cross_tenant_membership_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<OrganizationMember>(
            _fixture,
            () => new OrganizationMember
            {
                // A user who belongs to neither organization: only the foreign OrganizationId can
                // make this insert interesting, and the guard must reject it before it is sent.
                UserId = "mallory",
                Role = OrganizationRole.Member,
            },
            CancellationToken);

    [Fact]
    public Task Cross_tenant_project_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<Project>(
            _fixture,
            () => new Project
            {
                Name = "smuggled-app",
                RepositoryUrl = "https://example.test/smuggled.git",
                DefaultBranch = "main",
            },
            CancellationToken);

    [Fact]
    public Task Cross_tenant_worker_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<WorkerEntity>(
            _fixture,
            () => new WorkerEntity
            {
                Name = "smuggled-agent",
                Os = "Linux",
                Architecture = "X64",
                Version = "1.0.0",
                Tags = "dotnet",
                CpuCount = 1,
                MaxConcurrency = 1,
            },
            CancellationToken);

    [Fact]
    public Task Cross_tenant_worker_token_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<WorkerToken>(
            _fixture,
            () => new WorkerToken
            {
                WorkerId = null,
                Kind = WorkerTokenKind.Registration,
                TokenHash = "smuggled-registration-token-hash".PadRight(64, '-'),
            },
            CancellationToken);

    [Fact]
    public Task Cross_tenant_build_run_write_is_rejected_and_persists_nothing() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<BuildRun>(
            _fixture,
            () => new BuildRun
            {
                ProjectId = TwoOrgsFixture.ProjectAId,
                TriggerType = BuildRunTriggerType.Manual,
                RequiredTags = string.Empty,
                GitBranch = "main",
                PipelinePath = "pipeline.lua",
            },
            CancellationToken);

    [Fact]
    public Task Cross_tenant_build_log_write_is_rejected_and_persists_nothing()
    {
        // The log's primary key is composite (RunId, Seq), so the smuggled row needs a fresh Seq
        // per direction to keep the rejection purely a tenant-guard outcome, never a key clash.
        var seq = 900;
        return TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<BuildLog>(
            _fixture,
            () => new BuildLog
            {
                RunId = TwoOrgsFixture.BuildRunAId,
                Seq = seq++,
                Stream = LogStream.StdOut,
                Message = "smuggled-line",
                Timestamp = DateTimeOffset.UtcNow,
            },
            CancellationToken);
    }

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_memberships() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<OrganizationMember>(
            _fixture,
            TwoOrgsFixture.OrgAOwnerMembershipId,
            TwoOrgsFixture.OrgBOwnerMembershipId,
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_projects() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<Project>(
            _fixture,
            TwoOrgsFixture.ProjectAId,
            TwoOrgsFixture.ProjectBId,
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_workers() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<WorkerEntity>(
            _fixture,
            TwoOrgsFixture.WorkerAId,
            TwoOrgsFixture.WorkerBId,
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_worker_tokens() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<WorkerToken>(
            _fixture,
            TwoOrgsFixture.WorkerTokenAId,
            TwoOrgsFixture.WorkerTokenBId,
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_build_runs() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<BuildRun>(
            _fixture,
            TwoOrgsFixture.BuildRunAId,
            TwoOrgsFixture.BuildRunBId,
            CancellationToken);

    [Fact]
    public async Task Find_after_a_tenant_switch_returns_null_for_build_logs()
    {
        // BuildLog's primary key is composite (RunId, Seq), so the harness's single-id Find probe
        // does not apply; the same switch discipline is asserted against both key halves.
        object[] keyOfA = [TwoOrgsFixture.BuildRunAId, 1L];
        object[] keyOfB = [TwoOrgsFixture.BuildRunBId, 1L];

        _fixture.UseOrganizationA();
        await using var context = _fixture.CreateContext();

        Assert.NotNull(await context.FindAsync<BuildLog>(keyOfA, CancellationToken));
        Assert.Null(await context.FindAsync<BuildLog>(keyOfB, CancellationToken));

        _fixture.UseOrganizationB();

        // Find short-circuits in the change tracker, so only dropping the tracker makes these null.
        Assert.Null(await context.FindAsync<BuildLog>(keyOfA, CancellationToken));
        Assert.NotNull(await context.FindAsync<BuildLog>(keyOfB, CancellationToken));

        _fixture.UseOrganizationA();

        Assert.Null(await context.FindAsync<BuildLog>(keyOfB, CancellationToken));
        Assert.NotNull(await context.FindAsync<BuildLog>(keyOfA, CancellationToken));
    }

    // ---- F-016: artifacts -------------------------------------------------

    [Fact]
    public Task Artifacts_are_invisible_across_tenants() =>
        TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<Artifact>(_fixture, CancellationToken);

    [Fact]
    public Task A_cross_tenant_artifact_write_is_rejected() =>
        TenantIsolationProbes.AssertCrossTenantWriteRejectedAsync<Artifact>(
            _fixture,
            () => new Artifact
            {
                // Only the tenant field is wrong; everything else is a legal artifact.
                RunId = TwoOrgsFixture.BuildRunAId,
                Name = $"smuggled/{Guid.NewGuid():N}",
                Kind = "File",
                Status = "Pending",
            },
            CancellationToken);

    [Fact]
    public Task Find_after_a_tenant_switch_returns_null_for_artifacts() =>
        TenantIsolationProbes.AssertFindAfterTenantSwitchReturnsNullAsync<Artifact>(
            _fixture,
            TwoOrgsFixture.ArtifactAId,
            TwoOrgsFixture.ArtifactBId,
            CancellationToken);

    [Fact]
    public void Every_tenant_owned_entity_is_covered_by_the_harness()
    {
        // A later card that adds a tenant-owned entity without a probe would otherwise widen the
        // isolation surface with no test noticing. The registry is explicit so the diff shows it.
        var tenantOwnedEntityTypes = typeof(ZenkinsDbContext).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && typeof(ITenantOwned).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        var coveredEntityTypes = TenantIsolationProbes.CoveredEntityTypes
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(tenantOwnedEntityTypes, coveredEntityTypes);
    }

    [Fact]
    public async Task Fixture_seeds_both_organizations_their_people_and_their_projects()
    {
        // Pins the shape later cards read as prose: the probes above are only meaningful if this
        // seed is what the fixture claims it is.
        var memberships = await _fixture.ReadAcrossTenantsAsync<OrganizationMember>(
            _ => true, CancellationToken);
        Assert.Contains(memberships, membership =>
            membership.OrganizationId == TwoOrgsFixture.OrgAId
            && membership.UserId == TwoOrgsFixture.OrgAOwnerUserId
            && membership.Role == OrganizationRole.Owner);
        Assert.Contains(memberships, membership =>
            membership.OrganizationId == TwoOrgsFixture.OrgAId
            && membership.UserId == TwoOrgsFixture.OrgAMemberUserId
            && membership.Role == OrganizationRole.Member);
        Assert.Contains(memberships, membership =>
            membership.OrganizationId == TwoOrgsFixture.OrgBId
            && membership.UserId == TwoOrgsFixture.OrgBOwnerUserId
            && membership.Role == OrganizationRole.Owner);

        var projects = await _fixture.ReadAcrossTenantsAsync<Project>(_ => true, CancellationToken);
        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, project =>
            project.Id == TwoOrgsFixture.ProjectAId
            && project.OrganizationId == TwoOrgsFixture.OrgAId
            && project.Name == TwoOrgsFixture.ProjectAName);
        Assert.Contains(projects, project =>
            project.Id == TwoOrgsFixture.ProjectBId
            && project.OrganizationId == TwoOrgsFixture.OrgBId
            && project.Name == TwoOrgsFixture.ProjectBName);
    }
}
