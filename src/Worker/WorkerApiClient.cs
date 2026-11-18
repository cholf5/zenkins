using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenkins.Shared.Artifacts;
using Zenkins.Shared.Probes;
using Zenkins.Shared.RunCancellation;
using Zenkins.Worker.Git;

namespace Zenkins.Worker;

/// <summary>Registration payload (F-004 contract). Serialized camelCase.</summary>
public sealed record WorkerRegistrationRequest(
    string Name,
    string Os,
    string Architecture,
    string Version,
    string Tags,
    int CpuCount,
    int MaxConcurrency,

    /// <summary>Card I-005: whether this worker answers Git remote probes (absent = no, on the server).</summary>
    bool CanProbe = false);

/// <summary>Heartbeat payload; capability fields refresh the server row.</summary>
public sealed record WorkerHeartbeatRequest(
    string Os,
    string Architecture,
    string Version,
    string Tags,
    int CpuCount,
    int MaxConcurrency,

    /// <summary>Card I-005: the refreshed probe capability (absent leaves the server's value alone).</summary>
    bool? CanProbe = null);

public sealed record WorkerRegistrationResponse(string WorkerId, string WorkerToken, string WorkerName);

/// <summary>
/// The heartbeat response. <see cref="CancelRequestedRuns"/> is F-009's fallback cancel channel:
/// the server's still-Running runs with a pending cancel request for this worker. Null when the
/// server predates the field (and on any response that omits it) - the heartbeat loop treats that
/// as "no cancellations pending".
/// </summary>
public sealed record WorkerHeartbeatResponse(
    string WorkerId,
    DateTimeOffset LastHeartbeatAt,
    WorkerCancelRequest[]? CancelRequestedRuns = null);

/// <summary>
/// A queued run handed over by the claim endpoint (F-006 contract, extended by F-014, F-017 and F-020;
/// camelCase on the wire). Everything the worker needs to execute a run: for a Pipeline run, where the
/// repository is, which branch it builds and which pipeline file inside it declares the steps; for a
/// Freestyle run, the frozen spec the server snapshotted at trigger time.
/// </summary>
/// <param name="Commit">
/// F-017 (review M1): the exact commit this run must build, when the trigger already named one (a webhook
/// push). <see langword="null"/> - and absent from the JSON of an older server - means "build the branch
/// tip and report what you checked out", the manual behaviour. A non-null value is pinned with
/// <c>checkout()</c> after the clone, because the branch may have moved between the push and now.
/// </param>
/// <param name="RepositoryUrl">
/// F-020: the repository Pipeline runs clone. <see langword="null"/> for a Freestyle run - there is no
/// repository, and the Worker skips the clone entirely.
/// </param>
/// <param name="PipelineSpecJson">
/// F-020: the Freestyle run's frozen spec, snapshotted on the server at trigger time. <see langword="null"/>
/// (and absent from the JSON of an older server) for a Pipeline run, whose spec comes from the Worker's
/// own definition phase - the claim's presence is what makes a run Freestyle.
/// </param>
/// <param name="Parameters">
/// F-021: the run's resolved build-parameter values (name → value). <see langword="null"/> (and absent
/// from the JSON of an older server) when the project declares no parameters. They join the
/// server-provided environment layer - the same precedence the <c>ZENKINS_*</c> variables occupy -
/// so every step of both kinds reads them by name.
/// </param>
public sealed record WorkerClaimResponse(
    Guid RunId,
    Guid ProjectId,
    string? RepositoryUrl,
    string Branch,
    string PipelinePath,
    DateTimeOffset LeaseExpiresAt,
    string? Commit = null,
    string? PipelineSpecJson = null,
    IReadOnlyDictionary<string, string>? Parameters = null,

    /// <summary>
    /// Card F-036: the run's per-project build number, which the worker injects as
    /// <c>ZENKINS_BUILD_NUMBER</c>. Nullable so an older server's claim (and a claim parsed from a
    /// document that predates the field) still binds - a missing number then simply leaves the variable
    /// unset rather than failing the whole claim.
    /// </summary>
    int? Number = null,

    /// <summary>
    /// F-022: an inline Pipeline run's script, snapshotted on the server at trigger time. Nullable -
    /// and absent from an older server's JSON - for a repository or Freestyle run. A non-null value
    /// with no <see cref="RepositoryUrl"/> means "write this to temp/pipeline.lua and run the definition
    /// phase against it"; the Worker never reads the project or its source word.
    /// </summary>
    string? PipelineScript = null,

    /// <summary>
    /// Card F-041: the names of the <c>password</c>-typed parameters in <see cref="Parameters"/>. The worker
    /// registers their values with the run's redaction filter before the first step starts, so a step that echoes
    /// one logs the mask. Names only, and absent from an older server's claim - a claim without the field simply
    /// registers nothing, which is exactly what a run without password parameters needs.
    /// </summary>
    IReadOnlyList<string>? PasswordParameters = null,

    /// <summary>
    /// Card F-041: the names of the <c>file</c>-typed parameters. The worker fetches each one's bytes over the
    /// run-scoped route and writes them into the run's own directory before any step starts; the bytes are
    /// deliberately not in this payload (card F-040's "the claim carries names only"). Absent from an older
    /// server's claim, which is the same as a run with no file parameters.
    /// </summary>
    IReadOnlyList<string>? FileParameters = null,

    /// <summary>
    /// F-022: the run's definition shape word (<see cref="Zenkins.Shared.Pipelines.RunDefinitionSources"/>).
    /// The Worker switches on this - not on "is there a frozen spec?" - because an inline run can carry
    /// both a script and a spec. Absent from an older server's JSON: the Worker then derives the shape
    /// from the fields it does know, exactly as the Server did before the word existed.
    /// </summary>
    string? DefinitionSource = null);

