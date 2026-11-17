namespace Zenkins.Shared.Projects;

/// <summary>
/// Card F-023: how a repository Pipeline project decides which branches it builds.
/// <para>
/// <b>A project setting, not a project kind.</b> A <see cref="ProjectKinds.Freestyle"/> project and an
/// inline Pipeline project have no repository and therefore no branches, so neither can be switched to
/// <see cref="Branches"/>; the setting is only read for a repository Pipeline. The kind and the
/// definition source stay what they are - this value only changes which branches the one project
/// discovers, never where a run's definition comes from.
/// </para>
/// <para>
/// <b><see cref="Single"/> is the historical behaviour and the default.</b> Every value stored before
/// this card - and every project created without an explicit choice - is <see cref="Single"/>: one
/// default branch, one optional per-trigger override, and a webhook branch filter. Nothing about a
/// single-branch project's triggers, filtering or history changes.
/// </para>
/// </summary>
public static class BranchDiscoveryModes
{
    /// <summary>One job on the project's default branch (today's behaviour).</summary>
    public const string Single = "single";

    /// <summary>One branch job per discovered branch of the repository.</summary>
    public const string Branches = "branches";

    /// <summary>Every value the setting can carry.</summary>
    public static IReadOnlyList<string> All { get; } = [Single, Branches];

    /// <summary>Whether <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>Whether the project discovers branches (rather than building one branch).</summary>
    public static bool IsMultibranch(string? value) =>
        string.Equals(value, Branches, StringComparison.Ordinal);

    /// <summary>
    /// The stored value: a known mode, or <see cref="Single"/> for null/blank/unknown text. Read paths
    /// use this so a row written before the column existed behaves exactly like today.
    /// </summary>
    public static string Normalize(string? value) => IsKnown(value) ? value! : Single;
}
