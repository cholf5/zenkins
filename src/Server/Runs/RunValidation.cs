using System.Globalization;
using Zenkins.Server.Projects;

namespace Zenkins.Server.Runs;

/// <summary>
/// Pure rules for the run trigger form (cards F-006 + F-014): a manual run selects a <b>branch</b> (blank
/// means the project's default branch), required tags and an optional timeout. There is no command line
/// any more - a run executes the project's pipeline, so nothing here has a quoting rule to state (PRD §8
/// constraint 6 holds by construction: a pipeline's argv arrays are data).
/// <para>
/// Limits: required tags are comma-separated and bounded (<see cref="MaxRequiredTagsLength"/>); the
/// branch is validated with the same git ref rules the project's default branch uses, so the two can
/// never disagree. Existence among the organization's workers is checked by <see cref="RunService"/> (it
/// needs the database); everything else is here.
/// </para>
/// </summary>
public static class RunValidation
{
    public const int MaxRequiredTagsLength = 400;
    public const int MaxTagLength = 64;

    /// <summary>The per-run timeout override's bounds in minutes (F-009): one minute .. one week.</summary>
    public const int MinTimeoutMinutes = 1;
    public const int MaxTimeoutMinutes = 10_080;

    /// <summary>
    /// Validates the optional per-run timeout override (F-009, minutes). Blank means "use the
    /// server default" (<c>Runs:DefaultTimeoutMinutes</c>); anything that is not a whole number
    /// between <see cref="MinTimeoutMinutes"/> and <see cref="MaxTimeoutMinutes"/> is a field
    /// error - a silently dropped override would run with the wrong deadline.
    /// </summary>
    public static string? ValidateTimeoutMinutes(string? text, out int? timeoutMinutes)
    {
        timeoutMinutes = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < MinTimeoutMinutes
            || parsed > MaxTimeoutMinutes)
        {
            return RunErrorCodes.TimeoutInvalid;
        }

        timeoutMinutes = parsed;
        return null;
    }

    /// <summary>
    /// Validates the optional branch the trigger form selected and yields it trimmed. Blank is legal
    /// and means "the project's default branch" (<see cref="RunService"/> resolves it, because that is
    /// a row read). A non-blank branch must be a valid git ref for the same reasons the project's
    /// default branch must be (see <see cref="ProjectValidation.ValidateDefaultBranch"/>); the returned
    /// code is <see cref="RunErrorCodes.BranchInvalid"/>.
    /// </summary>
    public static string? ValidateBranch(string? branchText, out string normalized)
    {
        normalized = branchText?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return null;
        }

        return ProjectValidation.ValidateDefaultBranch(normalized, out normalized) is null
            ? null
            : RunErrorCodes.BranchInvalid;
    }

    /// <summary>
    /// Validates the optional comma-separated required tags and yields the normalized
    /// (trimmed, deduplicated, comma-joined) value. Returns an error code
    /// (<see cref="RunErrorCodes.TagInvalid"/>) or <c>null</c>; empty input is legal and means
    /// "no required tags". An entry <see cref="ParseTags"/> would drop for being over
    /// <see cref="MaxTagLength"/> or control-bearing is an error, not a silent drop: dropping the
    /// only required tag would turn the run into one any worker may claim. Existence among the
    /// organization's workers is a database question and lives in <see cref="RunService"/>.
    /// </summary>
    public static string? ValidateRequiredTags(string? tagsText, out string requiredTags)
    {
        if (HasDroppedTag(tagsText))
        {
            requiredTags = string.Empty;
            return RunErrorCodes.TagInvalid;
        }

        var tags = ParseTags(tagsText);
        requiredTags = string.Join(',', tags);

        if (requiredTags.Length > MaxRequiredTagsLength)
        {
            return RunErrorCodes.TagInvalid;
        }

        return null;
    }

    /// <summary>
    /// Whether any comma-separated entry, after trimming, is over <see cref="MaxTagLength"/> long
    /// or still carries a control character - exactly the entries <see cref="ParseTags"/> drops.
    /// Trimming already removes whitespace controls, so only non-whitespace controls survive it.
    /// </summary>
    private static bool HasDroppedTag(string? tagsText)
    {
        if (string.IsNullOrWhiteSpace(tagsText))
        {
            return false;
        }

        return tagsText
            .Split(',')
            .Select(raw => raw.Trim())
            .Any(tag => tag.Length > MaxTagLength || tag.Any(char.IsControl));
    }

    /// <summary>
    /// Trims, drops empties and over-long/control-bearing entries, deduplicates
    /// case-insensitively (first spelling wins). The drops themselves stay silent here; form
    /// validation rejects the over-long/control-bearing ones (<see cref="ValidateRequiredTags"/>).
    /// </summary>
    public static string[] ParseTags(string? tagsText)
    {
        if (string.IsNullOrWhiteSpace(tagsText))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();
        foreach (var raw in tagsText.Split(','))
        {
            var tag = raw.Trim();
            if (tag.Length == 0
                || tag.Length > MaxTagLength
                || tag.Any(char.IsControl)
                || !seen.Add(tag))
            {
                continue;
            }

            tags.Add(tag);
        }

        return [.. tags];
    }

    /// <summary>
    /// Whether the worker's comma-separated tags cover every required tag (case-insensitive). A
    /// run without required tags is satisfiable by any worker, including a tagless one.
    /// </summary>
    public static bool WorkerTagsCover(string requiredTags, string workerTags)
    {
        var required = ParseTags(requiredTags);
        if (required.Length == 0)
        {
            return true;
        }

        var available = ParseTags(workerTags)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required.All(available.Contains);
    }
}
