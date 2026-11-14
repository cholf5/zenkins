namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The closed set of <c>FailureReason</c> values a pipeline run can carry (card F-014). The UI, the
/// Worker and the tests agree on these strings, so a run page can say why a run failed without
/// parsing log lines - and the Server can refuse anything else a worker invents.
/// <para>
/// <b>Three families.</b> <c>git-*</c> comes from F-013's <c>GitFailureKind</c> (the clone/checkout
/// family), <c>pipeline-*</c> from the definition phase, and <c>step-failed</c> from the executor
/// loop. F-009's <c>timeout</c> / <c>canceled-by-user</c> stay server-owned and are deliberately
/// <b>not</b> in <see cref="WorkerReportable"/>: a worker may only report a reason the server itself
/// does not own.
/// </para>
/// <para>
/// <b>Why closed.</b> An open vocabulary would let a worker's bug become a UI state nobody can
/// translate; a closed set fails the report with a 400 instead. Adding a case is a one-line change
/// here plus its use site.
/// </para>
/// </summary>
public static class PipelineFailureReasons
{
    // ---- git-* (F-013) -------------------------------------------------

    /// <summary>The <c>git</c> executable itself could not be started (not on <c>PATH</c>, not runnable).</summary>
    public const string GitUnavailable = "git-unavailable";

    /// <summary>The repository URL is malformed or embeds a credential.</summary>
    public const string GitInvalidUrl = "git-invalid-url";

    /// <summary>The run's branch does not exist on the remote.</summary>
    public const string GitBranchNotFound = "git-branch-not-found";

    /// <summary>The remote refused the project's credential.</summary>
    public const string GitAuthRejected = "git-auth-rejected";

    /// <summary>
    /// The clone needed a credential and the project has <b>none assigned</b> (card F-018). A distinct
    /// reason rather than a bare <c>git-auth-rejected</c>: the fix is an Owner attaching a credential on
    /// the project's settings tab, not rotating one, and the page can say exactly that. The worker
    /// reports it only when the server delivered no material for the run.
    /// </summary>
    public const string GitCredentialNotAssigned = "git-credential-not-assigned";

    /// <summary>The remote could not be reached (DNS, connection, TLS).</summary>
    public const string GitUnreachable = "git-unreachable";

    /// <summary>One git invocation exceeded its budget.</summary>
    public const string GitTimeout = "git-timeout";

    /// <summary>The project's credential material is unusable.</summary>
    public const string GitCredential = "git-credential";

    /// <summary>The clone target already held something (a retried run never inherits the tree).</summary>
    public const string GitNonEmptyTarget = "git-non-empty-target";

    /// <summary>A path would have left the run's work directory.</summary>
    public const string GitPathEscape = "git-path-escape";

    /// <summary>Any other git failure; the log carries the redacted stderr tail.</summary>
    public const string GitFailed = "git-failed";

    // ---- definition phase ----------------------------------------------

    /// <summary><c>PipelinePath</c> does not exist in the checked-out tree (the message names it).</summary>
    public const string PipelineMissing = "pipeline-missing";

    /// <summary>The script did not compile; the message carries <c>source:(line,col)</c>.</summary>
    public const string PipelineSyntax = "pipeline-syntax";

    /// <summary>The declared spec was rejected, or the script raised while declaring it; the message
    /// carries the offending field path.</summary>
    public const string PipelineInvalid = "pipeline-invalid";

    /// <summary>The definition phase ran past the run's wall clock and was abandoned.</summary>
    public const string PipelineTimeout = "pipeline-timeout";

    // ---- executor ------------------------------------------------------

    /// <summary>A step exited non-zero; its exit code is recorded on the run, nothing is invented.</summary>
    public const string StepFailed = "step-failed";

    /// <summary>Every reason a worker may report through the <c>completed</c> endpoint.</summary>
    public static readonly string[] WorkerReportable =
    [
        GitUnavailable,
        GitInvalidUrl,
        GitBranchNotFound,
        GitAuthRejected,
        GitCredentialNotAssigned,
        GitUnreachable,
        GitTimeout,
        GitCredential,
        GitNonEmptyTarget,
        GitPathEscape,
        GitFailed,
        PipelineMissing,
        PipelineSyntax,
        PipelineInvalid,
        PipelineTimeout,
        StepFailed,
    ];

    /// <summary>Whether <paramref name="reason"/> is one of <see cref="WorkerReportable"/>.</summary>
    public static bool IsWorkerReportable(string? reason) =>
        reason is not null && Array.IndexOf(WorkerReportable, reason) >= 0;
}