/// <summary>
/// The accepted report's answer (card F-014 extends the bodyless answer): the run's execution deadline,
/// frozen by the server at accept time (F-009). The worker's definition phase runs against it, so the
/// script's budget is the run's own wall clock rather than a worker-side guess.
/// </summary>
public sealed record WorkerAcceptResponse(string Status, DateTimeOffset? DeadlineAt = null);

/// <summary>
/// The definition report (card F-014): the commit the worker checked out, and - once the definition
/// phase ran - the validated spec it froze on the run.
/// <para>
/// <b>Two sends, one endpoint.</b> The commit goes first (immediately after the clone) with a
/// <see langword="null"/> spec, so a run whose definition turns out to be missing, broken or timed out
/// still records what it checked out. The second send adds the spec, before the first step starts.
/// </para>
/// </summary>
public sealed record WorkerDefinitionRequest(string? GitCommit, string? PipelineSpecJson);

/// <summary>The definition report's answer.</summary>
public sealed record WorkerDefinitionResponse(string Status);

/// <summary>
/// The accepted report payload (card F-013 extends the bodyless accepted report): the exact commit the
/// worker checked out, when it cloned one. <c>null</c> for a run that has no repository. The server
/// records it once and never rewrites it.
/// </summary>
public sealed record WorkerAcceptRequest(string? GitCommit);

/// <summary>
/// The completed report payload: exit 0 → Success on the server, anything else → Failed. F-014 adds
/// <paramref name="FailureReason"/>: the closed vocabulary of <c>PipelineFailureReasons</c>, so a run
/// that failed before any process ran (<c>pipeline-missing</c>) or at a step (<c>step-failed</c>, with
/// the step's own exit code) is recorded honestly instead of as a bare exit code.
/// </summary>
public sealed record WorkerCompleteRequest(int? ExitCode, string? FailureReason = null);

/// <summary>The canceled report payload (F-009): <c>canceled</c> or <c>timeout</c>; the server owns
/// the mapping to the terminal state.</summary>
public sealed record WorkerCanceledRequest(string Outcome);

/// <summary>
/// One step transition's payload (card F-015): the 1-based position in the run's frozen spec, the name that
/// spec declares there, the state reached and - when nothing exited - the closed reason. Timestamps are the
/// server's, exactly like the run's own reports.
/// </summary>
public sealed record WorkerStepRequest(
    int Index,
    string Name,
    string Status,
    int? ExitCode = null,
    string? FailureReason = null,
    int? Attempts = null);

/// <summary>The step report's answer: the recorded row's id (stamped on the step's log lines), its state
/// and the run's.</summary>
public sealed record WorkerStepResponse(Guid StepId, string Status, string RunStatus);

/// <summary>
/// The run-scoped secret fetch (card F-015). The <b>name</b> travels in the body: a secret name has no
/// business in a URL or an access log, and the caller is already authenticated and scoped to the run.
/// </summary>
public sealed record WorkerSecretRequest(string Name);

/// <summary>The fetched value, for the worker's per-run in-memory cache only - never written to disk.</summary>
/// <summary>
/// The answer of the run-scoped fetch (card F-015, extended by F-040). <see cref="Kind"/> is the payload's kind:
/// absent from an older server, in which case <c>text</c> is what it could have stored. <see cref="Value"/> is
/// the decrypted payload, whose shape the kind decides - never a raw value the caller may use directly.
/// </summary>
public sealed record WorkerSecretResponse(string Name, string Value, string? Kind = null);

/// <summary>
/// The run-scoped parameter-file request (card F-041): the name of the uploaded <c>file</c> parameter the worker
/// needs. The name travels in the body, never in the URL, so it stays out of access logs.
/// </summary>
public sealed record WorkerParameterFileRequest(string Name);

