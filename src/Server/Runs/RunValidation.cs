using System.Globalization;
using System.Text.Json;

namespace Zenkins.Server.Runs;

/// <summary>
/// Pure rules for the run trigger form (card F-006): the command arrives as a textarea with one
/// argument per line and is stored as an argv JSON array - <b>no shell quoting interpretation</b>
/// anywhere (PRD §8 constraint 6): quotes are literal characters of an argument, exactly as
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> would receive them from F-007.
/// <para>
/// Limits: at most 100 arguments and at most 32 KiB per argument (decisions on the card). Blank
/// lines are dropped after trimming - a trailing newline in a textarea must not become an empty
/// argument. Required tags are comma-separated; existence among the organization's workers is
/// checked by <see cref="RunService"/> (it needs the database), everything else is here.
/// </para>
/// </summary>
public static class RunValidation
{
    public const int MaxArguments = 100;
    public const int MaxArgumentLength = 32 * 1024;
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
    /// Validates the command textarea and yields the argv. Returns an error code
    /// (<see cref="RunErrorCodes.ArgvEmpty"/>, <see cref="RunErrorCodes.ArgvTooManyArguments"/>,
    /// <see cref="RunErrorCodes.ArgvArgumentTooLong"/>) or <c>null</c> when the argv is usable.
    /// </summary>
    public static string? ValidateArguments(string? commandText, out string[] argv)
    {
        argv = ParseArguments(commandText);

        if (argv.Length == 0)
        {
            return RunErrorCodes.ArgvEmpty;
        }

        if (argv.Length > MaxArguments)
        {
            return RunErrorCodes.ArgvTooManyArguments;
        }

        if (argv.Any(argument => argument.Length > MaxArgumentLength))
        {
            return RunErrorCodes.ArgvArgumentTooLong;
        }

        return null;
    }

    /// <summary>
    /// One argument per line, trimmed; blank lines are dropped. Splitting happens on CR and LF
    /// before trimming, so CRLF textareas parse the same as LF ones.
    /// </summary>
    public static string[] ParseArguments(string? commandText)
    {
        if (string.IsNullOrEmpty(commandText))
        {
            return [];
        }

        return commandText
            .Split(['\r', '\n'], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
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

    /// <summary>Decodes the stored argv JSON. Corrupt rows cannot happen through the product path.</summary>
    public static string[] DecodeCommandJson(string commandJson)
    {
        if (string.IsNullOrEmpty(commandJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(commandJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Encodes the argv as the JSON array stored in <c>CommandJson</c>.</summary>
    public static string EncodeCommandJson(IReadOnlyList<string> argv) =>
        System.Text.Json.JsonSerializer.Serialize(argv);
}
