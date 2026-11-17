using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Probes;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-022's server half: an inline Pipeline project stores a script and no repository fields, a run
/// snapshots the script (never the project's current text), the claim carries it with an explicit
/// <c>definitionSource</c>, and the Validate action settles a definition phase without a run.
/// <para>
/// Everything goes through the product's own forms and its worker API; the validation facts play the
/// worker's claim/report half by hand, exactly as the probe tests do, because the routing (not a real
/// Lua process) is what these facts are about - the Worker's own semantics live in the runner tests.
/// </para>
/// </summary>
[Trait("Category", "Runs")]
public sealed class InlinePipelineRunTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    internal const string InlineScript = """
        pipeline {
          name = "inline-e2e",
          steps = {
            { name = "Restore", run = { "echo", "restore" } },
            { name = "Build", run = { "echo", "build" } },
          },
        }
        """;

    [Fact]
    public async Task An_inline_project_stores_the_script_and_null_repository_fields_and_its_source_is_immutable()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();

        var projectId = await CreateInlineProjectAsync(owner, "inline-project", InlineScript);

        var row = await host.ReadProjectAsync(projectId);
        Assert.Equal(ProjectKinds.Pipeline, row.Kind);
        Assert.Equal(ProjectSources.Inline, row.PipelineSource);
        Assert.Equal(InlineScript, row.InlineScript);

        // AC-1's honest empty value: the three repository fields are null on the row, exactly as a
        // Freestyle project's are. The run's pipeline path is a different fact (see below).
        Assert.Null(row.RepositoryUrl);
        Assert.Null(row.DefaultBranch);
        Assert.Null(row.PipelinePath);

        // F-022 + F-020's protection: an edit cannot turn the source (or the kind) into another shape.
        // The POST names both a different kind and a different source; the row keeps what it was created
        // with, and the script is updated through the same edit's inline half.
        var edited = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/edit",
            $"/projects/{projectId}/edit",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "inline-project",
                ["Kind"] = ProjectKinds.Freestyle,
                ["PipelineSource"] = ProjectSources.Repository,
                ["InlineScript"] = "pipeline { name = \"edited\", steps = { { name = \"One\", run = { \"echo\" } } } }",
            });
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);

        var after = await host.ReadProjectAsync(projectId);
        Assert.Equal(ProjectKinds.Pipeline, after.Kind);
        Assert.Equal(ProjectSources.Inline, after.PipelineSource);
        Assert.Null(after.RepositoryUrl);
        Assert.Null(after.DefaultBranch);
        Assert.Null(after.PipelinePath);
        Assert.Contains("edited", after.InlineScript!, StringComparison.Ordinal);

        // The kind-locked edit form renders no script field (the Pipelines tab owns the editor): a save
        // that carries only the name must keep the stored script rather than clear it.
        using var renamed = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/edit",
            $"/projects/{projectId}/edit",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = "inline-renamed" });
        Assert.Equal(HttpStatusCode.Redirect, renamed.StatusCode);
        var renamedRow = await host.ReadProjectAsync(projectId);
        Assert.Equal("inline-renamed", renamedRow.Name);
        Assert.Equal(after.InlineScript, renamedRow.InlineScript);
    }

    [Fact]
    public async Task A_repository_project_keeps_the_historical_source_and_no_script()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();

        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "repository-project",
                ["Kind"] = ProjectKinds.Pipeline,
                ["RepositoryUrl"] = "https://example.test/owner/repo.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var projectId = Guid.Parse(
            response.Headers.Location!.OriginalString["/projects/".Length..].Split('?')[0]);

        var row = await host.ReadProjectAsync(projectId);
        Assert.Equal(ProjectSources.Repository, row.PipelineSource);
        Assert.Null(row.InlineScript);
        Assert.Equal("https://example.test/owner/repo.git", row.RepositoryUrl);

        // R9's positive direction: a repository project cannot be turned inline by an edit either.
        using var edited = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/edit",
            $"/projects/{projectId}/edit",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "repository-project",
                ["Kind"] = ProjectKinds.Freestyle,
                ["PipelineSource"] = ProjectSources.Inline,
                ["InlineScript"] = "pipeline { name = \"should-not-apply\", steps = { { name = \"One\", run = { \"echo\" } } } }",
                ["RepositoryUrl"] = "https://example.test/owner/repo.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
            });
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        var editedRow = await host.ReadProjectAsync(projectId);
        Assert.Equal(ProjectKinds.Pipeline, editedRow.Kind);
        Assert.Equal(ProjectSources.Repository, editedRow.PipelineSource);
        Assert.Null(editedRow.InlineScript);
    }

    [Fact]
    public async Task An_inline_run_snapshots_the_script_and_the_claim_carries_it_with_pipeline_lua_and_no_repository()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateInlineProjectAsync(owner, "inline-snapshot", InlineScript);

        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        // The run's own snapshot: branch/commit honest empty values, the pipeline path is the synthetic
        // file the script becomes, and the script text is on the row.
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(InlineScript, run.PipelineScriptText);
        Assert.Equal(PipelineScriptLimits.InlineFileName, run.PipelinePath);
        Assert.Equal(string.Empty, run.GitBranch);
        Assert.Null(run.GitCommit);
        Assert.Null(run.PipelineSpecJson);

        // The project really changed after the run was queued (so the assertion below is not vacuous)...
        var rewritten = "pipeline { name = \"rewritten\", steps = { { name = \"Only\", run = { \"echo\" } } } }";
        Assert.True(await SaveScriptAsync(owner, projectId, rewritten));
        Assert.Equal(rewritten, (await host.ReadProjectAsync(projectId)).InlineScript);

        // ...while the queued run keeps the text it snapshotted, and the claim reads the RUN row: the
        // payload carries the old script, the explicit source word and no repository.
        var (workerToken, _) = await host.RegisterWorkerAsync();
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        using var document = JsonDocument.Parse(await claimed.Content.ReadAsStringAsync(CancellationToken));
        var root = document.RootElement;
        Assert.Equal(runId, root.GetProperty("runId").GetGuid());
        Assert.True(root.TryGetProperty("repositoryUrl", out var repositoryUrl));
        Assert.Equal(JsonValueKind.Null, repositoryUrl.ValueKind);
        Assert.Equal(string.Empty, root.GetProperty("branch").GetString());
        Assert.Equal(PipelineScriptLimits.InlineFileName, root.GetProperty("pipelinePath").GetString());
        Assert.Equal(InlineScript, root.GetProperty("pipelineScript").GetString());
        Assert.Equal(RunDefinitionSources.Inline, root.GetProperty("definitionSource").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pipelineSpecJson").ValueKind);
    }

    [Fact]
    public async Task An_empty_or_over_bound_inline_script_is_refused_at_create_and_at_save()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();

        var empty = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "inline-empty",
                ["Kind"] = ProjectKinds.Pipeline,
                ["PipelineSource"] = ProjectSources.Inline,
                ["InlineScript"] = "   ",
            });
        Assert.Equal(HttpStatusCode.Redirect, empty.StatusCode);
        Assert.Contains(
            $"error={ProjectErrorCodes.InlineScriptRequired}",
            empty.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        var projectId = await CreateInlineProjectAsync(owner, "inline-save", InlineScript);
        var tooLong = new string('x', PipelineScriptLimits.MaxScriptBytes + 1);
        var refusedSave = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/pipelines",
            $"/projects/{projectId}/inline/script",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Script"] = tooLong });
        Assert.Equal(HttpStatusCode.Redirect, refusedSave.StatusCode);
        Assert.Contains(
            $"error={ProjectErrorCodes.InlineScriptTooLong}",
            refusedSave.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        // Nothing was overwritten: the stored script is still the one the project was created with.
        Assert.Equal(InlineScript, (await host.ReadProjectAsync(projectId)).InlineScript);
    }

    [Fact]
    public async Task Validate_runs_a_definition_phase_on_a_worker_and_creates_no_run_and_takes_no_slot()
    {
        using var host = await RunTestHost.CreateAsync(configureAppConfiguration: builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DefinitionValidationOptions.WaitSecondsKey] = "10",
                [DefinitionValidationOptions.PollMillisecondsKey] = "20",
            }));
        var owner = await host.Inner.SignInOwnerAsync();
        var organizationId = await host.OrganizationIdAsync();
        var projectId = await CreateInlineProjectAsync(owner, "inline-validate", InlineScript);

        // The project is at its concurrency cap with one Running run: a build claim must find nothing,
        // yet a validation claim still succeeds - the two decisions are deliberately different.
        await SetMaxConcurrentRunsAsync(host, projectId, 1);
        var busyRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "inline-validator");
        using var workerClient = host.Host.CreateClient();
        using (var busyClaim = await RunTestHost.ClaimAsync(workerClient, workerToken))
        {
            Assert.Equal(HttpStatusCode.OK, busyClaim.StatusCode);
            using var busy = JsonDocument.Parse(await busyClaim.Content.ReadAsStringAsync(CancellationToken));
            Assert.Equal(busyRunId, busy.RootElement.GetProperty("runId").GetGuid());
            var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, busyRunId);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var beforeRuns = (await host.ReadRunsAsync(organizationId)).Count;
        var beforeNumbers = (await host.ReadRunsAsync(organizationId)).Select(run => run.Number).ToArray();

        // The page's Validate action, in the background: it waits for a worker while we play the worker.
        var validate = RunTestHost.StartValidateAsync(owner, projectId, InlineScript);
        var pending = await WaitForValidationAsync(host, projectId);
        Assert.Null((await ReadValidationRowAsync(host, pending.Id)).WorkerId);

        using (var claim = await ClaimValidationAsync(workerClient, workerToken))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            using var claimed = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(CancellationToken));
            Assert.Equal(pending.Id, claimed.RootElement.GetProperty("validationId").GetGuid());
            Assert.Equal(InlineScript, claimed.RootElement.GetProperty("script").GetString());
        }

        var specJson = PipelineSpecJson.Serialize(new PipelineSpec(
            PipelineSpecSchema.Version,
            "inline-validated",
            [
                new PipelineStep("Restore", Run: ["echo", "restore"]),
                new PipelineStep("Build", Run: ["echo", "build"]),
            ]));
        using (var reported = await ReportValidationAsync(
            workerClient,
            workerToken,
            pending.Id,
            DefinitionValidationOutcomes.Valid,
            specJson: specJson))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        using var response = await validate;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains($"validated={pending.Id}", response.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var page = await AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{projectId}/pipelines?validated={pending.Id}");
        Assert.Contains("inline-validation-valid", page, StringComparison.Ordinal);
        Assert.Contains("Restore", page, StringComparison.Ordinal);
        Assert.Contains("Build", page, StringComparison.Ordinal);

        // The query names the validation it wants to show: a stale/forged id renders no result rather
        // than silently showing this project's last one.
        var stale = await AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{projectId}/pipelines?validated={Guid.NewGuid()}");
        Assert.DoesNotContain("inline-validation-valid", stale, StringComparison.Ordinal);

        // The nine hard constraints this fact can see: no run row, no step, no consumed number, no
        // second run in the history, and the busy run is untouched.
        var afterRuns = await host.ReadRunsAsync(organizationId);
        Assert.Equal(beforeRuns, afterRuns.Count);
        Assert.Equal(beforeNumbers, afterRuns.Select(run => run.Number).ToArray());
        Assert.Empty(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<BuildStep>(step => step.RunId == busyRunId)));
    }

    [Fact]
    public async Task Validate_reports_a_broken_script_as_invalid_with_the_positioned_diagnostic()
    {
        using var host = await RunTestHost.CreateAsync(configureAppConfiguration: builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DefinitionValidationOptions.WaitSecondsKey] = "10",
                [DefinitionValidationOptions.PollMillisecondsKey] = "20",
            }));
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateInlineProjectAsync(owner, "inline-invalid", InlineScript);
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "inline-validator-invalid");
        using var workerClient = host.Host.CreateClient();

        var validate = RunTestHost.StartValidateAsync(owner, projectId, "pipeline {");
        var pending = await WaitForValidationAsync(host, projectId);
        using (var claim = await ClaimValidationAsync(workerClient, workerToken))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }

        const string diagnostic = "pipeline.lua:(2,1): '}' expected (to close '{' at line 1)";
        using (var reported = await ReportValidationAsync(
            workerClient,
            workerToken,
            pending.Id,
            DefinitionValidationOutcomes.Invalid,
            diagnostic: diagnostic,
            failureReason: PipelineFailureReasons.PipelineSyntax))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        using var response = await validate;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = await AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{projectId}/pipelines?validated={pending.Id}");
        Assert.Contains("inline-validation-invalid", page, StringComparison.Ordinal);
        Assert.Contains("pipeline.lua:(2,1)", page, StringComparison.Ordinal);
        Assert.Contains("pipeline-syntax", page, StringComparison.Ordinal);

        // Still no run: an invalid script is an answer, not a build.
        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
    }

    [Fact]
    public async Task Validate_without_a_worker_settles_honestly_and_creates_nothing()
    {
        using var host = await RunTestHost.CreateAsync(configureAppConfiguration: builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DefinitionValidationOptions.WaitSecondsKey] = "0.5",
                [DefinitionValidationOptions.PollMillisecondsKey] = "20",
            }));
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateInlineProjectAsync(owner, "inline-no-worker", InlineScript);

        using var response = await RunTestHost.StartValidateAsync(owner, projectId, InlineScript);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("validated=", response.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var page = await AuthTestHost.GetStringAsync(owner, response.Headers.Location!.OriginalString);
        Assert.Contains("inline-validation-no-worker", page, StringComparison.Ordinal);
        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
    }

    [Fact]
    public async Task Validate_refuses_a_repository_project_with_the_editor_code()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("repository-validate");

        using var response = await RunTestHost.StartValidateAsync(owner, projectId, InlineScript);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            $"error={ProjectErrorCodes.PipelineSourceInvalid}",
            response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        Assert.Empty((await HostRegistryAsync(host)).Snapshots);
        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
    }

    [Fact]
    public async Task The_inline_editor_and_the_validation_worker_api_stay_inside_the_token_tenant()
    {
        using var host = await RunTestHost.CreateAsync(configureAppConfiguration: builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DefinitionValidationOptions.WaitSecondsKey] = "10",
                [DefinitionValidationOptions.PollMillisecondsKey] = "20",
            }));
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateInlineProjectAsync(owner, "inline-tenant", InlineScript);

        // A second organization with its own Owner and inline project, through the real registration and
        // invitation paths.
        const string foreignEmail = "inline-foreign@example.test";
        await host.Inner.Inner.RegisterUserAsync(foreignEmail);
        await host.Inner.Inner.AddOrganizationAsync(
            "Inline Foreign",
            (foreignEmail, Zenkins.Server.Data.Entities.OrganizationRole.Owner));
        using var foreignOwner = await host.Inner.Inner.SignInAsync(foreignEmail);
        var foreignProjectId = await CreateInlineProjectAsync(foreignOwner, "inline-foreign", InlineScript);

        // The editor and the Validate action resolve the project through the tenant filter: a foreign id
        // is the same 404 as a missing one, and no validation is queued.
        using (var foreignSave = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/pipelines",
            $"/projects/{foreignProjectId}/inline/script",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Script"] = "pipeline { }" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignSave.StatusCode);
        }

        using (var foreignValidate = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/pipelines",
            $"/projects/{foreignProjectId}/inline/validate",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Script"] = InlineScript }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignValidate.StatusCode);
        }

        Assert.Empty((await HostRegistryAsync(host)).Snapshots);
        Assert.Equal(
            InlineScript,
            (await host.InSystemScopeAsync(context =>
                context.SystemQueryAsync<Project>(candidate => candidate.Id == foreignProjectId)))
                .Single().InlineScript);

        // A worker of another organization can neither claim nor settle this organization's validation.
        var (foreignToken, _) = await host.RegisterWorkerAsync(name: "inline-foreign-worker", ownerClient: foreignOwner);
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "inline-tenant-worker");
        using var foreignClient = host.Host.CreateClient();
        using var workerClient = host.Host.CreateClient();

        var validate = RunTestHost.StartValidateAsync(owner, projectId, InlineScript);
        var pending = await WaitForValidationAsync(host, projectId);

        using (var foreignClaim = await ClaimValidationAsync(foreignClient, foreignToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, foreignClaim.StatusCode);
        }

        using (var foreignReport = await ReportValidationAsync(
            foreignClient,
            foreignToken,
            pending.Id,
            DefinitionValidationOutcomes.Invalid,
            diagnostic: "pipeline.lua:(1,1): should not be visible"))
        {
            // The validation is another tenant's: the report is a bare 404 and changes nothing.
            Assert.Equal(HttpStatusCode.NotFound, foreignReport.StatusCode);
        }

        using (var claim = await ClaimValidationAsync(workerClient, workerToken))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }

        var specJson = PipelineSpecJson.Serialize(new PipelineSpec(
            PipelineSpecSchema.Version,
            "inline-tenant",
            [new PipelineStep("Only", Run: ["echo", "hi"])]));
        using (var reported = await ReportValidationAsync(
            workerClient,
            workerToken,
            pending.Id,
            DefinitionValidationOutcomes.Valid,
            specJson: specJson))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        using var response = await validate;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null((await HostRegistryAsync(host)).Find(pending.Id)!.Diagnostic);
    }

    [Fact]
    public async Task An_inline_run_offers_rebuild_and_replay_and_a_rebuild_reuses_the_same_script()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateInlineProjectAsync(owner, "inline-rerun", InlineScript);
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        // A finished inline run: the definition phase reported its spec in production; here the status
        // and spec are set directly so the page's re-run offer is what is under test.
        var specJson = PipelineSpecJson.Serialize(new PipelineSpec(
            PipelineSpecSchema.Version,
            "inline-rerun",
            [new PipelineStep("Only", Run: ["echo", "hi"])]));
        await host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId)).Single();
            run.Status = BuildRunStatus.Success;
            run.ExitCode = 0;
            run.EndedAt = DateTimeOffset.UtcNow;
            run.PipelineSpecJson = specJson;
            await context.SaveChangesAsync();
            return true;
        });

        // F-022 review F3: the page and the service must agree. Before the fix the page passed the
        // empty branch as "Freestyle" and rendered neither button (while the service would have run it).
        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains($"action=\"/runs/{runId}/rebuild\"", page, StringComparison.Ordinal);
        Assert.Contains($"action=\"/runs/{runId}/replay\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("run-replay-unavailable", page, StringComparison.Ordinal);

        using (var rebuilt = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/runs/{runId}",
            $"/runs/{runId}/rebuild",
            new Dictionary<string, string>(StringComparer.Ordinal)))
        {
            Assert.True((int)rebuilt.StatusCode is 302 or 303, $"Rebuild answered {(int)rebuilt.StatusCode}.");
            Assert.StartsWith("/runs/", rebuilt.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        using (var replayed = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/runs/{runId}",
            $"/runs/{runId}/replay",
            new Dictionary<string, string>(StringComparer.Ordinal)))
        {
            Assert.True((int)replayed.StatusCode is 302 or 303, $"Replay answered {(int)replayed.StatusCode}.");
        }

        var reruns = (await host.ReadRunsAsync(await host.OrganizationIdAsync()))
            .Where(candidate => candidate.RebuiltFromRunId == runId)
            .ToArray();
        Assert.Equal(2, reruns.Length);
        Assert.Contains(reruns, run => run.TriggerType == BuildRunTriggerType.Rebuild);
        Assert.Contains(reruns, run => run.TriggerType == BuildRunTriggerType.Replay);
        Assert.All(reruns, run =>
        {
            Assert.Equal(InlineScript, run.PipelineScriptText);
            Assert.Null(run.PipelineSpecJson);
            Assert.Equal(PipelineScriptLimits.InlineFileName, run.PipelinePath);
        });
    }

    [Fact]
    public async Task An_inline_run_ignores_an_out_of_band_default_branch()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var organizationId = await host.OrganizationIdAsync();

        // The seed/import shape: an inline row whose DefaultBranch was written outside the service. The
        // run's branch must still be the honest empty value, exactly as the schedule path decides it.
        var projectId = await host.InSystemScopeAsync(async context =>
        {
            var project = new Project
            {
                OrganizationId = organizationId,
                Name = "inline-out-of-band-branch",
                Kind = ProjectKinds.Pipeline,
                PipelineSource = ProjectSources.Inline,
                InlineScript = InlineScript,
                DefaultBranch = "main",
                PipelinePath = null,
            };
            context.Projects.Add(project);
            await context.SaveChangesAsync();
            return project.Id;
        });

        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(string.Empty, run.GitBranch);
        Assert.Equal(PipelineScriptLimits.InlineFileName, run.PipelinePath);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    internal static async Task<Guid> CreateInlineProjectAsync(HttpClient owner, string name, string script)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = ProjectKinds.Pipeline,
                ["PipelineSource"] = ProjectSources.Inline,
                ["InlineScript"] = script,
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/projects/", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        return Guid.Parse(response.Headers.Location.OriginalString["/projects/".Length..].Split('?')[0]);
    }

    internal static async Task<bool> SaveScriptAsync(HttpClient owner, Guid projectId, string script)
    {
        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/pipelines",
            $"/projects/{projectId}/inline/script",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Script"] = script });
        return response.StatusCode == HttpStatusCode.Redirect;
    }

    private static async Task<DefinitionValidationEntry> WaitForValidationAsync(
        RunTestHost host,
        Guid projectId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var registry = await HostRegistryAsync(host);
            var pending = registry.Snapshots.FirstOrDefault(entry => entry.ProjectId == projectId);
            if (pending is not null)
            {
                return pending;
            }

            await Task.Delay(20, CancellationToken);
        }

        throw new Xunit.Sdk.XunitException("No validation was queued within 10s.");
    }

    private static Task<DefinitionValidationRegistry> HostRegistryAsync(RunTestHost host) =>
        host.Host.InScopeAsync(services => Task.FromResult(services.GetRequiredService<DefinitionValidationRegistry>()));

    private static Task<DefinitionValidationEntry> ReadValidationRowAsync(RunTestHost host, Guid validationId) =>
        host.Host.InScopeAsync(services =>
        {
            var entry = services.GetRequiredService<DefinitionValidationRegistry>().Find(validationId);
            Assert.NotNull(entry);
            return Task.FromResult(entry!);
        });

    private static async Task<HttpResponseMessage> ClaimValidationAsync(HttpClient client, string workerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workers/definitions/claim");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ReportValidationAsync(
        HttpClient client,
        string workerToken,
        Guid validationId,
        string outcome,
        string? specJson = null,
        string? diagnostic = null,
        string? failureReason = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/workers/definitions/{validationId}/report")
        {
            Content = JsonContent.Create(new { outcome, specJson, diagnostic, failureReason }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);
        return await client.SendAsync(request);
    }

    private static Task SetMaxConcurrentRunsAsync(RunTestHost host, Guid projectId, int max) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.MaxConcurrentRuns = max;
            await context.SaveChangesAsync();
            return true;
        });
}
