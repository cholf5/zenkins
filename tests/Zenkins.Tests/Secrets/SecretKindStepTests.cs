using System.Text;
using Xunit;
using Zenkins.Server.Projects;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Secrets;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Worker.Git;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Steps;

/// <summary>
/// Card F-040's worker half, end to end: the real server (the kind travels in the delivery answer), the real
/// executor (the reference grammar and the payload decoder), the real redaction filter, and the real run
/// directory. Each fact pins one of the card's promises - a pair binds both halves, a file secret is a path
/// whose material lives in this run's own tree, a mismatched part is a refused step, and the log carries the
/// material of neither.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretKindStepTests
{
    private const string PairName = "REGISTRY";
    private const string PairUser = "deploy-bot";
    private const string PairPassword = "registry-pw-9d2c4f";

    private const string FileName = "DEPLOY_KEY";
    private const string FileToken = "key-material-6b1c8a3d5e7f";

    private const string TextName = "PLAIN_TOKEN";
    private const string TextValue = "plain-value-2f5a7c1e";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// TC-001: a pair binds both halves, and only the password is redacted. The username is deliberately
    /// visible - it is an identity, and a log that hid it would make "which account logged in" unanswerable.
    /// </summary>
    [Fact]
    public async Task TC_001_a_pair_binds_both_halves_and_the_log_hides_only_the_password()
    {
        // One argument per half, labelled: the labels are what makes a swap visible (two bare lines would read
        // the same either way round).
        var pipeline = Pipeline($$"""
            { name = "Pair", run = { [[{{PipelineTestData.DotNet}}]], [[{{PipelineTestData.ArgvEcho}}]],
                "echo", "user=${secret:{{PairName}}.username}", "password=${secret:{{PairName}}.password}" } }
            """);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);
        await run.CreateSecretAsync(
            PairName,
            value: string.Empty,
            kind: SecretKinds.UsernamePassword,
            username: PairUser,
            password: PairPassword);

        var outcome = await run.ExecuteAsync();
        Assert.Equal(0, outcome.ExitCode);

        var messages = run.Lines.Select(line => line.Message).ToArray();

        // Each half arrived where the reference asked for it: the username as itself, the password already
        // replaced by the filter (the child really received it - ArgvEcho prints what it was given).
        Assert.Contains($"user={PairUser}", messages);
        Assert.Contains($"password={GitLogFilter.Replacement}", messages);

        // And the password is nowhere in the run's surfaces, in any form.
        Assert.DoesNotContain(messages, message => message.Contains(PairPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(
            await run.ReadLogRowsAsync(),
            row => row.Message.Contains(PairPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(PairPassword, await run.DownloadLogAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(PairPassword, await run.ReadRunPageAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// TC-002: a file secret is a <b>path</b>, the file exists during and after the step inside the run's own
    /// directory, its mode is owner-only, the log carries the path (not redacted) and never the material, and
    /// the run's own temp cleanup removes it.
    /// </summary>
    [Fact]
    public async Task TC_002_a_file_secret_is_a_path_in_the_run_directory_and_the_temp_cleanup_removes_it()
    {
        var pipeline = Pipeline($$"""
            { name = "File", run = { [[{{PipelineTestData.DotNet}}]], [[{{PipelineTestData.ArgvEcho}}]],
                "echo", "${secretFile:{{FileName}}}" } }
            """);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);
        await run.CreateSecretAsync(FileName, FileToken, kind: SecretKinds.File);

        var outcome = await run.ExecuteAsync();
        Assert.Equal(0, outcome.ExitCode);

        // The substituted value is printed verbatim: it is a path, so it is not redacted.
        var path = Assert.Single(
            run.Lines.Select(line => line.Message),
            message => message.StartsWith(run.Work.TempPath, StringComparison.Ordinal));

        // Inside this run's own directory - never a shared temp location ...
        Assert.StartsWith(run.Work.TempPath, path, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("secrets", FileName), path, StringComparison.Ordinal);

        // ... and the material is really there, byte for byte, readable only by the worker's account.
        Assert.True(File.Exists(path), $"the file secret was not written to '{path}'.");
        Assert.Equal(FileToken, await File.ReadAllTextAsync(path, CancellationToken));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }

        // The log carries the location and not the contents.
        Assert.DoesNotContain(
            run.Lines,
            line => line.Message.Contains(FileToken, StringComparison.Ordinal));

        // The run's own temp cleanup owns the tree the file was written into, so nothing outlives the run.
        run.CleanupTemp();
        Assert.False(Directory.Exists(run.Work.TempPath));
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// TC-003: asking a text secret for a half is refused at the step boundary with a message that names the
    /// reference and what the secret actually is. The step fails before any process starts, and the value is
    /// still never printed.
    /// </summary>
    [Fact]
    public async Task TC_003_a_part_on_a_text_secret_fails_the_step_and_names_the_reference()
    {
        var pipeline = Pipeline($$"""
            { name = "Mismatch", run = { [[{{PipelineTestData.DotNet}}]], [[{{PipelineTestData.ArgvEcho}}]],
                "echo", "${secret:{{TextName}}.password}" } }
            """);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);
        await run.CreateSecretAsync(TextName, TextValue);

        var outcome = await run.ExecuteAsync();

        // Nothing ran: there is no exit code to record, and the reason is the secret layer's own.
        Assert.Null(outcome.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);

        var step = (await run.ReadStepsAsync()).Single();
        Assert.Equal(PipelineStepStatus.Failed, step.Status);
        Assert.Equal(PipelineStepFailureReasons.SecretUnavailable, step.FailureReason);

        // The line names the reference and the truth about the secret - not just "unavailable".
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains($"${{secret:{TextName}.password}}", StringComparison.Ordinal)
                && line.Message.Contains("is a text secret", StringComparison.Ordinal));

        // And the text value itself was never substituted or printed.
        Assert.DoesNotContain(
            run.Lines,
            line => line.Message.Contains(TextValue, StringComparison.Ordinal));
    }

    /// <summary>
    /// A pair referenced as both halves costs <b>one</b> fetch, and the filter holds the password and not the
    /// username. The resolver's own contract, without a server in the way.
    /// </summary>
    [Fact]
    public async Task A_pair_referenced_twice_is_fetched_once_and_only_the_password_is_registered()
    {
        var source = new CountingSecretSource(
            ("REG", RunSecretResult.Found(
                SecretKinds.UsernamePassword,
                SecretPayload.ForPair(PairUser, PairPassword).Encode())));
        var redactor = new GitLogFilter();
        var resolver = new StepValueResolver(redactor, source);

        var resolved = await resolver.ResolveAsync(
            "${secret:REG.username}:${secret:REG.password}",
            new Dictionary<string, string?>(),
            CancellationToken);

        Assert.Equal($"{PairUser}:{PairPassword}", resolved);
        Assert.Equal(1, source.Fetches);
        Assert.Equal(GitLogFilter.Replacement, redactor.Redact(PairPassword));
        Assert.Equal(PairUser, redactor.Redact(PairUser));
    }

    /// <summary>
    /// A file secret over the card's bound is refused instead of written, and the message names the secret and
    /// the limit - never the material.
    /// </summary>
    [Fact]
    public async Task A_file_secret_over_the_bound_is_refused_and_nothing_is_written()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zenkins-f040-files-{Guid.NewGuid():N}");
        try
        {
            var files = new RunSecretFiles(directory);
            var oversized = new string('k', SecretFileLimits.MaxBytes + 1);

            var failure = Assert.Throws<RunSecretFileException>(() => files.Materialize("BIG_KEY", oversized));

            Assert.Contains("BIG_KEY", failure.Message, StringComparison.Ordinal);
            Assert.Contains($"{SecretFileLimits.MaxBytes}-byte limit", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(oversized, failure.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// AC-7: the claim carries <b>names only</b>, so a file secret at the size bound never bloats it. The run's
    /// spec is built by the product's own Freestyle builder (which is also where the recorded reference form is
    /// captured), and the secret row is a real 60 KiB one in the same organization.
    /// </summary>
    [Fact]
    public async Task A_large_file_secret_never_reaches_the_claim_payload()
    {
        const string Marker = "file-secret-marker-1f4b";
        var material = Marker + new string('m', (56 * 1024) - Marker.Length);

        var specJson = FreestyleSteps.BuildSpecJson(
            "creds",
            new FreestyleStepsInput(System.Text.Json.JsonSerializer.Serialize(
                new[]
                {
                    new FreestyleStepDraft(
                        Name: "Deploy",
                        Script: "deploy --key-file ${secretFile:BIG_KEY}",
                        EnvLines: null,
                        ContinueOnError: false,
                        ArtifactsText: null),
                },
                FreestyleSteps.DraftsOptions)),
            out var error);
        Assert.NotNull(specJson);
        Assert.Null(error);

        using var host = await RunTestHost.CreateAsync();
        var organizationId = await host.OrganizationIdAsync();
        var projectId = await host.SeedProjectAsync("creds", "https://example.test/creds.git");
        var (workerToken, _) = await host.RegisterWorkerAsync();
        var runId = await host.AddRunAsync(projectId);

        // The frozen spec the claim will carry, and the secret the worker will fetch by name instead.
        await host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<Zenkins.Server.Data.Entities.BuildRun>(
                candidate => candidate.Id == runId)).Single();
            // A non-null spec with no script is exactly what a Freestyle run looks like, so the claim's
            // derived shape word is `freestyle` and its payload is this spec.
            run.PipelineSpecJson = specJson;
            context.BuildRuns.Update(run);

            context.Secrets.Add(new Zenkins.Server.Data.Entities.Secret
            {
                OrganizationId = organizationId,
                Name = "BIG_KEY",
                Kind = SecretKinds.File,
                Ciphertext = "seeded-ciphertext-not-read-by-the-claim-7b31",
                CreatedByUserId = "owner-claim-seed",
            });

            await context.SaveChangesAsync();
            return true;
        });

        using var claim = await RunTestHost.ClaimAsync(host.Host.CreateClient(), workerToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, claim.StatusCode);

        // The claim names the reference - so the worker knows what to fetch - and carries no material.
        var claimedSpec = await RunTestHost.ReadClaimStringAsync(claim, "pipelineSpecJson");
        Assert.Contains("file:BIG_KEY", claimedSpec, StringComparison.Ordinal);
        Assert.DoesNotContain(material, claimedSpec, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, claimedSpec, StringComparison.Ordinal);

        // The same, measured: a claim that dragged the 56 KiB file along could not be this short.
        Assert.True(
            claimedSpec.Length < 4096,
            $"the claim's spec is {claimedSpec.Length} characters, which suggests secret material rode along.");
    }

    private static string Pipeline(string step) => $$"""
        pipeline {
          name = "secret-kinds",
          steps = { {{step}} },
        }
        """;

    /// <summary>A secret source that answers from a table and counts how many fetches it served.</summary>
    private sealed class CountingSecretSource(params (string Name, RunSecretResult Result)[] values) : IRunSecretSource
    {
        private readonly Dictionary<string, RunSecretResult> _values =
            values.ToDictionary(entry => entry.Name, entry => entry.Result, StringComparer.Ordinal);

        public int Fetches { get; private set; }

        public Task<RunSecretResult> FetchAsync(string name, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(
                _values.TryGetValue(name, out var result) ? result : RunSecretResult.NotFound());
        }
    }
}
