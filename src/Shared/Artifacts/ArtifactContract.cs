namespace Zenkins.Shared.Artifacts;

/// <summary>
/// The two shapes a run artifact can take (card F-016, PRD §3.7): one file, or a directory whose tree
/// is kept. Stored as a string so the model stays provider-neutral, exactly like the run and step
/// statuses; the Server's check constraint keeps out-of-band writes honest.
/// </summary>
public static class ArtifactKind
{
    /// <summary>A single file: the download streams it with its own name.</summary>
    public const string File = "File";

    /// <summary>A directory: the download is a zip that restores the declared tree.</summary>
    public const string Directory = "Directory";

    /// <summary>Every kind a <c>create</c> request may name.</summary>
    public static IReadOnlyList<string> All { get; } = [File, Directory];

    /// <summary>Whether <paramref name="kind"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// The lifecycle of one artifact row (card F-016). <see cref="Pending"/> is the state between the
/// <c>create</c> call and a successful <c>finalize</c>: files may be streaming into the store, but the
/// artifact is <b>not</b> downloadable and the row is not a claim about the build output.
/// <para>
/// <b>Only <see cref="Ready"/> is served.</b> A worker that dies mid-upload leaves its row
/// <see cref="Pending"/>, which the sweeper turns into <see cref="Incomplete"/> - so a partially uploaded
/// artifact can never be downloaded, and no row stays Pending forever. The bytes an abandoned upload left
/// behind are <b>kept</b> (the row and its store directory are the diagnosis); only the row's state says the
/// artifact is unusable.
/// </para>
/// </summary>
public static class ArtifactStatus
{
    /// <summary>Reserved and being filled; never downloadable.</summary>
    public const string Pending = "Pending";

    /// <summary>Uploaded, hashed and manifested: the only state a download can reach.</summary>
    public const string Ready = "Ready";

    /// <summary>
    /// Abandoned mid-upload (the sweeper's or a refusal's verdict). Not downloadable; the bytes it had
    /// already received are kept for diagnosis.
    /// </summary>
    public const string Incomplete = "Incomplete";

    /// <summary>Every state a row can carry.</summary>
    public static IReadOnlyList<string> All { get; } = [Pending, Ready, Incomplete];
}

/// <summary>
/// The explicit caps of one artifact upload (card F-016). Every cap is a <b>loud</b> failure and the
/// message names the configuration key, because a truncated artifact would be a lie about the build
/// output: silently dropping the file that did not fit is the one outcome that must never happen.
/// <para>
/// <b>Where each cap lives.</b> The Server is the authority - it re-checks every file it receives
/// against its own configured limits, because it must not trust the worker binary. The Worker's
/// pre-checks use <see cref="Defaults"/> to fail early with the same wording; a stricter Server limit
/// still wins, and the upload's answer carries its message.
/// </para>
/// </summary>
/// <param name="MaxFileBytes">Largest single file accepted (<c>Artifacts:MaxFileBytes</c>).</param>
/// <param name="MaxArtifactBytes">Largest total of one artifact (<c>Artifacts:MaxArtifactBytes</c>).</param>
/// <param name="MaxFiles">Most files one artifact may contain (<c>Artifacts:MaxFiles</c>).</param>
public sealed record ArtifactLimits(long MaxFileBytes, long MaxArtifactBytes, int MaxFiles)
{
    /// <summary>Configuration key of <see cref="MaxFileBytes"/>.</summary>
    public const string MaxFileBytesConfigurationKey = "Artifacts:MaxFileBytes";

    /// <summary>Configuration key of <see cref="MaxArtifactBytes"/>.</summary>
    public const string MaxArtifactBytesConfigurationKey = "Artifacts:MaxArtifactBytes";

    /// <summary>Configuration key of <see cref="MaxFiles"/>.</summary>
    public const string MaxFilesConfigurationKey = "Artifacts:MaxFiles";

    /// <summary>512 MB: a build product larger than this is not what this feature is for.</summary>
    public const long DefaultMaxFileBytes = 512L * 1024 * 1024;

    /// <summary>2 GB per artifact, the sum of its files.</summary>
    public const long DefaultMaxArtifactBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>20 000 files - a directory artifact is a build product, not a tree walk of the machine.</summary>
    public const int DefaultMaxFiles = 20_000;

