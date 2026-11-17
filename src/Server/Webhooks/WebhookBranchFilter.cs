namespace Zenkins.Server.Webhooks;

/// <summary>
/// The branch filter of a webhook (card F-017): which pushes are interesting.
/// <para>
/// <b>Exact names and one trailing wildcard.</b> <c>main</c> matches only <c>main</c>; <c>release/*</c>
/// matches <c>release/1.2</c> and not <c>main</c>. A <c>*</c> anywhere but the end is refused rather
/// than given a meaning: a filter that looks like a glob but is matched as text would silently never
/// build.
/// </para>
/// <para>
/// <b>Its own validator, deliberately.</b> The manual trigger and the project's default branch go through
/// <see cref="Projects.ProjectValidation.ValidateDefaultBranch"/>, which (correctly) refuses <c>*</c>.
/// Loosening that shared rule to let <c>release/*</c> through would weaken every branch the product
/// hands to git; writing a second, equally strict rule here keeps the two apart. The only difference is
/// the one this feature needs: a trailing <c>*</c> is allowed, and the text before it may end with
/// <c>/</c> (<c>release/</c> is a legal wildcard prefix but not a legal branch name).
/// </para>
/// <para>
/// <b>The default is the project's default branch, resolved at delivery time.</b> An empty filter is not
/// stored as a copy of <c>main</c>; it is matched against the project's current default branch, so
/// moving the project's default moves the webhook's target with it (no stale duplicate to keep in sync).
/// </para>
/// </summary>
public static class WebhookBranchFilter
{
    /// <summary>Longest accepted comma-separated filter text.</summary>
    public const int MaxFilterLength = 1000;

    /// <summary>The one wildcard character, legal only as the last character of a pattern.</summary>
    public const char Wildcard = '*';

    /// <summary>
    /// Splits a filter into its patterns (trimmed, empties dropped, duplicates removed). Case-sensitive,
    /// like git ref names and the exact-match semantics.
    /// </summary>
    public static string[] Parse(string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return
        [
            .. filterText
                .Split(',')
                .Select(pattern => pattern.Trim())
                .Where(pattern => pattern.Length > 0 && seen.Add(pattern)),
        ];
    }

    /// <summary>
    /// Validates and normalises the filter. Blank is legal (the project's default branch). Every pattern
    /// must be a git ref shape plus, at most, one trailing <c>*</c>; the returned code is
    /// <see cref="WebhookErrorCodes.BranchFilterInvalid"/> otherwise.
    /// </summary>
    public static string? Validate(string? filterText, out string normalized)
    {
        normalized = string.Join(',', Parse(filterText));

        if (normalized.Length > MaxFilterLength)
        {
            return WebhookErrorCodes.BranchFilterInvalid;
        }

        foreach (var pattern in Parse(filterText))
        {
            if (ValidatePattern(pattern) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="branch"/> is selected by <paramref name="filterText"/>. An empty filter
    /// selects exactly <paramref name="defaultBranch"/>.
    /// </summary>
    public static bool Matches(string? filterText, string branch, string defaultBranch)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentException.ThrowIfNullOrEmpty(defaultBranch);

        var patterns = Parse(filterText);
        if (patterns.Length == 0)
        {
            return string.Equals(branch, defaultBranch, StringComparison.Ordinal);
        }

        foreach (var pattern in patterns)
        {
            if (MatchesPattern(pattern, branch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Card F-023's <b>discovery rule</b>: which pushes a multibranch project should turn into (or
    /// refresh) a branch job. Deliberately a separate method, not a mode flag on
    /// <see cref="Matches"/>:
    /// <list type="bullet">
    /// <item>an empty discovery filter means <b>every branch</b> - discovery has no single default
    /// branch to fall back on, which is exactly what "the branch filter becomes the discovery rule"
    /// means;</item>
    /// <item><see cref="Matches"/> keeps its three-argument meaning bit for bit (empty filter = the
    /// project's <em>current</em> default branch, resolved at delivery time), because that is F-017's
    /// contract and every single-branch project still depends on it.</item>
    /// </list>
    /// The pattern vocabulary is the same one - exact names and one trailing wildcard - so a filter
    /// written for a single-branch webhook is not re-interpreted.
    /// </summary>
    public static bool MatchesDiscovery(string? filterText, string branch)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);

        var patterns = Parse(filterText);
        if (patterns.Length == 0)
        {
            return true;
        }

        foreach (var pattern in patterns)
        {
            if (MatchesPattern(pattern, branch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One pattern against one branch (the shared vocabulary both rules above use).</summary>
    private static bool MatchesPattern(string pattern, string branch)
    {
        if (pattern.EndsWith(Wildcard))
        {
            // Trailing wildcard: a prefix match on the part before it. No other wildcard can reach
            // here - Validate refuses it - so "release/*" cannot accidentally become a regex.
            return branch.StartsWith(pattern[..^1], StringComparison.Ordinal);
        }

        return string.Equals(pattern, branch, StringComparison.Ordinal);
    }

    /// <summary>
    /// One pattern: git's ref-shape rules (the same set <see cref="Projects.ProjectValidation"/> states,
    /// restated here so the two validators stay independent) plus the trailing wildcard. A wildcard
    /// prefix may end with <c>/</c>; an exact name may not.
    /// </summary>
    private static string? ValidatePattern(string pattern)
    {
        var isWildcard = pattern.EndsWith(Wildcard);
        var candidate = isWildcard ? pattern[..^1] : pattern;

        if (candidate.Length == 0
            || candidate.Contains(Wildcard)
            || candidate[0] is '/' or '-'
            || candidate.Contains("..", StringComparison.Ordinal)
            || candidate.Contains('\\')
            || candidate.Contains("//", StringComparison.Ordinal)
            || candidate.Contains("@{", StringComparison.Ordinal)
            || candidate.IndexOfAny(['~', '^', ':', '?', '[', '\u007f']) >= 0
            || candidate.EndsWith('.')
            || candidate.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || candidate.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return WebhookErrorCodes.BranchFilterInvalid;
        }

        // An exact branch name cannot end with '/'; a wildcard prefix legitimately can (release/*).
        if (!isWildcard && candidate.EndsWith('/'))
        {
            return WebhookErrorCodes.BranchFilterInvalid;
        }

        return null;
    }
}