/// <summary>
/// A Git remote probe leased to this worker (card I-005): the probe's id, the repository snapshot to run
/// <c>git ls-remote</c> against, and until when the lease holds. No credential rides here - it is fetched
/// at probe time, through the probe-scoped credential route.
/// </summary>
public sealed record WorkerProbeClaimResponse(
    Guid ProbeId,
    Guid ProjectId,
    string RepositoryUrl,
    DateTimeOffset LeaseExpiresAt);

/// <summary>
/// A probe's answer: a <c>Zenkins.Shared.Probes.GitProbeOutcomes</c> value, the heads and remote HEAD on
/// success, or the diagnostic on failure. The shared contract validates it on both sides.
/// </summary>
public sealed record WorkerProbeReportRequest(
    string Outcome,
    GitProbeRef[]? Heads = null,
    string? HeadSha = null,
    string? HeadBranch = null,
    string? Message = null);

/// <summary>The probe report's answer (one word; the worker needs nothing else).</summary>
public sealed record WorkerProbeReportResponse(string Status);

/// <summary>
/// A definition-only task leased to this worker (cards F-022 and F-023): the id to report against, the
/// lease, and the subject. An <c>inline-script</c> task carries <see cref="Script"/>; a
/// <c>repository-branch</c> task carries the repository snapshot and no script. A null/absent
/// <see cref="Kind"/> is an inline task - the F-022 shape a pre-F-023 server sends.
/// </summary>
public sealed record WorkerDefinitionValidationClaimResponse(
    Guid ValidationId,
    string? Script,
    DateTimeOffset LeaseExpiresAt,
    string? Kind = null,
    string? RepositoryUrl = null,
    string? Branch = null,
    string? Commit = null,
    string? PipelinePath = null);

/// <summary>
/// One validation's answer (card F-022): <c>valid</c> carries the frozen spec, <c>invalid</c> the
/// diagnostic (which already carries <c>pipeline.lua:(line,col)</c>). The Server re-validates both.
/// </summary>
public sealed record WorkerDefinitionValidationReportRequest(
    string Outcome,
    string? SpecJson = null,
    string? Diagnostic = null,
    string? FailureReason = null);

/// <summary>The validation report's answer (one word; the worker needs nothing else).</summary>
public sealed record WorkerDefinitionValidationReportResponse(string Status);

