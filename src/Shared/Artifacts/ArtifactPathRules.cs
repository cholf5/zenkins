namespace Zenkins.Shared.Artifacts;

/// <summary>
/// Why a declared artifact path was refused (card F-016). The <see cref="Path"/> is the path <b>as
/// declared</b>, so the message names what the pipeline wrote - the whole point of a rejection is that
/// the person who wrote <c>artifact("../../etc/passwd")</c> can find it.
/// </summary>
public sealed record ArtifactPathRefusal(string Path, string Reason)
{
    /// <summary>One log/error line: <c>the artifact path '../../etc/passwd' leaves the artifact root.</c></summary>
    public string Message => $"the artifact path '{Path}' {Reason}";
}

/// <summary>
/// The one set of path rules for artifact paths (card F-016), used by <b>both</b> sides of the upload:
/// the Worker before it sends anything, and the Server on every relative path it receives, because the
/// Server must not trust a worker binary. Sharing the implementation is what keeps the two independent
/// validations from drifting - the test suite runs one vector table through both.
/// <para>
/// <b>What is refused.</b> An absolute path (<c>/etc/passwd</c>), a drive-qualified Windows path
/// (<c>C:\Windows</c> or <c>C:relative</c>), a path containing any other <c>:</c> (a stream name or an
/// alternate data stream), a control character, a path that escapes its root after canonicalisation
/// (<c>../../etc/passwd</c>, <c>bin/../../x</c>) and an empty result (<c>.</c>, <c>./</c>).
/// </para>
/// <para>
/// <b>What is normalised.</b> Both separators are treated as separators (<c>\</c> and <c>/</c>), so the
/// verdict does not depend on the host OS; <c>.</c> segments and empty segments are dropped and internal
/// <c>..</c> segments are resolved (<c>bin/../Release</c> → <c>Release</c>). The result always uses
/// <c>/</c> as its separator - it is a wire value, stored in the manifest and restored by the zip
/// download, not a host path.
/// </para>
/// <para>
/// <b>What this cannot see.</b> Symlinks are a file-system fact, so they are resolved where the files
/// actually are: the Worker resolves every path component inside the checkout before uploading, and the
/// <b>target</b> of a link that points outside is refused by name. The Server never follows a link: it
/// only ever writes new files under its own store, whose paths it built itself from an already
/// normalised relative path.
/// </para>
/// </summary>
public static class ArtifactPathRules
{
    /// <summary>The longest relative path a store row (and the manifest) accepts.</summary>
    public const int MaxRelativePathLength = 512;

    /// <summary>The separators both sides of the protocol accept; the normalised form uses <see cref="Separator"/>.</summary>
    private static readonly char[] InputSeparators = ['/', '\\'];

    /// <summary>The one separator in a normalised relative path (a wire value, not a host path).</summary>
    public const char Separator = '/';

    /// <summary>
    /// Canonicalises a declared path into the relative form the protocol, the store and the zip use.
    /// </summary>
    /// <param name="declaredPath">The path as the pipeline declared it (may be null when a Lua value was not a string).</param>
    /// <param name="relativePath">The normalised relative path (<c>bin/Release</c>) on success; empty otherwise.</param>
    /// <param name="refusal">Why the path was refused, or <see langword="null"/> on success.</param>
    public static bool TryNormalize(
        string? declaredPath,
        out string relativePath,
        out ArtifactPathRefusal? refusal)
    {
        relativePath = string.Empty;
        refusal = null;

        if (string.IsNullOrWhiteSpace(declaredPath))
        {
            refusal = new ArtifactPathRefusal(declaredPath ?? string.Empty, "is empty.");
            return false;
        }

        if (declaredPath.Length > MaxRelativePathLength)
        {
            refusal = new ArtifactPathRefusal(
                declaredPath,
                $"is longer than {MaxRelativePathLength} characters.");
            return false;
        }

        if (declaredPath.Any(character => char.IsControl(character)))
        {
            refusal = new ArtifactPathRefusal(declaredPath, "contains a control character.");
            return false;
        }

        // Drive-qualified first, so "C:\tmp\x" gets the specific reason rather than the generic ':' one.
        if (IsDriveQualified(declaredPath))
        {
            refusal = new ArtifactPathRefusal(declaredPath, "is a drive-qualified path; artifacts are relative to the repository.");
            return false;
        }

        if (declaredPath.Contains(':'))
        {
            refusal = new ArtifactPathRefusal(declaredPath, "contains ':'; artifacts are plain relative paths.");
            return false;
        }

        if (IsRooted(declaredPath))
        {
            refusal = new ArtifactPathRefusal(declaredPath, "is an absolute path; artifacts are relative to the repository.");
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in declaredPath.Split(InputSeparators))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    // The '..' has nowhere to go: the path leaves its root. Reported with the declared
                    // text, because that is what the pipeline author wrote.
                    refusal = new ArtifactPathRefusal(
                        declaredPath,
                        "leaves the artifact root ('..' above the declared root).");
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            refusal = new ArtifactPathRefusal(declaredPath, "resolves to no path.");
            return false;
        }

        relativePath = string.Join(Separator, segments);
        return true;
    }

    /// <summary>
    /// The absolute path of <paramref name="relativePath"/> inside <paramref name="root"/>, refusing a
    /// path that would land outside it. This is the second lock, applied after
    /// <see cref="TryNormalize"/>: even a caller that skipped normalisation cannot make the returned
    /// path escape, because the comparison happens on the fully resolved form.
    /// </summary>
    /// <exception cref="ArtifactPathException">The path escapes <paramref name="root"/>.</exception>
    public static string ResolveInside(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var rootFull = Path.GetFullPath(root);
        var native = relativePath.Replace(Separator, Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, native));
        if (!IsInside(rootFull, full))
        {
            throw new ArtifactPathException(
                $"the path '{relativePath}' leaves '{rootFull}'; refusing to touch it.");
        }

        return full;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> itself or lives under it. Both
    /// paths must already be absolute (callers pass <see cref="Path.GetFullPath(string)"/> output).
    /// </summary>
    public static bool IsInside(string root, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmedRoot, comparison))
        {
            return true;
        }

        return candidate.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, comparison)
            || candidate.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Whether the declared text is drive-qualified (<c>C:\x</c>, <c>C:x</c>, <c>C:/x</c>). Checked on
    /// every platform, so a Windows-shaped path declared on Linux is refused by the same rule - the
    /// worker and the server must not disagree because they run on different hosts.
    /// </summary>
    public static bool IsDriveQualified(string declaredPath) =>
        declaredPath.Length >= 2
        && char.IsAsciiLetter(declaredPath[0])
        && declaredPath[1] == ':';

    private static bool IsRooted(string declaredPath) =>
        declaredPath.Length > 0
        && (InputSeparators.Contains(declaredPath[0]) || Path.IsPathRooted(declaredPath));
}

/// <summary>A path that would leave the directory it is resolved against (card F-016).</summary>
public sealed class ArtifactPathException(string message) : Exception(message);
