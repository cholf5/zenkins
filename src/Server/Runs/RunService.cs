using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using Zenkins.Server.Auth;
using Zenkins.Server.Branches;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Notifications;
using Zenkins.Server.Environments;
using Zenkins.Server.Git;
using Zenkins.Server.Localization;
using Zenkins.Server.LogStreaming;
using Zenkins.Server.Projects;
using Zenkins.Server.Workers;
using System.Text.Json;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Projects;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.RunCancellation;
using Zenkins.Shared.Tenancy;
using Zenkins.Shared.Workers;

namespace Zenkins.Server.Runs;

/// <summary>Outcome of a run operation. The HTTP layer maps each value to one status code.</summary>
public enum RunOperationStatus
{
    Success = 0,

    /// <summary>The presented worker token is missing, unknown or revoked. Always a bare 401.</summary>
    InvalidToken = 1,

    /// <summary>The project or run id does not exist in the active tenant (also the foreign case).</summary>
    NotFound = 2,

    /// <summary>The caller is not a member of the active organization.</summary>
    Forbidden = 3,

    /// <summary>The payload failed validation; nothing was written.</summary>
    InvalidInput = 4,

    /// <summary>An illegal state transition, a lease that is not the caller's, or an expired lease.</summary>
    Conflict = 5,

    /// <summary>
    /// A trigger the project itself refused (cards F-028 and F-029): the project is disabled, or its
    /// queue is at its cap. Nothing was written.
    /// <para>
    /// <b>Why this is not <see cref="InvalidInput"/>.</b> The caller's input may be perfectly valid -
    /// the project's own state is what refuses it - and the distinction is what lets an API answer
    /// <c>409</c> (a conflict with the resource's state) instead of <c>400</c> (a malformed request),
    /// while the form maps the same answer to a form-level message. The specific cause travels as the
    /// field-error code (<c>project-disabled</c> / <c>queue-full</c>).
    /// </para>
    /// </summary>
    Refused = 6,
}

/// <summary>One field-level validation failure of the trigger form.</summary>
public sealed record RunFieldError(string Field, string Code);

/// <summary>Field names the trigger form's errors render against.</summary>
public static class RunFields
{
    /// <summary>F-014: the branch the run builds (blank means the project's default branch).</summary>
    public const string Branch = "branch";
    public const string RequiredTags = "requiredTags";
    public const string Timeout = "timeoutMinutes";

    /// <summary>F-020: the Freestyle step list (its own trigger error field).</summary>
    public const string Steps = "steps";

    /// <summary>F-021: one build parameter value, keyed by the parameter's name.</summary>
    public const string Parameter = "parameters";

    /// <summary>
    /// Cards F-028 + F-029: a refusal of the project itself (disabled, or queue at its cap). It is a
    /// form-level field because no input is at fault; the code inside it
    /// (<see cref="ProjectTriggerRefusals"/>) says which refusal it was.
    /// </summary>
    public const string Queue = "queue";

    /// <summary>
    /// Card B-008: the <c>Idempotency-Key</c> header. The key itself is optional, but when it is
    /// presented it names one request - a second, different request under it is refused against this
    /// field rather than replayed.
    /// </summary>
    public const string IdempotencyKey = "idempotency-key";

    /// <summary>
    /// Card F-037: a refusal of the re-run itself (the original did not finish, recorded no commit, its
    /// definition is unreadable, its project's kind changed, or a repository pipeline cannot be replayed).
    /// Form-level for the same reason <see cref="Queue"/> is: the person clicked one button on a finished
    /// run, and no input of theirs is wrong.
    /// </summary>
    public const string Rebuild = "rebuild";
}

/// <summary>The raw trigger-form values, before trimming and validation. <see cref="Parameters"/>
/// carries the trigger's build-parameter values (F-021); absent entries resolve to the definitions'
/// defaults, unknown names are refused.</summary>
public sealed record RunTriggerInput(
    string? Branch,
    string? RequiredTags,
    string? TimeoutMinutes = null,
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    /// <summary>
    /// Card F-041: the <c>file</c> parameters' uploaded bytes, keyed by parameter name. Absent (or a name
    /// missing from it) means "this trigger uploaded no file for that parameter", which resolves the parameter
    /// to the empty value - the honest answer for a schedule or a poll.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]>? Files { get; init; }
}

/// <summary>Result of triggering a run. <see cref="RunId"/> is the redirect target on success.</summary>
/// <param name="Replayed">
/// Card B-008: <see langword="true"/> when nothing was queued because the caller's
/// <c>Idempotency-Key</c> already names this request - <see cref="RunId"/> is then the run that key
/// queued earlier (a retried post, answered without a second run).
/// </param>
public sealed record RunTriggerResult(
    RunOperationStatus Status,
    Guid? RunId = null,
    string? Message = null,
    IReadOnlyList<RunFieldError>? FieldErrors = null,
    bool Replayed = false)
{
    public static RunTriggerResult Success(Guid runId) => new(RunOperationStatus.Success, runId);

    /// <summary>
    /// Card B-008: the answer to a retried trigger - the run the key already queued. Nothing was written
    /// by this call.
    /// </summary>
    public static RunTriggerResult ReplayedRun(Guid runId) =>
        new(RunOperationStatus.Success, runId, Replayed: true);

    public static RunTriggerResult NotFound() => new(RunOperationStatus.NotFound);

    public static RunTriggerResult Forbidden(string message) =>
        new(RunOperationStatus.Forbidden, Message: message);

    public static RunTriggerResult Invalid(params RunFieldError[] errors) =>
        new(RunOperationStatus.InvalidInput, FieldErrors: errors);

    /// <summary>
    /// The project refused the trigger (cards F-028 and F-029): nothing was written. The code is the
    /// refusal's stable spelling (<see cref="ProjectTriggerRefusals"/>), carried as a field error so
    /// every surface - the form's <c>?runError=</c>, a webhook delivery's outcome, a future API's 409
    /// body - reads the same value.
    /// </summary>
    public static RunTriggerResult Refused(string code, string message) =>
        new(
            RunOperationStatus.Refused,
            Message: message,
            FieldErrors: [new RunFieldError(RunFields.Queue, code)]);

    /// <summary>
    /// Card F-037: the re-run refused itself - the original's own state says this button cannot work.
    /// Nothing was written. The field is <see cref="RunFields.Rebuild"/> (no input of the caller is at
    /// fault), and the code is one of the <c>rebuild-*</c>/<c>replay-*</c> spellings on
    /// <see cref="RunErrorCodes"/> so the run page and any future API read the same value.
    /// </summary>
    public static RunTriggerResult RebuildRefused(string code, string message) =>
        new(
            RunOperationStatus.Refused,
            Message: message,
            FieldErrors: [new RunFieldError(RunFields.Rebuild, code)]);

    /// <summary>
    /// Card B-008: the caller's <c>Idempotency-Key</c> already names a <b>different</b> request (another
    /// project, or another payload). Nothing was written, and nothing is replayed: answering the first
    /// request's run would tell the caller a build exists that was never asked for.
    /// </summary>
    public static RunTriggerResult IdempotencyKeyReused(string message) =>
        new(
            RunOperationStatus.Refused,
            Message: message,
            FieldErrors: [new RunFieldError(RunFields.IdempotencyKey, RunErrorCodes.IdempotencyKeyReused)]);
}

/// <summary>A run as the pages display it. Records keep disposed contexts out of the UI.</summary>
/// <param name="PipelinePath">The pipeline file the run looked for (snapshot at trigger time).</param>
/// <param name="GitBranch">The branch the run built (snapshot at trigger time).</param>
/// <param name="GitCommit">The commit the worker resolved, once it reported one.</param>
/// <param name="Pipeline">The frozen spec, when the definition was reported; null before that.</param>
/// <param name="GitCredentialAssigned">
/// Whether the run's project had a Git credential assigned when the page was read (card F-018).
/// <see langword="null"/> when the project could not be read (a foreign or deleted row), so the page
/// says "unknown" rather than claiming a credential was missing. The value is a configuration fact -
/// never material - and it is what lets the page explain a <c>git-auth-rejected</c> failure instead of
/// showing the bare code.
/// </param>
public sealed record RunView(
    Guid Id,
    Guid ProjectId,
    string Status,
    string TriggerType,
    string? TriggerUserEmail,
    string? WorkerName,
    string PipelinePath,
    string GitBranch,
    string? GitCommit,
    PipelineSpec? Pipeline,
    string[] RequiredTags,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? TimeoutSeconds = null,
    DateTimeOffset? DeadlineAt = null,
    IReadOnlyList<RunStepView>? Steps = null,
    bool? GitCredentialAssigned = null,
    IReadOnlyDictionary<string, string>? Parameters = null,

    /// <summary>
    /// Card F-034: the organization environment layer this run was queued with (its own snapshot, not
    /// the organization's current variables) - shown with values on the run page, because it is
    /// configuration rather than a secret. <see langword="null"/> when the organization had none at
    /// trigger time.
    /// </summary>
    IReadOnlyDictionary<string, string>? Environment = null,

    /// <summary>
    /// Cards F-028: whether the run's project is disabled <em>now</em> (read when the page was read), so a
    /// run page and the dashboard can say "the project was disabled while this run was in flight" instead
    /// of leaving the operator to guess why nothing new is queuing. <see langword="null"/> when the
    /// project could not be read (a foreign or deleted row).
    /// </summary>
    bool? ProjectDisabled = null,

    /// <summary>
    /// Card F-036: this run's build number inside its project - the <c>#N</c> every human-facing surface
    /// shows and the value the permalink route carries. <see langword="null"/> only for a row no writer
    /// ever numbered (a database that predates the migration, read before it ran).
    /// </summary>
    int? Number = null,

    /// <summary>Card F-037: the run this one rebuilt or replayed, or <see langword="null"/>.</summary>
    Guid? RebuiltFromRunId = null,

    /// <summary>
    /// Card F-037: the original run's <see cref="Number"/>, resolved at read time so the page can print
    /// "rebuilt from #N". <see langword="null"/> when this is not a re-run, or when the original is gone
    /// (the page then falls back to <see cref="RebuiltFromRunId"/>'s short id instead of inventing one).
    /// </summary>
    int? RebuiltFromNumber = null,

    /// <summary>
    /// F-022: whether this run's definition came from a script snapshotted onto the run (an inline
    /// Pipeline) rather than from a repository. A re-run of such a run carries the same script, so the
    /// definition phase runs again instead of falling back to a frozen-spec-only Freestyle path.
    /// </summary>
    bool InlineDefinition = false,
    string? RequiredRequirementText = null,
    IReadOnlyList<string>? MissingWorkerLabels = null,
    bool WaitingForWorker = false,

    /// <summary>
    /// Card F-041: which parameter is of which new kind, read from the <b>run's own</b> record rather than from the
    /// project's current definitions - a definition retyped after the trigger must not change how history reads.
    /// A <c>password</c> entry is already masked in <see cref="Parameters"/>; this is what tells the page it is a
    /// mask, a <c>run</c> entry what it links to, and a <c>gitRef</c> entry that it is a ref. Empty for a run
    /// triggered before the column existed, where every parameter is a plain string.
    /// </summary>
    IReadOnlyDictionary<string, RunParameterDetail>? ParameterDetails = null,

    /// <summary>
    /// Card F-026: the upstream run that caused this one, when a chain edge triggered it. The id is what the page
    /// links to; <see cref="TriggeredByRunNumber"/> is the <c>#N</c> it prints. Both null for every other trigger.
    /// </summary>
    Guid? TriggeredByRunId = null,

    /// <summary>The upstream run's number, or null when it has none (a row from before F-036, or a deleted run).</summary>
    int? TriggeredByRunNumber = null,

    /// <summary>
    /// Card F-026: the downstream triggers this run owed, each with the outcome the dispatcher recorded - queued
    /// with its run, or skipped/failed with the reason. Empty for a run that declares no downstreams, so the page
    /// renders the chain section only when there is a chain to show.
    /// </summary>
    IReadOnlyList<RunChainLink>? DownstreamTriggers = null,

    /// <summary>
    /// Card F-026: the chain facts this run's steps read (a downstream run only), from the run's own frozen
    /// record. Rendered in the chain section as their own list - never among the build parameters.
    /// </summary>
    IReadOnlyDictionary<string, string>? UpstreamFacts = null,

    /// <summary>
    /// Card F-038: "keep this build forever" - exempt from both retention rules. Rendered as the run page's pin
    /// toggle, so the state the sweep reads is the state the page shows.
    /// </summary>
    bool Pinned = false)
{
    /// <summary>The step names in declaration order - what the run page lists (F-014).</summary>
    public string[] StepNames => Pipeline is { } spec ? [.. spec.Steps.Select(step => step.Name)] : [];

    /// <summary>The pipeline's declared name, or null while no definition was reported.</summary>
    public string? PipelineName => Pipeline?.Name;

    /// <summary>
    /// Card F-030: the run's steps grouped into stage sections, in declaration order, with each section's
    /// worst state, span and step count. A spec frozen before schema v3 (or a spec with no stage names)
    /// yields one unnamed section per contiguous run of unstaged steps, which the page renders as the flat
    /// list it has always been - no empty headers, no invented stages.
    /// </summary>
    public IReadOnlyList<RunStageView> StageGroups => RunStages.Group(MainSteps);

    /// <summary>
    /// Card F-031: the main list's steps (declared order), without the post phase's rows. Every v1/v2/v3 run
    /// written before the field existed has <see cref="StepList"/> == <see cref="MainSteps"/>.
    /// </summary>
    public IReadOnlyList<RunStepView> MainSteps => [.. StepList.Where(step => !step.IsPost)];

    /// <summary>
    /// Card F-031: the recorded post steps, in the order the run executed them. Only steps that the run actually
    /// reached appear - predicting which outcome set would run would be a claim about an unfinished run. The
    /// list is empty for a run whose spec declares no post step.
    /// </summary>
    public IReadOnlyList<RunStepView> PostSteps => [.. StepList.Where(step => step.IsPost)];

    /// <summary>
    /// Card F-031: the sentence the post section shows when a post step failed. Two honest shapes: the run
    /// failed because the project asks post failures to count (<c>post-failed</c>), or the run's result is
    /// unchanged and the page says exactly that. <see langword="null"/> when no post step failed.
    /// </summary>
    public string? PostFailureNote
    {
        get
        {
            var failed = PostSteps.Where(step => step.Status == PipelineStepStatus.Failed).ToArray();
            if (failed.Length == 0)
            {
                return null;
            }

            if (FailureReason == PipelineFailureReasons.PostFailed)
            {
                return "The run failed because a post step failed (the project is set to fail the run on a "
                    + "post step failure).";
            }

            return "A post step failed. The run's result is unchanged: a post step does not fail the run unless "
                + "the project enables that. The step's own log has the detail.";
        }
    }

    /// <summary>
    /// Card F-030: the first failed step, in declaration order - the one a failed run's page jumps to. Read
    /// through <see cref="StepList"/> so it is the same list every other surface shows.
    /// </summary>
    public RunStepView? FirstFailedStep =>
        StepList.FirstOrDefault(step => step.Status == PipelineStepStatus.Failed);

    /// <summary>
    /// The run's steps in declaration order (card F-015): each declared step merged with the row the
    /// worker recorded for it, or <see cref="PipelineStepStatus.Pending"/> when it has not been
    /// reached yet. A run page opened mid-run therefore shows completed, current and pending steps.
    /// </summary>
    public IReadOnlyList<RunStepView> StepList => Steps ?? [];

    /// <summary>Card F-037: whether this run has finished (only a finished run is a re-run input).</summary>
    public bool IsTerminal => RunReruns.IsTerminal(Status);

    /// <summary>
    /// Card F-037: whether the run page offers Rebuild for this run. It asks <see cref="RunReruns.Refusal"/> -
    /// the function the service refuses with - so the button exists exactly when the run's own facts allow the
    /// request. Two service-side checks are deliberately outside it (they read today's project rows, not the
    /// run's snapshot): a deleted Git credential or a renamed parameter can still turn the click into a
    /// refusal, and the page's redirect then explains it.
    /// </summary>
    public bool CanRebuild =>
        RunReruns.CanRebuild(
            Status,
            GitBranch,
            GitCommit,
            Pipeline,
            !InlineDefinition && RunReruns.IsFreestyleShape(GitBranch),
            InlineDefinition);

    /// <summary>Card F-037: whether the run page offers Replay for this run (a run whose snapshot <em>is</em>
    /// its definition - a Freestyle or inline run); a repository pipeline's spec cannot be replayed as source.</summary>
    public bool CanReplay =>
        RunReruns.CanReplay(
            Status,
            GitBranch,
            Pipeline,
            !InlineDefinition && RunReruns.IsFreestyleShape(GitBranch),
            InlineDefinition);
}

/// <summary>
/// Card F-037: whether a finished run can be rebuilt or replayed, and - when it cannot - which refusal the
/// caller reads. <b>One function answers both questions</b>: the run page renders its buttons from
/// <see cref="Refusal"/>(<see langword="null"/>) and <see cref="RunService"/> refuses the request with the very
/// same value, so a button that is offered is a button that works.
/// <para>
/// <b>Rebuild</b> needs the same inputs, and its one indispensable input is the commit a Pipeline run pinned
/// (a rebuild that took the branch head would rebuild different code under the same name). <b>Replay</b>
/// needs a definition <em>text</em>: a Freestyle run's snapshot is the definition itself, while a repository
/// pipeline's run stores the spec its worker produced - re-executing that as a definition would erase the
/// definition phase, which is why replay is deliberately absent there.
/// </para>
/// <para>
/// <b>What is deliberately NOT here.</b> The service adds two checks this function cannot make, because they
/// read today's project rows rather than the run's snapshot: the project's assigned Git credential must still
/// resolve, and today's parameter definitions must still accept the run's values. So the page can show a
/// button that the service then refuses - with a complete sentence, never a silent failure - when a
/// credential was deleted or a parameter renamed between the render and the click. That is the honest
/// direction of the drift: the button cannot exist without the run's own facts agreeing.
/// </para>
/// </summary>
public static class RunReruns
{
    /// <summary>Whether the run has finished - the only state a re-run may start from.</summary>
    public static bool IsTerminal(string status) =>
        status is not (BuildRunStatus.Queued or BuildRunStatus.Running);

    /// <summary>
    /// Whether the run executed a Freestyle definition (no repository, no branch, no commit) rather than a
    /// repository pipeline. The frozen spec is not enough to tell: a Pipeline run stores one too, once its
    /// worker reported it. The empty branch is the honest discriminator - F-020 writes it for exactly this
    /// kind - and it is a snapshot, so a later project edit cannot move it.
    /// </summary>
    public static bool IsFreestyleShape(string gitBranch) => string.IsNullOrEmpty(gitBranch);

    /// <summary>Whether a rebuild of this run is possible (and would be accepted).</summary>
    public static bool CanRebuild(
        string status,
        string gitBranch,
        string? gitCommit,
        PipelineSpec? pipeline,
        bool projectIsFreestyle,
        bool isInline = false) =>
        Refusal(BuildRunTriggerType.Rebuild, status, gitBranch, gitCommit, pipeline, projectIsFreestyle, isInline) is null;

    /// <summary>Whether a replay of this run is possible (and would be accepted).</summary>
    public static bool CanReplay(
        string status,
        string gitBranch,
        PipelineSpec? pipeline,
        bool projectIsFreestyle,
        bool isInline = false) =>
        Refusal(
            BuildRunTriggerType.Replay,
            status,
            gitBranch,
            gitCommit: null,
            pipeline,
            projectIsFreestyle,
            isInline) is null;

    /// <summary>
    /// The reason <paramref name="triggerType"/> cannot re-run this run, or <see langword="null"/> when it can.
    /// The order is the order a person would ask the questions: has it finished, does the project still run what
    /// this run ran, is the intent possible at all, and does the run carry what the intent needs.
    /// </summary>
    /// <param name="triggerType"><see cref="BuildRunTriggerType.Rebuild"/> or <see cref="BuildRunTriggerType.Replay"/>.</param>
    /// <param name="projectIsFreestyle">
    /// The project's <em>current</em> kind. The run page passes the run's own shape (it has no project row), so
    /// the kind check there only fires for a run whose snapshot is already inconsistent with itself; the service
    /// passes the project's kind and therefore also catches an out-of-band mismatch.
    /// </param>
    public static RunRerunRefusal? Refusal(
        string triggerType,
        string status,
        string gitBranch,
        string? gitCommit,
        PipelineSpec? pipeline,
        bool projectIsFreestyle,
        bool isInline = false)
    {
        var isReplay = triggerType == BuildRunTriggerType.Replay;
        var wasFreestyle = !isInline && IsFreestyleShape(gitBranch);

        if (!IsTerminal(status))
        {
            // Only a finished run is a re-run input: re-running a queued or executing one would fork work in
            // flight, and "the same inputs" would be a guess about a run that has not decided them yet.
            return new RunRerunRefusal(
                RunErrorCodes.RebuildNotTerminal,
                "This run has not finished yet, so there is nothing to rebuild or replay. Wait for its outcome.");
        }

        if (wasFreestyle != projectIsFreestyle)
        {
            // The project's kind is immutable in the product, so a mismatch means the row was written out of
            // band (or by an older release): refuse rather than execute a shape the project cannot run.
            return new RunRerunRefusal(
                RunErrorCodes.RebuildKindChanged,
                "The project's kind no longer matches the run's: this run cannot be rebuilt as the project is now.");
        }

        if (isReplay && (!wasFreestyle && !isInline || pipeline is null && !isInline))
        {
            return new RunRerunRefusal(
                RunErrorCodes.ReplayUnavailable,
                "This run stores the pipeline spec its worker produced, not the source that produced it, so it "
                + "cannot be replayed as a definition. Use Rebuild instead.");
        }

        if (!isReplay && !wasFreestyle && !isInline && string.IsNullOrWhiteSpace(gitCommit))
        {
            // No commit recorded means the original never got far enough to name what it built; rebuilding
            // would silently take the branch head instead of "the same inputs", so it is refused.
            return new RunRerunRefusal(
                RunErrorCodes.RebuildNoCommit,
                "This run recorded no commit, so a rebuild could not pin one and would build whatever the branch "
                + "points at now. Start a fresh run instead.");
        }

        if (!isReplay && wasFreestyle && pipeline is null)
        {
            return new RunRerunRefusal(
                RunErrorCodes.RebuildSpecUnavailable,
                "The definition this run executed is not readable any more, so it cannot be rebuilt.");
        }

        return null;
    }
}

