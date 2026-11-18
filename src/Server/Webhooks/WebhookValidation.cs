using Zenkins.Server.Projects;
using Zenkins.Server.Runs;

namespace Zenkins.Server.Webhooks;

/// <summary>Field names the webhook forms and the service refer to (card F-017).</summary>
public static class WebhookFields
{
    public const string Name = "Name";
    public const string BranchFilter = "BranchFilter";
    public const string RequiredTags = "RequiredTags";
}

/// <summary>
/// Stable validation codes. The service returns the code, the endpoint renders it, and the page maps it
/// back to a message - the same PRG shape F-003/F-012 use.
/// </summary>
public static class WebhookErrorCodes
{
    public const string NameRequired = "name-required";
    public const string NameTooLong = "name-too-long";
    public const string NameInvalid = "name-invalid";
    public const string NameDuplicate = "name-duplicate";
    public const string BranchFilterInvalid = "branch-filter-invalid";
    public const string TagsInvalid = "tags-invalid";

    /// <summary>F-020: the target project is a Freestyle project - there is no repository to push to,
    /// so a webhook can never fire honestly for it.</summary>
    public const string ProjectNotPipeline = "project-not-pipeline";

    /// <summary>The form field a code belongs to, or <see langword="null"/> for an unknown code.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameInvalid or NameDuplicate => WebhookFields.Name,
        BranchFilterInvalid or ProjectNotPipeline => WebhookFields.BranchFilter,
        TagsInvalid => WebhookFields.RequiredTags,
        _ => null,
    };

    /// <summary>The message rendered next to the field (and in the form-level alert).</summary>
    public static string? MessageFor(string? code) => code switch
    {
        NameRequired => "Enter a webhook name.",
        NameTooLong => $"The webhook name must be at most {WebhookValidation.MaxNameLength} characters.",
        NameInvalid => "The webhook name contains characters that are not allowed.",
        NameDuplicate => "A webhook with this name already exists in this project.",
        BranchFilterInvalid =>
            "Enter comma-separated branch names; a trailing '*' is the only wildcard (for example "
            + "main, release/*). Leave it blank to follow the project's default branch.",
        ProjectNotPipeline =>
            "A webhook triggers on git pushes; this project is a Freestyle project and has no repository.",
        TagsInvalid => "Required tags are comma-separated text, at most 64 characters per tag.",
        _ => null,
    };
}

/// <summary>
/// The webhook configuration's validation (card F-017). Pure rules only - the name's uniqueness is a
/// database question (the unique index), and every rule here is re-checked by
/// <see cref="WebhookService"/> because the HTML form is a convenience, never the enforcement.
/// </summary>
public static class WebhookValidation
{
    public const int MaxNameLength = 100;

    /// <summary>Validates and trims the name. Returns a <see cref="WebhookErrorCodes"/> value or null.</summary>
    public static string? ValidateName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return WebhookErrorCodes.NameRequired;
        }

        if (normalized.Length > MaxNameLength)
        {
            return WebhookErrorCodes.NameTooLong;
        }

        return normalized.Any(char.IsControl) ? WebhookErrorCodes.NameInvalid : null;
    }

    /// <summary>
    /// Validates the optional comma-separated required tags with the manual trigger's rules (the same
    /// tags, the same limits), and yields the normalized value. Unknown tags are not an error here: a
    /// webhook may legitimately target a worker that joins later, and the run simply stays queued.
    /// </summary>
    public static string? ValidateRequiredTags(string? value, out string normalized)
    {
        var error = RunValidation.ValidateRequiredTags(value, out normalized);
        return error is null ? null : WebhookErrorCodes.TagsInvalid;
    }
}

/// <summary>
/// The closed set of delivery outcomes a stored <see cref="Data.Entities.WebhookDelivery"/> records and a
/// duplicate answers with. The vocabulary is also what the settings page shows as "last outcome".
/// <para>
/// <b>The set is closed by <see cref="All"/>, and the delivery path is held to it.</b> C# cannot
/// enumerate a type's constants, so <see cref="All"/> is written out by hand - which would make it just
/// another list to fall behind. What keeps it honest is a fact that reflects over this type and fails
/// when a constant is added, renamed or re-valued without <see cref="All"/> following (and when
/// <see cref="All"/> names something no constant declares). The delivery path writes through
/// <see cref="Require"/>, so a value invented in one branch of it throws instead of being stored and
/// rendered on the settings page as-is. The column itself stays open on purpose: it also carries run
/// validation codes, so a check constraint there would be the wrong place for this rule.
/// </para>
/// </summary>
public static class WebhookOutcomes
{
    /// <summary>A run was queued for the pushed commit.</summary>
    public const string Queued = "queued";

