using BlazorBlueprint.Components;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Server.Components.Shared;

/// <summary>
/// The one place a build run's status and duration become display text (card D-002): the dashboard
/// and the runs list render the same run through these bits, so a run cannot read as, say,
/// Destructive on one page and Outline on the other. The run detail page uses them too.
/// <para>
/// Duration and elapsed share one formatter on purpose: a finished run shows its duration, a
/// running one shows live elapsed time from the caller's clock instant - never a blank and never a
/// zero - and a run that never started shows "-" rather than an invented duration.
/// </para>
/// </summary>
public static class RunDisplay
{
    /// <summary>
    /// The badge variant for a run status. Every well-known status has an explicit answer (a
    /// Canceled run is an outline, a Lost one is destructive) and an unknown status still renders
    /// as an outline instead of throwing.
    /// </summary>
    public static BadgeVariant BadgeFor(string status) => status switch
    {
        BuildRunStatus.Queued => BadgeVariant.Secondary,
        BuildRunStatus.Running => BadgeVariant.Default,
        BuildRunStatus.Success => BadgeVariant.Success,
        BuildRunStatus.Failed => BadgeVariant.Destructive,
        BuildRunStatus.Canceled => BadgeVariant.Outline,
        BuildRunStatus.Lost => BadgeVariant.Destructive,
        _ => BadgeVariant.Outline,
    };

    /// <summary>
    /// A duration as prose: minutes with zero-padded seconds once the run is a minute old, a
    /// one-decimal seconds readout below that. Hours stay in minutes (a 90-minute run reads
    /// "90m 05s") - the same shape every page has shown since F-006.
    /// </summary>
    public static string FormatDuration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s"
        : $"{duration.TotalSeconds:F1}s";

    /// <summary>
    /// What the duration column shows for one run at clock instant <paramref name="nowUtc"/>:
    /// the stored duration for a finished run, live elapsed time (marked as running) for one still
    /// executing, and "-" for a run that never started (queued, or canceled while queued).
    /// </summary>
    public static string DurationOrElapsed(
        DateTimeOffset? startedAt,
        DateTimeOffset? endedAt,
        DateTimeOffset nowUtc)
    {
        if (startedAt is not { } start)
        {
            return "-";
        }

        return endedAt is { } end
            ? FormatDuration(end - start)
            : $"{FormatDuration(nowUtc - start)} (running)";
    }

    /// <summary>
    /// A run's <c>FailureReason</c> in words (card F-018). The stored value is a closed code
    /// (<see cref="Zenkins.Shared.Pipelines.PipelineFailureReasons"/>); a page that only printed the code
    /// left an operator staring at <c>git-auth-rejected</c> with no next step, which is exactly what this
    /// card fixes.
    /// <para>
    /// <b>The credential answer is the point.</b> A rejected credential reads differently depending on
    /// whether the project had one assigned: with none, the sentence says so and says a credential on the
    /// worker host is not used; with one, it points at rotation. <paramref name="gitCredentialAssigned"/> is
    /// <see langword="null"/> when the project could not be read, and the sentence then stays general
    /// instead of guessing.
    /// </para>
    /// <para>
    /// <b>A code without a sentence returns the empty string, deliberately.</b> The run page always shows the
    /// stored code next to this text (that is the closed-vocabulary contract), and the page renders the
    /// explanation row only when there is one - so an untranslated reason reads as its code, never as a
    /// blank claim. Adding a sentence here is a presentation change; it can never hide a reason.
    /// </para>
    /// </summary>
    public static string FailureReasonText(string? reason, bool? gitCredentialAssigned)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        return reason switch
        {
            // Card F-041: a file parameter that could not be materialised - nothing ran, and the fix is to look at
            // the run's inputs rather than at a step.
            PipelineFailureReasons.ParameterFileUnavailable =>
                "A file parameter could not be prepared on the worker, so no step was started. The run log names "
                + "the parameter; check that the upload is still there and run again.",
            PipelineFailureReasons.GitAuthRejected when gitCredentialAssigned == false =>
                "The remote asked for credentials, and no Git credential is assigned to this project. "
                + "Attach one on the project's settings tab - a credential stored on the worker host is "
                + "not used.",
            PipelineFailureReasons.GitCredentialNotAssigned =>
                "The repository needs a credential and none is assigned to this project, so the worker did "
                + "not authenticate (it never borrows a credential from the worker host). Attach one on the "
                + "project's settings tab and run again.",
            PipelineFailureReasons.GitAuthRejected =>
                "The remote rejected the project's Git credential. If it expired or was revoked, rotate "
                + "it on the project's settings tab.",
            PipelineFailureReasons.GitCredential =>
                "The project's Git credential could not be used (its kind does not match the repository "
                + "URL, or the value is unusable). Check it on the project's settings tab.",
            PipelineFailureReasons.GitUnavailable =>
                "The worker could not run git. Install git on the worker host and try again.",
            PipelineFailureReasons.GitInvalidUrl =>
                "The repository URL is not one git can clone (it is malformed, or it embeds credentials).",
            PipelineFailureReasons.GitBranchNotFound =>
                "The branch this run builds does not exist on the remote.",
            PipelineFailureReasons.GitUnreachable =>
                "The worker could not reach the repository (DNS, connection or TLS). If the host uses a "
                + "private certificate authority, install it on the worker host.",
            PipelineFailureReasons.GitTimeout =>
                "A git command exceeded its budget and was stopped.",
            PipelineFailureReasons.GitNonEmptyTarget =>
                "The run's work directory was not empty, so the clone refused to overwrite it.",
            PipelineFailureReasons.GitPathEscape =>
                "A path would have left the run's work directory, so the operation was refused.",
            PipelineFailureReasons.GitFailed =>
                "So git reported a failure; the run log carries the detail.",
            PipelineFailureReasons.PipelineMissing =>
                "The pipeline file this project names does not exist in the checked-out commit.",
            PipelineFailureReasons.PipelineSyntax =>
                "The pipeline script did not compile; the log carries the line and column.",
            PipelineFailureReasons.PipelineInvalid =>
                "The pipeline script declared a pipeline the server refused; the log names the field.",
            PipelineFailureReasons.PipelineTimeout =>
                "The pipeline's definition phase ran past the run's deadline and was abandoned.",
            PipelineFailureReasons.StepFailed =>
                "A step exited non-zero, so the run failed.",
            PipelineFailureReasons.StepTimeout =>
                "A step exceeded the timeout it declares for itself and was killed there - the run's own "
                + "deadline (and the steps after it) still had time left. Open the step's log: it says which "
                + "limit fired.",
            PipelineFailureReasons.StepRetryExhausted =>
                "A step failed and then failed again on every attempt it declared; the run stopped there. "
                + "Each attempt's output is its own delimited section in the step's log.",
            _ => string.Empty,
        };
    }
}
