using System.Security.Cryptography;
using Zenkins.Shared.Artifacts;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Worker.Artifacts;

/// <summary>
/// A declared artifact path the worker could not publish (card F-016). <see cref="FailureReason"/> is one of
/// <see cref="PipelineStepFailureReasons"/>' artifact values, so the step's terminal state needs no string
/// matching anywhere else; <see cref="Exception.Message"/> is the run-log line and always names either the
/// path or the limit.
/// </summary>
public sealed class ArtifactPublishException(string failureReason, string message) : Exception(message)
{
    /// <summary>The closed step failure reason this refusal maps to.</summary>
    public string FailureReason { get; } = failureReason;

    /// <summary>A refused path (traversal, absolute, escaping symlink, missing).</summary>
    public static ArtifactPublishException PathRejected(string message) =>
        new(PipelineStepFailureReasons.ArtifactPathRejected, message);

    /// <summary>A cap, an upload or a protocol failure.</summary>
    public static ArtifactPublishException UploadFailed(string message) =>
        new(PipelineStepFailureReasons.ArtifactFailed, message);
}

/// <summary>What a declared path resolved to inside the checkout (card F-016).</summary>
/// <param name="Kind">A <see cref="ArtifactKind"/> value.</param>
/// <param name="RelativePath">The normalised declared path (the artifact's name).</param>
/// <param name="Files">The files to publish, with paths relative to the artifact root.</param>
public sealed record ResolvedArtifact(
    string Kind,
    string RelativePath,
    IReadOnlyList<DeclaredArtifactFile> Files);

/// <summary>One file of a resolved artifact: its wire path, its host path and its size.</summary>
public sealed record DeclaredArtifactFile(string RelativePath, string FullPath, long Bytes);

/// <summary>
/// The three artifact protocol verbs the publisher needs (card F-016), so a test can drive the publisher's
/// own mapping and path rules against a fake: <c>create → per-file upload → finalize</c>.
/// </summary>
public interface IArtifactProtocolClient
{
    /// <summary>Reserves the artifact row; returns its id.</summary>
    Task<Guid> CreateAsync(Guid runId, string name, string kind, CancellationToken cancellationToken);

    /// <summary>Streams one file (raw body, path and SHA-256 as query parameters) and returns the stored entry.</summary>
    Task<ArtifactFileEntry> UploadFileAsync(
        Guid runId,
        Guid artifactId,
        string relativePath,
        string sha256,
        string filePath,
        CancellationToken cancellationToken);

    /// <summary>Publishes the artifact (the server verifies the set, writes the manifest, flips to Ready).</summary>
    Task<ArtifactFinalizeResponse> FinalizeAsync(
        Guid runId,
        Guid artifactId,
        ArtifactFileEntry[] files,
        CancellationToken cancellationToken);
}

/// <summary>What the executor needs from the artifact layer; a null implementation publishes nothing.</summary>
public interface IRunArtifactPublisher
{
    /// <summary>
    /// Resolves <paramref name="declaredPath"/> inside the run's checkout and publishes it as one artifact of
    /// the run, or throws <see cref="ArtifactPublishException"/> with the closed step failure reason.
    /// </summary>
    Task PublishAsync(string declaredPath, CancellationToken cancellationToken);
}

/// <summary>
/// The worker half of card F-016: turn one step-declared path (<c>artifacts = { "bin/Release" }</c>) into a
/// run artifact on the server.
/// <para>
/// <b>Source root: <c>work/{run-id}/repository</c> only.</b> A path is normalised with the shared
/// <see cref="ArtifactPathRules"/> (the same rules the server re-applies), resolved inside the checkout and
/// then <b>link-resolved component by component</b>: a symlink whose target leaves the repository is
/// refused by name, because a checkout is untrusted content and <c>ln -s /etc/passwd leak.txt</c> must not
/// publish the wrong file. Nothing under <c>work/{run-id}/artifacts/</c> is publishable - it is not the
/// source root - so a pipeline cannot launder a previous run's output as this run's.
/// </para>
/// <para>
/// <b>Caps are checked before the first byte is sent</b> (count, total, per file), naming the limit and its
/// configuration key; the server enforces the same caps again with its own configuration, because it must
/// not trust this binary. Every file is hashed here and the server recomputes the hash while it streams -
/// a mismatch is a refused upload, never a silently different artifact.
/// </para>
/// <para>
/// <b>Interruptible.</b> Every call takes the run's cancellation token: the executor passes the linked
/// shutdown + server-cancel token, so a cancel during publishing kills the upload, fails the step with
/// F-009's reason and leaves no <c>Ready</c> row (the reserved row is <c>Pending</c>, and the sweeper turns
/// an abandoned one <c>Incomplete</c>).
/// </para>
/// </summary>
public sealed class ArtifactPublisher : IRunArtifactPublisher
{
    private readonly IArtifactProtocolClient _api;
    private readonly Guid _runId;
    private readonly string _workspaceRoot;
    private readonly string _repositoryRoot;
    private readonly ArtifactLimits _limits;
    private readonly Action<string> _log;