/// <summary>The server answered with a definitive failure (e.g. 401); retries cannot fix it.</summary>
public sealed class WorkerApiRejectedException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The worker's only dependency on the Server: a plain <see cref="HttpClient"/> and JSON
/// (card F-005). No ASP.NET Core, no SignalR, no inbound port - the worker dials out, authenticates
/// with its token, and everything else is the server's business.
/// <para>
/// Transport failures (DNS, TCP, TLS, timeouts, 5xx) surface as exceptions so the runner can
/// retry with backoff; transient 4xx answers (408/409/425/429) stay retryable too - a 409 on
/// register is what the loser of a same-name first-start race gets, and its retry succeeds once
/// the winner's row is committed. Every other 4xx is a <see cref="WorkerApiRejectedException"/>
/// - a definitive answer that must not be retried blindly.
/// </para>
/// </summary>
public sealed class WorkerApiClient(HttpClient httpClient)
{
    /// <summary>Per-request ceiling: a hung server must not hold the loop forever.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The ceiling of one artifact file upload (card F-016). Deliberately much larger than
    /// <see cref="RequestTimeout"/>: moving a build product is not a control call, and the run's own
    /// deadline plus its cancellation token are what bound it in practice.
    /// </summary>
    private static readonly TimeSpan ArtifactUploadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Single-use registration token → per-worker token (shown/stored once).</summary>
    public async Task<WorkerRegistrationResponse> RegisterAsync(
        string registrationToken,
        WorkerRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                "api/workers/register",
                registrationToken,
                request,
                WorkerJsonContext.Default.WorkerRegistrationRequest),
            cancellationToken);
        var response = inFlight.Response;
        await EnsureSuccessAsync(response, "registering", inFlight.Timeout);
        var body = await response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerRegistrationResponse, inFlight.Timeout)
            ?? throw new WorkerApiRejectedException("the server returned an empty registration response.", (int)response.StatusCode);
        return body;
    }

    /// <summary>Worker token → refreshed heartbeat.</summary>
    public async Task<WorkerHeartbeatResponse> HeartbeatAsync(
        string workerToken,
        WorkerHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                "api/workers/heartbeat",
                workerToken,
                request,
                WorkerJsonContext.Default.WorkerHeartbeatRequest),
            cancellationToken);
        var response = inFlight.Response;
        await EnsureSuccessAsync(response, "sending a heartbeat", inFlight.Timeout);
        var body = await response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerHeartbeatResponse, inFlight.Timeout)
            ?? throw new WorkerApiRejectedException("the server returned an empty heartbeat response.", (int)response.StatusCode);
        return body;
    }

    /// <summary>
    /// Asks for the next queued run (F-006). Returns <see langword="null"/> for the 204 answer
    /// (nothing eligible right now); a claim never learns why it got nothing.
    /// </summary>
    public async Task<WorkerClaimResponse?> ClaimAsync(
        string workerToken,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(HttpMethod.Post, "api/workers/claim", workerToken),
            cancellationToken);
        var response = inFlight.Response;
        await EnsureSuccessAsync(response, "claiming a run", inFlight.Timeout);
        if ((int)response.StatusCode == 204)
        {
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerClaimResponse, inFlight.Timeout)
            ?? throw new WorkerApiRejectedException("the server returned an empty claim response.", (int)response.StatusCode);
    }

    /// <summary>
    /// Reports a claimed run accepted (the single Queued → Running edge). Only the run-report
    /// definitive set (not-my-lease / gone / foreign id) is surfaced as a
    /// <see cref="WorkerApiRejectedException"/> and the run is given up, never retried; 408/425/429 stay
    /// transient like everywhere else (review m14).
    /// <para>
    /// F-014: the answer carries the run's frozen deadline, which bounds the definition phase. A server
    /// that predates the field answers without it; the caller then falls back to the runtime's own
    /// ceiling.
    /// </para>
    /// </summary>
    /// <param name="gitCommit">
    /// The exact commit the worker checked out (card F-013), or <see langword="null"/> for a run with no
    /// repository. The server writes it once and never rewrites it. F-014 reports the commit with the
    /// definition report instead (the clone happens after the accept), so this stays optional.
    /// </param>
    public async Task<WorkerAcceptResponse> AcceptAsync(
        string workerToken,
        Guid runId,
        string? gitCommit,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/accepted",
                workerToken,
                new WorkerAcceptRequest(gitCommit),
                WorkerJsonContext.Default.WorkerAcceptRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting the run accepted",
            inFlight.Timeout,
            isRunReport: true);
        return await inFlight.Response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerAcceptResponse, inFlight.Timeout)
            ?? new WorkerAcceptResponse("Running");
    }

    /// <summary>
    /// Reports the run's definition (card F-014): the resolved commit, and the frozen validated spec
    /// when <paramref name="pipelineSpecJson"/> is not null. Its definitive 4xx answers (the run is gone,
    /// not ours any more, or the spec was refused) surface as <see cref="WorkerApiRejectedException"/> and
    /// the run is given up; a throttling 429 or a request timeout is retried (review m14).
    /// </summary>
    public async Task ReportDefinitionAsync(
        string workerToken,
        Guid runId,
        string? gitCommit,
        string? pipelineSpecJson,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/definition",
                workerToken,
                new WorkerDefinitionRequest(gitCommit, pipelineSpecJson),
                WorkerJsonContext.Default.WorkerDefinitionRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting the run's pipeline definition",
            inFlight.Timeout,
            isRunReport: true);
    }

    /// <summary>
    /// Fetches the run's Git credential material through F-013's client (one run-scoped request,
    /// answered by the server for the worker currently executing the run). <c>null</c> means "no
    /// material": the clone proceeds anonymously.
    /// </summary>
    public Task<GitCredentialMaterial?> FetchGitCredentialAsync(
        string workerToken,
        Guid runId,
        CancellationToken cancellationToken) =>
        new GitCredentialClient(httpClient).FetchAsync(workerToken, runId, cancellationToken);

    /// <summary>
    /// Asks for the next Git remote probe (card I-005). Returns <see langword="null"/> for the 204 answer
    /// (nothing to probe right now) - a claim never learns why.
    /// <para>
    /// A 404 here is a Server that predates this card (the route does not exist): it surfaces as a
    /// <see cref="WorkerApiRejectedException"/> and the probe loop stops for the process's lifetime
    /// instead of retrying a route that will never appear.
    /// </para>
    /// </summary>
    public async Task<WorkerProbeClaimResponse?> ClaimProbeAsync(
        string workerToken,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(HttpMethod.Post, "api/workers/probes/claim", workerToken),
            cancellationToken);
        await EnsureSuccessAsync(inFlight.Response, "claiming a Git probe", inFlight.Timeout);
        if ((int)inFlight.Response.StatusCode == 204)
        {
            return null;
        }

        return await inFlight.Response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerProbeClaimResponse, inFlight.Timeout)
            ?? throw new WorkerApiRejectedException(
                "the server returned an empty probe claim response.",
                (int)inFlight.Response.StatusCode);
    }

    /// <summary>
    /// Reports one probe's answer (card I-005). Its definitive 4xx answers (the probe is gone, the lease
    /// was lost or the payload was refused) surface as <see cref="WorkerApiRejectedException"/> and the
    /// probe is given up - retrying a refused report cannot make it acceptable.
    /// </summary>
    public async Task ReportProbeAsync(
        string workerToken,
        Guid probeId,
        WorkerProbeReportRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/probes/{probeId}/report",
                workerToken,
                request,
                WorkerJsonContext.Default.WorkerProbeReportRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting a Git probe",
            inFlight.Timeout,
            isRunReport: true);
    }

    /// <summary>
    /// Asks for the next definition-only validation (card F-022). Returns <see langword="null"/> for
    /// the 204 answer. A 404 is a Server that predates this card: the loop stops for the process's
    /// lifetime, exactly like the probe loop's behaviour.
    /// </summary>
    public async Task<WorkerDefinitionValidationClaimResponse?> ClaimDefinitionValidationAsync(
        string workerToken,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(HttpMethod.Post, "api/workers/definitions/claim", workerToken),
            cancellationToken);
        await EnsureSuccessAsync(inFlight.Response, "claiming a pipeline definition validation", inFlight.Timeout);
        if ((int)inFlight.Response.StatusCode == 204)
        {
            return null;
        }

        return await inFlight.Response.Content
            .ReadFromJsonAsync(
                WorkerJsonContext.Default.WorkerDefinitionValidationClaimResponse,
                inFlight.Timeout)
            ?? throw new WorkerApiRejectedException(
                "the server returned an empty definition validation claim response.",
                (int)inFlight.Response.StatusCode);
    }

    /// <summary>
    /// Reports one definition validation's answer (card F-022). A definitive 4xx (the validation was
    /// settled, the lease lapsed, the payload was refused) is given up - retrying cannot change it.
    /// </summary>
    public async Task ReportDefinitionValidationAsync(
        string workerToken,
        Guid validationId,
        WorkerDefinitionValidationReportRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/definitions/{validationId}/report",
                workerToken,
                request,
                WorkerJsonContext.Default.WorkerDefinitionValidationReportRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting a pipeline definition validation",
            inFlight.Timeout,
            isRunReport: true);
    }

    /// <summary>
    /// The material for one probe this worker holds (card I-005): the probe-scoped twin of
    /// <see cref="FetchGitCredentialAsync"/>. <c>null</c> means "no credential assigned" and the probe
    /// runs anonymously.
    /// </summary>
    public Task<GitCredentialMaterial?> FetchProbeGitCredentialAsync(
        string workerToken,
        Guid probeId,
        CancellationToken cancellationToken) =>
        new GitCredentialClient(httpClient).FetchForProbeAsync(workerToken, probeId, cancellationToken);

    /// <summary>
    /// The material for one definition task this worker holds (card F-023's branch observation).
    /// <c>null</c> means "no credential assigned" and the observation clones anonymously.
    /// </summary>
    public Task<GitCredentialMaterial?> FetchDefinitionGitCredentialAsync(
        string workerToken,
        Guid validationId,
        CancellationToken cancellationToken) =>
        new GitCredentialClient(httpClient).FetchForDefinitionAsync(workerToken, validationId, cancellationToken);

    /// <summary>
    /// Reports a finished run with its exit code (0 → Success, non-zero → Failed on the server) and,
    /// when nothing produced one, the closed <paramref name="failureReason"/>. A 400/404/409/410 is
    /// definitive per-run: a 409 invalid-transition after a lost response means the first report already
    /// landed, so giving up is correct; 408/425/429 are retried like any other transient answer (m14).
    /// </summary>
    public async Task CompleteAsync(
        string workerToken,
        Guid runId,
        int? exitCode,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/completed",
                workerToken,
                new WorkerCompleteRequest(exitCode, failureReason),
                WorkerJsonContext.Default.WorkerCompleteRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting the run completed",
            inFlight.Timeout,
            isRunReport: true);
    }

    /// <summary>
    /// Reports that a server-requested cancellation killed the run (F-009). The outcome selects
    /// the server-side mapping: <c>canceled</c> → Canceled with the recorded cancel reason,
    /// <c>timeout</c> → Failed with reason <c>timeout</c> - one kill, one endpoint. Its definitive 4xx
    /// answers (a 409 means the run already reached a terminal state, e.g. a Lost transition won the
    /// race) are surfaced as a <see cref="WorkerApiRejectedException"/> and the run is given up;
    /// 408/425/429 are retried (review m14).
    /// </summary>
    public async Task ReportCanceledAsync(
        string workerToken,
        Guid runId,
        string outcome,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/canceled",
                workerToken,
                new WorkerCanceledRequest(outcome),
                WorkerJsonContext.Default.WorkerCanceledRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting the run canceled",
            inFlight.Timeout,
            isRunReport: true);
    }

    /// <summary>
    /// Reports one step transition (card F-015). Its definitive 4xx answers (the run is not this worker's,
    /// has ended, or a step arrived out of order) surface as <see cref="WorkerApiRejectedException"/> and the
    /// run is given up; 408/425/429 are retried like every other report.
    /// </summary>
    public async Task<WorkerStepResponse> ReportStepAsync(
        string workerToken,
        Guid runId,
        WorkerStepRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/steps",
                workerToken,
                request,
                WorkerJsonContext.Default.WorkerStepRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting a run step",
            inFlight.Timeout,
            isRunReport: true);
        return await inFlight.Response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerStepResponse, inFlight.Timeout)
            ?? throw new WorkerApiRejectedException(
                "the server returned an empty step report response.",
                (int)inFlight.Response.StatusCode);
    }

    /// <summary>Fetches one decrypted secret for the run this worker is executing (card F-015). A 404 means "no value
    /// for this run" - an unknown name, another organization's name, or a run that is not this worker's are
    /// deliberately indistinguishable - and answers <see langword="null"/>; a dead token is definitive.
    /// </summary>
    public async Task<WorkerSecretResponse?> FetchRunSecretAsync(
        string workerToken,
        Guid runId,
        string name,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/secrets",
                workerToken,
                new WorkerSecretRequest(name),
                WorkerJsonContext.Default.WorkerSecretRequest),
            cancellationToken);
        if ((int)inFlight.Response.StatusCode == 404)
        {
            return null;
        }

        await EnsureSuccessAsync(
            inFlight.Response,
            "fetching a run secret",
            inFlight.Timeout,
            isRunReport: true);
        return await inFlight.Response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerSecretResponse, inFlight.Timeout);
    }

    /// <summary>
    /// Fetches one uploaded <c>file</c> parameter's bytes for a run this worker is executing (card F-041). A 404
    /// is <see langword="null"/> - every refusal is the same one, so the worker cannot tell the reasons apart
    /// either. The response is raw bytes, never JSON: there is nothing to decode.
    /// </summary>
    public async Task<byte[]?> FetchRunParameterFileAsync(
        string workerToken,
        Guid runId,
        string name,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/parameter-files",
                workerToken,
                new WorkerParameterFileRequest(name),
                WorkerJsonContext.Default.WorkerParameterFileRequest),
            cancellationToken);
        if ((int)inFlight.Response.StatusCode == 404)
        {
            return null;
        }

        await EnsureSuccessAsync(
            inFlight.Response,
            "fetching a run parameter file",
            inFlight.Timeout,
            isRunReport: true);
        return await inFlight.Response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------
    // Artifacts (card F-016): create → per-file stream → finalize
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reserves one artifact row for a run this worker executes (card F-016). The answer is the artifact id
    /// every later verb addresses; the row is <c>Pending</c> until <see cref="FinalizeArtifactAsync"/>.
    /// </summary>
    public async Task<ArtifactCreateResponse> CreateArtifactAsync(
        string workerToken,
        Guid runId,
        ArtifactCreateRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/artifacts",
                workerToken,
                request,
                WorkerJsonContext.Default.ArtifactCreateRequest),
            cancellationToken);
        await EnsureArtifactSuccessAsync(inFlight.Response, "reserving an artifact", inFlight.Timeout);
        return await inFlight.Response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.ArtifactCreateResponse, inFlight.Timeout)
            ?? throw new ArtifactUploadRejectedException(
                "the server returned an empty artifact response.",
                (int)inFlight.Response.StatusCode);
    }

    /// <summary>
    /// Streams one file of a pending artifact as the raw request body (card F-016). The declared relative path
    /// and SHA-256 travel as query parameters; the body is the file's bytes, streamed from disk - never
    /// buffered, never multipart. The server re-validates the path and recomputes the hash.
    /// </summary>
    public async Task<ArtifactFileEntry> UploadArtifactFileAsync(
        string workerToken,
        Guid runId,
        Guid artifactId,
        string relativePath,
        string sha256,
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var path = $"api/workers/runs/{runId}/artifacts/{artifactId}/files"
            + $"?path={Uri.EscapeDataString(relativePath)}&sha256={Uri.EscapeDataString(sha256)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StreamContent(file, 64 * 1024),
        };
        request.Content.Headers.ContentLength = file.Length;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);

        // An upload is allowed a much longer ceiling than a JSON report: the per-request 10s limit is about
        // a hung control call, not about moving a 512 MB build product. The run's own cancellation token
        // still bounds it (a cancel or the deadline kills the upload mid-flight).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ArtifactUploadTimeout);
        using var response = await httpClient.SendAsync(request, timeout.Token);
        await EnsureArtifactSuccessAsync(response, "uploading an artifact file", timeout.Token);
        return await response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.ArtifactFileEntry, timeout.Token)
            ?? throw new ArtifactUploadRejectedException(
                "the server returned an empty artifact file response.",
                (int)response.StatusCode);
    }

    /// <summary>
    /// Publishes the artifact: the server verifies the announced set against what it stores, writes the
    /// manifest and flips the row to <c>Ready</c> (card F-016). A refusal means the artifact is not
    /// downloadable - the caller fails the step.
    /// </summary>
    public async Task<ArtifactFinalizeResponse> FinalizeArtifactAsync(
        string workerToken,
        Guid runId,
        Guid artifactId,
        ArtifactFileEntry[] files,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/artifacts/{artifactId}/finalize",
                workerToken,
                new ArtifactFinalizeRequest(files),
                WorkerJsonContext.Default.ArtifactFinalizeRequest),
            cancellationToken);
        await EnsureArtifactSuccessAsync(inFlight.Response, "finalizing an artifact", inFlight.Timeout);
        return await inFlight.Response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.ArtifactFinalizeResponse, inFlight.Timeout)
            ?? throw new ArtifactUploadRejectedException(
                "the server returned an empty artifact finalize response.",
                (int)inFlight.Response.StatusCode);
    }

    /// <summary>
    /// The artifact calls' status handling: every non-success answer carries the server's own message (which
    /// names a path or a cap), so the step's log line is the server's explanation rather than a code.
    /// </summary>
    private static async Task EnsureArtifactSuccessAsync(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = $"the server answered {(int)response.StatusCode} while {what}.";
        try
        {
            var body = await response.Content.ReadFromJsonAsync(
                WorkerJsonContext.Default.WorkerArtifactErrorResponse,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(body?.Message))
            {
                message = body.Message;
            }
        }
        catch (JsonException)
        {
            // A non-JSON body (a proxy error page) leaves the generic message in place.
        }
        catch (NotSupportedException)
        {
        }

        throw new ArtifactUploadRejectedException(message, (int)response.StatusCode);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage CreateRequest<T>(
        HttpMethod method,
        string path,
        string token,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload, typeInfo),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Sends one request under the linked shutdown + per-request-ceiling token. The returned
    /// scope owns the response and keeps the ceiling armed through the caller's status handling
    /// and body read (D, second review fix): a server that sends headers but stalls the body also
    /// hits the declared per-request limit, and the shutdown token still cancels at any point.
    /// </summary>
    private async Task<InFlightResponse> SendAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        try
        {
            return new InFlightResponse(await httpClient.SendAsync(createRequest(), timeoutCts.Token), timeoutCts);
        }
        catch
        {
            timeoutCts.Dispose();
            throw;
        }
    }

    /// <summary>The response of one request plus the token that bounds its whole lifetime.</summary>
    private sealed class InFlightResponse(HttpResponseMessage response, CancellationTokenSource timeout) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        /// <summary>Shutdown and per-request ceiling, linked; bounds status handling and body reads.</summary>
        public CancellationToken Timeout { get; } = timeout.Token;

        private readonly CancellationTokenSource _timeout = timeout;

        public void Dispose()
        {
            _timeout.Dispose();
            Response.Dispose();
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken,
        bool isRunReport = false)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var statusCode = (int)response.StatusCode;

        // 401/403 are definitive answers (B1 review fix): the credential is dead, so retrying
        // cannot help. Other non-transient 4xx are definitive too (the request as built will
        // never be accepted); transient 4xx fall through to the retryable branch below. The
        // runner surfaces any WorkerApiRejectedException as a one-line rejection; 401/403
        // additionally keep their "credential is dead" semantics.
        if (statusCode is 401 or 403)
        {
            // Definitive: the token is unknown, burned or revoked. Retrying with the same
            // credential cannot succeed.
            throw new WorkerApiRejectedException(
                $"the server rejected the worker token with {statusCode} while {what} "
                + "(unknown, used or revoked).",
                statusCode);
        }

        // A (second review fix): 408/409/425/429 are transient answers for the polling calls. A 409 on
        // register is exactly what the loser of a same-name first-start race receives (WorkerEndpoints
        // maps name-duplicate to 409); the winner's row is already committed, so the loser's retry
        // succeeds.
        //
        // Run reporting is the one place the classification changes, and it changes narrowly: a
        // 400/404/409/410 answers "this report will never be accepted for this run" (the payload is
        // wrong, the run is gone, another worker owns it, or it is already terminal), so the run is
        // given up. Everything else in the 4xx range stays transient even there - 408 (request
        // timeout), 425 (too early) and 429 (throttling) are answers a retry can fix, and treating a
        // 429 as definitive would starve a concurrency-1 worker over a rate limit (F-014 review m14:
        // the earlier flag widened the set to *every* 4xx, contradicting IsTransientStatus below).
        if (statusCode is >= 400 and < 500
            && (isRunReport ? IsDefinitiveRunReportStatus(statusCode) : !IsTransientStatus(statusCode)))
        {
            throw new WorkerApiRejectedException(
                $"the server rejected the request with {statusCode} while {what} "
                + "(payload or contract mismatch).",
                statusCode);
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"the server answered {statusCode} while {what}.",
            null,
            response.StatusCode)
        {
            // The body is server copy (e.g. a validation error code), keep it out of the message
            // chain but available for a future structured log; today the one-line log is enough.
            Data = { ["detail"] = detail.Length > 200 ? detail[..200] : detail },
        };
    }

    /// <summary>The 4xx answers whose retry can succeed (request timeout, race loser, throttling).</summary>
    private static bool IsTransientStatus(int statusCode) => statusCode is 408 or 409 or 425 or 429;

    /// <summary>
    /// The 4xx answers that end a run-reporting call for good: the payload is invalid (400), the run
    /// does not exist for this tenant (404), it is not this worker's any more or already terminal
    /// (409/410). Every other 4xx - 408, 425, 429 in particular - is retried with backoff, exactly like
    /// the same status on the polling calls.
    /// </summary>
    private static bool IsDefinitiveRunReportStatus(int statusCode) => statusCode is 400 or 404 or 409 or 410;
}

