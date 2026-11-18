using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Projects;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card Q-022's evidence debt on the delivery vocabulary: <see cref="WebhookOutcomes"/> was a hand-written
/// list of constants with no set and no rule, and the endpoint documentation repeated a subset of it.
/// <para>
/// <b>Why a fact and not a database constraint.</b> The stored outcome column is deliberately open - it also
/// carries the run-level refusal codes a project's gate answers with - so the words a delivery may record
/// cannot be a check constraint. They are held here instead: the set is the constants (nothing more, nothing
/// less), the delivery path only ever writes through the set, and the sentence a client reads lists every
/// member.
/// </para>
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookOutcomeVocabularyTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void The_webhook_outcome_vocabulary_is_exactly_the_declared_constants()
    {
        // Read the constants by reflection: a value added to the type without `All` (or removed from it)
        // is the failure this fact exists for. `All` is a hand-written set - C# cannot enumerate a type's
        // own constants - so the two must be held equal by something, and attention is not something.
        var declared = typeof(WebhookOutcomes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(
            declared.OrderBy(value => value, StringComparer.Ordinal),
            WebhookOutcomes.All.OrderBy(value => value, StringComparer.Ordinal));

        // No two constants may spell the same word: a set cannot hold it twice, and the column's
        // "last outcome" would then be ambiguous.
        Assert.Equal(declared.Length, declared.Distinct(StringComparer.Ordinal).Count());

        // Every member fits the column the delivery row stores it in.
        Assert.All(
            WebhookOutcomes.All,
            outcome => Assert.True(
                outcome.Length <= WebhookOutcomes.MaxLength,
                $"'{outcome}' is longer than the stored outcome column ({WebhookOutcomes.MaxLength})."));

        // And the value a delivery records is the constant, never a spelling of it.
        Assert.All(WebhookOutcomes.All, outcome => Assert.Equal(outcome, WebhookOutcomes.Require(outcome)));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebhookOutcomes.Require("ignored:brnach"));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebhookOutcomes.Require(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebhookOutcomes.Require(null));
        Assert.False(WebhookOutcomes.IsKnown("branch-invalid"));
    }

    [Fact]
    public async Task The_delivery_path_only_stores_outcomes_the_vocabulary_declares()
    {
        using var host = await WebhooksTestHost.CreateAsync();

        // One single-branch project: the three ref shapes and the filter.
        var singleWebhook = await host.SeedWebhookAsync("vocabulary-single", branchFilter: "main");

        // One multibranch project: the outcomes only the discovery path produces. The no-pipeline verdict
        // is seeded at the pushed commit on purpose - the delivery then records the stored observation
        // instead of asking for a read, which is the branch this matrix needs to visit.
        var multiProjectId = await host.AddProjectAsync(
            host.OrganizationId,
            "vocabulary-multi",
            WebhooksTestHost.RepositoryUrl,
            WebhooksTestHost.DefaultBranch,
            BranchDiscoveryModes.Branches);
        var multiWebhook = await host.SeedWebhookAsync(
            "vocabulary-multi-hook",
            branchFilter: string.Empty,
            projectId: multiProjectId);
        var noPipelineCommit = WebhookPayloads.Commit('e');
        await host.SeedBranchAsync(
            "docs-only",
            BranchPipelineStates.NoPipeline,
            noPipelineCommit,
            projectId: multiProjectId);

        var expected = new List<(Guid WebhookId, string Outcome)>();

        async Task Deliver(Guid webhookId, string name, string body, string deliveryId, string outcome)
        {
            using var response = await host.DeliverSignedAsync(
                webhookId,
                body,
                host.SecretFor(name),
                deliveryId: deliveryId);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            expected.Add((webhookId, outcome));
        }

        await Deliver(
            singleWebhook,
            "vocabulary-single",
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('a')),
            "vocabulary-1",
            WebhookOutcomes.Queued);
        await Deliver(
            singleWebhook,
            "vocabulary-single",
            WebhookPayloads.GitHub("feature/x", WebhookPayloads.Commit('b')),
            "vocabulary-2",
            WebhookOutcomes.IgnoredBranch);
        await Deliver(
            singleWebhook,
            "vocabulary-single",
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Deleted),
            "vocabulary-3",
            WebhookOutcomes.IgnoredDelete);
        await Deliver(
            singleWebhook,
            "vocabulary-single",
            WebhookPayloads.Tag(WebhookPayloads.Commit('c')),
            "vocabulary-4",
            WebhookOutcomes.IgnoredNotBranch);
        await Deliver(
            multiWebhook,
            "vocabulary-multi-hook",
            WebhookPayloads.GitHub("feature/new", WebhookPayloads.Commit('d')),
            "vocabulary-5",
            WebhookOutcomes.AwaitingObservation);
        await Deliver(
            multiWebhook,
            "vocabulary-multi-hook",
            WebhookPayloads.GitHub("docs-only", noPipelineCommit),
            "vocabulary-6",
            WebhookOutcomes.NoPipeline);
        await Deliver(
            multiWebhook,
            "vocabulary-multi-hook",
            WebhookPayloads.GitHub(new string('a', 201), WebhookPayloads.Commit('f')),
            "vocabulary-7",
            WebhookOutcomes.BranchTooLong);

        // Every outcome this matrix wrote is the constant it should be - and the property the card asks
        // for: the delivery path never stores a word that is not a member of the set. (A writer that
        // invented one would either throw at the write barrier or land here with `All.Contains` false.)
        var stored = new List<string>();
        foreach (var webhookId in expected.Select(entry => entry.WebhookId).Distinct())
        {
            foreach (var delivery in await host.ReadDeliveriesAsync(webhookId))
            {
                stored.Add(delivery.Outcome);
                Assert.True(
                    WebhookOutcomes.IsKnown(delivery.Outcome),
                    $"The delivery path stored '{delivery.Outcome}', which is not in WebhookOutcomes.All.");
            }
        }

        Assert.Equal(
            expected.Select(entry => entry.Outcome).OrderBy(outcome => outcome, StringComparer.Ordinal),
            stored.OrderBy(outcome => outcome, StringComparer.Ordinal));

        // The other three members (`project-disabled`, `queue-full`, `ignored:no-repository`) are recorded by
        // the facts that own their project shapes - WebhookProjectGateTests and WebhookFreestyleGateTests -
        // each asserting the delivery's outcome is the constant. Because the fact above holds every constant
        // inside the set, those three are members too; this matrix is about the paths one host can reach.
    }

    [Fact]
    public void The_endpoint_documentation_names_every_outcome_the_vocabulary_declares()
    {
        // The list a client reads is in the endpoint's own documentation, and it was already behind the set
        // (missing `ignored:no-repository`, never mentioning `awaiting-observation`, `no-pipeline` or
        // `ignored:branch-too-long`). Read the file the reader reads: every member has to be named there.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Server", "Webhooks", "WebhookEndpoints.cs"));

        var documented = Regex.Matches(source, "`(?<token>[^`]+)`")
            .Select(match => match.Groups["token"].Value)
            .Where(token => token.Length > 0 && token.All(character =>
                char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is ':' or '-'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.All(
            WebhookOutcomes.All,
            outcome => Assert.True(
                documented.Contains(outcome, StringComparer.Ordinal),
                $"WebhookEndpoints does not name the outcome '{outcome}'; the list a client reads is behind the set."));

        // The other direction: no word shaped like an outcome may be documented that the set does not
        // declare. `duplicate` is the one exception - it is the *status* a replay answers with, never a
        // stored outcome.
        Assert.All(
            documented,
            token => Assert.True(
                WebhookOutcomes.IsKnown(token) || token == "duplicate",
                $"WebhookEndpoints documents '{token}', which is not a member of WebhookOutcomes.All."));
    }

    /// <summary>The repository root (the directory holding <c>Zenkins.slnx</c>), as the docs facts locate it.</summary>
    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (File.Exists(Path.Combine(directory, "Zenkins.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory)!;
        }

        Assert.Fail("the repository root (Zenkins.slnx) was not found above the test output.");
        return string.Empty;
    }
}
