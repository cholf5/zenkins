using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-021 on the headless trigger: a git push has no person to fill the parameter form, so the
/// webhook run resolves the definitions' defaults and snapshots them like any other run. The values
/// are configuration, not secrets - the assertion reads the resolved object, names and values alike.
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookParameterDefaultsTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_webhook_run_resolves_the_parameter_defaults()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        await SaveParametersAsync(owner, host.ProjectId);

        var webhookId = await host.SeedWebhookAsync("parameter-defaults");
        var secret = host.SecretFor("parameter-defaults");
        using var accepted = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('d')),
            secret,
            deliveryId: "parameter-defaults-1");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(BuildRunTriggerType.Webhook, run.TriggerType);
        Assert.Null(run.TriggerUserId);

        var values = ReadParameters(run.ParametersJson);
        Assert.Equal("hello", values["GREETING"]);
        Assert.Equal("false", values["DRY_RUN"]);
        Assert.Equal("slow", values["MODE"]);
        Assert.Equal(string.Empty, values["NOTES"]);
    }

    [Fact]
    public async Task A_manual_trigger_value_never_leaks_into_a_later_webhook_run()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        await SaveParametersAsync(owner, host.ProjectId);

        // A manual run with a supplied value first.
        var manual = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}",
            $"/projects/{host.ProjectId}/runs/start",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Branch"] = string.Empty,
                ["RequiredTags"] = string.Empty,
                ["Parameters[GREETING]"] = "manual-only",
            });
        Assert.True((int)manual.StatusCode is 302 or 303);

        var webhookId = await host.SeedWebhookAsync("parameter-independence");
        var secret = host.SecretFor("parameter-independence");
        using var accepted = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('e')),
            secret,
            deliveryId: "parameter-independence-1");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var runs = await host.ReadRunsAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal("manual-only", ReadParameters(runs.Single(run => run.TriggerType == BuildRunTriggerType.Manual).ParametersJson)["GREETING"]);
        Assert.Equal("hello", ReadParameters(runs.Single(run => run.TriggerType == BuildRunTriggerType.Webhook).ParametersJson)["GREETING"]);
    }

    private static async Task SaveParametersAsync(HttpClient owner, Guid projectId)
    {
        var definitionsJson = JsonSerializer.Serialize(new object[]
        {
            new { name = "GREETING", type = "string", defaultValue = "hello", choicesText = "", description = "" },
            new { name = "NOTES", type = "text", defaultValue = "", choicesText = "", description = "" },
            new { name = "DRY_RUN", type = "boolean", defaultValue = "false", choicesText = "", description = "" },
            new { name = "MODE", type = "choice", defaultValue = "slow", choicesText = "fast,slow", description = "" },
        });

        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = definitionsJson });
        Assert.Contains("notice=parameters-saved", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> ReadParameters(string? json)
    {
        Assert.NotNull(json);
        return JsonSerializer.Deserialize(json!, ProjectParameterJsonContext.Default.DictionaryStringString)!;
    }
}
