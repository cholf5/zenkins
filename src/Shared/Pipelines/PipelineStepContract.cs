namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The recorded states of one pipeline step (card F-015, PRD §3.3.5). Stored as a string so the model
/// stays provider-neutral; the check constraint in the Server's <c>ZenkinsDbContext</c> keeps
/// out-of-band writes honest.
/// <para>
/// <b>A skipped step is recorded, not omitted.</b> <see cref="Skipped"/> is a terminal state a step
/// reaches without ever starting: nothing was claimed <see cref="Running"/> for it, no process was
/// spawned, no <c>StartedAt</c> was stamped, and it does not affect the run's success. That is
/// deliberately not the same thing as "ran and failed" or "never mentioned" - the run page shows a
/// step that did not run, with the reason in the run log.
/// </para>
/// <para>
/// <b>The vocabulary is closed.</b> The Server refuses a status it does not know (400), because a
/// status the page cannot render is a contract mismatch, not data to store and hope for.
/// </para>
/// </summary>
public static class PipelineStepStatus
{
    /// <summary>Reported before the step starts: the process is about to run (or already is).</summary>
    public const string Running = "Running";

    /// <summary>Terminal: the process exited 0.</summary>
    public const string Success = "Success";

    /// <summary>Terminal: the process exited non-zero, could not start, or could not be prepared.</summary>
    public const string Failed = "Failed";

    /// <summary>Terminal, never started: a <c>when</c> condition did not match, a previous step failed,
    /// or the run was canceled before this step was reached.</summary>
    public const string Skipped = "Skipped";

    /// <summary>
    /// Display-only: a declared step of the frozen spec that has no report yet. Never stored - it is
    /// what the run page renders for a step the worker has not reached (card F-015, AC-2).
    /// </summary>
    public const string Pending = "Pending";

    /// <summary>Every state a report may carry (not <see cref="Pending"/>, which is a view state).</summary>
    public static IReadOnlyList<string> Reportable { get; } = [Running, Success, Failed, Skipped];

    /// <summary>Whether <paramref name="status"/> is one a worker may report.</summary>
    public static bool IsReportable(string? status) => status is not null && Reportable.Contains(status);

    /// <summary>Whether <paramref name="status"/> is terminal (no further transition).</summary>
    public static bool IsTerminal(string? status) =>
        status is Success or Failed or Skipped;
}

/// <summary>
/// The closed vocabulary of a step's <c>FailureReason</c> (card F-015). A non-zero exit needs no reason
/// - the exit code is the record - so these cover only the cases where nothing exited: a step that
/// could not start, a secret that could not be resolved, and the reasons a step was skipped.
/// </summary>
public static class PipelineStepFailureReasons
{
    /// <summary>Failed: a user cancel (F-009's button) killed the step's process tree.</summary>
    public const string Canceled = "canceled";

    /// <summary>
    /// Failed: the run's <b>deadline</b> (F-009's timeout) killed the step's process tree. Deliberately a
    /// different value from <see cref="Canceled"/>: "someone stopped this" and "it ran out of time" are
    /// different stories on the run page, and the run's own reason (<c>timeout</c> vs
    /// <c>canceled-by-user …</c>) already distinguishes them.
    /// </summary>
    public const string TimedOut = "timed-out";

    /// <summary>
    /// Failed: the step's <b>own</b> declared timeout (<c>timeout = 600</c>) was exceeded, so its process tree
    /// was killed (card F-032). Deliberately a different value from <see cref="TimedOut"/>: "this step hit its
    /// own limit" is not "the run ran out of time", and the run's page and log must be able to say which of
    /// the two fired.
    /// </summary>
    public const string StepTimeout = "step-timeout";

    /// <summary>Failed: the process could not be started at all (bad argv[0], unusable work directory).</summary>
    public const string NotStarted = "not-started";

    /// <summary>
    /// Failed: the step declared an artifact path that the shared rules refused (card F-016) - a traversal,
    /// an absolute or drive-qualified path, or a symlink whose target leaves the checked-out repository.
    /// The run log names the path as the pipeline wrote it; nothing was uploaded and no row was created.
    /// </summary>
    public const string ArtifactPathRejected = "artifact-path-rejected";

    /// <summary>
    /// Failed: publishing an artifact did not complete (card F-016) - a cap was exceeded (the log names
    /// the limit), a file could not be read, the server refused a file, or the upload was cut off. The
    /// artifact stays not-<c>Ready</c>, so it is never downloadable.
    /// </summary>
    public const string ArtifactFailed = "artifact-failed";

    /// <summary>
    /// Failed: a <c>secret()</c> reference could not be resolved (the name does not exist in this
    /// organization, the run is not this worker's any more, or the server refused the fetch). The run
    /// log names the <b>name</b>, never a value.
    /// </summary>
    public const string SecretUnavailable = "secret-unavailable";

    /// <summary>Skipped: the step's <c>when</c> condition did not match the run's frozen facts.</summary>
    public const string WhenNotMatched = "when-not-matched";

    /// <summary>Skipped: an earlier step failed and that step did not set <c>continue_on_error</c>.</summary>
    public const string PreviousStepFailed = "previous-step-failed";

    /// <summary>Skipped: the run was canceled (or its deadline passed) before this step was reached.</summary>
    public const string RunCanceled = "run-canceled";

    /// <summary>Every reason a report may carry.</summary>
    public static IReadOnlyList<string> All { get; } =
        [
            Canceled, TimedOut, StepTimeout, NotStarted, SecretUnavailable, ArtifactPathRejected,
            ArtifactFailed, WhenNotMatched, PreviousStepFailed, RunCanceled,
        ];

    /// <summary>The reasons a <see cref="PipelineStepStatus.Skipped"/> report may carry.</summary>
    public static IReadOnlyList<string> SkipReasons { get; } = [WhenNotMatched, PreviousStepFailed, RunCanceled];

    /// <summary>The reasons a <see cref="PipelineStepStatus.Failed"/> report may carry.</summary>
    public static IReadOnlyList<string> FailReasons { get; } =
        [Canceled, TimedOut, StepTimeout, NotStarted, SecretUnavailable, ArtifactPathRejected, ArtifactFailed];

    /// <summary>Whether <paramref name="status"/> with <paramref name="failureReason"/> is a legal pair.</summary>
    public static bool IsLegal(string status, string? failureReason) => status switch
    {
        // A running or successful step has no failure reason; a failed one may carry a closed reason
        // or none at all (a non-zero exit code is its own explanation).
        PipelineStepStatus.Running or PipelineStepStatus.Success => failureReason is null,
        PipelineStepStatus.Failed => failureReason is null || FailReasons.Contains(failureReason),

        // A skipped step must say why: "skipped" without a reason is indistinguishable from a bug.
        PipelineStepStatus.Skipped => failureReason is not null && SkipReasons.Contains(failureReason),
        _ => false,
    };
}
