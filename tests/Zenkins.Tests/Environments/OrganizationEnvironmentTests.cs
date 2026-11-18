using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Environments;
using Zenkins.Server.Projects;
using Zenkins.Server.Secrets;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Steps;
using Zenkins.Tests.Webhooks;
using Zenkins.Worker;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Environments;

/// <summary>
/// Card F-034 on the product's own surfaces: the organization's environment variables are plain
/// configuration (Owner-editable, Member-readable, shown with their values), every trigger freezes the
/// layer onto the run, and the worker composes it into each step's environment in the documented order
/// (PRD §3.11.3): <c>ZENKINS_*</c> metadata → organization variables → parameters → pipeline <c>env</c>
/// → step <c>env</c>, above the worker's own inherited environment.
/// <para>
/// The end-to-end facts run the real server, the real claim and the real worker pipeline layer:
/// <see cref="StepsRun"/> composes the run environment from the claim it fetched with the product's own
/// <see cref="WorkerRunLoop.BuildRunEnvironment"/>, so nothing here re-implements the composition.
/// </para>
/// </summary>
[Trait("Category", "Runs")]
public sealed class OrganizationEnvironmentTests
{
    private const string RegistryHost = "REGISTRY_HOST";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // The definition: names, values, roles and caps
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_can_define_a_variable_and_the_page_shows_its_plain_value()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = owner;

        var created = await CreateVariableAsync(owner, RegistryHost, "registry.internal");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal(
            $"{OrganizationEnvironmentEndpoints.ListPath}?notice=created",
            created.Headers.Location?.OriginalString);

