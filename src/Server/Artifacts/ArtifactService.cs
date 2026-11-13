using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Workers;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Server.Artifacts;

/// <summary>Outcome of one artifact operation. The HTTP layer maps each value to one status code.</summary>
public enum ArtifactOperationStatus
{
    Success = 0,

    /// <summary>The presented worker token is missing, unknown or revoked. Always a bare 401.</summary>
    InvalidToken = 1,

    /// <summary>The run or artifact id does not exist in the active tenant (also the foreign case).</summary>
    NotFound = 2,

    /// <summary>The payload failed validation (a refused path, an unknown kind, a hash mismatch).</summary>
    InvalidInput = 3,

    /// <summary>The artifact is not in a state that allows this call (duplicate name, already finalized).</summary>
    Conflict = 4,

    /// <summary>One of the caps was exceeded; the message names the limit and its configuration key.</summary>
    TooLarge = 5,
}

/// <summary>The stable error codes the artifact API answers with (the Worker logs them verbatim).</summary>
public static class ArtifactErrorCodes
{
    public const string FieldInvalid = "artifact-invalid";
    public const string RunNotRunning = "run-not-running";
    public const string RunNotAssignedToWorker = "run-not-assigned-to-worker";
    public const string DuplicateName = "artifact-duplicate-name";
    public const string NotPending = "artifact-not-pending";
    public const string PathRejected = "artifact-path-rejected";
    public const string LimitExceeded = "artifact-limit-exceeded";
    public const string HashMismatch = "artifact-hash-mismatch";
    public const string FileSetMismatch = "artifact-file-set-mismatch";
    public const string FileMissing = "artifact-file-missing";
}

/// <summary>The <c>create</c> answer: the reserved row's id, or the refusal.</summary>
public sealed record ArtifactCreateResult(
    ArtifactOperationStatus Status,
    Guid? ArtifactId = null,
    string? ErrorCode = null,
    string? Message = null);

/// <summary>The <c>files</c> answer: the stored file's entry (path, sha256, bytes), or the refusal.</summary>
public sealed record ArtifactFileResult(
    ArtifactOperationStatus Status,
    ArtifactFileEntry? File = null,
    string? ErrorCode = null,
    string? Message = null);

/// <summary>The <c>finalize</c> answer: the published summary, or the refusal.</summary>
public sealed record ArtifactFinalizeResult(
    ArtifactOperationStatus Status,
    ArtifactFinalizeResponse? Artifact = null,
    string? ErrorCode = null,
    string? Message = null);

/// <summary>One downloadable artifact: the row and the files it holds (relative paths, sorted).</summary>
public sealed record ArtifactDownload(Artifact Artifact, IReadOnlyList<string> RelativeFiles);

