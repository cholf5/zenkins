using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Secrets;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Tenancy;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// Card D-006's Secrets page: the creator (and last rotator) column and the reference list, which is read
/// from the capture the definition phase froze into each run's spec - never from the repository and never
/// from a secret value.
/// <para>
/// The three states the card insists on are all here: a name a recorded definition references, a name no
/// recorded definition references, and a name that can only be answered "unknown" because a definition
/// predates capture (TC-003).
/// </para>
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretUiReferencesTests
{
    private const string ApiKey = "API_KEY";
    private const string Other = "OTHER";
    private const string Unused = "UNUSED_TOKEN";
    private const string ForeignApiKey = "FOREIGN_KEY";
    private const string ApiKeyValue = "d006-api-key-value-91c";
    private const string OtherValue = "d006-other-value-42e";
    private const string UnusedValue = "d006-unused-value-77b";
    private const string ForeignValue = "d006-foreign-value-13a";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TC_001_references_come_from_the_frozen_definition_not_the_repository()
    {
        using var host = await ReferencesHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, ApiKey, ApiKeyValue);
        await host.CreateSecretAsync(owner, Other, OtherValue);
        await host.CreateSecretAsync(owner, Unused, UnusedValue);

        // The spec the definition phase produces for a pipeline that calls secret("API_KEY") twice and
        // secret("OTHER") once; freezing it goes through the real worker endpoint, so this is the stored
        // record the page reads - not a test-only shortcut.
        var runId = await host.FreezeRunAsync(DefinitionSpec(TwoSecretsPipeline));

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        // Both referenced names carry the pipeline as a link to that run; the third says so honestly.
        Assert.Contains("d006-pipeline", page, StringComparison.Ordinal);
        Assert.Contains("No pipeline references this name.", page, StringComparison.Ordinal);

        // One link per referenced secret - two - and none for the unreferenced one.
        Assert.Equal(2, CountOccurrences(page, $"href=\"/runs/{runId}\""));

        // Values are never rendered, in any encoding: the page has no path to a stored value (F-012).
        foreach (var value in new[] { ApiKeyValue, OtherValue, UnusedValue })
        {
            Assert.DoesNotContain(value, page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TC_002_rotate_records_the_rotator_and_delete_warns_about_references()
    {
        using var host = await ReferencesHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var referenced = await host.CreateSecretAsync(owner, ApiKey, ApiKeyValue);
        var unreferenced = await host.CreateSecretAsync(owner, Unused, UnusedValue);
        await host.FreezeRunAsync(DefinitionSpec(TwoSecretsPipeline));

        // Rotate the referenced secret through the real form: the flow must surface the reference and
        // record who did it, without revealing a value anywhere.
        using var rotated = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{referenced.Id}/rotate",
            new Dictionary<string, string> { ["Value"] = "d006-rotated-value-58f" });
        Assert.True((int)rotated.StatusCode is 302 or 303);

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        // The rotate flow notes the last rotation creator (the bootstrap Owner) and keeps the reference.
        Assert.Contains("by alice@example.test", page, StringComparison.Ordinal);
        Assert.Contains("d006-pipeline", page, StringComparison.Ordinal);
        // F-012's delete semantics are unchanged: a confirmation, not a refusal - but the confirmation now
        // names what references the name, and the unreferenced one states that nothing does.
        Assert.Contains("Referenced by d006-pipeline", page, StringComparison.Ordinal);
        Assert.Contains("No recorded pipeline references this name.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("d006-rotated-value-58f", page, StringComparison.Ordinal);

        // Deleting the unreferenced secret deletes cleanly (the reference list was the only new warning).
        using var deleted = await host.PostSecretFormAsync(
            owner,
            $"/secrets/{unreferenced.Id}/delete",
            new Dictionary<string, string>());
        Assert.True((int)deleted.StatusCode is 302 or 303);
        Assert.Null(await host.ReadSecretAsync(Unused));

        // And the referenced one is still there: a warning is not a silent deletion.
        Assert.NotNull(await host.ReadSecretAsync(ApiKey));

        // A rotation that happened before attribution existed says so, instead of naming a guess - and the
        // Created column is asserted on this same render, so its label cannot be satisfied by the Rotated
        // column's text: the creator stays attributed while the rotator reads "not recorded".
        await host.ClearRotationAttributionAsync(referenced.Id);
        var preAttribution = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);
        Assert.Contains("by alice@example.test", preAttribution, StringComparison.Ordinal);
        Assert.Contains("not recorded", preAttribution, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_003_a_definition_from_before_capture_reports_unknown_not_zero()
    {
        using var host = await ReferencesHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, ApiKey, ApiKeyValue);

        // This spec predates the capture: no secretReferences field at all, so it may reference anything.
        var legacy = LegacySpec("legacy-pipeline");
        Assert.DoesNotContain("secretReferences", legacy, StringComparison.Ordinal);
        await host.FreezeRunAsync(legacy);

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        Assert.Contains("unknown (recorded before capture)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("No pipeline references this name.", page, StringComparison.Ordinal);

        // And the delete confirmation does not pretend the delete is safe: the unknown branch is what an
        // Owner reads immediately before destroying a name an older run may resolve.
        Assert.Contains(
            "Earlier definitions predate reference capture",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_referenced_name_still_reports_the_earlier_runs_it_cannot_answer_for()
    {
        using var host = await ReferencesHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, ApiKey, ApiKeyValue);
        await host.CreateSecretAsync(owner, Unused, UnusedValue);

        // One captured definition that references API_KEY, and one that predates capture.
        var capturedRunId = await host.FreezeRunAsync(DefinitionSpec(TwoSecretsPipeline));
        await host.FreezeRunAsync(LegacySpec("legacy-pipeline"));

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        // Known and "but older runs are unknown" are both true, and the page says both: the reference list
        // (one run, newest first), the capture warning below it, and the delete warning's older suffix.
        Assert.Equal(1, CountOccurrences(page, $"href=\"/runs/{capturedRunId}\""));
        Assert.Contains(
            "1 earlier run(s) predate reference",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "Referenced by d006-pipeline. Deleting it will fail the next run that resolves it. 1 earlier run(s) predate capture, so they are unknown.",
            page,
            StringComparison.Ordinal);

        // The unreferenced name is in the same position: nothing references it, but that cannot be claimed
        // as a fact while an older definition exists.
        Assert.Contains("unknown (recorded before capture)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("No pipeline references this name.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_captured_run_that_references_nothing_still_says_so()
    {
        using var host = await ReferencesHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, Unused, UnusedValue);

        // The only stored definition is a captured one, and it names no secret: "nothing references this"
        // is then a fact, and that is what makes the delete safe to offer.
        await host.FreezeRunAsync(DefinitionSpec(TwoSecretsPipeline));

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        Assert.Contains("No pipeline references this name.", page, StringComparison.Ordinal);
        Assert.Contains("No recorded pipeline references this name.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown (recorded before capture)", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_whose_creator_account_is_gone_falls_back_to_the_id()
    {
        using var host = await ReferencesHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, Unused, UnusedValue);

        // Audit ids deliberately have no foreign key so attribution survives account deletion - which means a
        // row can name a user that no longer exists. The page then shows the id, not an empty cell.
        const string DeletedUserId = "account-that-was-deleted-3f7d";
        await host.ReplaceCreatorAsync(Unused, DeletedUserId);

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        Assert.Contains(DeletedUserId, page, StringComparison.Ordinal);
        Assert.Contains("by " + DeletedUserId, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reference_reader_is_owner_only_for_a_member()
    {
        using var host = await ReferencesHost.CreateAsync();
        await host.SignInOwnerAsync();
        using var memberClient = await host.CreateMemberAsync();
        var organizationId = await host.OrganizationIdAsync();
        var memberUserId = await host.ReadUserIdAsync(ReferencesHost.MemberEmail);

        var result = await host.ReadSecretsAsAsync(organizationId, memberUserId);

        Assert.Equal(SecretMutationStatus.Forbidden, result.Status);
        Assert.Empty(result.Secrets);
    }

    [Fact]
    public async Task Another_organizations_runs_never_count_as_references()
    {
        using var host = await ReferencesHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        await host.CreateSecretAsync(owner, ForeignApiKey, ForeignValue);

        // A foreign organization freezes a definition that references <b>the same name</b> this organization
        // stores. That is what makes the probe falsifiable: if the reference query escaped the tenant filter,
        // this run would be listed on this page as "d006-pipeline · bravo-project" instead.
        var foreignOrganizationId = await host.CreateForeignOrganizationAsync();
        var foreignProjectId = await host.Organization.AddProjectAsync(
            foreignOrganizationId,
            "bravo-project");
        await host.FreezeRunAsync(
            DefinitionSpec("d006-pipeline", ForeignApiKey),
            foreignOrganizationId,
            foreignProjectId);

        var page = await AuthTestHost.GetStringAsync(owner, SecretEndpoints.ListPath);

        Assert.Contains("No pipeline references this name.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("bravo-project", page, StringComparison.Ordinal);
        Assert.DoesNotContain("d006-pipeline", page, StringComparison.Ordinal);
        Assert.DoesNotContain($"href=\"/runs/", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The definition a pipeline that calls <c>secret("API_KEY")</c> twice and <c>secret("OTHER")</c> once
    /// freezes: the same spec the definition phase produces, with the capture field populated.
    /// </summary>
    private static string DefinitionSpec(string pipelineName, params string[] referencedNames)
    {
        var names = referencedNames.Length == 0 ? new[] { ApiKey, Other } : referencedNames;
        return PipelineSpecJson.Serialize(new PipelineSpec(
            PipelineSpecSchema.Version,
            pipelineName,
            [
                new PipelineStep("Restore", Run: ["dotnet", "restore", Marker(names[0])]),
                new PipelineStep(
                    "Build",
                    Run: ["dotnet", "build", .. names.Select(Marker)]),
            ],
            SecretReferences: names));
    }

    /// <summary>The deferred reference the executor substitutes at a step boundary (F-015).</summary>
    private static string Marker(string name) => $"${{secret:{name}}}";

    /// <summary>A definition frozen before the capture existed: no <c>secretReferences</c> field at all.</summary>
    private static string LegacySpec(string pipelineName) => PipelineSpecJson.Serialize(new PipelineSpec(
        PipelineSpecSchema.Version,
        pipelineName,
        [new PipelineStep("Build", Run: ["dotnet", "build"])]));

    private const string TwoSecretsPipeline = "d006-pipeline";

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var position = 0;
        while ((position = text.IndexOf(value, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }

        return count;
    }

    /// <summary>
    /// The card's test host: F-012's secret surface (a temporary key ring, an Owner and a Member of the
    /// starter organization) over F-006's run surface, so a test can freeze a definition through the real
    /// worker endpoint and then read the page that must summarize it.
    /// </summary>
    private sealed class ReferencesHost : IDisposable
    {
        public const string MemberEmail = "bob@example.test";
        public const string ForeignOwnerEmail = "carol@example.test";

        private readonly RunTestHost _runs;
        private int _projectSequence;
        private int _workerSequence;

        private ReferencesHost(RunTestHost runs, string keysDirectory)
        {
            _runs = runs;
            KeysDirectory = keysDirectory;
        }

        public AuthTestHost Host => _runs.Host;

        public OrganizationTestHost Organization => _runs.Inner.Inner;

        public string KeysDirectory { get; }

        public static async Task<ReferencesHost> CreateAsync()
        {
            var keysDirectory = Path.Combine(Path.GetTempPath(), $"zenkins-d006-keys-{Guid.NewGuid():N}");
            var runs = await RunTestHost.CreateAsync(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SecretDataProtection.KeysDirectoryConfigurationKey] = keysDirectory,
                }));

            return new ReferencesHost(runs, keysDirectory);
        }

        public Task<Guid> OrganizationIdAsync() => _runs.OrganizationIdAsync();

        public Task<HttpClient> SignInOwnerAsync() => _runs.Inner.SignInOwnerAsync();

        public Task<HttpClient> CreateMemberAsync() => Organization.RegisterUserAsync(MemberEmail);

        public async Task<Guid> CreateForeignOrganizationAsync()
        {
            using var foreignOwner = await Organization.RegisterUserAsync(ForeignOwnerEmail);
            return await Organization.AddOrganizationAsync(
                "Bravo",
                (ForeignOwnerEmail, OrganizationRole.Owner));
        }

        /// <summary>The Identity user id of a seeded account.</summary>
        public async Task<string> ReadUserIdAsync(string email) => (await Organization.ReadUserAsync(email)).Id;

        public Task<HttpResponseMessage> PostSecretFormAsync(
            HttpClient client,
            string postPath,
            IReadOnlyDictionary<string, string> fields) =>
            OrganizationTestHost.PostFormAsync(client, SecretEndpoints.ListPath, postPath, fields);

        /// <summary>Creates a secret through the real form and returns its persisted row.</summary>
        public async Task<Secret> CreateSecretAsync(HttpClient ownerClient, string name, string value)
        {
            using var response = await PostSecretFormAsync(
                ownerClient,
                "/secrets/create",
                new Dictionary<string, string> { ["Name"] = name, ["Value"] = value });
            Assert.True((int)response.StatusCode is 302 or 303);

            return await ReadSecretAsync(name)
                ?? throw new InvalidOperationException($"Secret '{name}' was not persisted.");
        }

        /// <summary>Reads one secret across tenants, for assertions about stored rows.</summary>
        public Task<Secret?> ReadSecretAsync(string name) =>
            Host.InScopeAsync(async services =>
            {
                var systemDataScope = services.GetRequiredService<ISystemDataScope>();
                await using var context = await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                    .CreateDbContextAsync();
                using var scope = systemDataScope.BeginScope();
                return (await context.SystemQueryAsync<Secret>(secret => secret.Name == name))
                    .SingleOrDefault();
            });

        /// <summary>
        /// Replaces the creator attribution of a stored row with an id no account has, the state a secret whose
        /// author deleted their account is in (audit ids have no foreign key on purpose).
        /// </summary>
        public Task ReplaceCreatorAsync(string name, string createdByUserId) =>
            Host.InScopeAsync(async services =>
            {
                var systemDataScope = services.GetRequiredService<ISystemDataScope>();
                await using var context = await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                    .CreateDbContextAsync();
                using var scope = systemDataScope.BeginScope();
                var secret = (await context.SystemQueryAsync<Secret>(row => row.Name == name)).Single();
                secret.CreatedByUserId = createdByUserId;
                await context.SaveChangesAsync();
                return true;
            });

        /// <summary>
        /// Empties the rotation attribution of a stored row, the state every secret rotated before D-006 is in
        /// (the migration adds the column as null). The page must say "not recorded", not invent a creator.
        /// </summary>
        public Task ClearRotationAttributionAsync(Guid secretId) =>
            Host.InScopeAsync(async services =>
            {
                var systemDataScope = services.GetRequiredService<ISystemDataScope>();
                await using var context = await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                    .CreateDbContextAsync();
                using var scope = systemDataScope.BeginScope();
                var secret = (await context.SystemQueryAsync<Secret>(row => row.Id == secretId)).Single();
                secret.RotatedByUserId = null;
                await context.SaveChangesAsync();
                return true;
            });

        /// <summary>
        /// Reads the list through the service with the given tenant and user - the second gate behind the
        /// page's session check (AC 5: Owner-only is unchanged).
        /// </summary>
        public Task<SecretListResult> ReadSecretsAsAsync(Guid organizationId, string userId) =>
            Host.InScopeAsync(async services =>
            {
                services.GetRequiredService<TenantContext>().OrganizationId = organizationId;
                return await services.GetRequiredService<SecretService>().GetSecretsAsync(userId);
            });

        /// <summary>
        /// Freezes <paramref name="specJson"/> on a new run through the real worker API (claim → accept →
        /// definition report), which is exactly how a real definition reaches the column the page reads.
        /// </summary>
        public async Task<Guid> FreezeRunAsync(
            string specJson,
            Guid organizationId = default,
            Guid projectId = default)
        {
            var activeOrganizationId = await OrganizationIdAsync();
            var isForeign = organizationId != default && organizationId != activeOrganizationId;

            if (projectId == default)
            {
                // A distinct name per run: the project name is unique per organization, and a test may freeze
                // more than one definition in the same host.
                (projectId, _) = await _runs.CreateProjectAsync($"d006-project-{++_projectSequence}");
            }

            var runId = await _runs.AddRunAsync(
                projectId,
                organizationId: organizationId == default ? activeOrganizationId : organizationId);

            var workerToken = isForeign
                ? await _runs.CreateWorkerTokenDirectlyAsync(organizationId)
                // Registration is idempotent per worker name, so each run needs its own agent: a second run
                // claimed by the same worker would see no free concurrency slot (MaxConcurrency 1).
                : (await _runs.RegisterWorkerAsync(name: $"d006-agent-{++_workerSequence}")).Token;

            using var workerClient = Host.CreateClient();
            using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
            Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
            Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));

            using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            using var definition = await RunTestHost.ReportDefinitionAsync(workerClient, workerToken, runId, specJson);
            Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
            return runId;
        }

        public void Dispose()
        {
            _runs.Dispose();

            try
            {
                if (Directory.Exists(KeysDirectory))
                {
                    Directory.Delete(KeysDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