    /// <summary>The branch is not selected by the webhook's filter.</summary>
    public const string IgnoredBranch = "ignored:branch";

    /// <summary>A delete event (<c>after</c> all zeroes): nothing to build.</summary>
    public const string IgnoredDelete = "ignored:delete";

    /// <summary>A ref that is not a branch (a tag, a note): nothing to build.</summary>
    public const string IgnoredNotBranch = "ignored:not-branch";

    /// <summary>F-020, defence in depth: the webhook somehow targets a Freestyle project (creation
    /// already refuses that kind, and a project's kind is immutable) - nothing to build.</summary>
    public const string IgnoredNoRepository = "ignored:no-repository";

    /// <summary>
    /// Cards F-028: the project is disabled, so this push queued nothing. Recorded (and answered
    /// <c>2xx</c>) rather than refused at the HTTP level: a permanent <c>4xx</c> makes most git hosts
    /// disable the webhook automatically, which would silently break "enable the project again and the
    /// webhook works". The word is the same one a disabled project's trigger refusal uses.
    /// </summary>
    public const string ProjectDisabled = ProjectTriggerRefusals.ProjectDisabled;

    /// <summary>
    /// Card F-029: the project's queue is at its cap, so this push queued nothing. Recorded for the same
    /// reason as <see cref="ProjectDisabled"/> - the delivery is a fact, and a host that retries gets the
    /// same honest answer instead of a retry storm.
    /// </summary>
    public const string QueueFull = ProjectTriggerRefusals.QueueFull;

    /// <summary>
    /// Card F-023: the pushed branch's tree has not been read for this commit yet, so the delivery queued
    /// nothing <b>yet</b>. A Worker observation decides: has-pipeline queues the run at the pushed commit,
    /// no-pipeline records the branch and queues nothing. The push is not lost - the branch row remembers
    /// the commit and the retry sweeper re-asks - and "unknown" is never guessed in either direction.
    /// </summary>
    public const string AwaitingObservation = "awaiting-observation";

    /// <summary>
    /// Card F-023: the pushed branch has no pipeline file, so this delivery queued nothing. Recorded
    /// and answered 2xx like every other "the push was seen but nothing is built" outcome - a
    /// permanent 4xx would make most git hosts disable the webhook.
    /// </summary>
    public const string NoPipeline = "no-pipeline";

    /// <summary>
    /// Card F-023: the pushed branch name is longer than a run can store
    /// (<see cref="Zenkins.Server.Branches.BranchIdentity.MaxLength"/>), so the branch cannot become a
    /// branch job. Recorded instead of letting the run insert fail (probes may report 255-character
    /// refs; a run's branch column holds 200) and instead of truncating - a truncated name would be a
    /// different branch.
    /// </summary>
    public const string BranchTooLong = "ignored:branch-too-long";

    /// <summary>
    /// Card F-025: the push named a commit that a trigger <b>already</b> queued a run for - the same commit
    /// a poll observed, or an earlier push of the same commit under a different delivery id. The delivery is
    /// still recorded (the push was seen) and still answered 2xx; nothing is queued twice. The shared
    /// <c>(project, branch, commit)</c> ledger is what answers this, and the record travels with the run id
    /// it found.
    /// </summary>
    public const string IgnoredDuplicate = "ignored:duplicate";

    /// <summary>Longest stored outcome.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Every outcome a delivery can record - the one list the delivery path is held to and the endpoint
    /// documentation is checked against. Kept equal to the constants above by a fact
    /// (<c>WebhookOutcomeVocabularyTests</c>), never by attention.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Queued,
        IgnoredBranch,
        IgnoredDelete,
        IgnoredNotBranch,
        IgnoredNoRepository,
        ProjectDisabled,
        QueueFull,
        IgnoredDuplicate,
        AwaitingObservation,
        NoPipeline,
        BranchTooLong,
    };

    /// <summary>Whether <paramref name="outcome"/> is a member of <see cref="All"/>.</summary>
    public static bool IsKnown(string? outcome) => outcome is not null && All.Contains(outcome);

    /// <summary>
    /// The delivery path's write barrier: the stored outcome, or an exception naming the offending value.
    /// A misspelled or newly invented outcome fails at the writer instead of reaching the row and the
    /// page that renders it.
    /// </summary>
    public static string Require(string? outcome) => IsKnown(outcome)
        ? outcome!
        : throw new ArgumentOutOfRangeException(
            nameof(outcome),
            outcome,
            "A webhook delivery outcome must be a member of WebhookOutcomes.All.");
}