    /// <summary>The limits used when nothing is configured (and by the Worker's early pre-checks).</summary>
    public static ArtifactLimits Defaults { get; } = new(DefaultMaxFileBytes, DefaultMaxArtifactBytes, DefaultMaxFiles);

    /// <summary>
    /// Reads the three caps through <paramref name="read"/> (a configuration lookup), falling back to
    /// <see cref="Defaults"/> per key for a missing, unparsable or non-positive value. A zero or negative
    /// cap would mean "refuse everything", which is never an intention worth honouring silently.
    /// </summary>
    public static ArtifactLimits FromConfiguration(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return new ArtifactLimits(
            ReadPositiveLong(read(MaxFileBytesConfigurationKey), DefaultMaxFileBytes),
            ReadPositiveLong(read(MaxArtifactBytesConfigurationKey), DefaultMaxArtifactBytes),
            (int)Math.Min(
                ReadPositiveLong(read(MaxFilesConfigurationKey), DefaultMaxFiles),
                int.MaxValue));
    }

    /// <summary>The per-file cap as a message fragment that names its configuration key and value.</summary>
    public string DescribeFileLimit() =>
        $"{MaxFileBytesConfigurationKey} ({FormatBytes(MaxFileBytes)} per file)";

    /// <summary>The per-artifact total as a message fragment that names its configuration key and value.</summary>
    public string DescribeArtifactLimit() =>
        $"{MaxArtifactBytesConfigurationKey} ({FormatBytes(MaxArtifactBytes)} per artifact)";

    /// <summary>The file-count cap as a message fragment that names its configuration key and value.</summary>
    public string DescribeFileCountLimit() =>
        $"{MaxFilesConfigurationKey} ({MaxFiles} files per artifact)";

    /// <summary>A human-readable size ("512 MB", "1.5 MB"), for messages only.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} bytes"
            : $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static long ReadPositiveLong(string? raw, long fallback) =>
        long.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
}

/// <summary>
/// The upload protocol's payloads (card F-016): <c>create</c> reserves the row, one <c>files</c> call per
/// file streams it with its relative path and SHA-256, <c>finalize</c> writes the manifest and flips the
/// row to <see cref="ArtifactStatus.Ready"/>.
/// <para>
/// <b>Why three verbs instead of one multipart request.</b> A future S3-compatible backend implements the
/// same three shapes (reserve, put object, commit), and a per-file request keeps the Server's memory
/// bounded: the body is streamed straight to disk, never buffered into a request model.
/// </para>
/// </summary>
/// <param name="Name">The declared path (<c>artifact("bin/Release")</c>), also the artifact's identity in its run.</param>
/// <param name="Kind">A <see cref="ArtifactKind"/> value.</param>
public sealed record ArtifactCreateRequest(string? Name, string? Kind);

/// <summary>The reserved artifact's id, which every later verb addresses.</summary>
public sealed record ArtifactCreateResponse(Guid ArtifactId, string Status);

/// <summary>
/// One completed file of an artifact: the <b>normalised relative path</b> inside the artifact, the
/// SHA-256 of its bytes (lowercase hex) and its size.
/// </summary>
public sealed record ArtifactFileEntry(string Path, string Sha256, long Bytes);

/// <summary>
/// The <c>finalize</c> payload: every file the worker streamed, so the Server can prove the set it holds
/// is the set that was announced - and refuse a silent truncation instead of publishing it.
/// </summary>
public sealed record ArtifactFinalizeRequest(ArtifactFileEntry[]? Files);

/// <summary>The finalized artifact's summary (also what the run page renders).</summary>
public sealed record ArtifactFinalizeResponse(Guid ArtifactId, string Status, int FileCount, long TotalBytes);

/// <summary>One artifact as the run page lists it (card F-016).</summary>
/// <param name="Id">The artifact id (also the download URL's last segment).</param>
/// <param name="Name">The declared path the pipeline published.</param>
/// <param name="Kind">A <see cref="ArtifactKind"/> value.</param>
/// <param name="Status">A <see cref="ArtifactStatus"/> value.</param>
/// <param name="FileCount">How many files were finalized.</param>
/// <param name="TotalBytes">Their total size.</param>
public sealed record ArtifactView(Guid Id, string Name, string Kind, string Status, int FileCount, long TotalBytes)
{
    /// <summary>Whether this artifact can be downloaded (only a finalized artifact is served).</summary>
    public bool CanDownload => Status == ArtifactStatus.Ready;
}
