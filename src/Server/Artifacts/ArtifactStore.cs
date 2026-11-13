using System.Security.Cryptography;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Server.Artifacts;

/// <summary>
/// One file the store wrote: its absolute path, its SHA-256 (lowercase hex, computed while streaming) and
/// its size.
/// </summary>
public sealed record StoredArtifactFile(string FullPath, string Sha256, long Bytes);

/// <summary>
/// The on-disk half of the artifact feature (card F-016): <b>the only code that turns an
/// (organization, run, artifact) key into a path</b>. The layout is
/// <c>{root}/{organizationId}/{runId}/{artifactId}/{relativePath}</c>; the row holds only the id, so no
/// client-supplied path is ever stored and a download can never be pointed somewhere else.
/// <para>
/// <b>Two locks on every path.</b> The relative path comes from an untrusted worker, so it is normalised
/// by <see cref="ArtifactPathRules.TryNormalize"/> first (in the service, so the refusal message names the
/// declared path) and then resolved by <see cref="ArtifactPathRules.ResolveInside"/> against the artifact's
/// own directory here. Both are the shared rules the Worker uses, so the two sides cannot drift.
/// </para>
/// <para>
/// <b>Streaming, not buffering.</b> A file body is copied with a 64 KiB buffer straight to disk while its
/// SHA-256 is computed; nothing the client sends is ever held whole in memory. A cap breach deletes the
/// partial file before throwing, so a rejected upload leaves no bytes behind.
/// </para>
/// </summary>
public sealed class ArtifactStore
{
    /// <summary>The copy buffer: bounded, so a 512 MB artifact costs 64 KiB of managed memory per copy.</summary>
    private const int CopyBufferSize = 64 * 1024;

    private readonly ArtifactOptions _options;
    private readonly ArtifactTransferTelemetry _telemetry;

    public ArtifactStore(ArtifactOptions options, ArtifactTransferTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(telemetry);
        _options = options;
        _telemetry = telemetry;
    }

    /// <summary>The absolute store root every organization/run/artifact tree lives under.</summary>
    public string RootPath => _options.RootPath;

    /// <summary>The caps every upload is checked against.</summary>
    public ArtifactLimits Limits => _options.Limits;

    /// <summary>One run's artifact directory: <c>{root}/{org}/{run}</c>. Deleting it removes all its artifacts.</summary>
    public string RunDirectory(Guid organizationId, Guid runId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(organizationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(runId, Guid.Empty);
        return Path.Combine(RootPath, organizationId.ToString("D"), runId.ToString("D"));
    }

    /// <summary>One artifact's directory: <c>{root}/{org}/{run}/{artifact}</c>.</summary>
    public string ArtifactDirectory(Guid organizationId, Guid runId, Guid artifactId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(artifactId, Guid.Empty);
        return Path.Combine(
            RunDirectory(organizationId, runId),
            artifactId.ToString("D"));
    }

    /// <summary>
    /// The absolute path of <paramref name="relativePath"/> inside one artifact's directory. Throws
    /// <see cref="ArtifactPathException"/> when the resolved path would leave it.
    /// </summary>
    public string ResolveFile(Guid organizationId, Guid runId, Guid artifactId, string relativePath) =>
        ArtifactPathRules.ResolveInside(
            ArtifactDirectory(organizationId, runId, artifactId),
            relativePath);

    /// <summary>
    /// Streams <paramref name="body"/> into <paramref name="targetPath"/> (bounded buffer, SHA-256 computed
    /// on the way through) and enforces the per-file cap.
    /// </summary>
    /// <param name="targetPath">An absolute path already resolved by <see cref="ResolveFile"/>.</param>
    /// <param name="body">The request body (a file stream on the response side of the protocol).</param>
    /// <param name="declaredLength">
    /// The client's <c>Content-Length</c>, when it sent one. Used to refuse an over-cap upload <b>before</b>
    /// writing anything; the streaming cap below is what actually enforces it.
    /// </param>
    /// <param name="relativePath">The normalized relative path, for the refusal message.</param>
    /// <param name="cancellationToken">The request's token.</param>
    public async Task<StoredArtifactFile> WriteFileAsync(
        string targetPath,
        Stream body,
        long? declaredLength,
        string relativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var limit = _options.Limits.MaxFileBytes;
        if (declaredLength is { } declared && declared > limit)
        {
            throw new ArtifactLimitExceededException(
                $"the file '{relativePath}' is {ArtifactLimits.FormatBytes(declared)} and the limit is "
                + $"{_options.Limits.DescribeFileLimit()}.");
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        long written = 0;
        string sha256;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var file = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[CopyBufferSize];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    _telemetry.ObserveChunk(read);
                    written += read;
                    if (written > limit)
                    {
                        throw new ArtifactLimitExceededException(
                            $"the file '{relativePath}' is larger than {_options.Limits.DescribeFileLimit()}.");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
        catch
        {
            // A refused or interrupted upload leaves no half-file behind: the next file with the same path
            // must not inherit bytes from a request that was rejected.
            TryDelete(targetPath);
            throw;
        }

        _telemetry.RecordFile(written);
        return new StoredArtifactFile(targetPath, sha256, written);
    }

    /// <summary>The relative (<c>/</c>-separated) paths of every file in one artifact, sorted ordinally.</summary>
    public IReadOnlyList<string> EnumerateRelativeFiles(Guid organizationId, Guid runId, Guid artifactId)
    {
        var directory = ArtifactDirectory(organizationId, runId, artifactId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return
        [
            .. Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(prefix, path).Replace(Path.DirectorySeparatorChar, ArtifactPathRules.Separator))
                .OrderBy(path => path, StringComparer.Ordinal),
        ];
    }

    /// <summary>Opens one stored file for streaming to a client (async, sequential scan, shared read).</summary>
    public FileStream OpenRead(string absolutePath) => new(
        absolutePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        CopyBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Copies one stored file into a response stream with the same bounded buffer the upload uses (card
    /// F-016: downloads are streamed, so memory stays bounded for an artifact of any size) and records the
    /// transfer counters the streaming test asserts on.
    /// </summary>
    public async Task CopyFileToAsync(string absolutePath, Stream output, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentNullException.ThrowIfNull(output);

        long written = 0;
        var buffer = new byte[CopyBufferSize];
        await using var file = OpenRead(absolutePath);
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
        {
            _telemetry.ObserveChunk(read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
        }

        _telemetry.RecordSent(written);
    }

    /// <summary>
    /// Deletes one artifact's whole directory. A missing directory is fine: the sweeper must be able to
    /// clean up a row whose bytes never landed.
    /// </summary>
    public void DeleteArtifactDirectory(Guid organizationId, Guid runId, Guid artifactId)
    {
        var directory = ArtifactDirectory(organizationId, runId, artifactId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Deletes one run's whole artifact tree (used by the sweeper and by tests).</summary>
    public void DeleteRunDirectory(Guid organizationId, Guid runId)
    {
        var directory = RunDirectory(organizationId, runId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The original failure is the one worth reporting; a leftover partial file is unreachable
            // (its row is not Ready) and the sweeper deletes the directory with the row.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>One of the three caps was exceeded (card F-016); the message names the limit and its key.</summary>
public sealed class ArtifactLimitExceededException(string message) : Exception(message);
