using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Auth;
using Zenkins.Tests.LogStreaming;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card D-004's test host: everything <see cref="LogStreamingHost"/> does (the real server with its
/// real worker API, browser log hub, ingest and download endpoint), plus the one setup this card's
/// tests repeat - a run with a frozen multi-step definition and recorded step rows, whose ids are
/// what F-015 stamps on log lines and what the panel's filter must key on.
/// </summary>
internal sealed class RunUiHost : IDisposable
{
    private RunUiHost(LogStreamingHost inner) => Streaming = inner;

    public LogStreamingHost Streaming { get; }

    /// <summary>The raw <see cref="AuthTestHost"/> under the wrappers.</summary>
    public AuthTestHost Host => Streaming.Host;

    public static async Task<RunUiHost> CreateAsync() => new(await LogStreamingHost.CreateAsync());

    /// <summary>
    /// Creates a project, registers a worker, and drives one run to <c>Running</c> with the named
    /// steps frozen into a reported definition. No step reports are sent - the test drives each
    /// transition itself through <see cref="RunUiRun.ReportStepAsync"/> (so a failure test can fail
    /// its second step, a filter test can leave a third silent).
    /// </summary>
    public Task<RunUiRun> CreateRunAsync(params string[] stepNames) =>
        CreateRunAsync(
            [.. stepNames.Select(name => new PipelineStep(name, Run: ["dotnet", "--info"]))]);

    /// <summary>
    /// The same, with steps the test declares itself (card F-030: stage names), and optionally an older
    /// schema version - a v2 spec is how the "old runs still render flat" fact seeds its run honestly.
    /// </summary>
    public async Task<RunUiRun> CreateRunAsync(
        IReadOnlyList<PipelineStep> steps,
        int version = PipelineSpecSchema.Version,
        PipelinePost? post = null)
    {
        var (projectId, owner) = await Streaming.CreateProjectAsync();
        var (token, _) = await Streaming.RegisterWorkerAsync();
        var runId = await Streaming.AddRunAsync(projectId);
        var workerClient = Host.CreateClient();

        using var claimed = await RunTestHost.ClaimAsync(workerClient, token);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        using var accepted = await RunTestHost.AcceptAsync(workerClient, token, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var spec = new PipelineSpec(version, "runui-pipeline", [.. steps], Post: post);
        using var definition = await RunTestHost.ReportDefinitionAsync(
            workerClient, token, runId, PipelineSpecJson.Serialize(spec));
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);

        return new RunUiRun(this, runId, owner, workerClient, token, [.. steps.Select(step => step.Name)]);
    }

    public void Dispose() => Streaming.Dispose();
}

/// <summary>One run driven to <c>Running</c> with its frozen step names - what a D-004 test streams against.</summary>
internal sealed class RunUiRun
{
    private readonly RunUiHost _host;

    internal RunUiRun(
        RunUiHost host,
        Guid runId,
        HttpClient owner,
        HttpClient workerClient,
        string workerToken,
        string[] stepNames)
    {
        _host = host;
        RunId = runId;
        Owner = owner;
        WorkerClient = workerClient;
        WorkerToken = workerToken;
        StepNames = stepNames;
    }

    public Guid RunId { get; }

    public HttpClient Owner { get; }

    public HttpClient WorkerClient { get; }

    public string WorkerToken { get; }

    /// <summary>The frozen spec's step names, in declaration order.</summary>
    public string[] StepNames { get; }

    private List<Guid?> RecordedStepIds { get; } = [];

    /// <summary>The recorded step row ids in declaration order - the F-015 stamp values. A step the
    /// test has not reported yet holds null, and its id cannot exist (the row does not either).</summary>
    public IReadOnlyList<Guid?> StepIds
    {
        get
        {
            lock (RecordedStepIds)
            {
                return [.. RecordedStepIds];
            }
        }
    }

    /// <summary>
    /// Sends one step transition through the real step endpoint and records the answer's step row
    /// id - the value the test then stamps on that step's log lines.
    /// </summary>
    /// <param name="name">
    /// Card F-031: a post step's frozen record name (<c>post/…</c>), when the test reports one - the declared
    /// main names cover the main list only.
    /// </param>
    public async Task ReportStepAsync(
        int index,
        string status,
        int? exitCode = null,
        string? failureReason = null,
        string? name = null)
    {
        using var response = await RunTestHost.ReportStepAsync(
            WorkerClient, WorkerToken, RunId, index, name ?? StepNames[index - 1], status, exitCode, failureReason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stepId = await ReadStepIdAsync(response);
        lock (RecordedStepIds)
        {
            while (RecordedStepIds.Count < index)
            {
                RecordedStepIds.Add(null);
            }

            RecordedStepIds[index - 1] = stepId;
        }
    }

    private static async Task<Guid> ReadStepIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("stepId").GetGuid();
    }

    /// <summary>A browser-hub connection with a ticket minted the way the run page mints one.</summary>
    public async Task<HubConnection> BuildBrowserAsync()
    {
        var ownerUserId = await _host.Streaming.ReadOwnerUserIdAsync();
        var ticket = await _host.Streaming.MintTicketAsync(ownerUserId, await _host.Streaming.OrganizationIdAsync());
        return _host.Streaming.BuildBrowserHubConnection(ticket);
    }

    /// <summary>A worker-hub connection carrying this run's worker token.</summary>
    public HubConnection BuildWorkerAsync() => _host.Streaming.BuildWorkerHubConnection(WorkerToken);

    /// <summary>Appends one batch of lines through the worker hub, exactly as the worker's buffer does.</summary>
    public async Task AppendAsync(HubConnection worker, params LogLineMessage[] lines) =>
        await worker.InvokeAsync("AppendLinesAsync", RunId, lines, TestContext.Current.CancellationToken);

    /// <summary>Marks the run terminal through the real completed endpoint (flushes the ingest tail).</summary>
    public async Task CompleteAsync(int? exitCode = 0, string? failureReason = null)
    {
        using var completed = await RunTestHost.CompleteAsync(WorkerClient, WorkerToken, RunId, exitCode, failureReason);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    /// <summary>The run page's HTML, through the real authenticated page.</summary>
    public Task<string> ReadRunPageAsync() => AuthTestHost.GetStringAsync(Owner, $"/runs/{RunId}");

    /// <summary>The full-log download, through the real endpoint (the bytes the pin is about).</summary>
    public async Task<byte[]> DownloadLogAsync()
    {
        using var response = await Owner.GetAsync(
            $"/runs/{RunId}/log/download", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }
}
