using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Secrets;
using Zenkins.Shared.Secrets;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.Steps;

/// <summary>
/// Card F-040's delivery rule, over the real route: all three kinds travel to the worker executing the run, and
/// <b>the refusal contract does not widen</b> - a foreign run, a foreign name, another worker's run and an ended
/// run are one bodyless 404, byte-identical, so neither the response nor the log can tell them apart.
/// <para>
/// The kind is asserted because it is what makes the payload usable: a pair's payload is the length-prefixed
/// pair document and a file's is its bytes, so a worker that was not told would have to guess.
/// </para>
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretKindDeliveryTests
{
    private const string TextName = "DELIVER_TEXT";
    private const string TextValue = "deliver-text-4a7c";

    private const string PairName = "DELIVER_PAIR";
    private const string PairUser = "deliver-bot";
    private const string PairPassword = "deliver-pw-19e3b";

    private const string FileName = "DELIVER_FILE";
    private const string FileContent = "deliver-file-bytes-2c8f";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string DeliveryPath(Guid runId) => $"/api/workers/runs/{runId}/secrets";

    [Fact]
    public async Task The_run_scoped_fetch_serves_every_kind_and_refuses_every_miss_identically()
    {
        var pipeline = """
            pipeline {
              name = "delivery",
              steps = { { name = "Noop", run = { "git", "--version" } } },
            }
            """;

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);
        await run.CreateSecretAsync(TextName, TextValue);
        await run.CreateSecretAsync(
            PairName,
            value: string.Empty,
            kind: SecretKinds.UsernamePassword,
            username: PairUser,
            password: PairPassword);
        await run.CreateSecretAsync(FileName, FileContent, kind: SecretKinds.File);

        // 1. Every kind is served, with the shape its kind promises and no caching.
        var text = await FetchAsync(run, TextName);
        Assert.Equal(HttpStatusCode.OK, text.Status);
        Assert.Equal("no-store", text.CacheControl);
        Assert.Equal(SecretKinds.Text, text.Kind);
        Assert.Equal(TextValue, text.Value);

        var pair = await FetchAsync(run, PairName);
        Assert.Equal(HttpStatusCode.OK, pair.Status);
        Assert.Equal(SecretKinds.UsernamePassword, pair.Kind);
        var pairPayload = SecretPayload.Decode(pair.Kind!, pair.Value!);
        Assert.Equal(PairUser, pairPayload.Username);
        Assert.Equal(PairPassword, pairPayload.Password);

        var file = await FetchAsync(run, FileName);
        Assert.Equal(HttpStatusCode.OK, file.Status);
        Assert.Equal(SecretKinds.File, file.Kind);
        Assert.Equal(FileContent, SecretPayload.Decode(file.Kind!, file.Value!).Content);

        // 2. Every miss is the same bodyless 404 - the refusal contract is unchanged by the kinds.
        var unknownName = await FetchAsync(run, "NOT_A_SECRET");
        var unknownRun = await FetchAsync(run, TextName, runId: Guid.NewGuid());

        var otherWorkerToken = await host.CreateWorkerTokenDirectlyAsync(
            await host.OrganizationIdAsync(),
            name: "delivery-other-agent");
        using var otherWorkerClient = host.Host.CreateClient();
        using var otherWorkerResponse = await WorkerTestHost.SendWorkerJsonAsync(
            otherWorkerClient,
            otherWorkerToken,
            DeliveryPath(run.RunId),
            new { name = TextName });
        var otherWorkerBody = await otherWorkerResponse.Content.ReadAsStringAsync(CancellationToken);

        // 3. An ended run serves nothing either: the run this worker just finished is no longer a reader.
        using var completed = await RunTestHost.CompleteAsync(run.WorkerClient, run.WorkerToken, run.RunId, 0);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var endedRun = await FetchAsync(run, TextName);

        Assert.Equal(HttpStatusCode.NotFound, unknownName.Status);
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.Status);
        Assert.Equal(HttpStatusCode.NotFound, otherWorkerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, endedRun.Status);

        // Byte-identical: no body distinguishes "not yours" from "does not exist", and no kind leaks either.
        Assert.Equal(string.Empty, unknownName.Body);
        Assert.Equal(string.Empty, unknownRun.Body);
        Assert.Equal(string.Empty, otherWorkerBody);
        Assert.Equal(string.Empty, endedRun.Body);
    }

    private static async Task<(HttpStatusCode Status, string Body, string? Kind, string? Value, string? CacheControl)>
        FetchAsync(StepsRun run, string name, Guid? runId = null)
    {
        using var response = await WorkerTestHost.SendWorkerJsonAsync(
            run.WorkerClient,
            run.WorkerToken,
            DeliveryPath(runId ?? run.RunId),
            new { name });
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        var cacheControl = response.Headers.CacheControl?.ToString();

        if (response.StatusCode != HttpStatusCode.OK || body.Length == 0)
        {
            return (response.StatusCode, body, null, null, cacheControl);
        }

        using var document = JsonDocument.Parse(body);
        return (
            response.StatusCode,
            body,
            document.RootElement.GetProperty("kind").GetString(),
            document.RootElement.GetProperty("value").GetString(),
            cacheControl);
    }
}