/// <summary>
/// One downstream trigger an upstream run owed (card F-026), as the run page renders it: which project, what became
/// of it, and why when nothing ran. <see cref="Outcome"/> is a <see cref="DownstreamOutcomes"/> value, so the page
/// can render every case instead of printing a free-text status.
/// </summary>
/// <param name="DownstreamProjectName">The project the link names.</param>
/// <param name="DownstreamRunId">The queued run, when the outcome is <c>queued</c>.</param>
/// <param name="DownstreamRunNumber">Its number, for the <c>#N</c> a person reads.</param>
/// <param name="Outcome">A <see cref="DownstreamOutcomes"/> value.</param>
/// <param name="Reason">The dispatcher's one-line explanation, null for a queued run.</param>
public sealed record RunChainLink(
    string DownstreamProjectName,
    Guid? DownstreamRunId,
    int? DownstreamRunNumber,
    string Outcome,
    string? Reason);

/// <summary>One reason a finished run cannot be re-run (code for the URL, sentence for the page).</summary>
public sealed record RunRerunRefusal(string Code, string Message);

/// <summary>
/// One step of a run as the page displays it (card F-015). <see cref="Id"/> is the recorded row's id
/// (also what <see cref="Zenkins.Shared.LogStreaming.LogLineMessage.StepId"/> carries), null for a
/// declared step that has not been reported yet; <see cref="Index"/> is its 1-based position in the
/// frozen spec.
/// </summary>
public sealed record RunStepView(
    Guid? Id,
    int Index,
    string Name,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string? FailureReason,
    int Attempts = 1,

    /// <summary>
    /// Card F-030: the stage name the frozen spec recorded on this step, or <see langword="null"/> when the
    /// step declares none (every v1/v2 run, and an unstaged step of a v3 one). It is read from the spec only -
    /// never derived from <see cref="Name"/> - so a rename cannot rewrite an old run's sections.
    /// </summary>
    string? Stage = null,

    /// <summary>
    /// Card F-031: whether this step belongs to the post phase (<c>always</c>/<c>success</c>/<c>failure</c>)
    /// rather than the main list. Read from the frozen spec's post lists by name - never from the <c>post/</c>
    /// prefix alone - so the page can render the post section apart and the stage grouping can ignore it.
    /// </summary>
    bool IsPost = false,

    /// <summary>
    /// Card F-031: which post outcome set declared this step (<c>always</c>, <c>success</c>, <c>failure</c>),
    /// or <see langword="null"/> for a main step. The page uses it as the row's context; the outcome set that
    /// actually ran is the run's own history, not a promise this field makes.
    /// </summary>
    string? PostOutcome = null,

    /// <summary>
    /// The argv the step ran (or will run), read from the frozen spec - the page's command line. Empty for a
    /// script step, whose text lives in the spec instead.
    /// </summary>
    string[]? Argv = null)
{
    /// <summary>Whether the step is declared but not reached (no row recorded).</summary>
    public bool IsPending => Status == PipelineStepStatus.Pending;

    /// <summary>The step's duration, or null while it has no recorded end.</summary>
    public TimeSpan? Duration => StartedAt is { } start && EndedAt is { } end ? end - start : null;
}

/// <summary>The failure reasons the server itself records (the worker's exit code needs none).</summary>
public static class RunFailureReasons
{
    /// <summary>F-010: the worker stopped heartbeating while the run was still running.</summary>
    public const string WorkerOffline = "worker-offline";

    /// <summary>F-009 (PM ruling): the run passed its deadline plus the grace period without any
    /// terminal report from the worker. Honest wording: the result is unknown - the server never
    /// fabricates a Failed for a run it did not see end.</summary>
    public const string RunDeadlineExceededResultUnknown = "run-deadline-exceeded-result-unknown";
}

/// <summary>What a user cancel did. <see cref="RunCancelOutcome.AlreadyFinished"/> is the friendly
/// no-op: a terminal run cannot be canceled again, and the page says so instead of erroring.</summary>
public enum RunCancelOutcome
{
    /// <summary>The run was queued and is now <c>Canceled</c> (immediate, atomic).</summary>
    Canceled = 0,

    /// <summary>The run was running; the worker has been asked to cancel it.</summary>
    CancelRequested = 1,

    /// <summary>The run had already reached a terminal state; nothing was changed.</summary>
    AlreadyFinished = 2,
}

/// <summary>Outcome of the user-facing cancel action (the run page's cancel button).</summary>
public sealed record RunCancelResult(RunOperationStatus Status, RunCancelOutcome Outcome, string? Message = null)
{
    public static RunCancelResult Canceled() => new(RunOperationStatus.Success, RunCancelOutcome.Canceled);

    public static RunCancelResult CancelRequested() => new(RunOperationStatus.Success, RunCancelOutcome.CancelRequested);

    public static RunCancelResult AlreadyFinished() => new(RunOperationStatus.Success, RunCancelOutcome.AlreadyFinished);

    public static RunCancelResult Forbidden(string message) =>
        new(RunOperationStatus.Forbidden, RunCancelOutcome.AlreadyFinished, message);

    public static RunCancelResult NotFound() => new(RunOperationStatus.NotFound, RunCancelOutcome.AlreadyFinished);
}

/// <summary>
/// A worker's runs as the worker detail page shows them (card D-005). <see cref="Current"/> is the
/// worker's <c>Running</c> runs - the same predicate the roster's per-worker count aggregates;
/// <see cref="Recent"/> is everything else the worker has queued or finished, newest first, bounded.
/// </summary>
public sealed record WorkerRunsView(
    IReadOnlyList<RunView> Current,
    IReadOnlyList<RunView> Recent);

/// <summary>A queued run handed to a worker by the claim endpoint.</summary>
public sealed record RunClaimResult(
    RunOperationStatus Status,
    Guid? RunId = null,
    Guid? ProjectId = null,
    string? RepositoryUrl = null,
    string? Branch = null,

    // F-017 (review M1): the exact commit this run must build, when the trigger already named it. Null for
    // a manual run (the worker resolves the branch tip and reports it back) and for a webhook run whose
    // payload carried no usable commit (never happens today - the parser refuses that payload).
    string? Commit = null,
    string? PipelinePath = null,
    DateTimeOffset? LeaseExpiresAt = null,

    // F-020: the Freestyle run's frozen spec, snapshotted at trigger time. Null for a Pipeline run -
    // its spec comes from the Worker's definition phase, never from the claim.
    string? PipelineSpecJson = null,

    // F-022: the inline Pipeline run's script, snapshotted at trigger time. Null for a repository or
    // Freestyle run; a non-null value with no repository is what makes the Worker write the script to
    // temp/pipeline.lua and run the definition phase instead of cloning.
    string? PipelineScriptText = null,

    // F-022: the run's definition shape word (repository | freestyle | inline), written from the run's
    // own snapshot so the Worker never guesses from "is there a spec?" - an inline run can carry both.
    string? DefinitionSource = null,

    // F-021 + F-034: the run's server-resolved environment values - the organization layer with the
    // parameter values on top of it, already merged in the documented order. One map crosses the
    // boundary (see RunEnvironmentSnapshot.Merge); the worker injects it without comparing layers.
    IReadOnlyDictionary<string, string>? Parameters = null,

    /// <summary>
    /// Card F-036: the run's per-project build number (the <c>#N</c> every page prints). The worker turns
    /// it into <c>ZENKINS_BUILD_NUMBER</c>, so a step can name its own build without parsing the guid.
    /// <para>
    /// <b>Null, never 0.</b> A row that carries no positive number (a database read before the F-036
    /// migrations ran, or an out-of-band row the card's CHECK constraint refuses) answers
    /// <see langword="null"/>, so the worker leaves the variable unset instead of publishing a <c>0</c> that
    /// reads like a real build.
    /// </para>
    /// </summary>
    int? Number = null,

    /// <summary>
    /// Card F-041: the names of the <c>password</c>-typed parameters in <see cref="Parameters"/>. The worker
    /// registers their values with the run's redaction filter, so a step that echoes one logs the mask instead.
    /// Names only - a name is not a secret, and the values travel in the map the worker already receives.
    /// </summary>
    IReadOnlyList<string>? PasswordParameters = null,

    /// <summary>
    /// Card F-041: the names of the <c>file</c>-typed parameters. The worker fetches each one's bytes over the
    /// run-scoped route and materialises them into the run's own directory before any step starts. Names only.
    /// </summary>
    IReadOnlyList<string>? FileParameters = null,

    /// <summary>
    /// Card F-031: whether this run's project asks a failed post step to fail the run. False is the product
    /// default (the main list decides the result); the worker reads it from the claim, because the executor
    /// - not the page - is what turns it into the run's reported outcome.
    /// </summary>
    bool PostFailureFailsRun = false)
{
    /// <summary>The 204 answer: nothing eligible right now. Same for every other rejection reason -
    /// a worker polls again, it never learns why it got nothing.</summary>
    public static RunClaimResult NoRun() => new(RunOperationStatus.Success);
}

/// <summary>Outcome of the accepted/completed/definition reporting endpoints.</summary>
/// <param name="DeadlineAt">F-014: the run's frozen deadline, answered by the accept edge so the
/// worker's definition phase runs against the run's own wall clock.</param>
public sealed record RunReportResult(
    RunOperationStatus Status,
    string? ErrorCode = null,
    string? RunStatus = null,
    DateTimeOffset? DeadlineAt = null)
{
    public static RunReportResult Success(string runStatus, DateTimeOffset? deadlineAt = null) =>
        new(RunOperationStatus.Success, RunStatus: runStatus, DeadlineAt: deadlineAt);

    public static RunReportResult InvalidToken() => new(RunOperationStatus.InvalidToken);

    public static RunReportResult Invalid(string errorCode) => new(RunOperationStatus.InvalidInput, errorCode);

    public static RunReportResult NotFound() => new(RunOperationStatus.NotFound);

    public static RunReportResult Conflict(string errorCode) => new(RunOperationStatus.Conflict, errorCode);
}

/// <summary>
/// One step transition a worker reports (card F-015): the step's 1-based position in the frozen spec,
/// its name (checked against that spec), the state it reached and - when nothing exited - the closed
/// reason. Timestamps are the server's: the run's own <c>StartedAt</c>/<c>EndedAt</c> follow the same
/// discipline, so a worker clock skew cannot make a step appear to end before it started.
/// </summary>
public sealed record RunStepReport(
    int Index,
    string? Name,
    string? Status,
    int? ExitCode = null,
    string? FailureReason = null,
    int? Attempts = null);

/// <summary>The answer of a step report: the recorded row's id (which the worker stamps on the step's
/// log lines), its state and the run's state.</summary>
public sealed record RunStepResult(
    RunOperationStatus Status,
    Guid? StepId = null,
    string? ErrorCode = null,
    string? RunStatus = null,
    string? StepStatus = null)
{
    public static RunStepResult Success(Guid stepId, string stepStatus, string runStatus) =>
        new(RunOperationStatus.Success, stepId, RunStatus: runStatus, StepStatus: stepStatus);

    public static RunStepResult InvalidToken() => new(RunOperationStatus.InvalidToken);

    public static RunStepResult Invalid(string errorCode) => new(RunOperationStatus.InvalidInput, ErrorCode: errorCode);

    public static RunStepResult NotFound() => new(RunOperationStatus.NotFound);

    public static RunStepResult Conflict(string errorCode) => new(RunOperationStatus.Conflict, ErrorCode: errorCode);
}

