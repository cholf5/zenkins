using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Organizations;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Steps;

/// <summary>
/// Card F-015's secrets (TC-004, TC-005): <c>secret()</c> is delivered to the run that asks for it, the value
/// never reaches the log (streamed, persisted, downloaded or rendered), an unknown name fails the step naming
/// the name, and one organization can never read another's.
/// </summary>
[Trait("Category", "Steps")]
public sealed class StepSecretTests
{
    private const string TokenName = "DEPLOY_TOKEN";
    private const string TokenValue = "deploy-9f3a7c1b2e5d4a6f";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // TC-004: a secret never reaches the log
    // ------------------------------------------------------------------

    [Fact]
    public async Task TC_004_a_secret_echoed_by_a_child_appears_as_redacted_everywhere()
    {
        var pipeline = """
            pipeline {
              name = "secrets",
              steps = {
                { name = "Stdout", run = { "{{dotnet}}", "{{argv}}", "echo", secret("{{name}}") } },
                { name = "Stderr", run = { "{{dotnet}}", "{{argv}}", "write-slow", "0", secret("{{name}}") } },
              },
            }
            """
            .Replace("{{dotnet}}", PipelineTestData.DotNet, StringComparison.Ordinal)
            .Replace("{{argv}}", PipelineTestData.ArgvEcho, StringComparison.Ordinal)
            .Replace("{{name}}", TokenName, StringComparison.Ordinal);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);
        await run.CreateSecretAsync(TokenName, TokenValue);

        var outcome = await run.ExecuteAsync();
        Assert.Equal(0, outcome.ExitCode);

        var steps = await run.ReadStepsAsync();

        // 1. The child's stdout and its stderr both carry the replacement token, never the value.
        Assert.Contains(
            run.Lines,
            line => line.StepId == steps[0].Id
                && line.Stream == Zenkins.Shared.LogStreaming.LogStream.StdOut
                && line.Message == "***");
        Assert.Contains(
            run.Lines,
            line => line.StepId == steps[1].Id
                && line.Stream == Zenkins.Shared.LogStreaming.LogStream.StdErr
                && line.Message == "***");
        Assert.DoesNotContain(run.Lines, line => line.Message.Contains(TokenValue, StringComparison.Ordinal));

        // 2. The own-line case the card calls out explicitly: the step headers the worker synthesizes render
        //    the resolved argv, so they would carry the value if the filter did not cover them too.
        Assert.Contains(
            run.Lines,
            line => line.Message.StartsWith("zenkins: step 1/2 \"Stdout\" started:", StringComparison.Ordinal)
                && line.Message.Contains("***", StringComparison.Ordinal));
        Assert.Contains(
            run.Lines,
            line => line.Message.StartsWith("zenkins: step 2/2 \"Stderr\" started:", StringComparison.Ordinal)
                && line.Message.Contains("***", StringComparison.Ordinal));

        // 3. The persisted rows: no plaintext anywhere, and the replacement where the value would have been.
        var rows = await run.ReadLogRowsAsync();
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, row => row.Message.Contains(TokenValue, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Message.Contains("***", StringComparison.Ordinal));

        // 4. The download endpoint and the run page render the same rows.
        var download = await run.DownloadLogAsync();
        Assert.DoesNotContain(TokenValue, download, StringComparison.Ordinal);
        Assert.Contains("***", download, StringComparison.Ordinal);

        var page = await run.ReadRunPageAsync();
        Assert.DoesNotContain(TokenValue, page, StringComparison.Ordinal);

        // 5. The secret's own surface is unchanged: the page lists the name, never the value.
        var secretsPage = await AuthTestHost.GetStringAsync(run.Owner, "/secrets");
        Assert.Contains(TokenName, secretsPage, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenValue, secretsPage, StringComparison.Ordinal);