/// <summary>
/// The artifact feature's server-side logic (card F-016): <c>create → files → finalize</c> for the worker,
/// the run page's listing and the download gate.
/// <para>
/// <b>The Server does not trust the worker.</b> Every relative path it receives is re-validated with the
/// shared <see cref="ArtifactPathRules"/> (the same rules the Worker applies before sending), every file's
/// SHA-256 is recomputed while its bytes stream to disk and compared to the announced one, and every cap is
/// re-checked against the Server's own <see cref="ArtifactOptions.Limits"/>. That is why the traversal test
/// crafts an upload with a valid worker token: the second validation must hold on its own.
/// </para>
/// <para>
/// <b>Only a finalized artifact is served.</b> <see cref="ArtifactStatus.Ready"/> is written exactly once,
/// by <see cref="FinalizeAsync"/>, after the manifest's file set, per-file hashes and totals all check out.
/// A cap breach, a hash mismatch or an abandoned upload leaves the row not-Ready (the row is marked
/// <see cref="ArtifactStatus.Incomplete"/> so a human can see what happened), and
/// <see cref="OpenDownloadAsync"/> refuses anything that is not Ready.
/// </para>
/// </summary>
public sealed class ArtifactService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    WorkerService workerService,
    ArtifactStore store,
    ArtifactOptions options,
    ISystemDataScope systemDataScope,
    ILogger<ArtifactService> logger)
{
    private const int ManifestSchemaVersion = 1;

    /// <summary>The configured caps. Exposed so the upload endpoint can raise its request-body ceiling to them.</summary>
    public ArtifactLimits Limits => options.Limits;

    // ---------------------------------------------------------------------
    // Worker API: create → files → finalize
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reserves one artifact row for a run this worker executes: the name (a declared path) and the kind.
    /// The row starts <c>Pending</c>; a second artifact with the same name in the same run is a conflict,
    /// because two artifacts with one name would make the download ambiguous.
    /// </summary>
    public async Task<ArtifactCreateResult> CreateAsync(
        string? plaintextToken,
        Guid runId,
        ArtifactCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return new ArtifactCreateResult(ArtifactOperationStatus.InvalidToken);
        }

        try
        {
            if (!ArtifactKind.IsKnown(request.Kind))
            {
                return Invalid(ArtifactErrorCodes.FieldInvalid, "kind must be File or Directory.");
            }

            if (!ArtifactPathRules.TryNormalize(request.Name, out var name, out var refusal))
            {
                return Invalid(ArtifactErrorCodes.PathRejected, refusal!.Message);
            }

            var run = await FindRunningRunAsync(context, runId, worker.Id, cancellationToken);
            if (run is null)
            {
                var runRefusal = await RunRefusalAsync(context, runId, worker.Id, cancellationToken);
                return new ArtifactCreateResult(
                    runRefusal.Status,
                    ErrorCode: runRefusal.ErrorCode,
                    Message: runRefusal.Message);
            }

            // F-016: a duplicate name in the same run is a pipeline bug (the run page's download links
            // would be ambiguous), so it is refused before anything is written. The unique index is the
            // real guard - this read is only what turns a race into a readable message.
            var existing = await context.Set<Artifact>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.RunId == runId && candidate.Name == name,
                    cancellationToken);
            if (existing is not null)
            {
                return new ArtifactCreateResult(
                    ArtifactOperationStatus.Conflict,
                    ErrorCode: ArtifactErrorCodes.DuplicateName,
                    Message: $"this run already publishes an artifact named '{name}'.");
            }

            var artifact = new Artifact
            {
                OrganizationId = run.OrganizationId,
                RunId = runId,
                Name = name,
                Kind = request.Kind!,
                Status = ArtifactStatus.Pending,
                ManifestJson = SerializeManifest(new ArtifactManifest(ManifestSchemaVersion, 0, 0, null, [])),
            };
            context.Set<Artifact>().Add(artifact);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Artifacts"))
            {
                // Two concurrent creates of the same name: the database decides, and the loser answers
                // the same message as the pre-flight read above.
                return new ArtifactCreateResult(
                    ArtifactOperationStatus.Conflict,
                    ErrorCode: ArtifactErrorCodes.DuplicateName,
                    Message: $"this run already publishes an artifact named '{name}'.");
            }

            logger.LogInformation(
                "Artifact reserved. OrganizationId={OrganizationId} RunId={RunId} ArtifactId={ArtifactId} Name={Name} Kind={Kind}",
                artifact.OrganizationId,
                runId,
                artifact.Id,
                artifact.Name,
                artifact.Kind);

            return new ArtifactCreateResult(ArtifactOperationStatus.Success, artifact.Id);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Streams one file of a pending artifact into the store. The path is validated independently of the
    /// worker, the bytes are hashed on the way through and compared to the announced SHA-256, and every cap
    /// is enforced - a breach marks the artifact <c>Incomplete</c> so it can never be downloaded.
    /// </summary>
    public async Task<ArtifactFileResult> UploadFileAsync(
        string? plaintextToken,
        Guid runId,
        Guid artifactId,
        string? relativePath,
        string? declaredSha256,
        long? declaredLength,
        Stream body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return new ArtifactFileResult(ArtifactOperationStatus.InvalidToken);
        }

        try
        {
            if (!ArtifactPathRules.TryNormalize(relativePath, out var path, out var refusal))
            {
                return new ArtifactFileResult(
                    ArtifactOperationStatus.InvalidInput,
                    ErrorCode: ArtifactErrorCodes.PathRejected,
                    Message: refusal!.Message);
            }

            if (declaredSha256 is not null && !IsSha256Hex(declaredSha256))
            {
                return new ArtifactFileResult(
                    ArtifactOperationStatus.InvalidInput,
                    ErrorCode: ArtifactErrorCodes.FieldInvalid,
                    Message: "sha256 must be 64 lowercase hex characters.");
            }

            var run = await FindRunningRunAsync(context, runId, worker.Id, cancellationToken);
            if (run is null)
            {
                var runRefusal = await RunRefusalAsync(context, runId, worker.Id, cancellationToken);
                return new ArtifactFileResult(
                    runRefusal.Status,
                    ErrorCode: runRefusal.ErrorCode,
                    Message: runRefusal.Message);
            }

            var artifact = await context.Set<Artifact>().SingleOrDefaultAsync(
                candidate => candidate.Id == artifactId && candidate.RunId == runId,
                cancellationToken);
            if (artifact is null)
            {
                return new ArtifactFileResult(ArtifactOperationStatus.NotFound);
            }

            if (artifact.Status != ArtifactStatus.Pending)
            {
                return new ArtifactFileResult(
                    ArtifactOperationStatus.Conflict,
                    ErrorCode: ArtifactErrorCodes.NotPending,
                    Message: $"artifact '{artifact.Name}' is {artifact.Status}, so it accepts no more files.");
            }

            var entries = ReadEntries(artifact);

            // The caps are checked before anything is written, against the totals already on disk, so an
            // over-cap artifact costs the worker a refusal without a wasted transfer where possible.
            var existing = entries.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.Ordinal));
            var projectedCount = entries.Length + (existing is null ? 1 : 0);
            var projectedBytes = (existing is null ? artifact.TotalBytes : artifact.TotalBytes - existing.Bytes)
                + (declaredLength ?? 0);
            if (projectedCount > options.Limits.MaxFiles)
            {
                return await RefuseAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.TooLarge,
                    ArtifactErrorCodes.LimitExceeded,
                    $"the artifact '{artifact.Name}' would hold {projectedCount} files, over "
                    + $"{options.Limits.DescribeFileCountLimit()}.",
                    cancellationToken);
            }

            if (declaredLength is { } length && projectedBytes > options.Limits.MaxArtifactBytes)
            {
                return await RefuseAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.TooLarge,
                    ArtifactErrorCodes.LimitExceeded,
                    $"the artifact '{artifact.Name}' would hold {ArtifactLimits.FormatBytes(projectedBytes)}, over "
                    + $"{options.Limits.DescribeArtifactLimit()}.",
                    cancellationToken);
            }

            var target = store.ResolveFile(artifact.OrganizationId, artifact.RunId, artifact.Id, path);
            StoredArtifactFile stored;
            try
            {
                stored = await store.WriteFileAsync(target, body, declaredLength, path, cancellationToken);
            }
            catch (ArtifactLimitExceededException limit)
            {
                return await RefuseAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.TooLarge,
                    ArtifactErrorCodes.LimitExceeded,
                    limit.Message,
                    cancellationToken);
            }
            catch (ArtifactPathException escape)
            {
                return await RefuseAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.InvalidInput,
                    ArtifactErrorCodes.PathRejected,
                    escape.Message,
                    cancellationToken);
            }

            if (declaredSha256 is not null
                && !string.Equals(declaredSha256, stored.Sha256, StringComparison.Ordinal))
            {
                DeleteQuietly(stored.FullPath);
                return await RefuseAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.InvalidInput,
                    ArtifactErrorCodes.HashMismatch,
                    $"the file '{path}' hashed to {stored.Sha256}, not the announced {declaredSha256}; "
                    + "the upload was discarded.",
                    cancellationToken);
            }

            var updated = entries
                .Where(entry => !string.Equals(entry.Path, path, StringComparison.Ordinal))
                .Append(new ArtifactFileEntry(path, stored.Sha256, stored.Bytes))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToArray();

            var totalBytes = updated.Sum(entry => entry.Bytes);
            if (updated.Length > options.Limits.MaxFiles || totalBytes > options.Limits.MaxArtifactBytes)
            {
                return await RefuseAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.TooLarge,
                    ArtifactErrorCodes.LimitExceeded,
                    totalBytes > options.Limits.MaxArtifactBytes
                        ? $"the artifact '{artifact.Name}' holds {ArtifactLimits.FormatBytes(totalBytes)}, over "
                            + $"{options.Limits.DescribeArtifactLimit()}."
                        : $"the artifact '{artifact.Name}' holds {updated.Length} files, over "
                            + $"{options.Limits.DescribeFileCountLimit()}.",
                    cancellationToken);
            }

            artifact.FileCount = updated.Length;
            artifact.TotalBytes = totalBytes;
            artifact.ManifestJson = SerializeManifest(new ArtifactManifest(
                ManifestSchemaVersion,
                updated.Length,
                totalBytes,
                ManifestHash: null,
                updated));
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Artifact file stored. OrganizationId={OrganizationId} RunId={RunId} ArtifactId={ArtifactId} Path={Path} Bytes={Bytes}",
                artifact.OrganizationId,
                runId,
                artifactId,
                path,
                stored.Bytes);

            return new ArtifactFileResult(
                ArtifactOperationStatus.Success,
                new ArtifactFileEntry(path, stored.Sha256, stored.Bytes));
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Publishes the artifact: the announced file set must be exactly the set already stored (same paths,
    /// same hashes, same sizes), every file must still be on disk, and the total must fit the caps. Only
    /// then is the manifest hash computed and the row flipped to <c>Ready</c>.
    /// </summary>
    public async Task<ArtifactFinalizeResult> FinalizeAsync(
        string? plaintextToken,
        Guid runId,
        Guid artifactId,
        ArtifactFinalizeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return new ArtifactFinalizeResult(ArtifactOperationStatus.InvalidToken);
        }

        try
        {
            var run = await FindRunningRunAsync(context, runId, worker.Id, cancellationToken);
            if (run is null)
            {
                var runRefusal = await RunRefusalAsync(context, runId, worker.Id, cancellationToken);
                return new ArtifactFinalizeResult(
                    runRefusal.Status,
                    ErrorCode: runRefusal.ErrorCode,
                    Message: runRefusal.Message);
            }

            var artifact = await context.Set<Artifact>().SingleOrDefaultAsync(
                candidate => candidate.Id == artifactId && candidate.RunId == runId,
                cancellationToken);
            if (artifact is null)
            {
                return new ArtifactFinalizeResult(ArtifactOperationStatus.NotFound);
            }

            if (artifact.Status == ArtifactStatus.Ready)
            {
                // A re-delivered finalize (a lost response) answers the recorded summary instead of
                // flipping the row again or refusing an upload that in fact succeeded.
                return new ArtifactFinalizeResult(
                    ArtifactOperationStatus.Success,
                    new ArtifactFinalizeResponse(artifact.Id, artifact.Status, artifact.FileCount, artifact.TotalBytes));
            }

            if (artifact.Status != ArtifactStatus.Pending)
            {
                return new ArtifactFinalizeResult(
                    ArtifactOperationStatus.Conflict,
                    ErrorCode: ArtifactErrorCodes.NotPending,
                    Message: $"artifact '{artifact.Name}' is {artifact.Status}; it cannot be finalized.");
            }

            var stored = ReadEntries(artifact);
            var announced = await NormalizeAnnouncedFilesAsync(request.Files, cancellationToken);
            if (announced is null)
            {
                return await RefuseFinalizeAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.InvalidInput,
                    ArtifactErrorCodes.PathRejected,
                    "the finalized file list contains a path the artifact rules refuse.",
                    cancellationToken);
            }

            var storedByPath = stored.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            if (announced.Length != storedByPath.Count
                || announced.Any(entry => !storedByPath.TryGetValue(entry.Path, out var existing)
                    || !string.Equals(existing.Sha256, entry.Sha256, StringComparison.Ordinal)
                    || existing.Bytes != entry.Bytes))
            {
                return await RefuseFinalizeAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.InvalidInput,
                    ArtifactErrorCodes.FileSetMismatch,
                    $"the finalized file list does not describe what '{artifact.Name}' actually holds; "
                    + "the artifact is incomplete.",
                    cancellationToken);
            }

            foreach (var entry in announced)
            {
                var path = store.ResolveFile(artifact.OrganizationId, artifact.RunId, artifact.Id, entry.Path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != entry.Bytes)
                {
                    return await RefuseFinalizeAsync(
                        context,
                        artifact,
                        ArtifactOperationStatus.InvalidInput,
                        ArtifactErrorCodes.FileMissing,
                        $"the file '{entry.Path}' of artifact '{artifact.Name}' is missing from the store.",
                        cancellationToken);
                }
            }

            var totalBytes = announced.Sum(entry => entry.Bytes);
            if (totalBytes > options.Limits.MaxArtifactBytes || announced.Length > options.Limits.MaxFiles)
            {
                return await RefuseFinalizeAsync(
                    context,
                    artifact,
                    ArtifactOperationStatus.TooLarge,
                    ArtifactErrorCodes.LimitExceeded,
                    totalBytes > options.Limits.MaxArtifactBytes
                        ? $"the artifact '{artifact.Name}' holds {ArtifactLimits.FormatBytes(totalBytes)}, over "
                            + $"{options.Limits.DescribeArtifactLimit()}."
                        : $"the artifact '{artifact.Name}' holds {announced.Length} files, over "
                            + $"{options.Limits.DescribeFileCountLimit()}.",
                    cancellationToken);
            }

            var manifestHash = ComputeManifestHash(announced);
            artifact.Status = ArtifactStatus.Ready;
            artifact.FileCount = announced.Length;
            artifact.TotalBytes = totalBytes;
            artifact.ManifestJson = SerializeManifest(new ArtifactManifest(
                ManifestSchemaVersion,
                announced.Length,
                totalBytes,
                manifestHash,
                announced));
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Artifact finalized. OrganizationId={OrganizationId} RunId={RunId} ArtifactId={ArtifactId} Name={Name} Files={Files} Bytes={Bytes} ManifestHash={ManifestHash}",
                artifact.OrganizationId,
                runId,
                artifactId,
                artifact.Name,
                artifact.FileCount,
                artifact.TotalBytes,
                manifestHash);

            return new ArtifactFinalizeResult(
                ArtifactOperationStatus.Success,
                new ArtifactFinalizeResponse(artifact.Id, artifact.Status, artifact.FileCount, artifact.TotalBytes));
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------------
    // Reads: the run page and the download gate
    // ---------------------------------------------------------------------

    /// <summary>One run's artifacts, oldest first, as the run page lists them (tenant-filtered).</summary>
    public async Task<IReadOnlyList<ArtifactView>> GetRunArtifactsAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.Set<Artifact>()
            .AsNoTracking()
            .Where(artifact => artifact.RunId == runId)
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .OrderBy(artifact => artifact.CreatedAt)
                .ThenBy(artifact => artifact.Name, StringComparer.Ordinal)
                .Select(artifact => new ArtifactView(
                    artifact.Id,
                    artifact.Name,
                    artifact.Kind,
                    artifact.Status,
                    artifact.FileCount,
                    artifact.TotalBytes)),
        ];
    }

    /// <summary>
    /// The download gate: a <c>Ready</c> artifact of the active tenant's run, or <see langword="null"/>.
    /// A foreign organization's id, an unknown id, a pending or incomplete artifact and a run id that does
    /// not match are all the same <see langword="null"/> - the caller answers one plain 404 with no body, so
    /// there is no existence or timing oracle.
    /// </summary>
    public async Task<ArtifactDownload?> OpenDownloadAsync(
        Guid runId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var artifact = await context.Set<Artifact>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == artifactId
                    && candidate.RunId == runId
                    && candidate.Status == ArtifactStatus.Ready,
                cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        var files = store.EnumerateRelativeFiles(artifact.OrganizationId, artifact.RunId, artifact.Id);
        return files.Count == 0 ? null : new ArtifactDownload(artifact, files);
    }

    /// <summary>
    /// The absolute store path of one file of an already-gated artifact, resolved through the shared rules
    /// a second time (defence in depth: the enumerator never produces an escaping path, and this would
    /// refuse one if it did).
    /// </summary>
    public string ResolveStoredFile(Artifact artifact, string relativePath) => store.ResolveFile(
        artifact.OrganizationId,
        artifact.RunId,
        artifact.Id,
        relativePath);

    /// <summary>Streams one stored file to a response or zip entry with the bounded copy buffer.</summary>
    public Task CopyStoredFileAsync(string absolutePath, Stream output, CancellationToken cancellationToken) =>
        store.CopyFileToAsync(absolutePath, output, cancellationToken);

    // ---------------------------------------------------------------------
    // The abandoned-upload sweeper
    // ---------------------------------------------------------------------

    /// <summary>
    /// Marks every <c>Pending</c> artifact that has seen no write for
    /// <see cref="ArtifactOptions.PendingTimeout"/> as <c>Incomplete</c> (card F-016, AC-7): a worker that died
    /// mid-upload leaves a row no download can reach and no row that stays Pending forever. The bytes are kept -
    /// the row is the diagnosis.
    /// <para>
    /// <b>Staleness is measured from <c>UpdatedAt</c>, not <c>CreatedAt</c>.</b> The context refreshes
    /// <c>UpdatedAt</c> on every write, and the upload endpoint writes after each accepted file, so a legitimate
    /// large artifact that takes longer than the window to upload is not mistaken for an abandoned one - only a
    /// row nobody has touched for the whole window is.
    /// </para>
    /// </summary>
    /// <returns>How many rows were swept.</returns>
    public async Task<int> SweepAbandonedUploadsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - options.PendingTimeout;

        // Cross-tenant by design (the sweeper serves every organization), so the system scope is required.
        using var scope = systemDataScope.BeginScope();
        var pending = await context.SystemQueryAsync<Artifact>(
            artifact => artifact.Status == ArtifactStatus.Pending,
            cancellationToken);

        var abandoned = pending.Where(artifact => artifact.UpdatedAt <= cutoff).ToArray();
        if (abandoned.Length == 0)
        {
            return 0;
        }

        foreach (var artifact in abandoned)
        {
            artifact.Status = ArtifactStatus.Incomplete;
            logger.LogWarning(
                "Artifact upload abandoned. OrganizationId={OrganizationId} RunId={RunId} ArtifactId={ArtifactId} Name={Name} LastWriteAt={LastWriteAt}",
                artifact.OrganizationId,
                artifact.RunId,
                artifact.Id,
                artifact.Name,
                artifact.UpdatedAt);
        }

        await context.SaveChangesAsync(cancellationToken);
        return abandoned.Length;
    }

    /// <summary>
    /// Deletes store directories whose run no longer exists (card F-016, the run-delete half): the rows
    /// cascade in the database, and this best-effort pass removes the bytes they described. A directory that
    /// cannot be deleted is logged and left for the next tick - a cleanup failure must never fail a
    /// user's delete.
    /// </summary>
    /// <returns>How many run directories were removed.</returns>
    public async Task<int> SweepOrphanedDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(store.RootPath))
        {
            return 0;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        using var scope = systemDataScope.BeginScope();
        var organizationDirectories = Directory.EnumerateDirectories(store.RootPath).ToArray();
        var removed = 0;

        foreach (var organizationDirectory in organizationDirectories)
        {
            if (!Guid.TryParse(Path.GetFileName(organizationDirectory), out var organizationId))
            {
                continue;
            }

            foreach (var runDirectory in Directory.EnumerateDirectories(organizationDirectory))
            {
                if (!Guid.TryParse(Path.GetFileName(runDirectory), out var runId))
                {
                    continue;
                }

                var rows = await context.SystemQueryAsync<Artifact>(
                    artifact => artifact.RunId == runId,
                    cancellationToken);
                if (rows.Count > 0)
                {
                    continue;
                }

                try
                {
                    store.DeleteRunDirectory(organizationId, runId);
                    removed++;
                    logger.LogInformation(
                        "Orphaned artifact directory removed. OrganizationId={OrganizationId} RunId={RunId}",
                        organizationId,
                        runId);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(
                        failure,
                        "Could not remove the orphaned artifact directory for run {RunId}; it will be retried.",
                        runId);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(organizationDirectory).Any())
            {
                TryDeleteEmptyDirectory(organizationDirectory);
            }
        }

        return removed;
    }

    // ---------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------

    /// <summary>
    /// F-004's authentication, then the tenant-side re-read of token and worker (the revocation check may
    /// have raced the lookup). The caller owns - and must dispose - the context.
    /// </summary>
    private async Task<(WorkerToken? Token, Worker? Worker, ZenkinsDbContext Context)>
        AuthenticateWithWorkerAsync(string? plaintextToken, CancellationToken cancellationToken)
    {
        var workerToken = await workerService.AuthenticateWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null)
        {
            return (null, null, null!);
        }

        var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = workerToken.WorkerId is { } workerId
            ? await context.Workers.SingleOrDefaultAsync(candidate => candidate.Id == workerId, cancellationToken)
            : null;
        var tokenRow = await context.WorkerTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.Id && candidate.RevokedAt == null,
            cancellationToken);
        if (worker is null || tokenRow is null)
        {
            await context.DisposeAsync();
            return (null, null, null!);
        }

        return (workerToken, worker, context);
    }

    /// <summary>The run this worker is executing right now, or <see langword="null"/>.</summary>
    private static Task<BuildRun?> FindRunningRunAsync(
        ZenkinsDbContext context,
        Guid runId,
        Guid workerId,
        CancellationToken cancellationToken) =>
        context.BuildRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(
                run => run.Id == runId
                    && run.WorkerId == workerId
                    && run.Status == BuildRunStatus.Running,
                cancellationToken);

    /// <summary>
    /// The reason the run gate refused: an unknown/foreign run is a 404, a run owned by another worker or no
    /// longer running is a 409. The 404 carries no tenant detail - a foreign id and an unknown id are the
    /// same answer.
    /// </summary>
    private static async Task<ArtifactCreateResult> RunRefusalAsync(
        ZenkinsDbContext context,
        Guid runId,
        Guid workerId,
        CancellationToken cancellationToken)
    {
        var run = await context.BuildRuns
            .AsNoTracking()
            .Where(candidate => candidate.Id == runId)
            .Select(candidate => new { candidate.WorkerId, candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return new ArtifactCreateResult(ArtifactOperationStatus.NotFound);
        }

        return run.WorkerId != workerId
            ? new ArtifactCreateResult(
                ArtifactOperationStatus.Conflict,
                ErrorCode: ArtifactErrorCodes.RunNotAssignedToWorker,
                Message: "this run is not assigned to this worker.")
            : new ArtifactCreateResult(
                ArtifactOperationStatus.Conflict,
                ErrorCode: ArtifactErrorCodes.RunNotRunning,
                Message: $"this run is {run.Status}, so it publishes no artifacts.");
    }

    /// <summary>
    /// Records a refusal and - when the artifact exists - marks it <c>Incomplete</c> so a partially written
    /// artifact can never become downloadable (card F-016, decision 4). The bytes stay for diagnosis.
    /// </summary>
    private async Task<ArtifactFileResult> RefuseAsync(
        ZenkinsDbContext context,
        Artifact artifact,
        ArtifactOperationStatus status,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        artifact.Status = ArtifactStatus.Incomplete;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Artifact upload refused. OrganizationId={OrganizationId} RunId={RunId} ArtifactId={ArtifactId} Name={Name} Code={Code} Reason={Reason}",
            artifact.OrganizationId,
            artifact.RunId,
            artifact.Id,
            artifact.Name,
            errorCode,
            message);
        return new ArtifactFileResult(status, ErrorCode: errorCode, Message: message);
    }

    private async Task<ArtifactFinalizeResult> RefuseFinalizeAsync(
        ZenkinsDbContext context,
        Artifact artifact,
        ArtifactOperationStatus status,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        artifact.Status = ArtifactStatus.Incomplete;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Artifact finalize refused. OrganizationId={OrganizationId} RunId={RunId} ArtifactId={ArtifactId} Name={Name} Code={Code} Reason={Reason}",
            artifact.OrganizationId,
            artifact.RunId,
            artifact.Id,
            artifact.Name,
            errorCode,
            message);
        return new ArtifactFinalizeResult(status, ErrorCode: errorCode, Message: message);
    }

    private static ArtifactCreateResult Invalid(string errorCode, string message) =>
        new(ArtifactOperationStatus.InvalidInput, ErrorCode: errorCode, Message: message);

    /// <summary>
    /// The announced file list, normalized and de-duplicated - or <see langword="null"/> when any path is
    /// refused by the shared rules. The finalize step therefore inherits the same traversal protection the
    /// per-file upload has, on a path that never touches the file system.
    /// </summary>
    private static Task<ArtifactFileEntry[]?> NormalizeAnnouncedFilesAsync(
        ArtifactFileEntry[]? files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (files is null)
        {
            return Task.FromResult<ArtifactFileEntry[]?>([]);
        }

        var normalized = new List<ArtifactFileEntry>(files.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file is null
                || !ArtifactPathRules.TryNormalize(file.Path, out var path, out _)
                || file.Sha256 is null
                || !IsSha256Hex(file.Sha256)
                || file.Bytes < 0
                || !seen.Add(path))
            {
                return Task.FromResult<ArtifactFileEntry[]?>(null);
            }

            normalized.Add(new ArtifactFileEntry(path, file.Sha256, file.Bytes));
        }

        return Task.FromResult<ArtifactFileEntry[]?>(
            [.. normalized.OrderBy(entry => entry.Path, StringComparer.Ordinal)]);
    }

    private static ArtifactFileEntry[] ReadEntries(Artifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.ManifestJson))
        {
            return [];
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(artifact.ManifestJson, ArtifactJsonContext.Default.ArtifactManifest);
            return manifest?.Files ?? [];
        }
        catch (JsonException)
        {
            // A manifest that no longer parses cannot be trusted; the artifact is then finalized only if
            // nothing was announced, which the file-set check refuses. Never throw out of a read path that
            // the sweeper also uses.
            return [];
        }
    }

    private static string SerializeManifest(ArtifactManifest manifest) =>
        JsonSerializer.Serialize(manifest, ArtifactJsonContext.Default.ArtifactManifest);

    /// <summary>
    /// The manifest hash: SHA-256 over the canonical, path-sorted <c>path\0sha256\0bytes\n</c> lines. It is
    /// what makes the published manifest tamper-evident without re-reading the bytes on every download.
    /// </summary>
    private static string ComputeManifestHash(IReadOnlyList<ArtifactFileEntry> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            builder
                .Append(file.Path).Append('\0')
                .Append(file.Sha256).Append('\0')
                .Append(file.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void DeleteQuietly(string path)
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The stored manifest (card F-016): while Pending the served entries and running totals; once Ready also
/// the <see cref="ManifestHash"/> over those entries. Kept as one document so the row is self-describing.
/// </summary>
internal sealed record ArtifactManifest(
    int Version,
    int FileCount,
    long TotalBytes,
    string? ManifestHash,
    ArtifactFileEntry[] Files);

/// <summary>Source-generated JSON for the manifest (AOT/trim-friendly, and no reflection in a hot path).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ArtifactManifest))]
internal sealed partial class ArtifactJsonContext : JsonSerializerContext
{
}