    /// <param name="api">The worker's artifact protocol client.</param>
    /// <param name="runId">The run being executed (every call is run-scoped).</param>
    /// <param name="repositoryRoot">The checkout: <c>work/{run-id}/repository</c>. The only source root.</param>
    /// <param name="workspaceRoot">The run's directory: <c>work/{run-id}</c>, for the refusal messages.</param>
    /// <param name="limits">The worker's pre-check caps (the server's own are authoritative).</param>
    /// <param name="log">Where the artifact's own lines go (the run log).</param>
    public ArtifactPublisher(
        IArtifactProtocolClient api,
        Guid runId,
        string repositoryRoot,
        string workspaceRoot,
        ArtifactLimits limits,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(log);

        _api = api;
        _runId = runId;
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _limits = limits;
        _log = log;
    }

    /// <inheritdoc />
    public async Task PublishAsync(string declaredPath, CancellationToken cancellationToken)
    {
        try
        {
            await PublishCoreAsync(declaredPath, cancellationToken);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            // A transport failure mid-upload (a dropped connection, a closed socket, a file that vanished while
            // it was being sent) is this feature's failure, not a bug the run loop should swallow: it becomes the
            // step's closed reason, so the step can never be left Running by an escaped exception.
            throw ArtifactPublishException.UploadFailed(
                $"the artifact '{declaredPath}' could not be uploaded ({failure.Message}).");
        }
    }

    private async Task PublishCoreAsync(string declaredPath, CancellationToken cancellationToken)
    {
        var artifact = Resolve(declaredPath, _repositoryRoot, _workspaceRoot, _limits);
        _log(
            $"zenkins: publishing artifact '{artifact.RelativePath}' ({artifact.Kind}, "
            + $"{artifact.Files.Count} file(s), {ArtifactLimits.FormatBytes(artifact.Files.Sum(file => file.Bytes))}).");

        Guid artifactId;
        try
        {
            artifactId = await _api.CreateAsync(_runId, artifact.RelativePath, artifact.Kind, cancellationToken);
        }
        catch (ArtifactUploadRejectedException refusal)
        {
            throw ArtifactPublishException.UploadFailed(refusal.Message);
        }

        var entries = new List<ArtifactFileEntry>(artifact.Files.Count);
        foreach (var file in artifact.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sha256 = await HashFileAsync(file.FullPath, cancellationToken);
            try
            {
                var stored = await _api.UploadFileAsync(
                    _runId,
                    artifactId,
                    file.RelativePath,
                    sha256,
                    file.FullPath,
                    cancellationToken);
                entries.Add(stored);
            }
            catch (ArtifactUploadRejectedException refusal)
            {
                throw ArtifactPublishException.UploadFailed(refusal.Message);
            }
        }

        try
        {
            var finalized = await _api.FinalizeAsync(_runId, artifactId, [.. entries], cancellationToken);
            _log(
                $"zenkins: artifact '{artifact.RelativePath}' published: {finalized.FileCount} file(s), "
                + $"{ArtifactLimits.FormatBytes(finalized.TotalBytes)}.");
        }
        catch (ArtifactUploadRejectedException refusal)
        {
            throw ArtifactPublishException.UploadFailed(refusal.Message);
        }
    }

    /// <summary>
    /// Resolves one declared path inside the checkout (card F-016), refusing traversal, absolute and
    /// drive-qualified paths, an escaping symlink, a path that only exists outside the source root, and a
    /// path whose files exceed the declared caps. Pure file-system work: nothing is uploaded.
    /// </summary>
    /// <exception cref="ArtifactPublishException">The path is refused; the message names it.</exception>
    public static ResolvedArtifact Resolve(
        string declaredPath,
        string repositoryRoot,
        string workspaceRoot,
        ArtifactLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (!ArtifactPathRules.TryNormalize(declaredPath, out var relativePath, out var refusal))
        {
            throw ArtifactPublishException.PathRejected(refusal!.Message);
        }

        var root = Path.GetFullPath(repositoryRoot);
        string full;
        try
        {
            // The second lock: even a path that slipped past normalisation cannot resolve outside the checkout.
            full = ArtifactPathRules.ResolveInside(root, relativePath);
        }
        catch (ArtifactPathException)
        {
            throw ArtifactPublishException.PathRejected(
                $"the artifact path '{declaredPath}' leaves the checked-out repository.");
        }

        var resolved = ResolveLinks(root, full, declaredPath);

        if (File.Exists(resolved))
        {
            var length = new FileInfo(resolved).Length;
            EnforceCaps(declaredPath, limits, fileCount: 1, totalBytes: length, largestFile: length);
            return new ResolvedArtifact(
                ArtifactKind.File,
                relativePath,
                [new DeclaredArtifactFile(relativePath, resolved, length)]);
        }

        if (!Directory.Exists(resolved))
        {
            // A path that exists in the run's own work tree but not in the checkout (work/{run}/artifacts/…)
            // is refused with a message that says why: the run's artifacts directory is not a source root, so
            // one run's output can never be published as another run's artifact.
            var native = relativePath.Replace(ArtifactPathRules.Separator, Path.DirectorySeparatorChar);
            var inWorkspace = File.Exists(Path.Combine(Path.GetFullPath(workspaceRoot), native))
                || Directory.Exists(Path.Combine(Path.GetFullPath(workspaceRoot), native));
            throw ArtifactPublishException.PathRejected(
                $"the artifact path '{declaredPath}' does not exist in the checked-out repository ('{root}')"
                + (inWorkspace
                    ? "; only files of the checkout can be published, not the run's own artifacts directory."
                    : "."));
        }

        var files = new List<DeclaredArtifactFile>();
        CollectDirectory(root, resolved, string.Empty, declaredPath, files, []);

        EnforceCaps(
            declaredPath,
            limits,
            files.Count,
            files.Sum(file => file.Bytes),
            files.Count == 0 ? 0 : files.Max(file => file.Bytes));

        return new ResolvedArtifact(ArtifactKind.Directory, relativePath, files);
    }