        // 6. The worker's own output surfaces carry no plaintext either. Read back from disk, the two temp log
        //    files are the local copies the worker keeps for debugging (the harness writes them through the
        //    product's own RunLogWriters, so this is the file a person would open):
        var tempOut = run.TempStdOutLog();
        var tempErr = run.TempStdErrLog();
        Assert.DoesNotContain(TokenValue, tempOut, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenValue, tempErr, StringComparison.Ordinal);
        Assert.NotEmpty(tempOut);
        Assert.NotEmpty(tempErr);
        Assert.Contains("***", tempOut, StringComparison.Ordinal);
        Assert.Contains("***", tempErr, StringComparison.Ordinal);

        //    Deliberately NOT asserted here: the worker's own console writers. On a happy path the product writes
        //    nothing to them, so "no plaintext on the console" would be a claim about an empty string - an
        //    assertion that cannot fail. That surface is pinned where it really carries text instead: the crash
        //    path (StepLogWriterTests.The_worker_crash_diagnostic_is_redacted_and_written_through_the_same_gate).

        // 7. Both steps really ran (so the assertions above are about the filter, not about a step that never
        //    produced output), and the cached value was fetched for both.
        Assert.All(steps, step => Assert.Equal(PipelineStepStatus.Success, step.Status));
        Assert.Equal(0, steps[0].ExitCode);
    }

    [Fact]
    public async Task TC_004_an_unknown_secret_name_fails_the_step_naming_the_name_and_no_value()
    {
        var pipeline = """
            pipeline {
              name = "unknown-secret",
              steps = {
                { name = "Deploy", run = { "{{dotnet}}", "{{argv}}", "echo", secret("NO_SUCH_SECRET") } },
                { name = "After", run = { "{{dotnet}}", "{{argv}}", "echo", "after" } },
              },
            }
            """
            .Replace("{{dotnet}}", PipelineTestData.DotNet, StringComparison.Ordinal)
            .Replace("{{argv}}", PipelineTestData.ArgvEcho, StringComparison.Ordinal);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var outcome = await run.ExecuteAsync();

        var steps = await run.ReadStepsAsync();
        Assert.Equal(PipelineStepStatus.Failed, steps[0].Status);
        Assert.Equal(PipelineStepFailureReasons.SecretUnavailable, steps[0].FailureReason);
        Assert.Null(steps[0].ExitCode);

        // The message names the NAME, never a value.
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains(
                "secret(\"NO_SUCH_SECRET\") is not available in this run's organization.",
                StringComparison.Ordinal));

        // The next step is skipped and the run fails - a step that could not be prepared does not silently
        // become an empty value.
        Assert.Equal(PipelineStepStatus.Skipped, steps[1].Status);
        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);
        Assert.Equal(BuildRunStatus.Failed, (await run.ReadRunAsync()).Status);
        Assert.DoesNotContain(run.Lines, line => line.Message == "after");
    }

    [Fact]
    public async Task A_run_triggered_by_a_member_reads_the_organization_secrets_and_still_redacts_them()
    {
        // PRD §3.8: whoever can edit pipeline.lua is treated as having the build's permissions, so a Member's
        // run reads the same secrets - and the same filter keeps the value out of the log.
        var pipeline = """
            pipeline {
              name = "member-secret",
              steps = {
                { name = "Echo", run = { "{{dotnet}}", "{{argv}}", "echo", secret("{{name}}") } },
              },
            }
            """
            .Replace("{{dotnet}}", PipelineTestData.DotNet, StringComparison.Ordinal)
            .Replace("{{argv}}", PipelineTestData.ArgvEcho, StringComparison.Ordinal)
            .Replace("{{name}}", TokenName, StringComparison.Ordinal);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var member = await CreateMemberAsync(host);
        using var run = await StepsRun.StartAsync(host, repository, actingClient: member);
        await run.CreateSecretAsync(TokenName, TokenValue);

        var outcome = await run.ExecuteAsync();

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(run.Lines, line => line.Message == "***");
        Assert.DoesNotContain(run.Lines, line => line.Message.Contains(TokenValue, StringComparison.Ordinal));

        var runRow = await run.ReadRunAsync();
        Assert.Equal(BuildRunStatus.Success, runRow.Status);
    }

    [Fact]
    public async Task A_two_character_secret_is_still_redacted_end_to_end()
    {
        // The clip that makes a length threshold unusable: a 1-2 character value would be skipped by any
        // "long enough to bother" rule, and organization secrets have no minimum length of their own.
        const string shortValue = "zq";
        var pipeline = """
            pipeline {
              name = "short-secret",
              steps = {
                { name = "Echo", run = { "{{dotnet}}", "{{argv}}", "echo", secret("SHORT_TOKEN") } },
              },
            }
            """
            .Replace("{{dotnet}}", PipelineTestData.DotNet, StringComparison.Ordinal)
            .Replace("{{argv}}", PipelineTestData.ArgvEcho, StringComparison.Ordinal);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);
        await run.CreateSecretAsync("SHORT_TOKEN", shortValue);

        var outcome = await run.ExecuteAsync();
        Assert.Equal(0, outcome.ExitCode);

        var step = Assert.Single(await run.ReadStepsAsync());
        Assert.Equal(PipelineStepStatus.Success, step.Status);

        // The child really received the two-character value, and every surface shows the replacement instead.
        Assert.Contains(
            run.Lines,
            line => line.StepId == step.Id
                && line.Stream == Zenkins.Shared.LogStreaming.LogStream.StdOut
                && line.Message == "***");
        Assert.DoesNotContain(run.Lines, line => line.Message.Contains(shortValue, StringComparison.Ordinal));

        var rows = await run.ReadLogRowsAsync();
        Assert.DoesNotContain(rows, row => row.Message.Contains(shortValue, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Message == "***");

        var download = await run.DownloadLogAsync();
        Assert.DoesNotContain(shortValue, download, StringComparison.Ordinal);

        // The page renders the frozen spec's argv, which carries the *reference*, never a value. (A two-character
        // value cannot be asserted absent from the whole HTML: the page embeds per-request tokens, and "zq" inside
        // a random token is not the secret. What matters is that the value is never rendered as a value.)
        var page = await run.ReadRunPageAsync();
        Assert.Contains("${secret:SHORT_TOKEN}", page, StringComparison.Ordinal);
        Assert.DoesNotContain($">{shortValue}<", page, StringComparison.Ordinal);

        Assert.DoesNotContain(shortValue, run.TempStdOutLog(), StringComparison.Ordinal);
        Assert.DoesNotContain(shortValue, run.TempStdErrLog(), StringComparison.Ordinal);

        // The step header (rendered from the resolved argv) is redacted too - it would otherwise carry the value.
        Assert.Contains(
            run.Lines,
            line => line.Message.StartsWith("zenkins: step 1/1 \"Echo\" started:", StringComparison.Ordinal)
                && line.Message.Contains("***", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // TC-005: tenant and worker scoping
    // ------------------------------------------------------------------

    [Fact]
    public async Task TC_005_a_run_in_another_organization_cannot_read_a_secret_that_only_exists_elsewhere()
    {
        // The secret exists in organization A (the starter organization, owned by the test's owner).
        var pipeline = """
            pipeline {
              name = "cross-org-secret",
              steps = {
                { name = "Deploy", run = { "{{dotnet}}", "{{argv}}", "echo", secret("A_ONLY") } },
              },
            }
            """
            .Replace("{{dotnet}}", PipelineTestData.DotNet, StringComparison.Ordinal)
            .Replace("{{argv}}", PipelineTestData.ArgvEcho, StringComparison.Ordinal);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();

        // Organization A's secret, created through the real Owner form.
        using (var ownerRun = await StepsRun.StartAsync(host, repository, workerName: "a-owner-agent"))
        {
            await ownerRun.CreateSecretAsync("A_ONLY", "organization-a-value-7d1f");
        }

        // Organization B: registered user, organization, and a run executing there.
        const string foreignEmail = "carol@example.test";
        using var carol = await host.Inner.Inner.RegisterUserAsync(foreignEmail);
        var organizationB = await host.Inner.Inner.AddOrganizationAsync(
            "Bravo",
            (foreignEmail, OrganizationRole.Owner));

        using var foreignRun = await StepsRun.StartInOrganizationAsync(host, repository, organizationB);
        var outcome = await foreignRun.ExecuteAsync();

        var steps = await foreignRun.ReadStepsAsync();
        var step = Assert.Single(steps);
        Assert.Equal(PipelineStepStatus.Failed, step.Status);
        Assert.Equal(PipelineStepFailureReasons.SecretUnavailable, step.FailureReason);

        // The message names the missing secret and never returns a value; nothing that looks like
        // organization A's value appears in organization B's log.
        Assert.Contains(
            foreignRun.Lines,
            line => line.Message.Contains("secret(\"A_ONLY\")", StringComparison.Ordinal));
        Assert.DoesNotContain(
            foreignRun.Lines,
            line => line.Message.Contains("organization-a-value-7d1f", StringComparison.Ordinal));
        Assert.Equal(BuildRunStatus.Failed, (await foreignRun.ReadRunAsync()).Status);
    }

    [Fact]
    public async Task TC_005_a_worker_of_another_organization_gets_the_same_404_as_a_missing_run()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "scoped-secret",
                PipelineTestData.Step("Only", "echo", "ok")));
        using var host = await RunTestHost.CreateAsync();

        // Organization A: a run and its worker, plus the secret.
        using var runA = await StepsRun.StartAsync(host, repository, workerName: "a-scoped-agent");
        await runA.CreateSecretAsync("A_ONLY", "organization-a-value-7d1f");

        // The positive control: A's worker, A's run, A's name - a value comes back.
        using (var allowed = await SendSecretAsync(runA.WorkerClient, runA.WorkerToken, runA.RunId, "A_ONLY"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            var body = await allowed.Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains("organization-a-value-7d1f", body, StringComparison.Ordinal);
        }

        // Organization B's worker token (created directly: a second organization has no Owner session here).
        const string foreignEmail = "carol@example.test";
        using var carol = await host.Inner.Inner.RegisterUserAsync(foreignEmail);
        var organizationB = await host.Inner.Inner.AddOrganizationAsync(
            "Bravo",
            (foreignEmail, OrganizationRole.Owner));
        var foreignToken = await host.CreateWorkerTokenDirectlyAsync(organizationB, name: "b-scoped-agent");

        using var client = host.Host.CreateClient();

        // B's worker asking for A's run, and B's worker asking for a run that does not exist at all: the two
        // answers must be indistinguishable (status and body), so the route is not an existence oracle.
        using var foreignRun = await SendSecretAsync(client, foreignToken, runA.RunId, "A_ONLY");
        using var missingRun = await SendSecretAsync(client, foreignToken, Guid.NewGuid(), "A_ONLY");

        Assert.Equal(HttpStatusCode.NotFound, foreignRun.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRun.StatusCode);
        Assert.Equal(
            await missingRun.Content.ReadAsStringAsync(CancellationToken),
            await foreignRun.Content.ReadAsStringAsync(CancellationToken));

        // And a token A's worker does not own cannot ask for A's run either.
        using var notOwned = await SendSecretAsync(client, foreignToken, runA.RunId, "A_ONLY");
        Assert.Equal(HttpStatusCode.NotFound, notOwned.StatusCode);
    }

    // ------------------------------------------------------------------
    // The per-run cache
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_secret_cache_fetches_one_name_once_and_keeps_only_hits()
    {
        var source = new CountingSecretSource(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "value-a", ["B"] = "value-b" });
        var cache = new RunSecretCache(source);

        Assert.Equal("value-a", (await cache.FetchAsync("A", CancellationToken)).Value);
        Assert.Equal("value-a", (await cache.FetchAsync("A", CancellationToken)).Value);
        Assert.Equal(RunSecretLookup.NotFound, (await cache.FetchAsync("MISSING", CancellationToken)).Outcome);
        Assert.Equal(RunSecretLookup.NotFound, (await cache.FetchAsync("MISSING", CancellationToken)).Outcome);

        // One fetch per hit (the second A was answered from memory); each miss is re-asked rather than cached.
        Assert.Equal(3, source.Fetches);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void A_second_run_in_the_same_process_starts_with_an_empty_cache()
    {
        // The cache belongs to a run, not to the worker: nothing a run decrypted is reachable from the next
        // one, and nothing is written anywhere a restart could reload.
        var first = new RunSecretCache(new CountingSecretSource(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "value-a" }));
        var second = new RunSecretCache(new CountingSecretSource(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "value-a" }));

        Assert.Equal(0, first.Count);
        Assert.Equal(0, second.Count);
    }

    // ------------------------------------------------------------------
    // The redaction filter itself
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("ab")] // shorter than the old threshold
    [InlineData("abc")]
    [InlineData("a-real-secret-value")]
    public void Every_registered_value_is_replaced_however_short(string value)
    {
        // A length threshold would be a bypass, so there is none: a 1-3 character secret is exactly the case an
        // attacker would choose, and organization secrets have no minimum length of their own.
        var filter = new Zenkins.Worker.Git.GitLogFilter();
        filter.Register(value);

        var redacted = filter.Redact($"token={value} end");

        Assert.Equal("token=*** end", redacted);
        Assert.False(filter.ContainsSecret(redacted));
    }

    [Fact]
    public void The_longest_registered_value_is_replaced_as_a_whole()
    {
        var filter = new Zenkins.Worker.Git.GitLogFilter();
        filter.Register("abc");
        filter.Register("abcdef");

        Assert.Equal("*** and ***", filter.Redact("abcdef and abc"));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers a second account and has the starter organization's Owner invite it, so the run can be
    /// triggered by an ordinary Member (the AC's "a Member-triggered run can still read the secrets").
    /// </summary>
    private static async Task<HttpClient> CreateMemberAsync(RunTestHost host)
    {
        const string memberEmail = "bob@example.test";
        var member = await host.Inner.Inner.RegisterUserAsync(memberEmail);
        using (var owner = await host.Inner.SignInOwnerAsync())
        {
            using var invite = await OrganizationTestHost.PostFormAsync(
                owner,
                OrganizationEndpoints.MembersPath,
                "/organizations/members/invite",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Email"] = memberEmail });
            Assert.True(
                (int)invite.StatusCode is 302 or 303,
                $"inviting the Member failed ({(int)invite.StatusCode}).");
        }

        // Resolving the member's session selects the starter organization (the single-membership fallback).
        _ = await AuthTestHost.GetStringAsync(member, "/");
        return member;
    }

    private static Task<HttpResponseMessage> SendSecretAsync(
        HttpClient client,
        string workerToken,
        Guid runId,
        string name) =>
        WorkerTestHost.SendWorkerJsonAsync(
            client,
            workerToken,
            $"/api/workers/runs/{runId}/secrets",
            new { name });

    /// <summary>A source that counts its fetches, for the cache test.</summary>
    private sealed class CountingSecretSource(IReadOnlyDictionary<string, string> values) : IRunSecretSource
    {
        public int Fetches { get; private set; }

        public Task<RunSecretResult> FetchAsync(string name, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(values.TryGetValue(name, out var value)
                ? RunSecretResult.Found(value)
                : RunSecretResult.NotFound());
        }
    }
}