        // The value is configuration, not a secret: the list renders it verbatim under the name. The
        // id-anchored element is what pins "the value is shown", not merely "the strings appear".
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner, OrganizationEnvironmentEndpoints.ListPath);
        Assert.Contains($"id=\"environment-value-{RegistryHost}\"", page, StringComparison.Ordinal);
        Assert.Contains("registry.internal", page, StringComparison.Ordinal);

        // ...and it is stored verbatim (no encryption, no trimming, no masking).
        var row = Assert.Single(await ReadVariablesAsync(host));
        Assert.Equal(RegistryHost, row.Name);
        Assert.Equal("registry.internal", row.Value);
    }

    [Fact]
    public async Task A_member_reads_the_list_but_cannot_change_it()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = owner;
        await CreateVariableAsync(owner, RegistryHost, "registry.internal");

        // AC1: Member-readable. Unlike the Secrets page (Owner-only by design), a Member sees the names
        // and the values here - they reach every step whose log that Member may read - while every
        // mutation is a 403 with no state change.
        using var member = await host.Inner.Inner.RegisterUserAsync("bob@example.test");
        var invite = await OrganizationTestHost.PostFormAsync(
            owner,
            "/organizations/members",
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = "bob@example.test" });
        Assert.Equal(HttpStatusCode.Redirect, invite.StatusCode);

        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            member, OrganizationEnvironmentEndpoints.ListPath);
        Assert.Contains(RegistryHost, page, StringComparison.Ordinal);
        Assert.Contains("registry.internal", page, StringComparison.Ordinal);

        // The mutation controls are not rendered for a Member...
        Assert.DoesNotContain(
            $"/organizations/environment/{await ReadVariableIdAsync(host)}/delete",
            page,
            StringComparison.Ordinal);

        // ...and the endpoint is the enforcement, not the page.
        var refused = await CreateVariableAsync(member, "SMUGGLED_HOST", "value");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        Assert.Single(await ReadVariablesAsync(host));
    }

    [Theory]
    // The name rule IS the stored-secret rule: these are exactly the answers SecretValidation gives.
    [InlineData("BAD NAME")]
    [InlineData("1LEADING_DIGIT")]
    [InlineData("HAS-DASH")]
    [InlineData("")]
    [InlineData("ACCEPTED_NAME")]
    public async Task The_name_rule_is_the_stored_secret_rule(string name)
    {
        // One implementation, not a copy: the organization validator delegates to the secret one, and
        // this fact holds the two answers together for the shapes a person actually types.
        var organizationVerdict = OrganizationEnvironmentValidation.ValidateName(name, out var organizationName);
        var secretVerdict = SecretValidation.ValidateName(name, out var secretName);

        Assert.Equal(secretVerdict, organizationVerdict);
        Assert.Equal(secretName, organizationName);

        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = owner;

        var created = await CreateVariableAsync(owner, name, "value");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        if (organizationVerdict is null)
        {
            Assert.Single(await ReadVariablesAsync(host));
            return;
        }

        Assert.Contains($"error={organizationVerdict}", created.Headers.Location?.OriginalString);
        Assert.Empty(await ReadVariablesAsync(host));
    }

    [Theory]
    [InlineData("ZENKINS_RUN_ID")]
    [InlineData("ZENKINS_BUILD_NUMBER")]
    [InlineData("zenkins_home")]
    [InlineData("ZENKINS_")]
    public async Task An_organization_name_inside_the_server_prefix_is_refused_at_save_and_the_error_names_it(
        string name)
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = owner;

        var created = await CreateVariableAsync(owner, name, "spoofed");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        // AC3: refused when it is saved (not at injection time), with a field error naming the prefix,
        // and nothing stored.
        var location = created.Headers.Location?.OriginalString ?? string.Empty;
        Assert.Contains($"error={OrganizationEnvironmentErrorCodes.NameReserved}", location);
        // The redirect carries the submitted name (never the value), under the same lower-case key the
        // other PRG forms use.
        Assert.Contains(
            $"name={Uri.EscapeDataString(name.Trim())}",
            location,
            StringComparison.Ordinal);
        Assert.Empty(await ReadVariablesAsync(host));

        // The message is the one the page renders next to the field, and it names the reserved prefix.
        Assert.Equal(
            OrganizationEnvironmentFields.Name,
            OrganizationEnvironmentErrorCodes.FieldFor(OrganizationEnvironmentErrorCodes.NameReserved));
        Assert.Contains(
            "ZENKINS_",
            OrganizationEnvironmentErrorCodes.MessageFor(OrganizationEnvironmentErrorCodes.NameReserved),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parameter_name_inside_the_server_prefix_is_refused_at_save()
    {
        // The metadata is the LOWEST layer after F-034, so ordering can no longer protect it: the
        // parameter layer must refuse the namespace itself (otherwise ZENKINS_RUN_ID as a parameter would
        // win and a step could read a run identity the server never wrote).
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("reserved-parameter");
        using var _ = owner;

        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ParametersJson"] = """
                    [{"name":"ZENKINS_RUN_ID","type":"string","defaultValue":"spoofed","choicesText":"","description":""}]
                    """,
            });

        // The save is refused, the message names the collision, and nothing is stored.
        Assert.DoesNotContain(
            "notice=parameters-saved",
            response.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Null((await host.ReadProjectAsync(projectId)).ParametersJson);

        // ...and the same rule answers the validator directly, so the refusal is the catalog's, not the
        // form's.
        var definitions = ProjectParameters.Validate(
            [new ProjectParameterDraft("ZENKINS_RUN_ID", "string", "spoofed", null, null)],
            out var error);
        Assert.Empty(definitions);
        Assert.NotNull(error);
        Assert.Contains("ZENKINS_", error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_value_length_and_the_variable_count_are_capped_with_clear_errors()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = owner;

        // 4 KiB per value: the boundary itself is accepted, one character more is refused.
        var longest = new string('a', OrganizationEnvironmentValidation.MaxValueLength);
        await CreateVariableAsync(owner, "AT_THE_LIMIT", longest);
        Assert.Equal(longest, Assert.Single(await ReadVariablesAsync(host)).Value);

        var tooLong = await CreateVariableAsync(owner, "OVER_THE_LIMIT", longest + "b");
        Assert.Contains(
            $"error={OrganizationEnvironmentErrorCodes.ValueTooLong}",
            tooLong.Headers.Location?.OriginalString);
        Assert.Single(await ReadVariablesAsync(host));

        // 100 variables per organization: the 101st is refused as a form-level error naming the cap.
        await host.InSystemScopeAsync(async context =>
        {
            var organizationId = await host.OrganizationIdAsync();
            for (var index = 0; index < OrganizationEnvironmentValidation.MaxVariables - 1; index++)
            {
                context.OrganizationEnvironmentVariables.Add(new OrganizationEnvironmentVariable
                {
                    OrganizationId = organizationId,
                    Name = $"FILLER_{index:D3}",
                    Value = "value",
                    CreatedByUserId = Zenkins.Tests.Tenancy.TwoOrgsFixture.OrgAOwnerUserId,
                });
            }

            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

        Assert.Equal(OrganizationEnvironmentValidation.MaxVariables, (await ReadVariablesAsync(host)).Count);

        var overTheCount = await CreateVariableAsync(owner, "ONE_TOO_MANY", "value");
        Assert.Contains(
            $"error={OrganizationEnvironmentErrorCodes.LimitReached}",
            overTheCount.Headers.Location?.OriginalString);
        Assert.Equal(OrganizationEnvironmentValidation.MaxVariables, (await ReadVariablesAsync(host)).Count);

        // A form-level refusal has no field, and its message names the cap.
        Assert.Null(OrganizationEnvironmentErrorCodes.FieldFor(OrganizationEnvironmentErrorCodes.LimitReached));
        Assert.Contains(
            OrganizationEnvironmentValidation.MaxVariables.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            OrganizationEnvironmentErrorCodes.MessageFor(OrganizationEnvironmentErrorCodes.LimitReached),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // TC-001: one shared variable reaches every project's steps
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TC_001_a_shared_variable_reaches_two_projects_steps_without_either_defining_it()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = owner;
        await CreateVariableAsync(owner, RegistryHost, "registry.internal");

        // Two repositories, two projects, one organization variable. Neither pipeline mentions the value
        // anywhere - only the name - and neither project declares it as a parameter.
        using var first = PipelineRepository.Create(EchoPipeline("first", RegistryHost));
        using var second = PipelineRepository.Create(EchoPipeline("second", RegistryHost));

        using var firstRun = await StepsRun.StartAsync(host, first);
        var firstOutcome = await firstRun.ExecuteAsync();
        using var secondRun = await StepsRun.StartAsync(host, second);
        var secondOutcome = await secondRun.ExecuteAsync();

        foreach (var (run, outcome) in new[] { (firstRun, firstOutcome), (secondRun, secondOutcome) })
        {
            Assert.True(outcome.ExitCode == 0, Describe(run, outcome));

            // The claim carried the value (AC5: the one map the server resolved for this run), and the
            // step's own process printed it - the value travelled claim → worker composition → child.
            Assert.NotNull(run.Claim);
            Assert.Equal("registry.internal", run.Claim!.Parameters![RegistryHost]);
            Assert.Contains(
                run.Lines,
                line => line.Message.Contains("registry.internal", StringComparison.Ordinal));
        }

        // The run page shows the environment the run actually used, values included (AC6), under its own
        // heading - the parameter card is a different card, because the two snapshots are separate columns.
        var page = await firstRun.ReadRunPageAsync();
        Assert.Contains($"id=\"run-environment-{RegistryHost}\"", page, StringComparison.Ordinal);
        Assert.Contains("registry.internal", page, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // TC-002a: the adjacent edges of the documented order, proven by value
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TC_002a_the_four_adjacent_edges_resolve_low_to_high()
    {
        // The chain, lowest first: inherited < ZENKINS_* metadata < organization < parameters <
        // pipeline env < step env. The metadata edge cannot be shown by value (its namespace is refused
        // at save time, so no collision is representable) - TC-002b proves it by refusal and filtering.
        // These four edges are shown by value, and each one is a different layer winning one name:
        //     organization → parameters      (run 2)
        //     parameters → pipeline env      (run 1, the step that declares no env)
        //     pipeline env → step env        (run 1, the step that declares one)
        //     inherited → everything         (run 1: the inherited value is nowhere)
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = owner;

        await CreateVariableAsync(owner, "TARGET", "value-from-org");

        // Run 1: the project declares the pipeline env and a parameter; one step redeclares TARGET.
        using var layered = PipelineRepository.Create($$"""
            pipeline {
              name = "layered",
              env = { TARGET = "value-from-pipeline" },
              steps = {
                {
                  name = "StepWins",
                  run = { "{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "echo", env("TARGET") },
                  env = { TARGET = "value-from-step" },
                },
                {
                  name = "PipelineWins",
                  run = { "{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "echo", env("TARGET") },
                },
              },
            }
            """);

        // The project also declares TARGET as a parameter, so the pipeline-level value must beat it.
        using var layeredRun = await StepsRun.StartAsync(
            host,
            layered,
            prepareProject: (projectId, client) => SaveParameterAsync(client, projectId, "TARGET", "value-from-param"));
        layeredRun.InheritedEnvironment = new Dictionary<string, string?> { ["TARGET"] = "value-from-inherited" };
        var layeredOutcome = await layeredRun.ExecuteAsync();
        Assert.True(layeredOutcome.ExitCode == 0, Describe(layeredRun, layeredOutcome));

        var layeredSteps = await layeredRun.ReadStepsAsync();

        // Only the child's own stdout is asserted (the worker's "zenkins: …" lines share the step id), and
        // the values are distinctive on purpose: a substring match on "step"/"project" would pass whatever
        // layer won, which is exactly how a mis-ordered composition would slip through.
        Assert.Equal("value-from-step", ChildOutput(layeredRun, layeredSteps[0].Id));
        Assert.Equal("value-from-pipeline", ChildOutput(layeredRun, layeredSteps[1].Id));

        // The inherited value lost every edge: no step of this run ever read it.
        Assert.DoesNotContain(layeredRun.Lines, line =>
            line.Message.Contains("value-from-inherited", StringComparison.Ordinal));

        // Run 2: the project declares no pipeline env and its step declares none, but it has a parameter
        // with the same name - the parameter is above the organization layer, so it wins.
        using var parameterized = PipelineRepository.Create(EchoPipeline("parameterized", "TARGET"));
        using var parameterizedRun = await StepsRun.StartAsync(
            host,
            parameterized,
            prepareProject: (projectId, client) => SaveParameterAsync(client, projectId, "TARGET", "value-from-param"));
        var parameterizedOutcome = await parameterizedRun.ExecuteAsync();
        Assert.True(parameterizedOutcome.ExitCode == 0, Describe(parameterizedRun, parameterizedOutcome));

        var parameterizedSteps = await parameterizedRun.ReadStepsAsync();
        Assert.Equal("value-from-param", ChildOutput(parameterizedRun, parameterizedSteps[0].Id));

        // Run 3: nothing declared at project level and no parameter - the organization layer is what the
        // step reads. (Run 2 with the parameter removed would prove it by absence; a third run proves it by
        // presence, which is the stronger statement.)
        using var fromOrganization = PipelineRepository.Create(EchoPipeline("from-organization", "TARGET"));
        using var fromOrganizationRun = await StepsRun.StartAsync(host, fromOrganization);
        var fromOrganizationOutcome = await fromOrganizationRun.ExecuteAsync();
        Assert.True(fromOrganizationOutcome.ExitCode == 0, Describe(fromOrganizationRun, fromOrganizationOutcome));

        var fromOrganizationSteps = await fromOrganizationRun.ReadStepsAsync();
        Assert.Equal("value-from-org", ChildOutput(fromOrganizationRun, fromOrganizationSteps[0].Id));
    }

    [Fact]
    public async Task TC_002b_the_metadata_edge_is_proven_by_refusal_and_filtering()
    {
        // The organization and parameter layers are refused at save (see the two facts above). A pipeline
        // or step env cannot be refused - Lua has no save step - so the composition drops a reserved name
        // and reports it, and the metadata keeps its value. That is what replaces "the metadata happened
        // to be written last" as the mechanism.
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("reserved-layer-project");
        using var _ = owner;
        await CreateVariableAsync(owner, "TARGET", "org");

        using var repository = PipelineRepository.Create($$"""
            pipeline {
              name = "reserved",
              env = { ZENKINS_PROJECT_ID = "spoofed-by-pipeline" },
              steps = {
                {
                  name = "StepSpoofsTheRun",
                  run = { "{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "echo", env("ZENKINS_RUN_ID") },
                  env = { ZENKINS_RUN_ID = "spoofed-by-step" },
                },
              },
            }
            """);

        using var run = await StepsRun.StartAsync(host, repository);

        // The composition drops both reserved names and says so - once per name, naming it.
        var diagnostics = new List<string>();
        var environment = StepEnvironment.Compose(
            new Dictionary<string, string?>(),
            pipelineEnvironment: new Dictionary<string, string> { ["ZENKINS_PROJECT_ID"] = "spoofed-by-pipeline" },
            stepEnvironment: new Dictionary<string, string> { ["ZENKINS_RUN_ID"] = "spoofed-by-step" },
            WorkerRunLoop.BuildRunEnvironment(run.Claim!, "reserved-agent"),
            diagnostics.Add);
        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, message =>
            message.Contains("ZENKINS_PROJECT_ID", StringComparison.Ordinal));
        Assert.Contains(diagnostics, message => message.Contains("ZENKINS_RUN_ID", StringComparison.Ordinal));

        // The metadata won both names, and the declared values are gone from the map entirely.
        Assert.Equal(run.RunId.ToString(), environment["ZENKINS_RUN_ID"]);
        Assert.Equal(run.ProjectId.ToString(), environment["ZENKINS_PROJECT_ID"]);
        Assert.DoesNotContain("spoofed-by-step", environment.Values);
        Assert.DoesNotContain("spoofed-by-pipeline", environment.Values);

        // The same run, executed for real: the step never sees the spoofed id, and the run log carries the
        // diagnostic the composition produced.
        var outcome = await run.ExecuteAsync();
        Assert.True(outcome.ExitCode == 0, Describe(run, outcome));
        var steps = await run.ReadStepsAsync();
        Assert.Contains(run.Lines, line =>
            line.StepId == steps[0].Id && line.Message.Contains(run.RunId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(run.Lines, line =>
            line.Message.Contains("spoofed-by-step", StringComparison.Ordinal));
        Assert.Contains(run.Lines, line =>
            line.Message.Contains("ZENKINS_RUN_ID", StringComparison.Ordinal)
            && line.Message.Contains("reserved", StringComparison.Ordinal));

        // ...exactly once, with one location prefix: the composition's sentence carries no `zenkins: `
        // of its own (the runner writes the step's location), so the line a person reads is
        // `zenkins: step 1/1 "StepSpoofsTheRun": the step env sets 'ZENKINS_RUN_ID', …`.
        var dropped = Assert.Single(run.Lines, line =>
            line.Message.Contains("the step env sets 'ZENKINS_RUN_ID'", StringComparison.Ordinal));
        Assert.StartsWith(
            "zenkins: step 1/1 \"StepSpoofsTheRun\": the step env sets 'ZENKINS_RUN_ID'",
            dropped.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, dropped.Message.Split("zenkins: ").Length - 1);

        // The project's own parameter list cannot smuggle a reserved name either (fact above); the
        // project is untouched by this run.
        Assert.Null((await host.ReadProjectAsync(projectId)).ParametersJson);
    }

    // ---------------------------------------------------------------------------------------------
    // The secret boundary: an environment variable and a secret are two storages, not two layers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_variable_that_shares_a_secret_s_name_stays_configuration_and_the_secret_stays_masked()
    {
        // An organization variable and a secret may carry the same name without becoming the same thing.
        // They live in two storages and are read by two mechanisms (the claim's resolved environment map
        // vs the run's decrypted secret cache), and only the decrypted secret values are registered with
        // the redactor. Nothing asserted this boundary before, so nothing stopped a later change from
        // folding the two into "one environment" - which would either leak a secret into a plaintext
        // page or mask a value that is configuration.
        const string name = "SHARED_SETTING";
        const string configValue = "config-visible-4e19";
        const string secretValue = "secret-masked-8b2c";

        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.Inner.SignInAsync(OrganizationTestHost.OwnerEmail);
        using var _ = owner;
        await CreateVariableAsync(owner, name, configValue);

        // Two steps, one per reader: the organization layer is read from the step's *environment*, the
        // secret from `secret(name)` - Lua's two different sources.
        using var repository = PipelineRepository.Create($$"""
            pipeline {
              name = "secret-boundary",
              steps = {
                {
                  name = "FromEnvironment",
                  run = { "{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "env", "{{name}}" },
                },
                {
                  name = "FromSecret",
                  run = { "{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "echo", secret("{{name}}") },
                },
              },
            }
            """);

        using var run = await StepsRun.StartAsync(host, repository);
        await run.CreateSecretAsync(name, secretValue);

        var outcome = await run.ExecuteAsync();
        Assert.True(outcome.ExitCode == 0, Describe(run, outcome));

        var steps = await run.ReadStepsAsync();

        // 1. The environment reader got the organization's value: configuration, printed verbatim and
        //    never masked, because it is not a secret.
        Assert.Equal(configValue, ChildOutput(run, steps[0].Id));

        // 2. The secret reader got the *secret's* value - the one registered with the redactor - so the
        //    child's line is the replacement token. Had the two storages been conflated, this line would
        //    show the configuration value instead.
        Assert.Equal("***", ChildOutput(run, steps[1].Id));

        // 3. The secret value reaches no surface this run renders (log, persisted rows, page, download),
        //    while the configuration value is visible on the log and the run page - the two opposite
        //    rules, held apart on the same name.
        Assert.DoesNotContain(run.Lines, line => line.Message.Contains(secretValue, StringComparison.Ordinal));
        var rows = await run.ReadLogRowsAsync();
        Assert.DoesNotContain(rows, row => row.Message.Contains(secretValue, StringComparison.Ordinal));
        var page = await run.ReadRunPageAsync();
        Assert.DoesNotContain(secretValue, page, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, await run.DownloadLogAsync(), StringComparison.Ordinal);
        Assert.Contains(run.Lines, line => line.Message.Contains(configValue, StringComparison.Ordinal));
        Assert.Contains(configValue, page, StringComparison.Ordinal);

        // 4. The claim's one resolved map carries the configuration value under that name; the secret is
        //    not a layer, so nothing about it reaches the composed environment.
        Assert.NotNull(run.Claim);
        Assert.Equal(configValue, run.Claim!.Parameters![name]);
        var environment = StepEnvironment.Compose(
            new Dictionary<string, string?>(),
            pipelineEnvironment: null,
            stepEnvironment: null,
            WorkerRunLoop.BuildRunEnvironment(run.Claim, run.WorkerName));
        Assert.Equal(configValue, environment[name]);
        Assert.DoesNotContain(secretValue, environment.Values);

        // 5. Each page keeps its own rule: the environment page shows the value, the secrets page shows
        //    the name only (F-012's write-only rule is unchanged by the organization layer).
        var environmentPage = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner, OrganizationEnvironmentEndpoints.ListPath);
        Assert.Contains(configValue, environmentPage, StringComparison.Ordinal);
        var secretsPage = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, "/secrets");
        Assert.Contains(name, secretsPage, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, secretsPage, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // TC-003: the snapshot is frozen at trigger time
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TC_003_the_layer_is_frozen_when_the_run_is_queued_and_editing_it_later_does_not_move_it()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("frozen-project");
        await CreateVariableAsync(owner, RegistryHost, "registry.before");
        var variableId = await ReadVariableIdAsync(host);

        // Trigger, and do NOT let a worker claim it: the run is simply queued.
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var queued = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, queued.Status);
        Assert.Equal("registry.before", ReadSnapshot(queued)[RegistryHost]);

        // Change the organization's value after the run was queued.
        var updated = await OrganizationTestHost.PostFormAsync(
            owner,
            OrganizationEnvironmentEndpoints.ListPath,
            $"/organizations/environment/{variableId}/update",
            new Dictionary<string, string> { ["Name"] = RegistryHost, ["Value"] = "registry.after" });
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.Equal("registry.after", Assert.Single(await ReadVariablesAsync(host)).Value);

        // The queued run still carries the value it was created with...
        Assert.Equal("registry.before", ReadSnapshot(await host.ReadRunAsync(runId))[RegistryHost]);

        // ...and the claim the worker receives carries the same old value, which the worker's own
        // composition turns into the step's environment. The new value is nowhere in this run.
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "frozen-agent");
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        var claim = System.Text.Json.JsonSerializer.Deserialize(
            await claimed.Content.ReadAsStringAsync(CancellationToken),
            WorkerJsonContext.Default.WorkerClaimResponse);
        Assert.NotNull(claim);
        Assert.Equal("registry.before", claim!.Parameters![RegistryHost]);

        var environment = StepEnvironment.Compose(
            new Dictionary<string, string?>(),
            pipelineEnvironment: null,
            stepEnvironment: null,
            WorkerRunLoop.BuildRunEnvironment(claim, "frozen-agent"));
        Assert.Equal("registry.before", environment[RegistryHost]);
        Assert.DoesNotContain("registry.after", environment.Values);

        // ...and the run page shows the snapshot, not today's value (AC6).
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains("registry.before", page, StringComparison.Ordinal);
        Assert.DoesNotContain("registry.after", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_variable_leaves_the_runs_snapshot_and_its_page_intact()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("deleted-variable-project");
        await CreateVariableAsync(owner, RegistryHost, "registry.kept");
        var variableId = await ReadVariableIdAsync(host);

        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        var deleted = await OrganizationTestHost.PostFormAsync(
            owner,
            OrganizationEnvironmentEndpoints.ListPath,
            $"/organizations/environment/{variableId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Empty(await ReadVariablesAsync(host));

        // The run keeps its own copy (AC6): a removed variable never breaks history, and the page still
        // shows the value the run executed with.
        var run = await host.ReadRunAsync(runId);
        Assert.Equal("registry.kept", ReadSnapshot(run)[RegistryHost]);
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains("registry.kept", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_headless_webhook_trigger_freezes_the_layer_like_any_other_trigger()
    {
        // A git push has nobody signed in and no trigger form, so the organization layer must come from the
        // delivery's own run rather than from anything a person supplied. The read is keyed by the run's
        // organization and taken inside the system scope (RunEnvironmentSnapshot.ReadForOrganizationAsync);
        // the fact that it needs no request tenant at all is pinned separately, in
        // TenantIsolationProbeTests.The_run_snapshot_reader_reads_only_the_run_s_own_organization_…
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var created = await CreateVariableAsync(owner, RegistryHost, "registry.internal");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var webhookId = await host.SeedWebhookAsync("environment-headless");
        using var accepted = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('f')),
            host.SecretFor("environment-headless"),
            deliveryId: "environment-headless-1");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(BuildRunTriggerType.Webhook, run.TriggerType);
        Assert.Null(run.TriggerUserId);
        Assert.Equal("registry.internal", ReadSnapshot(run)[RegistryHost]);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>A one-step pipeline that echoes <c>${env:NAME}</c> with no layer of its own.</summary>
    private static string EchoPipeline(string name, string variable) => $$"""
        pipeline {
          name = "{{name}}",
          steps = {
            {
              name = "Echo",
              run = { "{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "echo", env("{{variable}}") },
            },
          },
        }
        """;

    private static async Task<HttpResponseMessage> CreateVariableAsync(
        HttpClient client,
        string name,
        string value) =>
        await OrganizationTestHost.PostFormAsync(
            client,
            OrganizationEnvironmentEndpoints.ListPath,
            "/organizations/environment/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Value"] = value,
            });

    /// <summary>Declares one string parameter with that default, through the real parameters form.</summary>
    private static async Task SaveParameterAsync(HttpClient owner, Guid projectId, string name, string value)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ParametersJson"] = $$"""
                    [{"name":"{{name}}","type":"string","defaultValue":"{{value}}","choicesText":"","description":""}]
                    """,
            });
        Assert.Contains("notice=parameters-saved", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>The one line a step's child process printed: its own stdout, not the worker's.</summary>
    private static string ChildOutput(StepsRun run, Guid stepId) =>
        Assert.Single(
            run.Lines,
            line => line.StepId == stepId
                && line.Stream == Zenkins.Shared.LogStreaming.LogStream.StdOut
                && !line.Message.StartsWith("zenkins: ", StringComparison.Ordinal)).Message;

    /// <summary>Why a run did not succeed: its failure reason and every line it emitted.</summary>
    private static string Describe(StepsRun run, Zenkins.Worker.Pipelines.PipelineRunOutcome outcome) =>
        $"exit={outcome.ExitCode} failure={outcome.FailureReason} abandonded={outcome.Abandoned}\n"
        + string.Join("\n", run.Lines.Select(line => line.Message));

    private static Task<IReadOnlyList<OrganizationEnvironmentVariable>> ReadVariablesAsync(RunTestHost host) =>
        host.InSystemScopeAsync(async context =>
        {
            var rows = await context.SystemQueryAsync<OrganizationEnvironmentVariable>(_ => true);
            return (IReadOnlyList<OrganizationEnvironmentVariable>)
                [.. rows.OrderBy(row => row.Name, StringComparer.Ordinal)];
        });

    private static async Task<Guid> ReadVariableIdAsync(RunTestHost host) =>
        Assert.Single(await ReadVariablesAsync(host)).Id;

    private static IReadOnlyDictionary<string, string> ReadSnapshot(BuildRun run) =>
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            RunEnvironmentSnapshot.Read(run.EnvironmentJson));
}