/// <summary>
/// Build runs, queue and scheduler (card F-006, PRD §3.5).
/// <para>
/// <b>Tenant boundary.</b> Page/trigger methods read the organization from
/// <see cref="ITenantContext"/> only; run lookups go through the tenant-filtered context, so a
/// foreign id is indistinguishable from a missing one (404, never 403). Worker-API methods
/// (claim/accepted/completed) authenticate with a worker token through
/// <see cref="WorkerService.AuthenticateWorkerAsync"/> - authentication before any payload
/// validation, the token row selects the tenant, every auth failure is a bare 401.
/// </para>
/// <para>
/// <b>Atomic claim (the pull model).</b> A claim never assigns: it leases. Candidates are the
/// tenant's <c>Queued</c> runs whose lease is free or expired, FIFO by creation; the first one
/// whose single-statement compare-and-set wins (<see cref="ZenkinsDbContext.TryLeaseBuildRunAsync"/>,
/// the guarded <c>ExecuteUpdate</c>) is returned. The UPDATE's WHERE restates the eligibility, so
/// two concurrent claims can never both win the same run under either provider's write locking.
/// The claim's whole unit of work - the worker-row lock, the concurrency-slot count and the
/// compare-and-set - runs in one transaction (B-002): the lock serializes a worker's own racing
/// claims, so the slot check cannot be passed twice before either lease commits.
/// </para>
/// <para>
/// <b>State transitions.</b> Only <see cref="BuildRunStateMachine"/> (through this service) changes
/// <see cref="BuildRun.Status"/>: <c>accepted</c> is the single <c>Queued → Running</c> edge,
/// <c>completed</c> maps exit 0 to <c>Success</c> and anything else to <c>Failed</c>. Illegal
/// transitions are <see cref="RunOperationStatus.Conflict"/> (409). The claim itself is not a
/// transition - a leased run stays <c>Queued</c> until accepted, which is what makes the lease
/// sweeper safe: it requeues only <c>Queued</c> runs with an expired lease, so a run that was
/// accepted (no lease anymore) is unreachable for it.
/// </para>
/// </summary>
public sealed class RunService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    TenantContext tenantContext,
    ISystemDataScope systemDataScope,
    WorkerService workerService,
    RunLogIngest runLogIngest,
    RunOptions options,
    WorkerMonitorOptions workerMonitorOptions,
    IHubContext<WorkerLogHub> workerLogHubs,
    RunParameterProtector parameterProtector,
    RunTerminalSignal terminalSignal,
    Localizer L10n,
    ILogger<RunService> logger)
{
    /// <summary>
    /// Upper bound on the candidates one claim request walks. Each candidate costs one CAS
    /// statement; with every lease contended, a worker re-polls instead of spinning here.
    /// </summary>
    private const int MaxClaimCandidates = 64;

    /// <summary>The most current runs the worker detail page lists (card D-005). The roster's count
    /// is uncapped - it counts, it does not render.</summary>
    private const int MaxCurrentRunsShown = 20;

    /// <summary>The most recent runs the worker detail page lists (card D-005).</summary>
    private const int MaxRecentRunsShown = 10;

    // ---------------------------------------------------------------------
    // Trigger and reads (cookie-authenticated pages and forms)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates a <c>Queued</c> run for a project of the active organization. Any member may trigger
    /// (PRD §3.1.2). F-014 replaced the argv command with a branch: blank means the project's default
    /// branch, and the run snapshots the branch and the pipeline path it will use so a later project
    /// edit cannot move it. Required tags must exist among the organization's workers, so a typo cannot
    /// queue a run no worker will ever take.
    /// </summary>
    public async Task<RunTriggerResult> TriggerAsync(
        string actingUserId,
        Guid projectId,
        RunTriggerInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return RunTriggerResult.Forbidden(
                "You are not a member of an organization, so there is no project to run.");
        }

        if (!await IsOrganizationMemberAsync(actingUserId, cancellationToken))
        {
            return RunTriggerResult.Forbidden("You are not a member of the active organization.");
        }

        return await CreateQueuedRunAsync(
            activeOrganizationId.Value,
            projectId,
            input,
            BuildRunTriggerType.Manual,
            actingUserId,
            idempotency: null,
            cancellationToken);
    }

    /// <summary>
    /// The organization API's trigger (card F-027): <b>the same routine the form runs</b> - the same
    /// project lookup through the tenant filter, the same F-028/F-029 gate, the same branch/tag/timeout/
    /// parameter validation and error codes, the same staging under the project row lock. The only
    /// differences are the ones an API necessarily has: the credential (a token row, which already
    /// selected the tenant) instead of a membership check, and no acting user, so the run is attributed
    /// to <see cref="BuildRunTriggerType.Api"/>.
    /// <para>
    /// <b>Why not a second trigger path.</b> A copy of the validation would let the API and the form drift
    /// (a rule fixed in one place would not hold in the other); this method exists only to hand the shared
    /// routine a tenant and a trigger type.
    /// </para>
    /// </summary>
    /// <param name="idempotency">
    /// Cards F-027 + B-008: the token and <c>Idempotency-Key</c> this post carries, or
    /// <see langword="null"/> when it carries none. The key's decision is made <b>here</b>, after the
    /// project lookup and before the project's gates, so a replay can never answer with another
    /// project's run and an unknown project id stays a bare 404.
    /// </param>
    public async Task<RunTriggerResult> TriggerForApiAsync(
        Guid projectId,
        RunTriggerInput input,
        RunTriggerIdempotency? idempotency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (tenantContext.OrganizationId is not { } organizationId)
        {
            // Unreachable through the endpoint (authentication sets the tenant); kept as a guard so a
            // future caller can never trigger inside an unspecified tenant.
            return RunTriggerResult.Forbidden(
                "No organization is selected for this request, so there is no project to run.");
        }

        return await CreateQueuedRunAsync(
            organizationId,
            projectId,
            input,
            BuildRunTriggerType.Api,
            actingUserId: null,
            idempotency,
            cancellationToken);
    }

    /// <summary>
    /// The one trigger implementation (cards F-003/F-014/F-020/F-021/F-028/F-029/F-027): every caller - the
    /// form, the API, and a future source - arrives here after it has proved it may act for
    /// <paramref name="organizationId"/>.
    /// </summary>
    private async Task<RunTriggerResult> CreateQueuedRunAsync(
        Guid organizationId,
        Guid projectId,
        RunTriggerInput input,
        string triggerType,
        string? actingUserId,
        RunTriggerIdempotency? idempotency,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle - and it stays the answer even
            // when the caller presents an Idempotency-Key (card B-008): the key's lookup runs below,
            // after this read, so a remembered key can never turn a 404 into a 200.
            return RunTriggerResult.NotFound();
        }

        // Cards F-027 + B-008: the idempotency decision, and it is taken here - after the project
        // exists, before the project's own gates and before field validation.
        //
        //   * Precedence: a replay wins over F-028's disabled gate and F-029's queue cap. A replay is
        //     the idempotency semantic itself ("this request already happened"), not a new trigger, so
        //     a retried deploy answers the run it queued even if the project has been disabled since -
        //     the alternative is a caller that loses the answer to a request the server did accept.
        //     Written down in docs/api-v1.md and on both cards instead of left to be discovered.
        //   * Scope: the record is looked up by (token, key) and then compared against this project and
        //     this payload's hash; a mismatch is a refusal and writes nothing (the audit's finding).
        PendingIdempotency? pending = null;
        if (idempotency is not null)
        {
            var fingerprint = RunTriggerFingerprint.Of(input);
            var remembered = await FindIdempotencyRecordAsync(context, idempotency, cancellationToken);
            if (remembered is not null)
            {
                if (remembered.ProjectId != projectId
                    || !string.Equals(remembered.RequestHash, fingerprint, StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "Trigger refused: the Idempotency-Key already names a different request. OrganizationId={OrganizationId} TokenId={TokenId} ProjectId={ProjectId} RememberedProjectId={RememberedProjectId}",
                        organizationId,
                        idempotency.ApiTokenId,
                        projectId,
                        remembered.ProjectId);
                    return RunTriggerResult.IdempotencyKeyReused(IdempotencyKeyReusedMessage);
                }

                return RunTriggerResult.ReplayedRun(remembered.RunId);
            }

            pending = new PendingIdempotency(
                idempotency.ApiTokenId,
                idempotency.Key,
                projectId,
                fingerprint,
                DateTimeOffset.UtcNow);
        }

        // F-028: a disabled project refuses the trigger here - before its own field validation - because
        // the reason is the actionable answer and a form that will be refused must not spend the caller's
        // attention on a branch name (the project page does not even render the form). The staging routine
        // repeats the check under the project row lock, which is what makes it a decision rather than a
        // check-then-act: this early read only decides *which* refusal the caller reads first.
        if (ProjectTriggerGuard.DisabledRefusal(project.Disabled) is { } disabledCode)
        {
            logger.LogInformation(
                "Trigger refused: the project is disabled. OrganizationId={OrganizationId} ProjectId={ProjectId} Refusal={Refusal} TriggerType={TriggerType} ByUserId={UserId}",
                organizationId,
                project.Id,
                disabledCode,
                triggerType,
                actingUserId);
            return RunTriggerResult.Refused(
                disabledCode,
                ProjectTriggerGuard.DisabledMessage(project.DisabledReason, L10n));
        }

        // F-020: a Freestyle project runs its frozen spec and has no branch to name. The branch input
        // is ignored on this path (the form does not render it), and the run must already have a spec -
        // a Freestyle project whose steps were never saved cannot run anything.
        var isFreestyle = project.Kind == ProjectKinds.Freestyle;
        if (isFreestyle && project.FreestyleSpecJson is null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Steps, RunErrorCodes.StepsMissing));
        }

        // F-022: an inline Pipeline has no repository and no branch - the run snapshots the script at
        // trigger time. The script must exist (the create/edit paths already require it; this is the
        // same defence the Freestyle spec gets, so a row written out of band cannot queue nothing).
        var isInline = !isFreestyle
            && project.Kind == ProjectKinds.Pipeline
            && project.PipelineSource == ProjectSources.Inline;
        if (isInline && string.IsNullOrWhiteSpace(project.InlineScript))
        {
            return RunTriggerResult.Invalid(
                new RunFieldError(ProjectFields.InlineScript, ProjectErrorCodes.InlineScriptRequired));
        }

        var requestedBranch = string.Empty;
        if (!isFreestyle && !isInline)
        {
            var branchError = RunValidation.ValidateBranch(input.Branch, out requestedBranch);
            if (branchError is not null)
            {
                return RunTriggerResult.Invalid(new RunFieldError(RunFields.Branch, branchError));
            }
        }

        // F-022: an inline run's branch is always the honest empty value - even for an out-of-band row
        // that carries a DefaultBranch. This is the manual path's half of the schedule path's rule
        // (B-006's "decide by kind/source, not by whatever the row holds").
        var branchForRun = isInline
            ? string.Empty
            : requestedBranch.Length > 0 ? requestedBranch : project.DefaultBranch;

        var tagsError = RunValidation.ValidateRequiredTags(input.RequiredTags, out var requiredTags);
        if (tagsError is not null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.RequiredTags, tagsError));
        }

        var timeoutError = RunValidation.ValidateTimeoutMinutes(input.TimeoutMinutes, out var timeoutMinutes);
        if (timeoutError is not null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Timeout, timeoutError));
        }

        if (requiredTags.Length > 0)
        {
            // Card F-033: only a requirement that can *never* hold is refused here (`linux,!linux`). A
            // requirement nobody satisfies right now is queued: a Worker that registers later is ordinary
            // operation, and refusing it would turn every poll/webhook/schedule trigger into a silent drop.
            // The run records what it waits on, the page says which label is missing, and the claim loop
            // asks the same predicate - no "tag exists somewhere" check remains (Q-010's asymmetry).
            var requirement = WorkerRequirement.FromTags(requiredTags);
            if (requirement.IsContradiction)
            {
                return RunTriggerResult.Invalid(
                    new RunFieldError(RunFields.RequiredTags, RunErrorCodes.TagInvalid));
            }
        }

        // F-021 + F-041: resolve the trigger's parameter values against the project's definitions - every
        // defined name gets exactly one value (supplied, or default, or the type's neutral default), a
        // password is split into the masked value map and the sealed value, and a run selection is checked
        // against this project's own runs. The three documents below are what the run and every step see.
        var parameters = await ResolveRunParametersAsync(
            context,
            project,
            input.Parameters,
            inheritedProtected: null,
            uploadedFiles: input.Files,
            cancellationToken);
        if (parameters.Error is { } parametersError)
        {
            return RunTriggerResult.Invalid(parametersError);
        }

        var run = BuildQueuedRun(
            organizationId,
            project,
            branchForRun,
            requiredTags,
            timeoutMinutes is { } minutes ? minutes * 60 : null,
            triggerType,
            actingUserId,
            gitCommit: null,
            // F-020: a Freestyle run freezes the project's spec at trigger time - the same snapshot
            // discipline the branch and pipeline path follow (a later edit cannot move this run).
            pipelineSpecJson: isFreestyle ? project.FreestyleSpecJson : null,
            parametersJson: parameters.ParametersJson,
            protectedParametersJson: parameters.ProtectedParametersJson,
            parameterDetailsJson: parameters.ParameterDetailsJson,
            parameterFilesJson: parameters.ParameterFilesJson,

            // F-022: an inline run freezes the script the same way. The branch and commit stay empty
            // (there is no repository), while the pipeline path is pipeline.lua - the file the script
            // becomes - which is the run's own fact, not the project row's null.
            pipelineScriptText: isInline ? project.InlineScript : null);

        // F-028 + F-029: the project's own gate (disabled / queue cap) is applied inside the staging
        // routine, under the project row lock, in the same transaction as the insert - never as a
        // separate pre-flight check here. Cards F-027 + B-008: the idempotency record rides in that same
        // transaction, so "one key, one run" cannot be broken by a crash between two units of work.
        return await QueueStagedRunAsync(context, run, triggerType, actingUserId, pending, cancellationToken);
    }

    /// <summary>
    /// Stages one built run through the project's gate and commits it - <b>the one insert path</b> for
    /// every trigger source there has ever been: the manual form, the API (F-027), a webhook delivery
    /// (F-017, which stages on its own context), and the rebuild/replay re-runs of F-037. The gate, the
    /// build number (F-036) and the transaction all live in
    /// <see cref="StageQueuedRunAsync"/>; this method only opens the unit of work and logs the queueing,
    /// so no caller can invent a second way to insert a run.
    /// </summary>
    /// <param name="context">The caller's context. The transaction is this method's own entirely: a
    /// refusal changes nothing and a failed commit leaves no run behind.</param>
    /// <param name="idempotency">
    /// Card B-008: the resolved <c>Idempotency-Key</c> binding to write in the same transaction, or
    /// <see langword="null"/> for a trigger that carries no key. It also decides what a unique-index
    /// refusal means: a concurrent post won the key, this transaction rolls back (so its run is never
    /// queued) and the winner's run is the answer.
    /// </param>
    private async Task<RunTriggerResult> QueueStagedRunAsync(
        ZenkinsDbContext context,
        BuildRun run,
        string triggerType,
        string? actingUserId,
        PendingIdempotency? idempotency,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        RunTriggerResult staged;
        try
        {
            staged = await StageQueuedRunAsync(context, run, idempotency, cancellationToken);
        }
        catch (DbUpdateException failure)
            when (idempotency is not null && DatabaseErrors.IsUniqueViolation(failure, "ApiIdempotencyRecords"))
        {
            // Two posts carried the same new key: the other one's record committed first. Rolling back
            // the whole transaction is what makes "one key, one run" true even under the race - this
            // call's run is discarded, and the key's answer (the winner's run) is returned instead.
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation(
                "Idempotency-Key claimed by a concurrent post; answering its run. OrganizationId={OrganizationId} TokenId={TokenId} ProjectId={ProjectId}",
                run.OrganizationId,
                idempotency.ApiTokenId,
                run.ProjectId);

            return await ResolveRacedIdempotencyAsync(context, idempotency, cancellationToken)
                ?? Rethrow(failure);
        }

        if (staged.Status != RunOperationStatus.Success)
        {
            return staged;
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Run queued. OrganizationId={OrganizationId} ProjectId={ProjectId} RunId={RunId} Number={Number} Branch={Branch} PipelinePath={PipelinePath} TriggerType={TriggerType} RebuiltFrom={RebuiltFrom} ByUserId={UserId}",
            run.OrganizationId,
            run.ProjectId,
            run.Id,
            run.Number,
            run.GitBranch,
            run.PipelinePath,
            triggerType,
            run.RebuiltFromRunId,
            actingUserId);
        return staged;
    }

    /// <summary>Whether the account is a member of the request's active organization (the trigger right).</summary>
    private async Task<bool> IsOrganizationMemberAsync(string userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.OrganizationMembers.AnyAsync(
            member => member.UserId == userId,
            cancellationToken);
    }

    /// <summary>
    /// Cards F-028 + F-029 + F-036 + B-008: the one place a trigger's run is staged, used by <b>every</b>
    /// trigger source (the manual form, the API, the webhook, and F-037's rebuild/replay). The caller owns
    /// the transaction; this method owns the decision and the run's build number.
    /// <para>
    /// <b>Why a project row lock.</b> The gate's facts are the project's own state, and two writers change
    /// them concurrently with a trigger: the disable mutation and every other trigger of the same
    /// project. Taking the project row's write lock first
    /// (<see cref="ZenkinsDbContext.LockProjectForTriggerAsync"/>) makes the reads and the insert one
    /// step, the same shape B-002 gave the worker's slot check:
    /// </para>
    /// <list type="bullet">
    /// <item>a trigger that read <c>Disabled</c> before a disable committed cannot insert afterwards -
    /// it waits for the lock, then reads the flag the disable wrote;</item>
    /// <item>two concurrent triggers cannot both pass the queue-cap count, because only the lock holder
    /// raises the count (an insert is the only statement that can);</item>
    /// <item>two concurrent triggers cannot both read the same <c>MAX(Number)</c>, because the number is
    /// assigned inside the same lock - and, one layer below, the unique <c>(ProjectId, Number)</c> index
    /// refuses a duplicate even if a future caller ever stages outside it (card F-036).</item>
    /// </list>
    /// <para>
    /// Both refusals answer <see cref="RunOperationStatus.Refused"/>: nothing is written, so a webhook can
    /// still record its delivery and a form can still name the reason.
    /// </para>
    /// </summary>
    private async Task<RunTriggerResult> StageQueuedRunAsync(
        ZenkinsDbContext context,
        BuildRun staged,
        PendingIdempotency? idempotency,
        CancellationToken cancellationToken)
    {
        if (await context.LockProjectForTriggerAsync(staged.ProjectId, cancellationToken) == 0)
        {
            // The project is gone (deleted between the trigger's read and the lock): nothing to queue.
            return RunTriggerResult.NotFound();
        }

        var project = await context.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == staged.ProjectId, cancellationToken);
        if (project is null)
        {
            return RunTriggerResult.NotFound();
        }

        // F-028: a disabled project queues nothing, from any source, and says why.
        if (ProjectTriggerGuard.DisabledRefusal(project.Disabled) is { } disabledCode)
        {
            logger.LogInformation(
                "Trigger refused: the project is disabled. OrganizationId={OrganizationId} ProjectId={ProjectId} Refusal={Refusal}",
                project.OrganizationId,
                project.Id,
                disabledCode);
            return RunTriggerResult.Refused(disabledCode, ProjectTriggerGuard.DisabledMessage(project.DisabledReason, L10n));
        }

        // F-029: the queue cap. Counted under the same lock, so "below the cap" stays true until this
        // trigger's insert commits - two racing triggers cannot both squeeze in.
        var queued = await context.BuildRuns.CountAsync(
            run => run.ProjectId == staged.ProjectId && run.Status == BuildRunStatus.Queued,
            cancellationToken);
        if (ProjectTriggerGuard.IsQueueFull(project.MaxQueuedRuns, queued))
        {
            var cap = project.MaxQueuedRuns!.Value;
            logger.LogInformation(
                "Trigger refused: the project's queue is full. OrganizationId={OrganizationId} ProjectId={ProjectId} Queued={Queued} Cap={Cap}",
                project.OrganizationId,
                project.Id,
                queued,
                cap);
            return RunTriggerResult.Refused(
                ProjectTriggerRefusals.QueueFull,
                ProjectTriggerGuard.QueueFullMessage(queued, cap, L10n));
        }

        // F-034: the organization environment layer is resolved *here*, in the one insert path every
        // trigger source funnels through (manual form, API, webhook, schedule, rebuild), and frozen onto
        // the row. Editing or deleting a variable afterwards cannot reach this run any more: the row is
        // the snapshot, exactly like the branch, the spec and the parameter values beside it.
        //
        // The read is keyed by *this run's* organization and taken inside the system scope, because a
        // webhook delivery and a schedule tick have no request tenant: leaning on the request's active
        // organization would hand those runs an empty layer - silently, and for exactly the runs nobody
        // is watching. An organization with no variables leaves the column null ("no layer").
        staged.EnvironmentJson = RunEnvironmentSnapshot.Serialize(
            await RunEnvironmentSnapshot.ReadForOrganizationAsync(
                context,
                systemDataScope,
                staged.OrganizationId,
                cancellationToken));

        context.BuildRuns.Add(staged);
        await SaveStagedRunAsync(context, staged, cancellationToken);
        if (idempotency is not null)
        {
            // Card B-008: the binding is written inside the same transaction (and through a second
            // SaveChanges, so the run row - and its build number - exist before the foreign key to it).
            context.ApiIdempotencyRecords.Add(new ApiIdempotencyRecord
            {
                OrganizationId = staged.OrganizationId,
                ApiTokenId = idempotency.ApiTokenId,
                Key = idempotency.Key,
                ProjectId = idempotency.ProjectId,
                RequestHash = idempotency.RequestHash,
                RunId = staged.Id,
                CreatedAt = idempotency.CreatedAt,
                ExpiresAt = idempotency.ExpiresAt,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        return RunTriggerResult.Success(staged.Id);
    }

    /// <summary>The name of the savepoint the numbering retry rolls back to.</summary>
    private const string BuildNumberSavepoint = "build-number";

    /// <summary>Card B-008: the one sentence a reused key answers with (refusal, nothing written).</summary>
    private const string IdempotencyKeyReusedMessage =
        "This Idempotency-Key was already used for a different request (another project, or a different "
        + "branch, tag set, timeout or parameter value). Send a new key, or repeat the original request "
        + "exactly. Nothing was queued.";

    /// <summary>
    /// Card B-008: the binding a trigger writes in the same transaction as its run. Resolved before the
    /// gates (the key's decision is taken there); written only when the trigger actually queues a run.
    /// </summary>
    /// <param name="RequestHash">The payload hash (<see cref="RunTriggerFingerprint"/>).</param>
    /// <param name="CreatedAt">When the request arrived; <see cref="ExpiresAt"/> is this plus the retention.</param>
    private sealed record PendingIdempotency(
        Guid ApiTokenId,
        string Key,
        Guid ProjectId,
        string RequestHash,
        DateTimeOffset CreatedAt)
    {
        public DateTimeOffset ExpiresAt => CreatedAt + ApiIdempotencyRecord.Retention;
    }

    /// <summary>
    /// Card B-008: the live binding of one key, or <see langword="null"/> when the key is new <b>or its
    /// binding has outlived <see cref="ApiIdempotencyRecord.Retention"/>.</b> An expired binding is
    /// deleted here (one statement, before the trigger's transaction) so the key may name a fresh request
    /// without a second sweeper - the retention is enforced where it is read, not only by a background job.
    /// </summary>
    private async Task<ApiIdempotencyRecord?> FindIdempotencyRecordAsync(
        ZenkinsDbContext context,
        RunTriggerIdempotency scope,
        CancellationToken cancellationToken)
    {
        var record = await context.ApiIdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.ApiTokenId == scope.ApiTokenId && candidate.Key == scope.Key,
                cancellationToken);
        if (record is null)
        {
            return null;
        }

        if (record.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return record;
        }

        await context.ApiIdempotencyRecords
            .Where(candidate => candidate.Id == record.Id)
            .ExecuteDeleteAsync(cancellationToken);
        logger.LogInformation(
            "Idempotency-Key expired and was forgotten. OrganizationId={OrganizationId} TokenId={TokenId} ProjectId={ProjectId} ExpiredAt={ExpiredAt}",
            record.OrganizationId,
            record.ApiTokenId,
            record.ProjectId,
            record.ExpiresAt);
        return null;
    }

    /// <summary>
    /// Card B-008: what a concurrent post's winning record means for this one - the winner's run when the
    /// two requests agree, a refusal when they do not, and <see langword="null"/> when the record is gone
    /// (an expiry racing the insert, which is a real error for the caller, not a replay).
    /// </summary>
    private async Task<RunTriggerResult?> ResolveRacedIdempotencyAsync(
        ZenkinsDbContext context,
        PendingIdempotency idempotency,
        CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        var winner = await context.ApiIdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.ApiTokenId == idempotency.ApiTokenId
                    && candidate.Key == idempotency.Key,
                cancellationToken);
        if (winner is null || winner.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return winner.ProjectId == idempotency.ProjectId
            && string.Equals(winner.RequestHash, idempotency.RequestHash, StringComparison.Ordinal)
                ? RunTriggerResult.ReplayedRun(winner.RunId)
                : RunTriggerResult.IdempotencyKeyReused(IdempotencyKeyReusedMessage);
    }

    /// <summary>Re-raises a caught exception from a helper's null answer (the caller's catch block cannot).</summary>
    private static RunTriggerResult Rethrow(DbUpdateException failure) =>
        throw new InvalidOperationException(
            "The idempotency record vanished between the failed insert and the read-back.",
            failure);

    /// <summary>
    /// Card F-036: assigns <paramref name="staged"/> its build number and inserts it, retrying once if the
    /// unique <c>(ProjectId, Number)</c> index refuses the number.
    /// <para>
    /// <b>The lock is the guarantee; the retry is the belt.</b> The caller holds the project row lock, so
    /// two triggers of one project cannot read the same <c>MAX(Number)</c> - the retry only ever fires when
    /// a writer outside this routine (an uncommitted insert from another connection, an out-of-band row)
    /// took the number this statement chose. A savepoint makes the retry possible at all: on PostgreSQL a
    /// failed statement poisons the whole transaction, so "try again" has to restore the transaction to the
    /// point before the insert rather than simply issue a second one.
    /// </para>
    /// <para>
    /// A second collision is not retried: it is a real error and surfaces as one. Nothing partial is left
    /// behind either way - the caller's transaction decides.
    /// </para>
    /// </summary>
    private async Task SaveStagedRunAsync(
        ZenkinsDbContext context,
        BuildRun staged,
        CancellationToken cancellationToken)
    {
        staged.Number = await NextBuildNumberAsync(context, staged.ProjectId, cancellationToken);

        var transaction = context.Database.CurrentTransaction;
        if (transaction is null)
        {
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        await transaction.CreateSavepointAsync(BuildNumberSavepoint, cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "BuildRuns"))
        {
            logger.LogWarning(
                "Build number taken between the read and the insert; retrying once. OrganizationId={OrganizationId} ProjectId={ProjectId} Number={Number}",
                staged.OrganizationId,
                staged.ProjectId,
                staged.Number);

            await transaction.RollbackToSavepointAsync(BuildNumberSavepoint, cancellationToken);

            // Only this run is detached: the caller's other tracked rows (a webhook's delivery record,
            // for instance) belong to the same unit of work and must survive the retry.
            context.Entry(staged).State = EntityState.Detached;

            staged.Number = await NextBuildNumberAsync(context, staged.ProjectId, cancellationToken);
            context.BuildRuns.Add(staged);
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.ReleaseSavepointAsync(BuildNumberSavepoint, cancellationToken);
    }

    /// <summary>
    /// The next build number of one project: <c>MAX(Number) + 1</c>, read inside the caller's project row
    /// lock. Numbers are never reused or renumbered - the maximum is taken over every run of the project,
    /// whatever its state - so a cancelled history still leaves the sequence monotonic, and the permalink
    /// space never recycles an address.
    /// </summary>
    private static async Task<int> NextBuildNumberAsync(
        ZenkinsDbContext context,
        Guid projectId,
        CancellationToken cancellationToken) =>
        (await context.BuildRuns
            .Where(run => run.ProjectId == projectId)
            .MaxAsync(run => (int?)run.Number, cancellationToken) ?? 0) + 1;

    // ---------------------------------------------------------------------
    // Rebuild and replay (card F-037)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Card F-037's <b>Rebuild</b>: re-runs a finished run with the same inputs - the branch, the commit
    /// that run pinned, its parameters, required tags and timeout, and the definition it executed - and
    /// attributes the new run to <see cref="BuildRunTriggerType.Rebuild"/> with
    /// <see cref="BuildRun.RebuiltFromRunId"/> pointing at the original.
    /// <para>
    /// <b>The same queue, not a second one.</b> The new run is built from the original's snapshots and then
    /// handed to <see cref="QueueStagedRunAsync"/> - the one insert path - so F-028 (a disabled project),
    /// F-029 (the queue cap) and F-036 (the build number) apply to a rebuild exactly as they apply to a
    /// fresh trigger, and the project row lock decides both facts once. Nothing writes to the original run:
    /// a rebuild is a new row, so its artifacts, logs and steps stay exactly as they were.
    /// </para>
    /// <para>
    /// <b>What is re-validated, and why.</b> <see cref="RunReruns.Refusal"/> answers the run's own questions
    /// (finished, same kind, the intent is possible, the pinned commit or the definition exists) with the
    /// very codes the run page's buttons were rendered from. On top of that the service checks what only
    /// today's project rows can answer: the project's assigned Git credential must still resolve
    /// (<c>rebuild-credential-missing</c>), and today's parameter definitions must still accept the run's
    /// values (the form's own <c>parameter-unknown</c>/<c>parameter-invalid</c> codes).
    /// </para>
    /// </summary>
    /// <param name="parameterOverrides">
    /// The "Rebuild with parameters…" form's values, or <see langword="null"/> for "exactly as it ran".
    /// Overrides are resolved against the original's values and today's definitions by the same
    /// <see cref="ProjectParameters.ResolveValues"/> the trigger form uses, so their validation cannot drift
    /// from a fresh trigger's.
    /// </param>
    public async Task<RunTriggerResult> RebuildAsync(
        string actingUserId,
        Guid runId,
        IReadOnlyDictionary<string, string>? parameterOverrides = null,
        CancellationToken cancellationToken = default) =>
        await RecreateRunAsync(
            actingUserId,
            runId,
            BuildRunTriggerType.Rebuild,
            parameterOverrides,
            cancellationToken);

    /// <summary>
    /// Card F-037's <b>Replay</b>: re-runs a finished run against <b>the definition text the run itself
    /// carried</b>, ignoring whatever the project or the repository says now.
    /// <para>
    /// <b>Only where a definition text exists.</b> A repository pipeline's run stores the frozen spec the
    /// worker produced, not the source that produced it - re-executing that spec as a definition would
    /// erase the definition phase and pretend the two are the same thing. So Replay is offered (and
    /// accepted) only for a run whose snapshot <em>is</em> its definition - today a Freestyle run - and a
    /// repository pipeline's replay is refused with <c>replay-unavailable</c>, which is what the run page's
    /// disabled button explains.
    /// </para>
    /// </summary>
    public async Task<RunTriggerResult> ReplayAsync(
        string actingUserId,
        Guid runId,
        CancellationToken cancellationToken = default) =>
        await RecreateRunAsync(
            actingUserId,
            runId,
            BuildRunTriggerType.Replay,
            parameterOverrides: null,
            cancellationToken);

    /// <summary>
    /// The one re-run implementation behind Rebuild and Replay: the original's snapshot, re-validated
    /// against today's project, staged through the one insert path. The two intents differ in exactly two
    /// places - which definition the new run gets (rebuild: the project's current definition for a Pipeline
    /// run, read from the pinned commit; replay: the run's own stored text, whatever the project says) and
    /// the attribution they write.
    /// </summary>
    private async Task<RunTriggerResult> RecreateRunAsync(
        string actingUserId,
        Guid runId,
        string triggerType,
        IReadOnlyDictionary<string, string>? parameterOverrides,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is not { } organizationId)
        {
            return RunTriggerResult.Forbidden(
                "You are not a member of an organization, so there is no run to re-execute.");
        }

        if (!await IsOrganizationMemberAsync(actingUserId, cancellationToken))
        {
            return RunTriggerResult.Forbidden("You are not a member of the active organization.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Foreign or unknown id: the same 404, no existence oracle.
        var original = await context.BuildRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);
        if (original is null)
        {
            return RunTriggerResult.NotFound();
        }

        var project = await context.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == original.ProjectId, cancellationToken);
        if (project is null)
        {
            return RunTriggerResult.NotFound();
        }

        var originalIsInline = original.PipelineScriptText is not null;
        var originalWasFreestyle = !originalIsInline && RunReruns.IsFreestyleShape(original.GitBranch);
        var originalPipeline = ReadPipeline(original.PipelineSpecJson);

        // One predicate, shared with the run page's buttons: `Refusal` answers "can this intent re-run this
        // run, and if not, why". The project's *current* kind is passed in (the page only has the run's own
        // shape), so the kind mismatch an out-of-band row can produce is caught here as well.
        if (RunReruns.Refusal(
                triggerType,
                original.Status,
                original.GitBranch,
                original.GitCommit,
                originalPipeline,
                project.Kind == ProjectKinds.Freestyle,
                originalIsInline) is { } refusal)
        {
            return RunTriggerResult.RebuildRefused(refusal.Code, refusal.Message);
        }

        // The credential is not an input of the run (the worker fetches the project's current one), but an
        // assignment that no longer resolves would queue a clone that cannot authenticate: say so now.
        if (project.GitCredentialId is { } credentialId
            && !await context.GitCredentials.AnyAsync(
                credential => credential.Id == credentialId,
                cancellationToken))
        {
            return RunTriggerResult.RebuildRefused(
                RunErrorCodes.RebuildCredentialMissing,
                "The Git credential assigned to this project no longer exists. Assign one on the project's settings tab, then rebuild.");
        }

        // F-021: the original's values (or the form's overrides on top of them) are re-resolved against
        // today's definitions by the trigger's own routine - so a renamed, removed or retyped parameter is
        // refused with the same code a fresh trigger would give, and a newly declared one takes its default.
        // Card F-041: a password's value in the original's snapshot is the <b>mask</b>, so it must not be carried
        // forward as if it were a typed value. Only a submitted override can set a new one; an empty override (the
        // form's "leave it as it is") is answered from the original's sealed document, which the caller below
        // passes without ever holding the plaintext.
        var passwordNames = (ReadParameterDefinitions(project.ParametersJson) ?? [])
            .Where(definition => ProjectParameterTypes.IsMasked(definition.Type))
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);

        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in ReadParameters(original.ParametersJson) ?? new Dictionary<string, string>())
        {
            if (!passwordNames.Contains(name))
            {
                supplied[name] = value;
            }
        }

        foreach (var (name, value) in parameterOverrides ?? new Dictionary<string, string>())
        {
            supplied[name] = value;
        }

        var parameters = await ResolveRunParametersAsync(
            context,
            project,
            supplied.Count == 0 ? null : supplied,
            RunParameterDocuments.ReadProtected(original.ProtectedParametersJson, parameterProtector),
            MergeParameterFiles(original),
            cancellationToken);
        if (parameters.Error is { } parameterError)
        {
            return RunTriggerResult.Invalid(parameterError);
        }

        var run = new BuildRun
        {
            OrganizationId = organizationId,
            ProjectId = project.Id,
            TriggerType = triggerType,
            TriggerUserId = actingUserId,

            // The original's snapshots, verbatim: tags, branch, pipeline path and timeout are what that run
            // was queued with, and a later project edit must not move them (the F-014 snapshot rule).
            RequiredTags = original.RequiredTags,
            GitBranch = original.GitBranch,
            PipelinePath = original.PipelinePath,
            TimeoutSeconds = original.TimeoutSeconds,

            // F-017/F-037: the commit the original pinned is pinned again - never the branch head. A
            // Freestyle run has no commit and no repository, and the claim skips the clone for it.
            GitCommit = originalWasFreestyle ? null : original.GitCommit,

            // The definition: a Freestyle run executes the snapshot the original executed (a "definition
            // change since" is exactly what must not leak in); an inline run carries the same script, so
            // its definition phase runs again; a repository Pipeline run re-reads its definition from the
            // pinned commit, so the claim carries no spec.
            PipelineSpecJson = originalWasFreestyle ? original.PipelineSpecJson : null,
            PipelineScriptText = originalIsInline ? original.PipelineScriptText : null,

            ParametersJson = parameters.ParametersJson,
            ProtectedParametersJson = parameters.ProtectedParametersJson,
            ParameterDetailsJson = parameters.ParameterDetailsJson,
            ParameterFilesJson = parameters.ParameterFilesJson,
            RebuiltFromRunId = original.Id,
        };

        return await QueueStagedRunAsync(context, run, triggerType, actingUserId, idempotency: null, cancellationToken);
    }

    /// <summary>
    /// Queues a run for a push delivery (card F-017), <b>on the caller's context and inside the caller's
    /// unit of work</b>. The webhook decides <em>which</em> branch and commit; this is the same construction
    /// routine the manual trigger uses (one routine, one queue, one state machine), so a webhook run is
    /// never a special case downstream.
    /// <para>
    /// <b>Why it takes a context.</b> The delivery's replay record and the run it creates must commit
    /// together (review m6): a crash between "record the delivery" and "queue the run" would otherwise
    /// lose that push forever - a retry would answer <c>duplicate</c> with no run behind it. The caller
    /// owns the transaction, and this method stages the run without saving it.
    /// </para>
    /// <para>
    /// <b>Differs from <see cref="TriggerAsync"/> in exactly three ways, each deliberate.</b> There is no
    /// acting user (nobody signed in) and therefore no membership check - the caller proved possession of
    /// the webhook's secret, and the webhook row selected the tenant before this call. Required tags are
    /// <b>not</b> checked against the organization's workers: a push to a branch whose tag set no worker
    /// covers yet stays <c>Queued</c>, which is the honest answer (the AC's "no eligible worker keeps the
    /// run Queued") rather than a rejected delivery. And <see cref="BuildRun.GitCommit"/> is recorded at
    /// creation from the pushed <c>after</c>, because the webhook already names the commit - the claim
    /// hands that same commit to the worker to pin (review M1), so the recorded commit and the built tree
    /// cannot drift apart when the branch moves between push and clone.
    /// </para>
    /// <para>
    /// <b>The repository is the project's, never the payload's.</b> Nothing on this path reads a
    /// <c>clone_url</c>: the claim hands the worker <c>project.RepositoryUrl</c>, so a valid signature
    /// cannot redirect a build to another repository.
    /// </para>
    /// <para>
    /// <b>Cards F-028 + F-029 apply here too, through the same gate.</b> A disable or a full queue is
    /// not a delivery error: this method returns <see cref="RunOperationStatus.Refused"/> with the
    /// refusal's code, the caller records it on the delivery row and answers the git host
    /// <c>2xx</c> - a permanent <c>4xx</c> makes most hosts disable the webhook automatically, which
    /// would silently break the "enable it again and it works" promise.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see cref="RunTriggerResult.Success"/> with the staged run's id (saved on the caller's context, so
    /// it commits with the caller's transaction), <see cref="RunOperationStatus.Refused"/> when the
    /// project's own gate refused it (nothing written), or the reason nothing was staged - in which case
    /// the caller rolls its transaction back.
    /// </returns>
    public async Task<RunTriggerResult> QueueWebhookRunAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string gitCommit,
        IReadOnlyList<string> requiredTags,
        CancellationToken cancellationToken = default,
        string triggerType = BuildRunTriggerType.Webhook)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requiredTags);

        // Card F-033: this is the funnel every non-form trigger uses - a webhook delivery, a schedule, a poll,
        // and the branch observation that queues a run after a tree read. A requirement that can never hold
        // (`linux,!linux`, arriving from a webhook's stored tags or an observation entry) is refused here,
        // because the alternative is a run waiting for a machine that cannot exist. A requirement nobody
        // satisfies *yet* is not refused: that is ordinary operation and the run waits, visibly.
        if (WorkerRequirement.FromTags(string.Join(',', requiredTags)).IsContradiction)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.RequiredTags, RunErrorCodes.TagInvalid));
        }

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            // The webhook row selects the tenant; reaching here without one is a wiring fault, not a
            // user error - refuse rather than write an untagged run.
            return RunTriggerResult.Forbidden("No organization is selected for this delivery.");
        }

        // The branch came from `ref` and the commit from `after`; both are re-validated here so a
        // malformed payload cannot reach the queue through a future caller of this method.
        if (RunValidation.ValidateBranch(branch, out var normalizedBranch) is not null
            || normalizedBranch.Length == 0)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Branch, RunErrorCodes.BranchInvalid));
        }

        if (!GitCommitValidation.IsValid(gitCommit))
        {
            return RunTriggerResult.Invalid(
                new RunFieldError(RunFields.Branch, GitCommitValidation.InvalidCode));
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return RunTriggerResult.NotFound();
        }

        // F-020, defence in depth: a webhook triggers on git pushes, and a project with no repository
        // (Freestyle, or an inline Pipeline) has nothing to push to. Webhook creation already refuses
        // those kinds/sources, and both are immutable, so this line should be unreachable - it exists so
        // a future caller of this method cannot queue a run nothing can execute. F-022 reuses the one
        // shared predicate rather than adding a second condition beside the Freestyle one.
        if (!ProjectSources.HasRepository(project.Kind, project.PipelineSource))
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Steps, RunErrorCodes.NotPipeline));
        }

        var headless = await ResolveRunParametersAsync(
            context,
            project,
            provided: null,
            inheritedProtected: null,
            uploadedFiles: null,
            cancellationToken);
        if (headless.Error is { } headlessError)
        {
            return RunTriggerResult.Invalid(headlessError);
        }

        var run = BuildQueuedRun(
            activeOrganizationId.Value,
            project,
            normalizedBranch,
            string.Join(',', requiredTags),
            timeoutSeconds: null,
            triggerType,
            triggerUserId: null,
            gitCommit,
            pipelineSpecJson: null,
            // F-021 + F-041: a webhook run has no user to fill the form, so the parameters resolve to the
            // definitions' defaults (and a run parameter to the project's latest successful run) - the honest
            // answer for a headless trigger. A resolution that cannot answer refuses the delivery instead of
            // queueing a run with a silently missing value.
            parametersJson: headless.ParametersJson,
            protectedParametersJson: headless.ProtectedParametersJson,
            parameterDetailsJson: headless.ParameterDetailsJson,
            parameterFilesJson: headless.ParameterFilesJson);
        var staged = await StageQueuedRunAsync(context, run, idempotency: null, cancellationToken);
        if (staged.Status != RunOperationStatus.Success)
        {
            // Cards F-028 + F-029: the project's own gate refused this trigger (disabled, or the queue
            // is at its cap). The caller records the refusal on its delivery row; nothing is staged, so
            // there is no orphan run to clean up and a retry after re-enabling can build.
            return staged;
        }

        logger.LogInformation(
            "Run queued by a repository change. OrganizationId={OrganizationId} ProjectId={ProjectId} RunId={RunId} Number={Number} Branch={Branch} Commit={Commit} PipelinePath={PipelinePath} TriggerType={TriggerType}",
            activeOrganizationId,
            project.Id,
            run.Id,
            run.Number,
            run.GitBranch,
            run.GitCommit,
            run.PipelinePath,
            triggerType);
        return staged;
    }

    /// <summary>
    /// Card F-024's scheduled trigger: <b>the same run the manual form and a webhook delivery queue</b>, with the
    /// definition's defaults filled in for the inputs a headless trigger cannot supply.
    /// <list type="bullet">
    /// <item>the branch is the project's default (a Pipeline run) and the definition is the project's frozen
    /// Freestyle spec (a Freestyle run) - the two things a click would have chosen;</item>
    /// <item>the parameters resolve to their definitions' defaults (F-021) and no required tags are set, because a
    /// schedule has no form to collect either;</item>
    /// <item>the trigger type is <see cref="BuildRunTriggerType.Schedule"/> and there is no triggering user - nobody
    /// was signed in.</item>
    /// </list>
    /// <para>
    /// <b>Why the caller passes its context.</b> The schedule sweeper needs the run and the due-time record it
    /// explains to commit together: a crash between them would either lose the run without a record or record a
    /// due time that queued nothing. So this method stages on the caller's context inside the caller's transaction,
    /// and the staging routine applies the same F-028/F-029 gate under the same project row lock as every other
    /// trigger. There is no second insert path and no schedule-only validation.
    /// </para>
    /// <para>
    /// <b>Refusals are answers, not exceptions.</b> A disabled project returns <c>Refused(project-disabled)</c>, a
    /// full queue <c>Refused(queue-full)</c>, and a Freestyle project with no saved steps
    /// <c>Invalid(steps-missing)</c> - the schedule records whichever it gets, so the page says why nothing was
    /// queued instead of showing a missing run.
    /// </para>
    /// </summary>
    public async Task<RunTriggerResult> QueueScheduledRunAsync(
        ZenkinsDbContext context,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            // The schedule row selects the tenant; reaching here without one is a wiring fault, not a user error.
            return RunTriggerResult.Forbidden("No organization is selected for this schedule.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return RunTriggerResult.NotFound();
        }

        // F-020: a Freestyle project runs its frozen spec. A project whose steps were never saved cannot run
        // anything, and a schedule must not queue a run whose only future is that failure.
        var isFreestyle = project.Kind == ProjectKinds.Freestyle;
        if (isFreestyle && project.FreestyleSpecJson is null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Steps, RunErrorCodes.StepsMissing));
        }

        // F-022: an inline project's schedule carries the script snapshot, not a branch - exactly the
        // manual trigger's shape (a headless trigger has no branch input either).
        var isInline = !isFreestyle
            && project.Kind == ProjectKinds.Pipeline
            && project.PipelineSource == ProjectSources.Inline;
        if (isInline && string.IsNullOrWhiteSpace(project.InlineScript))
        {
            return RunTriggerResult.Invalid(
                new RunFieldError(ProjectFields.InlineScript, ProjectErrorCodes.InlineScriptRequired));
        }

        var scheduled = await ResolveRunParametersAsync(
            context,
            project,
            provided: null,
            inheritedProtected: null,
            uploadedFiles: null,
            cancellationToken);
        if (scheduled.Error is { } scheduledError)
        {
            return RunTriggerResult.Invalid(scheduledError);
        }

        var run = BuildQueuedRun(
            activeOrganizationId.Value,
            project,
            isFreestyle || isInline ? string.Empty : project.DefaultBranch,
            requiredTags: string.Empty,
            timeoutSeconds: null,
            BuildRunTriggerType.Schedule,
            triggerUserId: null,
            gitCommit: null,
            // F-020: the Freestyle run freezes the project's spec at trigger time (the same snapshot discipline
            // a click follows); a Pipeline run's worker reports its own definition.
            pipelineSpecJson: isFreestyle ? project.FreestyleSpecJson : null,
            // F-022: an inline run freezes the project's script at trigger time.
            pipelineScriptText: isInline ? project.InlineScript : null,
            // F-021 + F-041: a headless trigger resolves the definitions' defaults (a run parameter to the
            // project's latest successful run), the same answer a webhook delivery gives - and a resolution
            // that cannot answer refuses the tick instead of queueing a run with a missing value.
            parametersJson: scheduled.ParametersJson,
            protectedParametersJson: scheduled.ProtectedParametersJson,
            parameterDetailsJson: scheduled.ParameterDetailsJson,
            parameterFilesJson: scheduled.ParameterFilesJson);

        var staged = await StageQueuedRunAsync(context, run, idempotency: null, cancellationToken);
        if (staged.Status != RunOperationStatus.Success)
        {
            // Cards F-028 + F-029: the project's own gate refused this trigger (disabled, or the queue is at its
            // cap). Nothing is staged, so the schedule records the refusal with no orphan run to clean up.
            return staged;
        }

        logger.LogInformation(
            "Run queued by schedule. OrganizationId={OrganizationId} ProjectId={ProjectId} RunId={RunId} Number={Number} Branch={Branch} PipelinePath={PipelinePath}",
            activeOrganizationId,
            project.Id,
            run.Id,
            run.Number,
            run.GitBranch,
            run.PipelinePath);
        return staged;
    }

    /// <summary>
    /// The three parameter documents one trigger resolves to (card F-021 + card F-041), or the field error that
    /// stopped it. A record rather than an <c>out</c> parameter because the resolution is asynchronous and needs
    /// the database (a <c>run</c> selection), and one answer is easier to read at the call sites than two.
    /// </summary>
    private sealed record ResolvedRunParameters(
        string? ParametersJson = null,
        string? ProtectedParametersJson = null,
        string? ParameterDetailsJson = null,
        string? ParameterFilesJson = null,
        RunFieldError? Error = null);

    /// <summary>
    /// Resolves a trigger's parameters into the run's three documents (cards F-021 + F-041): the value map every
    /// reader shares, the sealed <c>password</c> values, and the per-parameter type record.
    /// <para>
    /// <b>The run kind is answered here, not in <see cref="ProjectParameters.ResolveValues"/>.</b> A
    /// <c>run</c> parameter has to name one of <em>this project's</em> runs and its default is "the most recent
    /// successful one" - both are facts of the database, so they are resolved where the database is (and inside
    /// the caller's context, so the lookup sees the same snapshot the run row is written with). A selection that
    /// names nothing, or a project with no successful run to default to, refuses the trigger with a typed reason
    /// instead of queueing a run whose steps would read an empty number.
    /// </para>
    /// <para>
    /// <b>A password never enters the value map.</b> <paramref name="provided"/> supplies it, the sealed document
    /// keeps it, and the map gets <see cref="ProjectParameterTypes.MaskedValue"/> - so the page, the rebuild form
    /// and any future reader of the row see the mask, whatever they do with the document.
    /// <paramref name="inheritedProtected"/> is a rebuild's way to keep the original value without the caller ever
    /// holding it: an empty supplied value means "carry the original forward".
    /// </para>
    /// </summary>
    private async Task<ResolvedRunParameters> ResolveRunParametersAsync(
        ZenkinsDbContext context,
        Project project,
        IReadOnlyDictionary<string, string>? provided,
        IReadOnlyDictionary<string, string>? inheritedProtected,
        IReadOnlyDictionary<string, byte[]>? uploadedFiles,
        CancellationToken cancellationToken)
    {
        var definitions = ReadParameterDefinitions(project.ParametersJson);
        var resolved = ProjectParameters.ResolveValues(definitions, provided, out var parameterError);
        if (resolved is null)
        {
            return new ResolvedRunParameters(
                Error: new RunFieldError(RunFields.Parameter, parameterError!));
        }

        var values = new Dictionary<string, string>(resolved, StringComparer.Ordinal);
        Dictionary<string, string>? protectedValues = null;
        Dictionary<string, RunParameterDetail>? details = null;

        foreach (var definition in definitions ?? [])
        {
            switch (definition.Type)
            {
                case ProjectParameterTypes.Password:
                {
                    var secret = values[definition.Name];
                    if (secret.Length == 0
                        && inheritedProtected is not null
                        && inheritedProtected.TryGetValue(definition.Name, out var inherited))
                    {
                        secret = inherited;
                    }

                    (protectedValues ??= new(StringComparer.Ordinal))[definition.Name] = secret;
                    values[definition.Name] = ProjectParameterTypes.MaskedValue;
                    (details ??= new(StringComparer.Ordinal))[definition.Name] =
                        new RunParameterDetail(ProjectParameterTypes.Password);
                    break;
                }

                case ProjectParameterTypes.Run:
                {
                    var selection = await ResolveRunSelectionAsync(
                        context,
                        project.Id,
                        values[definition.Name],
                        cancellationToken);
                    if (selection is null)
                    {
                        return new ResolvedRunParameters(
                            Error: new RunFieldError(
                                RunFields.Parameter,
                                values[definition.Name].Length == 0
                                    ? RunErrorCodes.ParameterNoRun
                                    : RunErrorCodes.ParameterInvalid));
                    }

                    values[definition.Name] = selection.Number.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    (details ??= new(StringComparer.Ordinal))[definition.Name] = new RunParameterDetail(
                        ProjectParameterTypes.Run,
                        selection.Id.ToString(),
                        selection.GitCommit);
                    break;
                }

                case ProjectParameterTypes.GitRef:
                    (details ??= new(StringComparer.Ordinal))[definition.Name] =
                        new RunParameterDetail(ProjectParameterTypes.GitRef);
                    break;

                case ProjectParameterTypes.File:
                {
                    // Card F-041: the step reads a path the worker writes, so the value the server records is the
                    // run-relative location - truthful on the page and never a byte of the file. A trigger that
                    // uploaded nothing (a schedule, a poll) leaves it empty; the worker then injects nothing and
                    // the step can tell.
                    var hasBytes = uploadedFiles is not null && uploadedFiles.ContainsKey(definition.Name);
                    values[definition.Name] = hasBytes
                        ? ProjectParameterLimits.FileLocation(definition.Name)
                        : string.Empty;
                    (details ??= new(StringComparer.Ordinal))[definition.Name] =
                        new RunParameterDetail(ProjectParameterTypes.File);
                    break;
                }
            }
        }

        return new ResolvedRunParameters(
            SerializeParameters(values),
            RunParameterDocuments.SerializeProtected(protectedValues, parameterProtector),
            RunParameterDocuments.SerializeDetails(details),
            RunParameterFiles.Serialize(uploadedFiles, parameterProtector));
    }

    /// <summary>
    /// A rebuild's file inputs (card F-041): the original's files, carried forward verbatim - the same rule a
    /// password follows, and the reason a rebuild never asks for either again. The rebuild form has no file input
    /// on purpose: to change the uploaded file, trigger a fresh run.
    /// </summary>
    private IReadOnlyDictionary<string, byte[]>? MergeParameterFiles(BuildRun original)
    {
        var files = RunParameterFiles.ReadAll(original.ParameterFilesJson, parameterProtector);
        return files.Count == 0 ? null : files;
    }

    /// <summary>
    /// The run a <c>run</c> parameter selects (card F-041): the named number when the trigger supplied one, and
    /// the project's most recent <b>successful</b> run when it supplied nothing - the same run the trigger form
    /// prefills. Only this project's runs are eligible; a number that belongs to another project (or to no run)
    /// answers <see langword="null"/>, which the caller turns into a typed refusal.
    /// </summary>
    private static async Task<BuildRun?> ResolveRunSelectionAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string selection,
        CancellationToken cancellationToken)
    {
        if (selection.Length == 0)
        {
            return await context.BuildRuns
                .AsNoTracking()
                .Where(run => run.ProjectId == projectId && run.Status == BuildRunStatus.Success)
                .OrderByDescending(run => run.Number)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (!ProjectParameterRules.IsRunNumber(selection, out var number))
        {
            return null;
        }

        return await context.BuildRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(
                run => run.ProjectId == projectId && run.Number == number,
                cancellationToken);
    }

    /// <summary>
    /// The parameter map a claim hands to the worker (cards F-021 + F-041): the run's plain values with the sealed
    /// passwords opened and a run selection's <c>&lt;NAME&gt;_RUN_ID</c> / <c>&lt;NAME&gt;_COMMIT</c> added. This
    /// is the <b>only</b> place a protected value is decrypted, and its output goes to the worker executing the
    /// run - never to a page, a log line or an API response.
    /// </summary>
    private IReadOnlyDictionary<string, string>? BuildClaimParameters(BuildRun run)
    {
        Dictionary<string, string>? values = ReadParameters(run.ParametersJson) is { } plain
            ? new Dictionary<string, string>(plain, StringComparer.Ordinal)
            : null;

        foreach (var (name, value) in RunParameterDocuments.ReadProtected(
                     run.ProtectedParametersJson,
                     parameterProtector))
        {
            (values ??= new(StringComparer.Ordinal))[name] = value;
        }

        foreach (var (name, detail) in RunParameterDocuments.ReadDetails(run.ParameterDetailsJson))
        {
            if (!ProjectParameterTypes.IsRunSelection(detail.Type) || detail.RunId is null)
            {
                continue;
            }

            (values ??= new(StringComparer.Ordinal))[name + "_RUN_ID"] = detail.RunId;

            // Always set, empty when the selected run had no commit (a Freestyle run): `$X_COMMIT` would expand
            // to nothing either way, and `env("X_COMMIT")` now agrees with the composed environment instead of
            // answering "unset" for a variable this run deliberately provides.
            values[name + "_COMMIT"] = detail.Commit ?? string.Empty;
        }

        return values is { Count: > 0 } ? values : null;
    }

    /// <summary>
    /// The chain facts a <b>downstream</b> run's steps read (card F-026): they were frozen onto the run at trigger
    /// time and are merged into the claim map here, beside the parameters - one map crosses the boundary, so the
    /// worker needs no rule of its own for them.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? MergeUpstreamFacts(
        BuildRun run,
        IReadOnlyDictionary<string, string>? parameters)
    {
        if (string.IsNullOrWhiteSpace(run.UpstreamFactsJson))
        {
            return parameters;
        }

        var facts = JsonSerializer.Deserialize(
            run.UpstreamFactsJson,
            ProjectParameterJsonContext.Default.DictionaryStringString);
        if (facts is not { Count: > 0 })
        {
            return parameters;
        }

        var values = parameters is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        foreach (var (name, value) in facts)
        {
            values[name] = value;
        }

        return values;
    }

    /// <summary>The names of the password-typed parameters in a run's claim (card F-041).</summary>
    private static IReadOnlyList<string>? PasswordParameterNames(BuildRun run) =>
        ParameterNamesOfType(run, ProjectParameterTypes.Password);

    /// <summary>The names of the parameters of one kind in a run's own record (cards F-041).</summary>
    private static IReadOnlyList<string>? ParameterNamesOfType(BuildRun run, string type)
    {
        var names = RunParameterDocuments.ReadDetails(run.ParameterDetailsJson)
            .Where(entry => string.Equals(entry.Value.Type, type, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0 ? null : names;
    }

    /// <summary>
    /// Queues one <b>downstream</b> run for a chain edge (card F-026). The caller (the outbox dispatcher) passes its
    /// own context so the run and the outbox row that owes it settle in one unit of work - a crash between them
    /// would either queue a downstream twice or lose it.
    /// <para>
    /// <b>The same staging path as every other trigger.</b> A downstream run is an ordinary queued run: the F-028
    /// gate and the F-029 queue cap apply, the numbering is the project's, and the worker contract is unchanged.
    /// What makes it a chain run is that it records <c>TriggerType = Upstream</c>, the run that caused it, and how
    /// many links deep it is - the counter the runtime loop protection reads.
    /// </para>
    /// </summary>
    /// <param name="branch">
    /// The branch the link's policy chose, empty for a project with no repository. The caller has already decided
    /// it: the policy belongs to the <em>link</em>, not to the trigger path.
    /// </param>
    public async Task<RunTriggerResult> QueueUpstreamRunAsync(
        ZenkinsDbContext context,
        Guid projectId,
        Guid upstreamRunId,
        int hops,
        string branch,
        IReadOnlyDictionary<string, string> upstreamFacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            // The outbox row selects the tenant; reaching here without one is a wiring fault, not a user error.
            return RunTriggerResult.Forbidden("No organization is selected for this downstream trigger.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return RunTriggerResult.NotFound();
        }

        // The same refusal a schedule gives: a Freestyle project whose steps were never saved cannot run anything.
        var isFreestyle = project.Kind == ProjectKinds.Freestyle;
        if (isFreestyle && project.FreestyleSpecJson is null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Steps, RunErrorCodes.StepsMissing));
        }

        // F-021 + F-041: a downstream run is a headless trigger, so its parameters resolve exactly the way a
        // schedule's and a webhook delivery's do - the definitions' defaults, a run selection's latest successful
        // run, a password sealed (empty: nobody typed one) and a file's upload (none). One resolver, one answer.
        var parameters = await ResolveRunParametersAsync(
            context,
            project,
            provided: null,
            inheritedProtected: null,
            uploadedFiles: null,
            cancellationToken);
        if (parameters.Error is { } parameterError)
        {
            return RunTriggerResult.Invalid(parameterError);
        }

        var isInline = project.PipelineSource == ProjectSources.Inline;
        var run = BuildQueuedRun(
            activeOrganizationId.Value,
            project,
            isFreestyle || isInline ? string.Empty : branch,
            requiredTags: string.Empty,
            timeoutSeconds: null,
            BuildRunTriggerType.Upstream,
            triggerUserId: null,
            gitCommit: null,
            pipelineSpecJson: isFreestyle ? project.FreestyleSpecJson : null,
            parametersJson: parameters.ParametersJson,
            pipelineScriptText: isInline ? project.InlineScript : null,
            protectedParametersJson: parameters.ProtectedParametersJson,
            parameterDetailsJson: parameters.ParameterDetailsJson,
            parameterFilesJson: parameters.ParameterFilesJson);

        run.TriggeredByRunId = upstreamRunId;
        run.UpstreamHops = hops;
        run.UpstreamFactsJson = upstreamFacts.Count == 0
            ? null
            : JsonSerializer.Serialize(
                new Dictionary<string, string>(upstreamFacts),
                ProjectParameterJsonContext.Default.DictionaryStringString);

        return await QueueStagedRunAsync(
            context,
            run,
            BuildRunTriggerType.Upstream,
            actingUserId: null,
            idempotency: null,
            cancellationToken);
    }

    /// <summary>
    /// The one run construction routine both trigger sources use: the tenant from the active request (the
    /// data layer rejects anything else), the branch and pipeline path snapshotted so a later project edit
    /// cannot move this run, and the trigger source's own attribution. Adding and saving is the caller's
    /// step (<c>StageQueuedRunAsync</c>, which applies the project gate first), so each source keeps its own
    /// unit of work (the manual trigger saves one row; a webhook saves its replay record and the run
    /// together).
    /// </summary>
    private static BuildRun BuildQueuedRun(
        Guid organizationId,
        Project project,
        string? branch,
        string requiredTags,
        int? timeoutSeconds,
        string triggerType,
        string? triggerUserId,
        string? gitCommit,
        string? pipelineSpecJson,
        string? parametersJson,
        string? pipelineScriptText = null,
        string? protectedParametersJson = null,
        string? parameterDetailsJson = null,
        string? parameterFilesJson = null) =>
        new()
        {
            OrganizationId = organizationId,
            ProjectId = project.Id,
            TriggerType = triggerType,
            TriggerUserId = triggerUserId,
            RequiredTags = requiredTags,

            // F-033: the meaning those tags had when the run was created, normalized once. An empty string is
            // "no requirement" - the claim loop reads that as "any Worker", the same answer the legacy
            // comma-list rule gives an empty list.
            RequiredRequirement = WorkerRequirement.FromTags(requiredTags).StorageForm,

            // F-014 snapshots: the branch the trigger chose and the project's pipeline path. Both are
            // copies, so editing the project later never moves this run. A Freestyle run (F-020) has
            // neither a branch nor a pipeline file - the empty branch is the honest record, and the
            // frozen spec below is what executes. B-006: the pipeline path follows the same rule the
            // branch does, decided by the kind rather than by whatever the row holds - a Freestyle row
            // written without `ProjectService.Validate` (a seed, an import, a hand-run migration) can
            // carry a path, and a run must not report a pipeline file for a project with no repository.
            GitBranch = branch ?? string.Empty,
            PipelinePath = project.Kind == ProjectKinds.Freestyle
                ? string.Empty
                : project.PipelineSource == ProjectSources.Inline
                    ? PipelineScriptLimits.InlineFileName
                    : project.PipelinePath ?? string.Empty,
            TimeoutSeconds = timeoutSeconds,

            // F-020: the Freestyle run's frozen spec, snapshotted by the trigger source. A Pipeline
            // run leaves it null - its Worker reports the spec through the definition endpoint.
            PipelineSpecJson = pipelineSpecJson,

            // F-022: the inline run's script snapshot. A repository/Freestyle run leaves it null; the
            // Worker's own rule is "script and no repository" - not a kind or source word it cannot read.
            PipelineScriptText = pipelineScriptText,

            // F-021: the resolved parameter values, frozen with everything else the run builds. A password's
            // entry here is the mask - the value itself is in ProtectedParametersJson (F-041).
            ParametersJson = parametersJson,
            ProtectedParametersJson = protectedParametersJson,
            ParameterDetailsJson = parameterDetailsJson,
            ParameterFilesJson = parameterFilesJson,

            // F-017: a webhook delivery already names the commit; a manual trigger leaves it to the
            // worker's accepted report (F-013).
            GitCommit = gitCommit,
        };

    /// <summary>The definitions a project declared, parsed from its row (F-021); null for none.</summary>
    private static ProjectParameter[]? ReadParameterDefinitions(string? parametersJson) =>
        string.IsNullOrWhiteSpace(parametersJson)
            ? null
            : JsonSerializer.Deserialize(parametersJson, ProjectParameterJsonContext.Default.ProjectParameterArray);

    /// <summary>Serializes the resolved values for the run row (F-021); an empty set stays null.</summary>
    private static string? SerializeParameters(IReadOnlyDictionary<string, string>? values) =>
        values is not { Count: > 0 }
            ? null
            : JsonSerializer.Serialize(new Dictionary<string, string>(values), ProjectParameterJsonContext.Default.DictionaryStringString);

    /// <summary>The values a claim hands to the worker (F-021); a row without parameters stays null.</summary>
    private static IReadOnlyDictionary<string, string>? ReadParameters(string? parametersJson) =>
        string.IsNullOrWhiteSpace(parametersJson)
            ? null
            : JsonSerializer.Deserialize(parametersJson, ProjectParameterJsonContext.Default.DictionaryStringString);

    /// <summary>Runs of one project in the active organization, newest first.</summary>
    public async Task<IReadOnlyList<RunView>> GetProjectRunsAsync(
        Guid projectId,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // F-023's branch filter. The project id and the branch are both part of the query, and the
        // context's tenant filter adds the organization: a same-named branch in another project of the
        // same organization can never appear here, and a foreign project id answers an empty list.
        // The branch is matched ordinally, exactly as BuildRun.GitBranch stores it.
        var runs = await context.BuildRuns
            .Where(run => run.ProjectId == projectId)
            .Where(run => branch == null || run.GitBranch == branch)
            .ToListAsync(cancellationToken);
        return (await ToViewsAsync(context, runs, cancellationToken))
            .OrderByDescending(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .ToArray();
    }

    /// <summary>
    /// Card F-029: how many runs of one project are <c>Queued</c> right now (claimed-but-not-yet-accepted
    /// ones included - the count the trigger's cap is measured against, and the number the refusal
    /// message quotes). Read through the tenant filter, so a foreign project id simply counts nothing.
    /// </summary>
    public async Task<int> GetProjectQueuedRunCountAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.BuildRuns.CountAsync(
            run => run.ProjectId == projectId && run.Status == BuildRunStatus.Queued,
            cancellationToken);
    }

    /// <summary>
    /// One run of the active organization, or <see langword="null"/> when the id belongs to another
    /// organization or does not exist. Both cases are deliberately indistinguishable.
    /// </summary>
    public async Task<RunView?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.BuildRuns.SingleOrDefaultAsync(
            candidate => candidate.Id == runId,
            cancellationToken);
        return run is null ? null : (await ToViewsAsync(context, [run], cancellationToken))[0];
    }

    /// <summary>
    /// Card F-036's permalink read: one project's run by its build number. The project id is part of the
    /// lookup (not only the number), so the query goes through the tenant filter and a foreign project
    /// answers <see langword="null"/> - the same 404 a missing one does, with no existence oracle.
    /// </summary>
    public async Task<RunView?> GetRunByNumberAsync(
        Guid projectId,
        int number,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.BuildRuns.SingleOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Number == number,
            cancellationToken);
        return run is null ? null : (await ToViewsAsync(context, [run], cancellationToken))[0];
    }

    /// <summary>
    /// Card F-036: the project's newest run, by build number - the <c>/runs/last</c> permalink. Ordering by
    /// <see cref="BuildRun.Number"/> rather than by a timestamp is deliberate: the number is assigned at
    /// trigger time under the project row lock, so it <em>is</em> the creation order, and it is unique per
    /// project (a timestamp tie can no longer make "last" ambiguous).
    /// </summary>
    public async Task<RunView?> GetLastRunAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.BuildRuns
            .Where(candidate => candidate.ProjectId == projectId)
            .OrderByDescending(candidate => candidate.Number)
            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : (await ToViewsAsync(context, [run], cancellationToken))[0];
    }

    /// <summary>
    /// Card F-036: the project's newest <c>Success</c> run - the <c>/lastSuccessful</c> permalink every badge
    /// definition means. Same ordering as <see cref="GetLastRunAsync"/>; a project with no successful run yet
    /// answers <see langword="null"/> (a 404), never a failed run standing in for one.
    /// </summary>
    public async Task<RunView?> GetLastSuccessfulRunAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.BuildRuns
            .Where(candidate => candidate.ProjectId == projectId && candidate.Status == BuildRunStatus.Success)
            .OrderByDescending(candidate => candidate.Number)
            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : (await ToViewsAsync(context, [run], cancellationToken))[0];
    }

    /// <summary>
    /// Card F-036: one run's build number, or <see langword="null"/> for a foreign/unknown id. The log
    /// download names its file after it, which is why this reads the number alone rather than a whole view.
    /// </summary>
    public async Task<int?> GetRunNumberAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.BuildRuns
            .Where(candidate => candidate.Id == runId)
            .Select(candidate => (int?)candidate.Number)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>Whether the run exists in the active organization (download endpoint's 404 gate).</summary>
    public async Task<bool> RunExistsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.BuildRuns.AnyAsync(run => run.Id == runId, cancellationToken);
    }

    // ---------------------------------------------------------------------
    // Worker page reads (card D-005)
    // ---------------------------------------------------------------------

    /// <summary>
    /// How many <see cref="WorkerRunsView.Current"/>-style runs each worker of the active
    /// organization holds, in one read. Card D-005's decision: the roster's per-worker count comes
    /// from the same query shape as the detail page's current-runs list - the worker's
    /// <c>Running</c> runs - never from a denormalised column that could drift from it.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> GetCurrentRunCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.WorkerId != null && run.Status == BuildRunStatus.Running)
            .Select(run => new { WorkerId = run.WorkerId!.Value })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.WorkerId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    /// <summary>
    /// The runs the worker detail page lists (card D-005): the worker's current runs (<c>Running</c>,
    /// the same predicate <see cref="GetCurrentRunCountsAsync"/> aggregates for the roster) and its
    /// recent runs (everything else - queued, finished, canceled, lost - newest first), both capped.
    /// The worker id resolves through the tenant filter, so a foreign worker's runs are simply empty.
    /// </summary>
    public async Task<WorkerRunsView> GetWorkerRunsAsync(
        Guid workerId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // In-memory ordering on purpose (SQLite cannot ORDER BY DateTimeOffset columns) - the same
        // shape GetProjectRunsAsync uses. A worker's Running runs are bounded by MaxConcurrency, so
        // the current read is small by construction.
        var currentRows = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.WorkerId == workerId && run.Status == BuildRunStatus.Running)
            .ToListAsync(cancellationToken);
        var recentRows = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.WorkerId == workerId && run.Status != BuildRunStatus.Running)
            .ToListAsync(cancellationToken);

        var runs = currentRows
            .OrderByDescending(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .Take(MaxCurrentRunsShown)
            .Concat(recentRows
                .OrderByDescending(run => run.CreatedAt)
                .ThenBy(run => run.Id)
                .Take(MaxRecentRunsShown))
            .ToList();
        var views = await ToViewsAsync(context, runs, cancellationToken);
        return new WorkerRunsView(
            views.Where(run => run.Status == BuildRunStatus.Running).ToArray(),
            views.Where(run => run.Status != BuildRunStatus.Running).ToArray());
    }

    /// <summary>
    /// Streams one run's persisted log ordered by <see cref="Entities.BuildLog.Seq"/> (F-008's
    /// download). The database read is streamed - the caller writes each line as it arrives, so
    /// memory stays bounded no matter how large the log grew. Lines never leave the active
    /// organization: the query runs through the tenant filter, and callers gate the 404 beforehand.
    /// </summary>
    public async IAsyncEnumerable<LogLineMessage> StreamRunLogAsync(
        Guid runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in StreamRunLogAsync(runId, fromSeq: 0, cancellationToken))
        {
            yield return line;
        }
    }

    /// <summary>
    /// The same stream starting after <paramref name="fromSeq"/> (card F-027's <c>?from=&lt;offset&gt;</c>
    /// tailing): a client that already holds every line up to <c>fromSeq</c> asks again with that value
    /// and receives only what is new. The lines are the same already-redacted rows the UI's download
    /// streams - there is no second redaction step anywhere on the server, so the two surfaces cannot
    /// diverge.
    /// </summary>
    public async IAsyncEnumerable<LogLineMessage> StreamRunLogAsync(
        Guid runId,
        long fromSeq,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = context.BuildLogs
            .AsNoTracking()
            .Where(log => log.RunId == runId && log.Seq > fromSeq)
            .OrderBy(log => log.Seq)
            .Select(log => new LogLineMessage(log.Seq, log.Stream, log.Message, log.Timestamp, log.StepId));

        await foreach (var line in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return line;
        }
    }

    /// <summary>
    /// The highest log sequence this run holds (0 when it has none). The API answers it in the log
    /// response's <c>X-Zenkins-Last-Seq</c> header before streaming starts, so a tailing client learns the
    /// offset to ask for next without parsing the body.
    /// </summary>
    public async Task<long> GetRunLogLastSeqAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.BuildLogs
            .Where(log => log.RunId == runId)
            .MaxAsync(log => (long?)log.Seq, cancellationToken) ?? 0;
    }

    // ---------------------------------------------------------------------
    // Worker API (token-authenticated; F-004's authentication pattern)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Atomically leases the next eligible queued run to the calling worker: worker
    /// <c>Online</c>, a free concurrency slot (every run still assigned to the worker occupies
    /// one - running, or leased and not yet swept), required tags covered by the worker's tags,
    /// FIFO by creation. No eligible run - or every lease lost to a concurrent claim - is a
    /// 204-shaped <see cref="RunClaimResult.NoRun"/>: a worker polls again and never learns why
    /// it got nothing.
    /// <para>
    /// <b>The cap is part of the claim (B-002).</b> The slot count and the lease run inside one
    /// transaction that opens by locking the caller's own worker row
    /// (<see cref="ZenkinsDbContext.LockWorkerForClaimAsync"/>), so concurrent claims of one
    /// worker serialize instead of interleaving the count with the lease - the enforced
    /// <c>MaxConcurrency</c> is the data layer's, not the client's good behaviour. The lease
    /// compare-and-set keeps its F-006 semantics unchanged.
    /// </para>
    /// </summary>
    public async Task<RunClaimResult> ClaimAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        // 1. Authenticate before anything else (F-004 pattern): bare 401 with no tenant detail.
        var workerToken = await AuthenticateAsync(plaintextToken, cancellationToken);
        if (workerToken is null)
        {
            return new RunClaimResult(RunOperationStatus.InvalidToken);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        // 2. Eligibility of the worker itself: Online, not disabled (card D-005: the disabled flag is
        //    a server fact enforced here, so a worker cannot bypass it by ignoring the page - a
        //    disabled worker's claim gets the same not-assigned answer as any other rejection).
        //    Runs already in flight are untouched: nothing below blocks the accepted/completed
        //    reports of a running run.
        var worker = await context.Workers.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.WorkerId,
            cancellationToken);
        if (worker is null || worker.Status != WorkerStatus.Online || worker.IsDisabled)
        {
            return RunClaimResult.NoRun();
        }

        // 3. B-002: the concurrency cap and the lease are ONE unit of work. The slot check used to
        //    be check-then-act - count in one statement, lease in another - so two racing claims of
        //    one valid worker token could both pass the count before either lease committed and
        //    admit more running runs than the worker's reported MaxConcurrency. The claim therefore
        //    runs in one transaction, opened by locking the worker's own row:
        //    - LockWorkerForClaimAsync takes the provider's row lock (PostgreSQL) or the database's
        //      single write lock (SQLite) and holds it to commit, so two claims of one worker
        //      serialize here instead of interleaving count and lease;
        //    - inside the lock the count below can only ever be raised by this claim itself (a
        //      requeue, cancel or completion only releases slots), so count + one lease cannot
        //      cross the cap;
        //    - the compare-and-set itself is unchanged: the same guarded statement leases the same
        //      candidate exactly once, with the same lease semantics (F-006 untouched).
        //    The early returns below dispose the transaction - the lock writes nothing.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var locked = await context.LockWorkerForClaimAsync(worker.Id, cancellationToken);
        if (locked == 0)
        {
            // The worker row is gone (deleted between the read and the lock): nothing to admit.
            return RunClaimResult.NoRun();
        }

        // A queued-but-leased run still occupies one of the worker's slots until the sweeper
        // clears the expired lease, so claiming cannot outrun MaxConcurrency through leases.
        var assigned = await context.BuildRuns.CountAsync(
            run => run.WorkerId == worker.Id
                && (run.Status == BuildRunStatus.Running || run.Status == BuildRunStatus.Queued),
            cancellationToken);
        if (assigned >= worker.MaxConcurrency)
        {
            return RunClaimResult.NoRun();
        }

        // 4. The candidate queue: Queued and unleased (never leased, or requeued by the sweeper),
        //    FIFO by creation. Ordered in memory - a small list per tenant, and SQLite cannot
        //    ORDER BY DateTimeOffset columns. An expired lease is NOT claimable here: the lease
        //    sweeper is the single authority on expiry, and SQLite cannot compare DateTimeOffset
        //    values in SQL, so the claim never needs a time predicate (see TryLeaseBuildRunAsync).
        //    F-014: the project is included because the pipeline layer needs its repository URL.
        var queued = await context.BuildRuns
            .AsNoTracking()
            .Include(run => run.Project)
            .Where(run => run.Status == BuildRunStatus.Queued && run.LeaseExpiresAt == null)
            .ToListAsync(cancellationToken);
        var candidates = queued
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .Where(run => RunValidation.WorkerSatisfies(run.RequiredRequirement, run.RequiredTags, worker.Tags))
            .Take(MaxClaimCandidates)
            .ToArray();

        // 5. Compare-and-set loop: the first candidate whose single-statement lease wins is the
        //    answer. A lost race means another claim took exactly that run - move to the next. Each
        //    candidate first passes the project's own gate (cards F-028 + F-029), which may itself take
        //    the project row lock.
        var leaseExpiresAt = now.AddSeconds(options.LeaseSeconds);
        foreach (var candidate in candidates)
        {
            if (!await ProjectAllowsClaimAsync(context, candidate, now, cancellationToken))
            {
                continue;
            }

            var leased = await context.TryLeaseBuildRunAsync(
                candidate.Id,
                worker.Id,
                leaseExpiresAt,
                now,
                cancellationToken);
            if (leased == 1)
            {
                // The slot was reserved and the run leased inside one transaction: commit is what
                // makes both visible together, and it releases the worker-row lock for the next
                // claim.
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Run leased. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} LeaseExpiresAt={LeaseExpiresAt}",
                    workerToken.OrganizationId,
                    candidate.Id,
                    worker.Id,
                    leaseExpiresAt);

                // F-014: the claim carries what the pipeline layer executes - the repository, the
                // branch this run snapshotted and the pipeline path it snapshotted. F-017 adds the exact
                // commit when the trigger already named one (a webhook push): the worker pins that commit
                // instead of trusting the branch tip, which may have moved by now. F-020: a Freestyle
                // run carries its frozen spec (and no repository - the claim's URL is null and the
                // Worker skips the clone entirely).
                return new RunClaimResult(
                    RunOperationStatus.Success,
                    candidate.Id,
                    candidate.ProjectId,
                    candidate.Project.RepositoryUrl,
                    candidate.GitBranch,
                    candidate.GitCommit,
                    candidate.PipelinePath,
                    leaseExpiresAt,
                    candidate.PipelineSpecJson,

                    // F-022: the inline run's script snapshot. Null for a repository/Freestyle run, so
                    // an older Worker that does not know the field simply skips it.
                    candidate.PipelineScriptText,

                    // F-022: the shape word the Worker switches on (never "has a spec" - an inline
                    // run can carry both a script and a spec).
                    RunDefinitionSources.For(
                        candidate.PipelineScriptText,
                        candidate.PipelineSpecJson,
                        candidate.GitBranch),

                    // F-034: the one map that crosses to the worker - the run's frozen organization layer
                    // with the parameter values on top (parameters win a shared name). The two columns stay
                    // separate on the row for the page and for a rebuild; the order is resolved here, once,
                    // so the worker never holds a second copy of it.
                    RunEnvironmentSnapshot.Merge(
                        RunEnvironmentSnapshot.Read(candidate.EnvironmentJson),

                        // F-041: the plain map plus the password values the page never sees plus a run
                        // selection's two convenience variables. One map crosses the boundary, so the
                        // worker needs no second rule. F-026: a downstream run's chain facts ride on top.
                        MergeUpstreamFacts(candidate, BuildClaimParameters(candidate))),

                    // F-036 (review M1): a non-positive number is "no number", never `#0`.
                    candidate.Number is > 0 ? candidate.Number : null,

                    // F-041: the names the worker must register with its redaction filter. Names only -
                    // the values are in the map above, and a name is not a secret.
                    PasswordParameterNames(candidate),

                    // F-041: the file parameters' names. The bytes travel over their own run-scoped route,
                    // because a claim carries names and never material (card F-040).
                    ParameterNamesOfType(candidate, ProjectParameterTypes.File),

                    // F-031: the project's explicit "a post failure fails the run" switch. Read from the
                    // project at claim time - the worker is the one that executes the post phase, so it is the
                    // one that has to know what the run should report.
                    candidate.Project.PostFailureFailsRun);
            }
        }

        return RunClaimResult.NoRun();
    }

    /// <summary>
    /// Cards F-028 + F-029: whether one candidate is claimable for its project's own reasons.
    /// <para>
    /// <b>Three answers, in the card's documented order.</b> A disabled project's queued run is never
    /// claimed (F-028 - defence in depth: disabling cancels the queue, so such a row should not exist);
    /// a run still inside its project's quiet period is not claimable yet (F-029, measured from the run's
    /// own <c>CreatedAt</c>; a trigger inside the period is an ordinary queued run, not a coalesced one);
    /// and a project at its <c>MaxConcurrentRuns</c> leaves the run queued for later.
    /// </para>
    /// <para>
    /// <b>The concurrency limit is decided under the project row lock, not before it.</b> Counting and
    /// then leasing in two steps is exactly the check-then-act window B-002 closed at the worker level;
    /// here the racing counterparts are two <em>different</em> workers claiming two different runs of one
    /// project, which the worker row lock cannot serialize. So the limit's count runs inside the caller's
    /// transaction after <see cref="ZenkinsDbContext.LockProjectForTriggerAsync"/>, and only the lock
    /// holder can raise the count - a claim is the only statement that adds an assigned run. Projects
    /// without a limit pay neither the lock nor the count.
    /// </para>
    /// <para>
    /// <b>Locks are taken in the queue's own order.</b> The candidate list is a subsequence of one global
    /// <c>(CreatedAt, Id)</c> order, so two claims attempt the same projects in the same relative order -
    /// no lock cycle is possible.
    /// </para>
    /// </summary>
    private static async Task<bool> ProjectAllowsClaimAsync(
        ZenkinsDbContext context,
        BuildRun candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var project = candidate.Project;

        // F-028, defence in depth - NOT the guarantee. `project` is the candidate snapshot read before
        // this transaction took any project lock, so this check can be stale; what actually keeps a
        // disabled project's runs from being built is that the disable cancels every Queued run in the
        // same transaction as its flag, and the lease CAS below only matches `Status == Queued` (see
        // `ZenkinsDbContext.LockProjectForTriggerAsync` for the two interleavings). Checking the snapshot
        // here only saves the work of considering such a candidate at all.
        if (ProjectTriggerGuard.DisabledRefusal(project.Disabled) is not null)
        {
            return false;
        }

        // F-029: the quiet period. Measured against the run's own creation instant, in memory (SQLite
        // cannot compare DateTimeOffset in SQL) - and time only moves forward, so a run inside its
        // period can never become claimable inside this same transaction.
        if (project.QuietPeriodSeconds > 0
            && candidate.CreatedAt.AddSeconds(project.QuietPeriodSeconds) > now)
        {
            return false;
        }

        // F-029: no limit, no lock, no count - today's behaviour for every project that never opened the
        // limits form.
        if (project.MaxConcurrentRuns is not { } maxConcurrentRuns)
        {
            return true;
        }

        if (await context.LockProjectForTriggerAsync(project.Id, cancellationToken) == 0)
        {
            // The project was deleted between the candidate read and the lock.
            return false;
        }

        // "Active" is the same predicate the worker's own slot count uses: Running, plus
        // claimed-but-not-yet-accepted runs (they hold the slot until the lease sweeper clears an expired
        // lease or the worker accepts). Counted under the project lock, so count + one lease cannot cross
        // the limit.
        var active = await context.BuildRuns.CountAsync(
            run => run.ProjectId == project.Id
                && (run.Status == BuildRunStatus.Running
                    || (run.Status == BuildRunStatus.Queued && run.WorkerId != null)),
            cancellationToken);
        return active < maxConcurrentRuns;
    }

    /// <summary>
    /// Reports that the worker accepted a run it holds a valid lease for: the single
    /// <c>Queued → Running</c> transition, start time recorded, lease cleared (after acceptance
    /// there is no lease - cancellation F-009 and offline detection F-010 own the rest).
    /// <para>
    /// <b>F-013's accepted-report extension.</b> <paramref name="gitCommit"/> carries the exact commit
    /// the worker checked out (<c>git rev-parse HEAD</c>), or <see langword="null"/> for a run with no
    /// repository. It is validated as an object name (never a branch label, never a revision
    /// expression) and stamped through the guarded compare-and-set
    /// <see cref="ZenkinsDbContext.TryStampGitCommitAsync"/> - once. The column is never derived from
    /// the branch and never rewritten: a re-delivered accept, or a second accept, leaves the first
    /// value in place.
    /// </para>
    /// <para>
    /// <b>The transition and the commit are one transaction (review M6).</b> They are two statements -
    /// an entity update for the state and a guarded <c>ExecuteUpdate</c> for the commit - so without a
    /// transaction an interruption between them would leave the run <c>Running</c> with
    /// <c>GitCommit</c> NULL. That state is unrecoverable by design: <c>Running → Running</c> is not a
    /// legal transition, so every retried accepted report is a 409 and the missing commit could never be
    /// filled in. Committing both together means an interrupted attempt changes nothing at all - the run
    /// stays <c>Queued</c> with its lease intact - so the worker's retry completes it.
    /// </para>
    /// </summary>
    public async Task<RunReportResult> AcceptAsync(
        string? plaintextToken,
        Guid runId,
        string? gitCommit,
        CancellationToken cancellationToken = default)
    {
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunReportResult.InvalidToken();
        }

        try
        {
            // AsNoTracking on purpose: the transition below is a single guarded UPDATE, so the
            // tracked entity must never be able to write a stale status afterwards.
            var run = await context.BuildRuns.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == runId,
                cancellationToken);
            if (run is null)
            {
                // Foreign or unknown id: the same 404, no existence oracle.
                return RunReportResult.NotFound();
            }

            // Authentication and run ownership are settled above; the commit shape is validated before
            // the transition so a malformed report cannot leave a half-accepted run behind.
            if (gitCommit is not null && !GitCommitValidation.IsValid(gitCommit))
            {
                return RunReportResult.Invalid(GitCommitValidation.InvalidCode);
            }

            var now = DateTimeOffset.UtcNow;

            // Only a run this worker holds a live lease for can be accepted. The transition guard
            // comes first so an already-accepted run reports invalid-transition (not the misleading
            // lease-expired its cleared lease would produce).
            if (run.WorkerId != worker.Id)
            {
                return RunReportResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            if (!BuildRunStateMachine.CanTransition(run.Status, BuildRunStatus.Running))
            {
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            // F-017 (review M1): a webhook run already records the commit the push named, and the claim
            // handed that same commit to this worker to pin. A report that disagrees is refused - never
            // silently overwritten (the column is write-once) and never silently accepted (the run would
            // claim to have built one tree while the worker built another). The check runs BEFORE any
            // write, so a refused report leaves the run exactly as it was.
            if (run.GitCommit is not null
                && gitCommit is not null
                && !string.Equals(run.GitCommit, gitCommit, StringComparison.Ordinal))
            {
                return RunReportResult.Conflict(RunErrorCodes.GitCommitMismatch);
            }

            if (run.LeaseExpiresAt is null || run.LeaseExpiresAt <= now)
            {
                return RunReportResult.Conflict(RunErrorCodes.LeaseExpired);
            }

            // F-009: the execution deadline is frozen at accept - the run's per-run override or the
            // server default. The deadline sweeper never touches the run before this instant, and a
            // later configuration change never moves an accepted run's deadline.
            var timeout = run.TimeoutSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : options.DefaultTimeout;
            var deadlineAt = now + timeout;

            // F-013 + review M6: the transition and the commit stamp are ONE unit of work, or an
            // interruption between them would leave the run Running with GitCommit NULL - a state its
            // own guard (Running -> Running is not a legal transition, so every retry is a 409) makes
            // permanently unrecoverable. A failed attempt therefore changes nothing at all: the run
            // stays Queued with its lease intact, so the worker's retry completes it.
            //
            // F-014 closes F-013's check-then-act window: the transition itself is now the guarded
            // compare-and-set TryAcceptBuildRunAsync (WHERE restates Queued + this worker + a live
            // lease), so two concurrent accepted reports for one run cannot both win - the loser
            // updates zero rows and answers a conflict, exactly as the queued cancel does.
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var accepted = await context.TryAcceptBuildRunAsync(
                run.Id,
                worker.Id,
                now,
                deadlineAt,
                now,
                cancellationToken);
            if (accepted == 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                // Why did the CAS lose? A run that is no longer Queued was accepted (or canceled)
                // concurrently - a conflict either way; one that is still Queued lost its lease.
                var currentStatus = await context.BuildRuns
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == run.Id)
                    .Select(candidate => candidate.Status)
                    .SingleOrDefaultAsync(cancellationToken);
                return currentStatus == BuildRunStatus.Queued
                    ? RunReportResult.Conflict(RunErrorCodes.LeaseExpired)
                    : RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            string? recordedCommit = null;
            if (gitCommit is not null)
            {
                // The CAS's WHERE is "GitCommit IS NULL", so a second report (or a re-delivery of this
                // one) updates zero rows and the first value - the commit this run actually built -
                // survives. A zero-row answer inside this path means a concurrent accept already
                // stamped it; the recorded value is read back for the log rather than assumed.
                var stamped = await context.TryStampGitCommitAsync(run.Id, gitCommit, now, cancellationToken);
                recordedCommit = stamped == 1
                    ? gitCommit
                    : await context.BuildRuns
                        .Where(candidate => candidate.Id == run.Id)
                        .Select(candidate => candidate.GitCommit)
                        .SingleOrDefaultAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Run accepted. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} DeadlineAt={DeadlineAt} GitCommit={GitCommit}",
                workerToken.OrganizationId,
                run.Id,
                worker.Id,
                deadlineAt,
                recordedCommit);

            // F-014: the answer carries the frozen deadline, so the worker's definition phase runs
            // against the run's own wall clock instead of a worker-side guess.
            return RunReportResult.Success(BuildRunStatus.Running, deadlineAt);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Reports the definition of a run this worker executes (card F-014): the commit it resolved and
    /// the validated spec it froze - sent once, before the first step starts, so the run page can show
    /// the step list of a run that is only just beginning.
    /// <para>
    /// <b>Server-side gate, Worker-side strictness.</b> The Worker owns the field-path validation (it is
    /// the only side holding the Lua table); this method re-checks the envelope the issue asks for - the
    /// JSON parses, the schema version is known, the step list is 1..200 and the payload is under the
    /// size cap - so a buggy or hostile worker cannot store something the page would then have to
    /// trust.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> Both writes are compare-and-sets (the commit's <c>IS NULL</c>, the spec's
    /// <c>IS NULL</c>): a re-delivered report after a lost response updates nothing and still answers
    /// success while the run is Running with a spec - it must not turn a completed report into a 409.
    /// </para>
    /// </summary>
    public async Task<RunReportResult> ReportDefinitionAsync(
        string? plaintextToken,
        Guid runId,
        string? gitCommit,
        string? pipelineSpecJson,
        CancellationToken cancellationToken = default)
    {
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunReportResult.InvalidToken();
        }

        try
        {
            // F-014: two reports share this endpoint. The commit-only one (a null spec) lands right
            // after the clone, so a run whose definition fails still records what it checked out; the
            // second one adds the frozen spec before the first step starts.
            var hasSpec = !string.IsNullOrWhiteSpace(pipelineSpecJson);
            if (gitCommit is null && !hasSpec)
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            if (gitCommit is not null && !GitCommitValidation.IsValid(gitCommit))
            {
                return RunReportResult.Invalid(GitCommitValidation.InvalidCode);
            }

            if (hasSpec && !PipelineSpecJson.TryRead(pipelineSpecJson, out _, out var specError))
            {
                logger.LogWarning(
                    "Run definition refused. RunId={RunId} WorkerId={WorkerId} Reason={Reason}",
                    runId,
                    worker.Id,
                    specError);
                return RunReportResult.Invalid(RunErrorCodes.PipelineSpecInvalid);
            }

            var run = await context.BuildRuns.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == runId,
                cancellationToken);
            if (run is null)
            {
                return RunReportResult.NotFound();
            }

            if (run.WorkerId != worker.Id)
            {
                return RunReportResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            if (run.Status != BuildRunStatus.Running)
            {
                // A definition can only be frozen on a running run; anything else is a stale or
                // misdirected report.
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            // F-017 (review M1): the definition report is where a worker normally states the commit it
            // checked out. A webhook run was created with the pushed commit, and the claim told the worker
            // to pin exactly that; a report naming a different commit means the worker built something
            // else, which is a conflict, not a detail to absorb. Nothing is written on the refusal path -
            // no commit rewrite, no frozen spec - so the run keeps its recorded commit and stays
            // re-reportable.
            if (run.GitCommit is not null
                && gitCommit is not null
                && !string.Equals(run.GitCommit, gitCommit, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Run definition refused: the reported commit disagrees with the run's. RunId={RunId} WorkerId={WorkerId}",
                    run.Id,
                    worker.Id);
                return RunReportResult.Conflict(RunErrorCodes.GitCommitMismatch);
            }

            var now = DateTimeOffset.UtcNow;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            if (gitCommit is not null)
            {
                // The same once-only stamp the accept report uses (F-013): the first value - the commit
                // this run actually built - survives.
                await context.TryStampGitCommitAsync(run.Id, gitCommit, now, cancellationToken);
            }

            if (!hasSpec)
            {
                // Commit only: nothing was defined yet, so no spec is frozen.
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "Run commit recorded. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} GitCommit={GitCommit}",
                    workerToken.OrganizationId,
                    run.Id,
                    worker.Id,
                    gitCommit);
                return RunReportResult.Success(BuildRunStatus.Running, run.DeadlineAt);
            }

            var frozen = await context.FreezePipelineDefinitionAsync(
                run.Id,
                worker.Id,
                pipelineSpecJson!,
                now,
                cancellationToken);
            if (frozen == 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                // The spec column is write-once. A re-delivery of the SAME report (a retry after a lost
                // response) is the idempotent success; a DIFFERENT spec for the same run is refused -
                // answering 200 would silently accept a second, contradictory definition.
                var existing = await context.BuildRuns
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == run.Id)
                    .Select(candidate => candidate.PipelineSpecJson)
                    .SingleOrDefaultAsync(cancellationToken);
                if (existing is null)
                {
                    return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
                }

                return string.Equals(existing, pipelineSpecJson, StringComparison.Ordinal)
                    ? RunReportResult.Success(BuildRunStatus.Running, run.DeadlineAt)
                    : RunReportResult.Conflict(RunErrorCodes.PipelineSpecFrozen);
            }

            await transaction.CommitAsync(cancellationToken);

            // F-023: a frozen spec is evidence that the branch's tree contains the pipeline file. The
            // write is evidence-only and best effort: a registry hiccup must never turn a definition
            // report the worker can no longer retry into a failure.
            await RecordBranchEvidenceAsync(
                run.ProjectId,
                run.GitBranch,
                BranchPipelineStates.HasPipeline,
                gitCommit ?? run.GitCommit,
                cancellationToken);

            logger.LogInformation(
                "Run definition frozen. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} GitCommit={GitCommit} SpecBytes={SpecBytes}",
                workerToken.OrganizationId,
                run.Id,
                worker.Id,
                gitCommit,
                pipelineSpecJson!.Length);
            return RunReportResult.Success(BuildRunStatus.Running, run.DeadlineAt);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Records one step transition of a run this worker executes (card F-015, PRD §3.3.5). Every step
    /// is reported before it starts (<c>Running</c>) and again when it ends (<c>Success</c>/
    /// <c>Failed</c>/<c>Skipped</c>), so a run page opened mid-run shows completed, current and pending
    /// steps; a skipped step is reported too, with its reason, so the page never shows a step that
    /// silently vanished.
    /// <para>
    /// <b>Guards, in this order.</b> The token (bare 401), the payload's closed vocabulary (400), the
    /// run's existence through the tenant filter (404 - a foreign id and a missing one are the same
    /// answer), the run's ownership and state (409), the frozen spec (409: a step only exists inside a
    /// definition), the position and name against that spec (400), then ordering across the rows already
    /// recorded (409). A position that already has a row is never inserted twice: the same report
    /// re-delivered is idempotent, and a terminal step refuses a further transition.
    /// </para>
    /// <para>
    /// <b>Timestamps are the server's.</b> Started/ended instants follow the run's own discipline
    /// (<c>BuildRun.StartedAt</c> is stamped at accept), so a skewed worker clock can never make a step
    /// end before it started. A skipped step gets no start time: nothing ran.
    /// </para>
    /// </summary>
    public async Task<RunStepResult> ReportStepAsync(
        string? plaintextToken,
        Guid runId,
        RunStepReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Authentication before payload validation (the accepted/completed discipline).
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunStepResult.InvalidToken();
        }

        try
        {
            // 1. The payload's shape: a status outside the closed vocabulary, a failure reason that does
            //    not belong to it, or an exit code a non-exiting state claims are all contract mismatches.
            var status = report.Status;
            if (!PipelineStepStatus.IsReportable(status) || report.Index < 1)
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            if (!PipelineStepFailureReasons.IsLegal(status!, report.FailureReason))
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            if (status == PipelineStepStatus.Success && report.ExitCode is not (null or 0))
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            if (status is PipelineStepStatus.Running or PipelineStepStatus.Skipped && report.ExitCode is not null)
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            // F-032: the attempt count is bounded by the schema's own retry cap, and only a terminal report
            // carries one ("Running" has no attempt to count yet). Null means the default, one attempt.
            if (report.Attempts is { } reportedAttempts
                && (reportedAttempts < PipelineSpecSchema.MinStepRetry
                    || reportedAttempts > PipelineSpecSchema.MaxStepRetry
                    || status == PipelineStepStatus.Running))
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            // 2. The run: this worker's, still Running, and carrying the frozen spec the step belongs to.
            //    The lookup goes through the tenant query filter, so a foreign run id is the same 404.
            var run = await context.BuildRuns
                .AsNoTracking()
                .Where(candidate => candidate.Id == runId)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.OrganizationId,
                    candidate.WorkerId,
                    candidate.Status,
                    candidate.PipelineSpecJson,
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (run is null)
            {
                return RunStepResult.NotFound();
            }

            if (run.WorkerId != worker.Id)
            {
                return RunStepResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            if (run.Status != BuildRunStatus.Running)
            {
                return RunStepResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            if (!PipelineSpecJson.TryRead(run.PipelineSpecJson, out var spec, out _) || spec is null)
            {
                // A step only exists inside a definition: without one there is nothing to index into.
                return RunStepResult.Conflict(RunErrorCodes.StepUnknown);
            }

            // F-031: a post step's position continues after the main list, and which outcome set runs is the
            // run's own outcome - the server cannot predict it. So beyond the main list a position is accepted
            // for any *declared* post step (by its frozen `post/…` name), while the main list stays positional.
            // The ordering check below still makes (RunId, Index) monotonic and write-once.
            string? declaredName = null;
            if (report.Index <= spec.Steps.Length)
            {
                if (string.Equals(report.Name, spec.Steps[report.Index - 1].Name, StringComparison.Ordinal))
                {
                    declaredName = spec.Steps[report.Index - 1].Name;
                }
            }
            else if (spec.Post is { } post
                && report.Index <= spec.Steps.Length + post.Count
                && post.Find(report.Name) is not null)
            {
                declaredName = report.Name;
            }

            if (declaredName is null)
            {
                // The position must exist in the frozen spec and agree with it; that is what makes
                // (RunId, Index) an ordering authority rather than a bucket of worker-chosen rows.
                return RunStepResult.Invalid(RunErrorCodes.StepUnknown);
            }

            // 3. Ordering and idempotence, from the rows already recorded for this run.
            var existing = await context.BuildSteps
                .Where(step => step.RunId == runId)
                .ToListAsync(cancellationToken);
            var row = existing.SingleOrDefault(candidate => candidate.Index == report.Index);
            var highestIndex = existing.Count == 0 ? 0 : existing.Max(candidate => candidate.Index);
            var now = DateTimeOffset.UtcNow;

            if (row is null)
            {
                if (report.Index <= highestIndex)
                {
                    return RunStepResult.Conflict(RunErrorCodes.StepOutOfOrder);
                }

                row = new BuildStep
                {
                    // The run's own tenant, which the filter has already proved is the active one.
                    OrganizationId = run.OrganizationId,
                    RunId = runId,
                    Index = report.Index,
                    Name = declaredName,
                    Status = status!,
                    ExitCode = report.ExitCode,
                    FailureReason = report.FailureReason,

                    // F-032: the attempt count rides on the terminal report; absent means one attempt.
                    Attempts = report.Attempts ?? 1,

                    // A skipped step never ran, so it has no start instant; a coalesced terminal report
                    // (a single Success/Failed without a preceding Running) starts and ends now.
                    StartedAt = status == PipelineStepStatus.Skipped ? null : now,
                    EndedAt = status == PipelineStepStatus.Skipped ? now
                        : PipelineStepStatus.IsTerminal(status) ? now : null,
                };
                context.BuildSteps.Add(row);
            }
            else if (string.Equals(row.Status, status, StringComparison.Ordinal))
            {
                // A re-delivery of the same report (a lost response): the stored row is the answer.
                return RunStepResult.Success(row.Id, row.Status, run.Status);
            }
            else if (PipelineStepStatus.IsTerminal(row.Status))
            {
                return RunStepResult.Conflict(RunErrorCodes.StepAlreadyFinished);
            }
            else
            {
                // Running -> terminal (or Running -> Running, a retried progress report): the first
                // recorded start instant survives.
                row.Status = status!;
                row.ExitCode = report.ExitCode;
                row.FailureReason = report.FailureReason;
                row.Attempts = report.Attempts ?? row.Attempts;
                row.StartedAt ??= status == PipelineStepStatus.Skipped ? null : now;
                if (PipelineStepStatus.IsTerminal(status))
                {
                    row.EndedAt = now;
                }
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "BuildSteps"))
            {
                // Two concurrent reports of the same position: one row wins the unique (RunId, Index)
                // index. The loser answers what is stored instead of inventing a second record.
                context.ChangeTracker.Clear();
                var stored = await context.BuildSteps
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.RunId == runId && candidate.Index == report.Index,
                        cancellationToken);
                if (stored is null)
                {
                    throw;
                }

                return string.Equals(stored.Status, status, StringComparison.Ordinal)
                    ? RunStepResult.Success(stored.Id, stored.Status, run.Status)
                    : RunStepResult.Conflict(RunErrorCodes.StepOutOfOrder);
            }

            logger.LogInformation(
                "Run step recorded. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} StepIndex={StepIndex} StepStatus={StepStatus} ExitCode={ExitCode} Attempts={Attempts}",
                run.OrganizationId,
                runId,
                worker.Id,
                report.Index,
                row.Status,
                row.ExitCode,
                row.Attempts);
            return RunStepResult.Success(row.Id, row.Status, run.Status);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Reports the end of a run this worker executes (card F-014 extends F-006's exit code with a
    /// closed failure reason):    /// <list type="bullet">
    /// <item>exit 0, no reason → <c>Success</c>.</item>
    /// <item>a non-zero exit (optionally with <c>step-failed</c>) → <c>Failed</c> with the code recorded
    /// - the code is the record, and the reason adds which phase produced it.</item>
    /// <item>no exit code but a reason (<c>pipeline-missing</c>, <c>git-unavailable</c>, …) →
    /// <c>Failed</c> with no invented code: nothing ran, so there is no code to record.</item>
    /// </list>
    /// End time is recorded; duration is derived from start and end on display.
    /// </summary>
    public async Task<RunReportResult> CompleteAsync(
        string? plaintextToken,
        Guid runId,
        int? exitCode,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        // Authentication before payload validation (F-004 review fix): a tokenless caller gets
        // 401 even with an invalid payload, so 400s only ever reach an authenticated worker.
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunReportResult.InvalidToken();
        }

        try
        {
            if (exitCode is null && failureReason is null)
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            // The vocabulary is closed: a reason the server does not know is a contract mismatch, not
            // something to store and hope the UI can render.
            if (failureReason is not null && !PipelineFailureReasons.IsWorkerReportable(failureReason))
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            // A successful exit with a failure reason contradicts itself; refuse it rather than guess
            // which half the worker meant.
            if (exitCode == 0 && failureReason is not null)
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            var run = await context.BuildRuns.SingleOrDefaultAsync(
                candidate => candidate.Id == runId,
                cancellationToken);
            if (run is null)
            {
                return RunReportResult.NotFound();
            }

            // Only the executing worker can complete the run, and only from Running.
            if (run.WorkerId != worker.Id)
            {
                return RunReportResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            var target = exitCode == 0 && failureReason is null
                ? BuildRunStatus.Success
                : BuildRunStatus.Failed;
            if (!BuildRunStateMachine.CanTransition(run.Status, target))
            {
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            var now = DateTimeOffset.UtcNow;
            run.Status = target;
            run.ExitCode = exitCode;
            run.FailureReason = failureReason;
            run.EndedAt = now;

            // Card F-026: the downstream triggers this completion owes are staged on the SAME context, so they
            // commit with the terminal status or not at all. A Server that dies here leaves the obligation on disk;
            // the dispatcher settles it after commit (or on the next start).
            await DownstreamTriggers.StageAsync(context, run, target, now, cancellationToken);

            // Card F-039: the completion notifications this terminal state owes ride the same outbox and the same
            // unit of work - the event (success/recovered/failed) is decided here, where the previous run is still
            // the previous run.
            await CompletionNotifications.StageAsync(context, run, target, cancellationToken);

            await context.SaveChangesAsync(cancellationToken);

            // F-023: `pipeline-missing` is the worker's proof that this branch's checkout has no
            // pipeline file. It is the only run-side path that may record `no-pipeline`, and it records
            // the commit it is evidence about - so the state can never be inferred from a missing run or
            // a failed probe. Best effort: the run is already terminal.
            if (string.Equals(failureReason, PipelineFailureReasons.PipelineMissing, StringComparison.Ordinal))
            {
                await RecordBranchEvidenceAsync(
                    run.ProjectId,
                    run.GitBranch,
                    BranchPipelineStates.NoPipeline,
                    run.GitCommit,
                    cancellationToken);
            }

            // The run is terminal: flush the ingest tail and evict its buffer, so a run recorded
            // as finished never leaves lines behind nor keeps server memory (F-008's completed
            // path mirrors F-007's cleanup-before-report discipline on the server side).
            await runLogIngest.CompleteRunAsync(run.Id);

            // Card F-038: the project's retention policy is evaluated after this terminal state - off the request
            // path, so trimming history can never slow down or fail a worker's completed report.
            terminalSignal.Pulse(run.ProjectId);

            logger.LogInformation(
                "Run completed. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} Status={Status} ExitCode={ExitCode} FailureReason={FailureReason}",
                workerToken.OrganizationId,
                run.Id,
                worker.Id,
                target,
                exitCode,
                failureReason);
            return RunReportResult.Success(target);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Records evidence a run produced about its branch (card F-023): a frozen definition proves the
    /// pipeline file exists, and <c>pipeline-missing</c> proves it does not. <b>Only a multibranch
    /// project is recorded</b> (a single-branch project's behaviour, including its branch rows, is
    /// unchanged), and only a storable branch identity. The write is its own short unit of work and
    /// never throws: the run or the definition report it follows is already durable, and a registry
    /// failure is logged instead of rewriting that outcome.
    /// </summary>
    private async Task RecordBranchEvidenceAsync(
        Guid projectId,
        string branch,
        string state,
        string? commit,
        CancellationToken cancellationToken)
    {
        if (!BranchIdentity.IsStorable(branch) || tenantContext.OrganizationId is not { } organizationId)
        {
            return;
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var discovery = await context.Projects
                .AsNoTracking()
                .Where(project => project.Id == projectId)
                .Select(project => project.BranchDiscovery)
                .SingleOrDefaultAsync(cancellationToken);
            if (!BranchDiscoveryModes.IsMultibranch(discovery))
            {
                return;
            }

            await BranchService.RecordObservationAsync(
                context,
                organizationId,
                projectId,
                branch,
                state,
                commit,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception failure)
        {
            logger.LogWarning(
                failure,
                "Branch evidence not recorded. OrganizationId={OrganizationId} ProjectId={ProjectId} Branch={Branch} State={State}",
                organizationId,
                projectId,
                branch,
                state);
        }
    }

    // ---------------------------------------------------------------------
    // Lease sweeper (background service and tests)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns every claimed-but-never-accepted run whose lease expired to the queue (clears the
    /// lease columns). Spans tenants, hence the system scope. The expiry check happens in memory
    /// (SQLite cannot compare DateTimeOffset values in SQL), and each requeue is then an exact
    /// compare-and-set on the expired lease value - so an accepted/Running run (no lease) is
    /// unreachable no matter how late the sweep fires, and a lease stamped after the read is never
    /// clobbered. Returns the number of requeued runs.
    /// </summary>
    public async Task<int> SweepExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Queued runs that still carry a lease (accepted runs have none; terminal runs never requeue).
        var leased = await context.BuildRuns
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(run => run.Status == BuildRunStatus.Queued && run.LeaseExpiresAt != null)
            .Select(run => new { run.Id, run.LeaseExpiresAt })
            .ToListAsync(cancellationToken);

        var requeued = 0;
        foreach (var run in leased)
        {
            if (run.LeaseExpiresAt is not { } leaseExpiresAt || leaseExpiresAt > now)
            {
                continue;
            }

            requeued += await context.RequeueExpiredBuildRunLeaseAsync(
                run.Id,
                leaseExpiresAt,
                now,
                cancellationToken);
        }

        if (requeued > 0)
        {
            logger.LogInformation("Lease sweeper requeued {Count} expired run(s).", requeued);
        }

        return requeued;
    }

    // ---------------------------------------------------------------------
    // User cancel (F-009): the run page's cancel button
    // ---------------------------------------------------------------------

    /// <summary>
    /// Cancels a run on behalf of an organization member (Owner or Member - the trigger right).
    /// <list type="bullet">
    /// <item><c>Queued</c>: a guarded compare-and-set flips it to <c>Canceled</c> atomically. Under
    /// a concurrent claim storm exactly one of the two statements can win the row (both WHEREs
    /// require <c>Status == Queued</c>); a run canceled after a claim but before its accept makes
    /// the leaseholder's accepted report a 409, so the process is never started.</item>
    /// <item><c>Running</c>: the run is marked cancel-requested (reason
    /// <c>canceled-by-user &lt;user&gt;</c>, first request wins) and the worker is signaled over
    /// its hub connection (the heartbeat response is the fallback). The run stays Running until
    /// the worker kills the tree and reports through the canceled endpoint.</item>
    /// <item>Terminal: a friendly no-op - nothing changes, the page says the run had finished.
    /// Double-cancel is the same no-op.</item>
    /// </list>
    /// A foreign-organization run is indistinguishable from a missing one (404, no existence
    /// oracle); a non-member gets 403.
    /// </summary>
    public async Task<RunCancelResult> CancelAsync(
        string actingUserId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return RunCancelResult.Forbidden(
                "You are not a member of an organization, so there is no run to cancel.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.OrganizationMembers.AnyAsync(
                member => member.UserId == actingUserId,
                cancellationToken))
        {
            return RunCancelResult.Forbidden("You are not a member of the active organization.");
        }

        var actor = await context.Users
            .Where(user => user.Id == actingUserId)
            .Select(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);

        return await CancelRunCoreAsync(
            runId,
            RunCancelReasons.ForUser(actor ?? actingUserId),
            $"ByUserId={actingUserId}",
            cancellationToken);
    }

    /// <summary>
    /// The organization API's cancel (card F-027): the same cancellation routine the run page's button
    /// runs - the same queued compare-and-set, the same running cancel-request path, the same friendly
    /// no-op for a terminal run, the same 404 for a foreign id. The caller proved its authority with a
    /// token carrying the <c>cancel</c> scope (the endpoint's policy), so there is no membership check
    /// here; the reason names the token instead of a person, because a person was not involved.
    /// </summary>
    public async Task<RunCancelResult> CancelForApiAsync(
        Guid runId,
        string tokenName,
        CancellationToken cancellationToken = default)
    {
        if (tenantContext.OrganizationId is null)
        {
            return RunCancelResult.Forbidden(
                "No organization is selected for this request, so there is no run to cancel.");
        }

        return await CancelRunCoreAsync(
            runId,
            RunCancelReasons.ForApi(tokenName),
            $"ByApiToken={tokenName}",
            cancellationToken);
    }

    /// <summary>
    /// F-009's cancel, from the run lookup to the last write - shared by the page's button and the API so
    /// the queued race (F-009's re-read) has exactly one implementation. The caller has already resolved
    /// the actor and the tenant; nothing here checks authority.
    /// </summary>
    private async Task<RunCancelResult> CancelRunCoreAsync(
        Guid runId,
        string reason,
        string actorLog,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var run = await context.BuildRuns.SingleOrDefaultAsync(
            candidate => candidate.Id == runId,
            cancellationToken);
        if (run is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return RunCancelResult.NotFound();
        }

        var now = DateTimeOffset.UtcNow;

        if (run.Status is BuildRunStatus.Queued or BuildRunStatus.Running)
        {
            // Still non-terminal: the only two states with outgoing edges.
            if (run.Status == BuildRunStatus.Queued)
            {
                var canceled = await context.TryCancelQueuedBuildRunAsync(runId, reason, now, cancellationToken);
                if (canceled == 1)
                {
                    logger.LogInformation(
                        "Queued run canceled. OrganizationId={OrganizationId} RunId={RunId} {Actor}",
                        run.OrganizationId,
                        runId,
                        actorLog);

                    // Card F-038: Canceled is terminal, so the project's retention gate runs for it.
                    terminalSignal.Pulse(run.ProjectId);
                    return RunCancelResult.Canceled();
                }

                // Lost the race: the run left Queued between the read and the CAS. Re-read and
                // fall through - it is either Running now (cancel-request path) or terminal
                // (the friendly no-op). The re-read must bypass the change tracker: this context
                // already tracks the row from the read above, and the queued-cancel CAS
                // (ExecuteUpdate) writes the database without touching tracked state, so a tracked
                // re-read hands back the stale Queued snapshot - the Running branch never runs, the
                // cancel is dropped behind a 302, and the run executes on with no recorded cancel
                // request (the windows gate recorded exactly that race).
                var reread = await context.BuildRuns
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);
                if (reread is null)
                {
                    // The run's project (and with it the run) is gone; nothing left to cancel.
                    return RunCancelResult.NotFound();
                }

                run = reread;
            }

            if (run.Status == BuildRunStatus.Running)
            {
                // First request wins (CancelRequestedAt IS NULL in the CAS): a double cancel or a
                // user cancel racing the deadline sweeper records one reason and never overwrites.
                await context.TryRequestBuildRunCancelAsync(runId, reason, now, cancellationToken);
                if (run.WorkerId is { } workerId)
                {
                    await BroadcastCancelAsync(workerId, runId, reason, cancellationToken);
                }

                logger.LogInformation(
                    "Running run cancel requested. OrganizationId={OrganizationId} RunId={RunId} {Actor}",
                    run.OrganizationId,
                    runId,
                    actorLog);
                return RunCancelResult.CancelRequested();
            }
        }

        // Terminal (including a run that turned terminal during the race above): friendly no-op.
        return RunCancelResult.AlreadyFinished();
    }

    // ---------------------------------------------------------------------
    // Worker canceled report (F-009)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Records the terminal state of a run its worker killed on a server cancel request. The
    /// worker's outcome selects the mapping (the server owns it): <c>canceled</c> →
    /// <c>Canceled</c> with the recorded cancel reason (<c>canceled-by-user &lt;user&gt;</c>),
    /// <c>timeout</c> → <c>Failed</c> with reason <c>timeout</c>. One endpoint for both so a kill
    /// never needs a second action.
    /// <para>
    /// Guards: worker token (bare 401), tenant-filtered run lookup (foreign id 404), own-run check
    /// (409), a pending cancel request (409 <c>cancel-not-requested</c> - the worker may only kill
    /// what the server asked it to kill), and the compare-and-set on <c>Status == Running</c>
    /// (409 - the report racing a Lost transition loses and answers honestly).
    /// </para>
    /// </summary>
    public async Task<RunReportResult> ReportCanceledAsync(
        string? plaintextToken,
        Guid runId,
        string? outcome,
        CancellationToken cancellationToken = default)
    {
        // Authentication before payload validation (the accepted/completed pattern).
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunReportResult.InvalidToken();
        }

        try
        {
            var mappedOutcome = outcome switch
            {
                null or "" or RunCanceledOutcomes.Canceled => RunCanceledOutcomes.Canceled,
                RunCanceledOutcomes.Timeout => RunCanceledOutcomes.Timeout,
                _ => null,
            };
            if (mappedOutcome is null)
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            var run = await context.BuildRuns.SingleOrDefaultAsync(
                candidate => candidate.Id == runId,
                cancellationToken);
            if (run is null)
            {
                return RunReportResult.NotFound();
            }

            if (run.WorkerId != worker.Id)
            {
                return RunReportResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            if (run.CancelRequestedAt is null)
            {
                return RunReportResult.Conflict(RunErrorCodes.CancelNotRequested);
            }

            var (target, failureReason) = mappedOutcome == RunCanceledOutcomes.Timeout
                ? (BuildRunStatus.Failed, RunCancelReasons.Timeout)
                : (BuildRunStatus.Canceled, run.CancelReason ?? RunCancelReasons.CanceledByUser);

            var now = DateTimeOffset.UtcNow;

            // Card F-039: a timeout is a failure, so it owes a notification - and it owes it with the same guarantee
            // as a completed run. The CAS below writes through ExecuteUpdate, so the staging needs an explicit
            // transaction to share: the terminal status and its obligation commit together or not at all.
            await using var terminalTransaction =
                await context.Database.BeginTransactionAsync(cancellationToken);

            var reported = await context.TryReportCanceledBuildRunAsync(
                runId,
                worker.Id,
                target,
                failureReason,
                now,
                cancellationToken);
            if (reported != 1)
            {
                // The run left Running between the read and the CAS (a Lost transition won).
                await terminalTransaction.RollbackAsync(cancellationToken);
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            await CompletionNotifications.StageAsync(context, run, target, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await terminalTransaction.CommitAsync(cancellationToken);

            // Terminal: flush the ingest tail and evict the run's buffer (the same completed-path
            // discipline F-008 established; closes the Canceled-run gap its log recorded).
            await runLogIngest.CompleteRunAsync(run.Id);

            // Card F-038: the retention evaluation the terminal state owes (see CompleteAsync's note).
            terminalSignal.Pulse(run.ProjectId);

            logger.LogInformation(
                "Run canceled by its worker. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} Status={Status} Reason={Reason}",
                workerToken.OrganizationId,
                runId,
                worker.Id,
                target,
                failureReason);
            return RunReportResult.Success(target);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------------
    // Deadline sweep (F-009) and offline sweep (F-010) - background services
    // ---------------------------------------------------------------------

    /// <summary>The deadline sweep's effects: how many runs got a timeout cancel request, how many
    /// were honestly marked Lost after the grace period.</summary>
    public sealed record DeadlineSweepResult(int CancelRequested, int Lost);

    /// <summary>
    /// The deadline sweeper (F-009): every <c>Running</c> run past its <see cref="Entities.BuildRun.DeadlineAt"/>
    /// gets a cancel request (reason <c>timeout</c>, signaled like a user cancel); a run still
    /// Running after the grace period (deadline + 2 × <c>Workers:OfflineAfterSeconds</c>, the PM
    /// ruling that closes F-007's online-but-unreportable gap) is marked <c>Lost</c> with reason
    /// <c>run-deadline-exceeded-result-unknown</c> - never Failed, never requeued: the result is
    /// unknown, not a failure. Before the deadline the sweep changes nothing.
    /// <para>
    /// Spans tenants (system scope). The expiry comparison happens in memory - SQLite cannot
    /// compare DateTimeOffset values in SQL (the lease sweep's discovery) - and each per-run write
    /// is a compare-and-set, so a run that leaves Running during the sweep is never touched.
    /// </para>
    /// </summary>
    public async Task<DeadlineSweepResult> SweepDeadlinesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var grace = TimeSpan.FromSeconds(2 * workerMonitorOptions.OfflineAfterSeconds);
        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var running = await context.BuildRuns
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(run => run.Status == BuildRunStatus.Running)
            .Select(run => new { run.Id, run.ProjectId, run.WorkerId, run.DeadlineAt, run.CancelRequestedAt })
            .ToListAsync(cancellationToken);

        var result = new DeadlineSweepResult(0, 0);
        foreach (var run in running)
        {
            if (run.DeadlineAt is not { } deadline || deadline > now)
            {
                continue;
            }

            if (deadline + grace > now)
            {
                // Within the grace period: ask the worker to kill the run (timeout). The CAS only
                // records the request when none is pending and the run is still Running.
                var requested = await context.TryRequestBuildRunCancelAcrossTenantsAsync(
                    run.Id,
                    RunCancelReasons.Timeout,
                    now,
                    cancellationToken);
                if (requested == 1)
                {
                    result = result with { CancelRequested = result.CancelRequested + 1 };
                    if (run.WorkerId is { } workerId)
                    {
                        await BroadcastCancelAsync(workerId, run.Id, RunCancelReasons.Timeout, cancellationToken);
                    }
                }
            }
            else
            {
                // Grace over, no terminal report: the honest unknown. A Lost run never requeues.
                var lost = await context.TryTerminateRunningBuildRunAsync(
                    run.Id,
                    BuildRunStatus.Lost,
                    RunFailureReasons.RunDeadlineExceededResultUnknown,
                    now,
                    cancellationToken);
                if (lost == 1)
                {
                    result = result with { Lost = result.Lost + 1 };
                    await runLogIngest.CompleteRunAsync(run.Id);

                    // Card F-038: Lost is terminal too, so the project's retention gate runs for it.
                    terminalSignal.Pulse(run.ProjectId);
                }
            }
        }

        if (result.CancelRequested > 0 || result.Lost > 0)
        {
            logger.LogInformation(
                "Deadline sweep: {CancelRequested} timeout cancel request(s), {Lost} run(s) marked Lost after the grace period.",
                result.CancelRequested,
                result.Lost);
        }

        return result;
    }

    /// <summary>
    /// The offline sweep (F-010): every Online worker whose last heartbeat is older than the
    /// threshold is flipped Offline through a compare-and-set, and only the sweep that wins a
    /// worker's flip marks that worker's <c>Running</c> runs <c>Lost</c> (reason
    /// <c>worker-offline</c>) - the exactly-once guarantee for concurrent ticks. Claimed-but-not-
    /// accepted leases are deliberately left alone: the lease sweeper requeues them (they never
    /// executed, so a requeue is not a retry), and a Lost marking there would be a lie.
    /// <para>
    /// Spans tenants (system scope); the staleness comparison happens in memory (SQLite cannot
    /// compare DateTimeOffset values in SQL). A worker with no heartbeat timestamp is never
    /// flipped - unknown data must not produce an Offline. The flip's compare-and-set also carries
    /// the snapshot's heartbeat value as its staleness evidence: a fresh heartbeat that lands
    /// between the snapshot read and the flip leaves zero rows, the tick skips that worker's runs
    /// entirely (no fake Offline, no wrong Lost), and the next tick re-evaluates it from a fresh
    /// snapshot.
    /// </para>
    /// </summary>
    public async Task<OfflineSweepResult> SweepOfflineWorkersAsync(
        TimeSpan offlineAfter,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var onlineWorkers = await context.Workers
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(worker => worker.Status == WorkerStatus.Online)
            .Select(worker => new { worker.Id, worker.OrganizationId, worker.LastHeartbeatAt })
            .ToListAsync(cancellationToken);

        var result = new OfflineSweepResult(0, 0);
        foreach (var worker in onlineWorkers)
        {
            if (worker.LastHeartbeatAt is not { } lastHeartbeat || lastHeartbeat > now - offlineAfter)
            {
                // Inside the threshold (or no heartbeat data at all): no flapping on one beat.
                continue;
            }

            var offlined = await context.TryMarkWorkerOfflineAsync(
                worker.Id,
                lastHeartbeat,
                now,
                cancellationToken);
            if (offlined != 1)
            {
                // Another sweep already flipped the worker, or a fresh heartbeat landed after the
                // snapshot read (the CAS's observed-heartbeat evidence no longer matches): this
                // tick skips the worker and its runs - the next tick re-evaluates from a fresh
                // snapshot.
                continue;
            }

            result = result with { WorkersOfflined = result.WorkersOfflined + 1 };

            // Exactly the runs still Running on the lost worker become Lost - the claimed-but-
            // unaccepted lease stays Queued for the lease sweeper (F-010's decision).
            var runningIds = await context.BuildRuns
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(run => run.WorkerId == worker.Id && run.Status == BuildRunStatus.Running)
                .Select(run => new { run.Id, run.ProjectId })
                .ToListAsync(cancellationToken);
            foreach (var run in runningIds)
            {
                var lost = await context.TryTerminateRunningBuildRunAsync(
                    run.Id,
                    BuildRunStatus.Lost,
                    RunFailureReasons.WorkerOffline,
                    now,
                    cancellationToken);
                if (lost == 1)
                {
                    result = result with { RunsLost = result.RunsLost + 1 };
                    await runLogIngest.CompleteRunAsync(run.Id);

                    // Card F-038: Lost is terminal too, so the project's retention gate runs for it.
                    terminalSignal.Pulse(run.ProjectId);
                }
            }

            logger.LogInformation(
                "Worker {WorkerId} (organization {OrganizationId}) went Offline after a stale heartbeat; "
                + "{RunsLost} running run(s) marked Lost.",
                worker.Id,
                worker.OrganizationId,
                result.RunsLost);
        }

        return result;
    }

    /// <summary>
    /// Pushes the cancelRun signal to one worker's hub group. Fire-and-forget by design: a missed
    /// signal is recovered by the heartbeat response's cancelRequested fallback (F-009's decision),
    /// so a broadcast failure is logged, never thrown into the cancel path.
    /// </summary>
    private async Task BroadcastCancelAsync(
        Guid workerId,
        Guid runId,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await workerLogHubs.Clients
                .Group(WorkerLogGroups.ForWorker(workerId))
                .SendAsync("cancelRun", runId, reason, cancellationToken);
        }
        catch (Exception failure)
        {
            logger.LogWarning(
                failure,
                "Broadcasting the cancel signal for run {RunId} to worker {WorkerId} failed; "
                + "the heartbeat fallback will deliver it.",
                runId,
                workerId);
        }
    }

    // ---------------------------------------------------------------------
    // Shared plumbing
    // ---------------------------------------------------------------------

    /// <summary>
    /// F-004's authentication: resolve the worker token through the system scope (setting the
    /// tenant), then re-read worker and token through the tenant filter - the re-read rechecks
    /// revocation, which may have happened between the two lookups.
    /// </summary>
    private async Task<WorkerToken?> AuthenticateAsync(
        string? plaintextToken,
        CancellationToken cancellationToken) =>
        await workerService.AuthenticateWorkerAsync(plaintextToken, cancellationToken);

    /// <summary>
    /// The full reporting preamble: token authentication, then the tenant-side re-read of the
    /// token (revocation) and the worker row. The caller owns (and must dispose) the context.
    /// </summary>
    private async Task<(WorkerToken? Token, Worker? Worker, ZenkinsDbContext Context)>
        AuthenticateWithWorkerAsync(string? plaintextToken, CancellationToken cancellationToken)
    {
        var workerToken = await AuthenticateAsync(plaintextToken, cancellationToken);
        if (workerToken is null)
        {
            return (null, null, null!);
        }

        var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers.SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.WorkerId,
            cancellationToken);
        var tokenRow = await context.WorkerTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.Id && candidate.RevokedAt == null,
            cancellationToken);
        if (worker is null || tokenRow is null)
        {
            await context.DisposeAsync();
            return (null, null, null!);
        }

        return (workerToken, worker, context);
    }

    /// <summary>
    /// Projects runs into views, resolving the display names (trigger user's email, worker's
    /// name) in two batched lookups so a page render costs a fixed number of queries.
    /// </summary>
    private static async Task<List<RunView>> ToViewsAsync(
        ZenkinsDbContext context,
        IReadOnlyList<BuildRun> runs,
        CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            return [];
        }

        var userIds = runs
            .Where(run => run.TriggerUserId is not null)
            .Select(run => run.TriggerUserId!)
            .Distinct()
            .ToArray();
        var emails = await context.Users
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);

        var workerIds = runs
            .Where(run => run.WorkerId is not null)
            .Select(run => run.WorkerId!.Value)
            .Distinct()
            .ToArray();
        var workerNames = await context.Workers
            .Where(worker => workerIds.Contains(worker.Id))
            .ToDictionaryAsync(worker => worker.Id, worker => worker.Name, cancellationToken);

        // F-015: every recorded step of every run on the page, in one read (the run page shows
        // completed, current and pending steps for a run that is still executing).
        var runIds = runs.Select(run => run.Id).ToArray();
        var stepRows = await context.BuildSteps
            .AsNoTracking()
            .Where(step => runIds.Contains(step.RunId))
            .ToListAsync(cancellationToken);
        var stepsByRun = stepRows.ToLookup(step => step.RunId);

        // F-018: whether each run's project has a Git credential assigned, in one batched read. The
        // run page needs it to say "no credential was assigned" instead of leaving a failed clone
        // unexplained; the tenant filter keeps another organization's project out of the dictionary.
        //
        // "Assigned" means the credential row really resolves, not merely that the column is set: the
        // foreign key makes a dangling reference impossible, and this condition keeps the run page's
        // wording honest even against a database that predates the constraint. A project whose reference
        // does not resolve reads as "no credential", which is what its runs actually got.
        // F-037: the original run's number for every re-run on this page, in one batched read. The
        // originals may live in any state (a rebuild can be rebuilt); a missing row - the original's
        // project was deleted - simply stays out of the dictionary and the page shows the short id.
        var rebuiltFromIds = runs
            .Where(run => run.RebuiltFromRunId is not null)
            .Select(run => run.RebuiltFromRunId!.Value)
            .Distinct()
            .ToArray();
        var rebuiltFromNumbers = rebuiltFromIds.Length == 0
            ? []
            : await context.BuildRuns
                .Where(run => rebuiltFromIds.Contains(run.Id))
                .Select(run => new { run.Id, run.Number })
                .ToDictionaryAsync(row => row.Id, row => row.Number, cancellationToken);

        // F-033: one read of the tag catalogues for the whole page, then the same SatisfiedBy the claim loop
        // uses decides which queued runs are waiting and which labels nobody carries.
        var workerTagLists = await WorkerTagCatalogue.TagsByWorkerAsync(context, cancellationToken);
        var requirementFacts = runs.ToDictionary(
            run => run.Id,
            run =>
            {
                var requirement = RunValidation.RequirementFor(run.RequiredRequirement, run.RequiredTags);
                if (requirement.IsNone)
                {
                    return ((string?)null, (IReadOnlyList<string>?)null, false);
                }

                var satisfied = workerTagLists.Any(tags => requirement.SatisfiedBy(tags));
                var waiting = !satisfied && string.Equals(run.Status, BuildRunStatus.Queued, StringComparison.Ordinal);
                IReadOnlyList<string>? missing = waiting
                    ? [.. requirement.Atoms.Where(atom => !workerTagLists.Any(tags =>
                        tags.Contains(atom, StringComparer.OrdinalIgnoreCase)))]
                    : null;
                return (requirement.ReadableForm, missing, waiting);
            });

        // Card F-026: the chain, read in two bounded lookups - the upstream runs that caused these ones (for the
        // "triggered by #N" link) and the downstream obligations these runs owed (for the "triggered ..." list).
        var upstreamRunIds = runs
            .Select(run => run.TriggeredByRunId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var upstreamNumbers = upstreamRunIds.Length == 0
            ? new Dictionary<Guid, int>()
            : await context.BuildRuns
                .AsNoTracking()
                .Where(run => upstreamRunIds.Contains(run.Id))
                .Select(run => new { run.Id, run.Number })
                .ToDictionaryAsync(run => run.Id, run => run.Number, cancellationToken);

        var chainRunIds = runs.Select(run => run.Id).ToArray();
        var outboxRows = await context.OutboxMessages
            .AsNoTracking()
            .Where(message => chainRunIds.Contains(message.UpstreamRunId))
            .Select(message => new
            {
                message.UpstreamRunId,
                message.DownstreamRunId,
                message.Outcome,
                message.Reason,
                Link = context.ProjectLinks
                    .Where(link => link.Id == message.LinkId)
                    .Select(link => link.DownstreamProject.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var downstreamRunNumbers = outboxRows
            .Select(row => row.DownstreamRunId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var downstreamNumbers = downstreamRunNumbers.Length == 0
            ? new Dictionary<Guid, int>()
            : await context.BuildRuns
                .AsNoTracking()
                .Where(run => downstreamRunNumbers.Contains(run.Id))
                .Select(run => new { run.Id, run.Number })
                .ToDictionaryAsync(run => run.Id, run => run.Number, cancellationToken);

        var chainLinks = outboxRows
            .GroupBy(row => row.UpstreamRunId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RunChainLink>)
                [
                    .. group
                        .Select(row => new RunChainLink(
                            row.Link ?? "(deleted project)",
                            row.DownstreamRunId,
                            row.DownstreamRunId is { } queued && downstreamNumbers.TryGetValue(queued, out var queuedNumber)
                                ? queuedNumber
                                : null,
                            row.Outcome ?? "pending",
                            row.Reason))
                        .OrderBy(link => link.DownstreamProjectName, StringComparer.Ordinal),
                ]);

        var projectIds = runs.Select(run => run.ProjectId).Distinct().ToArray();
        var credentialAssigned = await context.Projects
            .Where(project => projectIds.Contains(project.Id))
            .Select(project => new
            {
                project.Id,
                Assigned = project.GitCredentialId != null
                    && context.GitCredentials.Any(
                        credential => credential.Id == project.GitCredentialId.Value),

                // F-028: whether the project is disabled right now - the run page and the dashboard say
                // so next to a run that is still in flight, instead of leaving the operator to wonder why
                // the queue stopped moving.
                project.Disabled,
            })
            .ToDictionaryAsync(row => row.Id, row => row, cancellationToken);

        return runs.Select(run =>
        {
            var pipeline = ReadPipeline(run.PipelineSpecJson);
            return new RunView(
                run.Id,
                run.ProjectId,
                run.Status,
                run.TriggerType,
                run.TriggerUserId is not null && emails.TryGetValue(run.TriggerUserId, out var email)
                    ? email
                    : null,
                run.WorkerId is not null && workerNames.TryGetValue(run.WorkerId.Value, out var name)
                    ? name
                    : null,
                run.PipelinePath,
                run.GitBranch,
                run.GitCommit,
                pipeline,
                RunValidation.ParseTags(run.RequiredTags),
                run.StartedAt,
                run.EndedAt,
                run.ExitCode,
                run.FailureReason,
                run.CreatedAt,
                run.UpdatedAt,
                run.TimeoutSeconds,
                run.DeadlineAt,
                BuildStepViews(pipeline, [.. stepsByRun[run.Id]]),
                credentialAssigned.TryGetValue(run.ProjectId, out var projectFacts)
                    ? projectFacts.Assigned
                    : null,
                // F-021: the values this run was triggered with (the snapshot, not the project's
                // current definitions). Null when the project declared no parameters.
                ReadParameters(run.ParametersJson),

                // F-034: the organization environment layer frozen onto this run, read from the run's own
                // column - never from the organization's current variables, which may have been edited or
                // deleted since (a removed variable still shows the value this run executed with).
                RunEnvironmentSnapshot.Read(run.EnvironmentJson),

                // F-028: the project's current disabled state (null when it could not be read).
                projectFacts is null ? (bool?)null : projectFacts.Disabled,

                // F-036: the run's own build number - the #N every surface prints.
                run.Number,

                // F-037: the run this one re-ran, and that run's number when it still resolves.
                run.RebuiltFromRunId,
                run.RebuiltFromRunId is { } origin && rebuiltFromNumbers.TryGetValue(origin, out var originNumber)
                    ? originNumber
                    : null,

                // F-022: the run's definition came from its snapshotted script (inline), so a re-run
                // knows to carry the script instead of guessing a Freestyle shape from the empty branch.
                run.PipelineScriptText is not null,

                // F-033: the requirement in readable form, and - for a queued run nobody can claim - the
                // labels no Worker carries. Computed from the same predicate the claim loop uses.
                requirementFacts[run.Id].Item1,
                requirementFacts[run.Id].Item2,
                requirementFacts[run.Id].Item3,

                // F-041: which parameter is of which new kind, from the run's own record - so the page renders a
                // masked entry as a mask, a run selection as a link and a ref as a ref, whatever the project's
                // definitions say today. Empty for a run triggered before the column existed (all plain strings).
                RunParameterDocuments.ReadDetails(run.ParameterDetailsJson),

                // F-026: the chain this run is part of - who caused it, and what it caused.
                run.TriggeredByRunId,
                run.TriggeredByRunId is { } causedBy && upstreamNumbers.TryGetValue(causedBy, out var causedByNumber)
                    ? causedByNumber
                    : null,
                chainLinks.TryGetValue(run.Id, out var links) ? links : null,
                string.IsNullOrWhiteSpace(run.UpstreamFactsJson)
                    ? null
                    : JsonSerializer.Deserialize(
                        run.UpstreamFactsJson,
                        ProjectParameterJsonContext.Default.DictionaryStringString),

                // F-038: whether this build is exempt from both retention rules.
                run.Pinned);
        }).ToList();
    }

    /// <summary>
    /// The steps a run page lists (card F-015): every step of the frozen spec in declaration order,
    /// merged with the row the worker recorded for that position, or <c>Pending</c> when it has not been
    /// reached. Recorded positions the spec does not cover (a spec that no longer parses, or a step
    /// reported before a spec was frozen) are appended rather than hidden - a step that produced output
    /// must not disappear from its own page.
    /// </summary>
    private static RunStepView[] BuildStepViews(
        PipelineSpec? pipeline,
        IReadOnlyList<BuildStep> recorded)
    {
        var views = new List<RunStepView>();
        var byIndex = recorded.ToDictionary(step => step.Index);

        if (pipeline is { } spec)
        {
            for (var index = 1; index <= spec.Steps.Length; index++)
            {
                var declared = spec.Steps[index - 1];
                views.Add(byIndex.TryGetValue(index, out var step)
                    ? ToMainStepView(step, declared)
                    : new RunStepView(
                        null, index, declared.Name, PipelineStepStatus.Pending, null, null, null, null,
                        Stage: declared.Stage, Argv: declared.Argv));
            }
        }

        // A recorded position the spec covers beyond the main list is a post step (F-031): resolve it against
        // the frozen post lists by its `post/…` name, so it renders in the post section with its own command
        // line. A position the spec does not cover at all has no declaration to read from and stays honestly
        // unstaged rather than guessed into a neighbour's section.
        views.AddRange(recorded
            .Where(step => views.All(view => view.Index != step.Index))
            .OrderBy(step => step.Index)
            .Select(step => ToPostStepView(step, pipeline?.Post?.Find(step.Name), pipeline)));

        return [.. views];
    }

    /// <summary>One recorded step of the main list: its declaration (stage, argv) and no post fields.</summary>
    private static RunStepView ToMainStepView(BuildStep step, PipelineStep declared) => new(
        step.Id,
        step.Index,
        step.Name,
        step.Status,
        step.StartedAt,
        step.EndedAt,
        step.ExitCode,
        step.FailureReason,
        step.Attempts,
        Stage: declared.Stage,
        Argv: declared.Argv);

    /// <summary>
    /// One recorded step that the main list does not cover. A post step (card F-031) is known by its frozen
    /// <c>post/…</c> declaration; anything else stays an honestly unstaged, undeclared row.
    /// </summary>
    private static RunStepView ToPostStepView(BuildStep step, PipelineStep? declared, PipelineSpec? spec)
    {
        var isPost = declared is not null
            && step.Name.StartsWith(PipelineSpecSchema.PostNamePrefix, StringComparison.Ordinal);
        return new RunStepView(
            step.Id,
            step.Index,
            step.Name,
            step.Status,
            step.StartedAt,
            step.EndedAt,
            step.ExitCode,
            step.FailureReason,
            step.Attempts,
            Stage: declared?.Stage,
            IsPost: isPost,
            PostOutcome: isPost ? spec?.Post?.OutcomeOf(step.Name) : null,
            Argv: declared?.Argv);
    }

    /// <summary>
    /// The frozen spec as the page reads it (card F-014). A row whose JSON no longer parses is rendered
    /// as "no definition" rather than failing the page: the run's status and log are still the record.
    /// </summary>
    private static PipelineSpec? ReadPipeline(string? pipelineSpecJson) =>
        PipelineSpecJson.TryRead(pipelineSpecJson, out var spec, out _) ? spec : null;
}