/// <summary>A refused artifact call (card F-016): the server's own message, so the step's log line names the
/// path or the cap the server refused. Definitive by definition - the replayed request would be refused
/// again - so the publisher fails the step instead of retrying.</summary>
public sealed class ArtifactUploadRejectedException(string message, int statusCode) : Exception(message)
{
    /// <summary>The HTTP status the server answered.</summary>
    public int StatusCode { get; } = statusCode;
}

/// <summary>The artifact endpoint's refusal body (see the server's ArtifactEndpoints).</summary>
public sealed record WorkerArtifactErrorResponse(string Error, string Message);

/// <summary>Source-generated JSON (trim-safe; the Worker must stay small and AOT-friendly).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkerRegistrationRequest))]
[JsonSerializable(typeof(WorkerHeartbeatRequest))]
[JsonSerializable(typeof(WorkerRegistrationResponse))]
[JsonSerializable(typeof(WorkerHeartbeatResponse))]
[JsonSerializable(typeof(WorkerClaimResponse))]
[JsonSerializable(typeof(WorkerAcceptRequest))]
[JsonSerializable(typeof(WorkerAcceptResponse))]
[JsonSerializable(typeof(WorkerDefinitionRequest))]
[JsonSerializable(typeof(WorkerDefinitionResponse))]
[JsonSerializable(typeof(WorkerCompleteRequest))]
[JsonSerializable(typeof(WorkerCanceledRequest))]
[JsonSerializable(typeof(WorkerStepRequest))]
[JsonSerializable(typeof(WorkerStepResponse))]
[JsonSerializable(typeof(WorkerSecretRequest))]
[JsonSerializable(typeof(WorkerSecretResponse))]
[JsonSerializable(typeof(WorkerParameterFileRequest))]
// I-005: the Git remote probe's claim and report (source-generated, like every other wire type).
[JsonSerializable(typeof(WorkerProbeClaimResponse))]
[JsonSerializable(typeof(GitProbeRef))]
[JsonSerializable(typeof(GitProbeRef[]))]
[JsonSerializable(typeof(WorkerProbeReportRequest))]
[JsonSerializable(typeof(WorkerProbeReportResponse))]
// F-022: the definition-only validation's claim and report.
[JsonSerializable(typeof(WorkerDefinitionValidationClaimResponse))]
[JsonSerializable(typeof(WorkerDefinitionValidationReportRequest))]
[JsonSerializable(typeof(WorkerDefinitionValidationReportResponse))]
[JsonSerializable(typeof(ArtifactCreateRequest))]
[JsonSerializable(typeof(ArtifactCreateResponse))]
[JsonSerializable(typeof(ArtifactFileEntry))]
[JsonSerializable(typeof(ArtifactFinalizeRequest))]
[JsonSerializable(typeof(ArtifactFinalizeResponse))]
[JsonSerializable(typeof(WorkerArtifactErrorResponse))]
[JsonSerializable(typeof(WorkerCancelRequest))]
// I-004: the SignalR log-stream payload (F-008). The JSON hub protocol serializes hub-method
// arguments through PayloadSerializerOptions; under NativeAOT the reflection resolver is
// unavailable (and the package suppresses the resulting IL2026/IL3050 internally), so
// LogStreamClient points that options instance at this context. The wire shape is unchanged:
// the protocol's default settings are mirrored (camelCase, case-insensitive reads) and the
// context's JsonSourceGenerationOptions names LogLineMessage's properties camelCase as well.
[JsonSerializable(typeof(Zenkins.Shared.LogStreaming.LogLineMessage))]
[JsonSerializable(typeof(System.Collections.Generic.List<Zenkins.Shared.LogStreaming.LogLineMessage>))]
[JsonSerializable(typeof(Zenkins.Shared.LogStreaming.LogLineMessage[]))]
[JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<Zenkins.Shared.LogStreaming.LogLineMessage>))]
// The SignalR hub-protocol primitives (AppendLinesAsync's runId, cancelRun's (runId, reason)
// arguments): registered explicitly so the TC-002 missing-registration assertion covers the
// whole payload surface, not only the composite types.
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(string))]
internal sealed partial class WorkerJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
