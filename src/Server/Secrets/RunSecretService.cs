using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Workers;

namespace Zenkins.Server.Secrets;

/// <summary>Outcome of a worker's run-scoped secret fetch (card F-015).</summary>
public enum RunSecretFetchStatus
{
    /// <summary>The decrypted value, for a run assigned to the calling worker.</summary>
    Success = 0,

    /// <summary>Missing/unknown/revoked worker token: a bare 401.</summary>
    InvalidToken = 1,

    /// <summary>
    /// No value for this caller. <b>Every</b> reason collapses into this one value: an unknown run,
    /// another tenant's run, a run assigned to a different worker, a run that already ended and a name the
    /// active organization does not have are all the same bodyless 404 - so neither the response nor the
    /// server log distinguishes "not yours" from "does not exist".
    /// </summary>
    NotFound = 2,
}

/// <summary>
/// The answer of a worker's fetch: the name it asked for, the payload's <b>kind</b> and the decrypted payload,
/// on success only (card F-040).
/// <para>
/// The kind is part of the answer because the payload's shape depends on it - a pair is the length-prefixed
/// pair document and a file is its bytes. The worker already has <c>SecretPayload.Decode</c> for that; a server
/// that withheld the kind would force the worker to guess, and a guess reads a password as a whole document.
/// </para>
/// </summary>
public sealed record RunSecretFetchResult(
    RunSecretFetchStatus Status,
    string? Name = null,
    string? Kind = null,
    string? Value = null)
{
    public static RunSecretFetchResult Success(string name, string kind, string value) =>
        new(RunSecretFetchStatus.Success, name, kind, value);

    public static RunSecretFetchResult InvalidToken() => new(RunSecretFetchStatus.InvalidToken);

    public static RunSecretFetchResult NotFound() => new(RunSecretFetchStatus.NotFound);
}

/// <summary>
/// The run-scoped secret delivery <c>secret()</c> relies on (card F-015, PRD §3.8).
/// <para>
/// <b>A worker-side fetch, not a claim-payload field.</b> Same reasoning as F-013's Git credential: the
/// claim carries what a run needs to be described, never a plaintext secret. The worker asks for one
/// name at a time while the step that references it is about to start, authenticated by its worker
/// token, scoped to <b>the run it is executing and that run's organization</b>, and the server decrypts.
/// The worker caches the value per run in memory for the duration of the run and never writes it to
/// disk (<c>RunSecretCache</c> on the worker side).
/// </para>
/// <para>
/// <b>One 404 for every miss.</b> The token selects the tenant, the run lookup goes through the tenant
/// query filter, and the name lookup is tenant-filtered too - so a foreign organization's secret is
/// indistinguishable from a name that does not exist, and neither is distinguishable from a run the
/// caller does not own. The log line for every miss is identical and carries no reason.
/// </para>
/// <para>
/// <b>The value is the only thing that leaves.</b> No endpoint or page on the user side returns a value;
/// this route answers a worker token that has already proved it is executing the run. The response is
/// explicitly uncacheable, and neither the value nor the ciphertext is ever logged.
/// </para>
/// <para>
/// <b>Why a member-triggered run may read an organization secret.</b> PRD §3.8's ruling: whoever can edit
/// <c>pipeline.lua</c> is treated as having the build's permissions, so the organization's secrets are the
/// build's secrets. A <em>different organization</em> can never read them: its worker token selects its
/// own tenant, where the run id does not exist at all.
/// </para>
/// </summary>
public sealed class RunSecretService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    SecretProtector protector,
    WorkerService workers,
    ILogger<RunSecretService> logger)
{
    /// <summary>
    /// Fetches and decrypts one secret for a run the calling worker is executing. Returns
    /// <see cref="RunSecretFetchStatus.NotFound"/> for every miss (unknown run, foreign run, another
    /// worker's run, ended run, unknown name).
    /// </summary>
    public async Task<RunSecretFetchResult> FetchValueForRunAsync(
        string? plaintextWorkerToken,
        Guid runId,
        string? name,
        CancellationToken cancellationToken = default)
    {
        var workerToken = await workers.AuthenticateWorkerAsync(plaintextWorkerToken, cancellationToken);
        if (workerToken is null)
        {
            return RunSecretFetchResult.InvalidToken();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == workerToken.WorkerId, cancellationToken);

        // The run lookup is tenant-filtered: a foreign organization's run id simply is not there, which
        // is exactly the same answer as "that run does not exist".
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
            || trimmedName.Length == 0)
        {
            LogMiss(runId, workerToken.WorkerId);
            return RunSecretFetchResult.NotFound();
        }

        // F-040: the kind travels with the ciphertext, because it decides the payload's shape. A row written
        // before the kind column exists reads 'text' from the column's default, which is what it always was.
        var stored = await context.Secrets
            .AsNoTracking()
            .Where(secret => secret.Name == trimmedName)
            .Select(secret => new { secret.Kind, secret.Ciphertext })
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            LogMiss(runId, worker.Id);
            return RunSecretFetchResult.NotFound();
        }

        string value;
        try
        {
            value = protector.Unprotect(stored.Ciphertext);
        }
        catch (CryptographicException failure)
        {
            // A deployment fault, not "not found": the key ring or the purpose changed. Logged (never the
            // value) and surfaced as a 500, because pretending the secret is absent would send an
            // operator looking in the wrong place.
            logger.LogError(
                failure,
                "Run secret could not be decrypted. OrganizationId={OrganizationId} RunId={RunId} Name={Name}",
                run.OrganizationId,
                runId,
                trimmedName);
            throw;
        }

        // The name and the kind are logged; the payload never is.
        logger.LogInformation(
            "Run secret delivered. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} Name={Name} "
            + "Kind={Kind}",
            run.OrganizationId,
            runId,
            worker.Id,
            trimmedName,
            stored.Kind);
        return RunSecretFetchResult.Success(trimmedName, stored.Kind, value);
    }

    /// <summary>
    /// The single log line for every miss, deliberately carrying no reason: "foreign run", "not assigned
    /// to this worker", "unknown id", "run finished" and "no such name" must be indistinguishable to
    /// anyone reading the server log.
    /// </summary>
    private void LogMiss(Guid runId, Guid? workerId) =>
        logger.LogInformation(
            "Run secret request resolved no value. RunId={RunId} WorkerId={WorkerId}",
            runId,
            workerId);

    private static bool IsTerminal(string status) =>
        status is BuildRunStatus.Success
            or BuildRunStatus.Failed
            or BuildRunStatus.Canceled
            or BuildRunStatus.Lost;
}