    private static void EnforceCaps(
        string declaredPath,
        ArtifactLimits limits,
        int fileCount,
        long totalBytes,
        long largestFile)
    {
        if (fileCount > limits.MaxFiles)
        {
            throw ArtifactPublishException.UploadFailed(
                $"the artifact '{declaredPath}' holds {fileCount} files, over {limits.DescribeFileCountLimit()}.");
        }

        if (largestFile > limits.MaxFileBytes)
        {
            throw ArtifactPublishException.UploadFailed(
                $"a file of the artifact '{declaredPath}' is {ArtifactLimits.FormatBytes(largestFile)}, over "
                + $"{limits.DescribeFileLimit()}.");
        }

        if (totalBytes > limits.MaxArtifactBytes)
        {
            throw ArtifactPublishException.UploadFailed(
                $"the artifact '{declaredPath}' totals {ArtifactLimits.FormatBytes(totalBytes)}, over "
                + $"{limits.DescribeArtifactLimit()}.");
        }
    }

    /// <summary>
    /// Walks one directory of the checkout, link-resolving every entry and refusing a link whose target leaves
    /// the repository. A directory reached through a link is enumerated once (a visited set), so a loop or a
    /// diamond cannot make the walk unbounded - a hostile checkout must not be able to hang the worker.
    /// </summary>
    private static void CollectDirectory(
        string root,
        string directory,
        string prefix,
        string declaredPath,
        List<DeclaredArtifactFile> files,
        HashSet<string> visited)
    {
        if (!visited.Add(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(entry);
            var wirePath = prefix.Length == 0
                ? name
                : prefix + ArtifactPathRules.Separator + name;
            var resolved = ResolveLinks(root, entry, declaredPath);

            if (Directory.Exists(resolved))
            {
                CollectDirectory(root, resolved, wirePath, declaredPath, files, visited);
            }
            else if (File.Exists(resolved))
            {
                files.Add(new DeclaredArtifactFile(wirePath, resolved, new FileInfo(resolved).Length));
            }
        }
    }

    /// <summary>
    /// The real path of one entry with <b>every path component</b> link-resolved, not just the last one.
    /// <para>
    /// Resolving only the leaf is not enough: <c>artifact("link/secret.txt")</c> where <c>link</c> is a symlink to
    /// a directory outside the checkout has no link in its last component, so a leaf-only check publishes a file
    /// from outside the repository. Each component is therefore canonicalised in turn, and the first one that
    /// leaves the repository aborts the resolution with the <b>declared</b> path in the message.
    /// </para>
    /// </summary>
    private static string ResolveLinks(string root, string path, string declaredPath)
    {
        var full = Path.GetFullPath(path);
        if (!ArtifactPathRules.IsInside(root, full))
        {
            throw Outside(declaredPath, Path.GetRelativePath(root, full));
        }

        var relative = Path.GetRelativePath(root, full);
        if (relative is "." or "")
        {
            return full;
        }

        var current = root;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = ResolveEntryTarget(Path.Combine(current, segment));
            if (!ArtifactPathRules.IsInside(root, current))
            {
                throw Outside(declaredPath, relative);
            }
        }

        return current;
    }

    /// <summary>
    /// One component's canonical path: the link target (resolved to its final target) when the entry is a
    /// symlink, otherwise the path itself. A broken link keeps its own path, so the existence check reports
    /// "does not exist" rather than "leaves the repository".
    /// </summary>
    private static string ResolveEntryTarget(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is null)
        {
            return Path.GetFullPath(path);
        }

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? Path.GetFullPath(path) : Path.GetFullPath(target.FullName);
    }

    private static ArtifactPublishException Outside(string declaredPath, string component) =>
        ArtifactPublishException.PathRejected(
            $"the artifact path '{declaredPath}' links outside the checked-out repository "
            + $"('{component}' resolves outside it).");

    /// <summary>The file's SHA-256 (lowercase hex), computed by streaming it once.</summary>
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(file, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
