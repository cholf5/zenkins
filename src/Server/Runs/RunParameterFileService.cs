using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Workers;

namespace Zenkins.Server.Runs;

/// <summary>Outcome of a worker's run-scoped parameter-file fetch (card F-041).</summary>
public enum RunParameterFileFetchStatus
{
    /// <summary>The file's bytes, for a run assigned to the calling worker.</summary>
    Success = 0,

    /// <summary>Missing/unknown/revoked worker token: a bare 401.</summary>
    InvalidToken = 1,

    /// <summary>
    /// No bytes for this caller. <b>Every</b> reason collapses into this one value: an unknown run, another
    /// tenant's run, a run assigned to a different worker, a run that already ended, a name this run does not hold
    /// as a <c>file</c> parameter, and a parameter that was triggered without an upload are all the same bodyless
    /// 404 - so neither the response nor the server log distinguishes "not yours" from "does not exist".
    /// </summary>
    NotFound = 2,
}

/// <summary>
/// The run-scoped delivery a <c>file</c> parameter's bytes take to the worker (card F-041).
/// <para>
/// <b>Why a route and not the claim.</b> Card F-040 settled that the claim carries names, never material; a file
/// parameter can be a megabyte, and every worker reads the claim of every run. The worker therefore asks for the
/// one file it needs, by name, authenticated by the token of the worker executing that run - the same shape a file
/// <em>secret</em> already takes, down to the refusal contract.
/// </para>
/// <para>
/// <b>One 404 for every miss.</b> The token selects the tenant, the run lookup goes through the tenant filter, and
/// the name is resolved against the run's own sealed document - so a foreign organization's run is
/// indistinguishable from a run that does not exist, and neither is distinguishable from a run this worker is not
/// executing. The log line for every miss is identical and carries no reason.
/// </para>
/// </summary>
public sealed class RunParameterFileService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    RunParameterProtector protector,
    WorkerService workers,
    ILogger<RunParameterFileService> logger)
{
    /// <summary>
    /// Fetches one uploaded file for a run the calling worker is executing. Returns
    /// <see cref="RunParameterFileFetchStatus.NotFound"/> for every miss.
    /// </summary>
    public async Task<(RunParameterFileFetchStatus Status, byte[]? Bytes)> FetchAsync(
        string? plaintextWorkerToken,
        Guid runId,
        string? name,
        CancellationToken cancellationToken = default)
    {
        var workerToken = await workers.AuthenticateWorkerAsync(plaintextWorkerToken, cancellationToken);
        if (workerToken is null)
        {
            return (RunParameterFileFetchStatus.InvalidToken, null);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == workerToken.WorkerId, cancellationToken);

        // Tenant-filtered: a foreign organization's run id simply is not there.
        var run = worker is null
            ? null
            : await context.BuildRuns
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);

        var trimmedName = name?.Trim() ?? string.Empty;
        if (worker is null
            || run is null
            || run.WorkerId != worker.Id
            || IsTerminal(run.Status)
            || trimmedName.Length == 0
            || run.ParameterFilesJson is null)
        {
            LogMiss(runId, workerToken.WorkerId);
            return (RunParameterFileFetchStatus.NotFound, null);
        }

        byte[]? bytes;
        try
        {
            bytes = RunParameterFiles.Read(run.ParameterFilesJson, trimmedName, protector);
        }
        catch (CryptographicException failure)
        {
            // A deployment fault, not "not found": the key ring or the purpose changed. Logged (never the bytes)
            // and surfaced as a 500, because pretending the file is absent would send an operator looking in the
            // wrong place - and would fail a run with a reason that blames its inputs.
            logger.LogError(
                failure,
                "Run parameter file could not be decrypted. OrganizationId={OrganizationId} RunId={RunId} Name={Name}",
                run.OrganizationId,
                runId,
                trimmedName);
            throw;
        }

        if (bytes is null)
        {
            LogMiss(runId, worker.Id);
            return (RunParameterFileFetchStatus.NotFound, null);
        }

        // The name is logged; the bytes never are.
        logger.LogInformation(
            "Run parameter file delivered. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} Name={Name} Bytes={Bytes}",
            run.OrganizationId,
            runId,
            worker.Id,
            trimmedName,
            bytes.Length);
        return (RunParameterFileFetchStatus.Success, bytes);
    }

    /// <summary>
    /// The single log line for every miss, deliberately carrying no reason: "foreign run", "not assigned to this
    /// worker", "unknown id", "run finished" and "no such file parameter" must be indistinguishable to anyone
    /// reading the server log.
    /// </summary>
    private void LogMiss(Guid runId, Guid? workerId) =>
        logger.LogInformation(
            "Run parameter file request resolved no file. RunId={RunId} WorkerId={WorkerId}",
            runId,
            workerId);

    private static bool IsTerminal(string status) =>
        status is BuildRunStatus.Success
            or BuildRunStatus.Failed
            or BuildRunStatus.Canceled
            or BuildRunStatus.Lost;
}
