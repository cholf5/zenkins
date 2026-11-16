using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.RunCancellation;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Workers;

/// <summary>Stable error codes the worker API and the /workers page render.</summary>
public static class WorkerErrorCodes
{
    public const string NameRequired = "name-required";
    public const string NameTooLong = "name-too-long";
    public const string NameInvalid = "name-invalid";
    public const string NameDuplicate = "name-duplicate";
    public const string FieldInvalid = "field-invalid";
    public const string TokenAlreadyConsumed = "token-already-consumed";
    public const string TokenAlreadyRevoked = "token-already-revoked";

    /// <summary>Card D-005: a worker with run history cannot be deleted - its runs keep the reference.</summary>
    public const string WorkerHasRuns = "worker-has-runs";
}

/// <summary>Outcome of a worker-API or worker-management operation.</summary>
public enum WorkerOperationStatus
{
    Success = 0,

    /// <summary>The presented token is missing, unknown, burned or revoked. Always a 401 with no tenant detail.</summary>
    InvalidToken = 1,

    /// <summary>The payload failed validation; nothing was written.</summary>
    InvalidInput = 2,

    /// <summary>The caller is not an Owner (management operations) or has no active organization.</summary>
    Forbidden = 3,

    /// <summary>A token or worker id does not exist in the active organization (also the foreign-tenant case).</summary>
    NotFound = 4,

    /// <summary>Two concurrent registrations claimed the same worker name (unique index).</summary>
    NameConflict = 5,
}

/// <summary>The raw registration payload, before trimming and validation.</summary>
public sealed record WorkerRegistrationInput(
    string? Name,
    string? Os,
    string? Architecture,
    string? Version,
    string? Tags,
    int? CpuCount,
    int? MaxConcurrency,

    /// <summary>
    /// Card I-005: whether this Worker can answer Git remote probes. <see langword="null"/> is what a
    /// Worker built before the probe card sends, and it means <b>no</b> - see
    /// <see cref="Zenkins.Shared.Probes.GitProbeCapability"/>.
    /// </summary>
    bool? CanProbe = null);

/// <summary>The raw heartbeat payload. Every capability field is optional (refresh when present).</summary>
public sealed record WorkerHeartbeatInput(
    string? Os,
    string? Architecture,
    string? Version,
    string? Tags,
    int? CpuCount,
    int? MaxConcurrency,

    /// <summary>
    /// Card I-005: the refreshed capability flag. Absent (null) leaves the stored value alone, so an
    /// old Worker's heartbeat cannot silently revoke a capability a new registration granted - and a
    /// new Worker that turned probing off sends <c>false</c>, which does revoke it.
    /// </summary>
    bool? CanProbe = null);

/// <summary>A worker as the /workers pages show it (roster and detail, card D-005).</summary>
public sealed record WorkerView(
    Guid Id,
    string Name,
    string Os,
    string Architecture,
    string Version,
    string Tags,
    string Status,
    int CpuCount,
    int MaxConcurrency,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset CreatedAt,
    bool IsDisabled = false,

    /// <summary>Card I-005: whether this Worker can be handed Git remote probes.</summary>
    bool CanProbe = false);

/// <summary>A registration token as the Owner's token list shows it. No secrets.</summary>
public sealed record WorkerRegistrationTokenView(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>Outcome of a registration: the worker token plaintext is returned exactly once.</summary>
public sealed record WorkerRegistrationResult(
    WorkerOperationStatus Status,
    Guid? WorkerId = null,
    string? WorkerName = null,
    string? WorkerToken = null,
    string? ErrorCode = null)
{
    public static WorkerRegistrationResult Success(Guid workerId, string workerName, string workerToken) =>
        new(WorkerOperationStatus.Success, workerId, workerName, workerToken);

    public static WorkerRegistrationResult InvalidToken() => new(WorkerOperationStatus.InvalidToken);

    public static WorkerRegistrationResult Invalid(string errorCode) =>
        new(WorkerOperationStatus.InvalidInput, ErrorCode: errorCode);
}

/// <summary>Outcome of a heartbeat: enough for the worker to keep its loop running - plus the
/// runs the server has asked this worker to cancel (F-009's fallback signal channel: a worker
/// whose SignalR connection is down learns the cancel request at most one heartbeat period late).</summary>
public sealed record WorkerHeartbeatResult(
    WorkerOperationStatus Status,
    Guid? WorkerId = null,
    DateTimeOffset? LastHeartbeatAt = null,
    IReadOnlyList<WorkerCancelRequest>? CancelRequestedRuns = null)
{
    public static WorkerHeartbeatResult Success(Guid workerId, DateTimeOffset lastHeartbeatAt) =>
        new(WorkerOperationStatus.Success, workerId, lastHeartbeatAt);

    public static WorkerHeartbeatResult Success(
        Guid workerId,
        DateTimeOffset lastHeartbeatAt,
        IReadOnlyList<WorkerCancelRequest> cancelRequestedRuns) =>
        new(WorkerOperationStatus.Success, workerId, lastHeartbeatAt, cancelRequestedRuns);

    public static WorkerHeartbeatResult InvalidToken() => new(WorkerOperationStatus.InvalidToken);
}

/// <summary>Outcome of Owner-side registration-token management.</summary>
public sealed record WorkerManagementResult(
    WorkerOperationStatus Status,
    string? ErrorCode = null,
    string? Message = null)
{
    public static WorkerManagementResult Success() => new(WorkerOperationStatus.Success);

    public static WorkerManagementResult Invalid(string errorCode) =>
        new(WorkerOperationStatus.InvalidInput, ErrorCode: errorCode);

    public static WorkerManagementResult Forbidden(string message) =>
        new(WorkerOperationStatus.Forbidden, Message: message);

    public static WorkerManagementResult NotFound() => new(WorkerOperationStatus.NotFound);
}

/// <summary>Outcome of minting a registration token. <see cref="Token"/> is plaintext, shown once.</summary>
public sealed record WorkerTokenIssuedResult(
    WorkerOperationStatus Status,
    Guid? TokenId = null,
    string? Token = null,
    string? Message = null)
{
    public static WorkerTokenIssuedResult Success(Guid tokenId, string token) =>
        new(WorkerOperationStatus.Success, tokenId, token);

    public static WorkerTokenIssuedResult Forbidden(string message) =>
        new(WorkerOperationStatus.Forbidden, Message: message);
}

/// <summary>
/// Outcome of deleting a worker (card D-005). <see cref="RunCount"/> carries the number of runs the
/// worker executed when deletion was refused - the count the refusal message names, so the page can
/// say exactly why the worker stayed.
/// </summary>
public sealed record WorkerDeleteResult(
    WorkerOperationStatus Status,
    int? RunCount = null,
    string? Message = null)
{
    public static WorkerDeleteResult Success() => new(WorkerOperationStatus.Success);

    public static WorkerDeleteResult Refused(int runCount) =>
        new(WorkerOperationStatus.InvalidInput, RunCount: runCount);

    public static WorkerDeleteResult Forbidden(string message) =>
        new(WorkerOperationStatus.Forbidden, Message: message);

    public static WorkerDeleteResult NotFound() => new(WorkerOperationStatus.NotFound);
}

/// <summary>
/// Worker registration, heartbeat and registration-token management (card F-004, PRD §3.4.2/§3.4.3).
/// <para>
/// <b>Token authentication.</b> Registration and heartbeat authenticate with a plaintext token
/// (`zreg_…`/`zwrk_…`) resolved by its SHA-256 hash. The lookup must run before any tenant exists,
/// so it goes through <see cref="ZenkinsDbContext.SystemQueryAsync{TEntity}"/> inside an
/// <see cref="ISystemDataScope"/>; the token row then selects the tenant
/// (<c>WorkerToken.OrganizationId</c>) and all following work happens behind the tenant filter.
/// A cookie can never authorize a worker action: these methods never look at
/// <see cref="Microsoft.AspNetCore.Identity.IdentityUser"/>.
/// </para>
/// <para>
/// <b>One-time tokens.</b> A registration token is burned (<c>UsedAt</c>) in the same
/// <see cref="ZenkinsDbContext.SaveChanges"/> that creates/updates the worker, so a replay cannot
/// slip through between the burn and the write. Re-registering with a known name updates the row,
/// revokes its active worker tokens and issues a fresh one (container restarts must not duplicate
/// workers).
/// </para>
/// <para>
/// <b>Tenant boundary.</b> Management methods (mint/list/revoke) read the organization from
/// <see cref="ITenantContext"/> only and require an Owner membership; the id-based revoke resolves
/// through the tenant filter, so a foreign id is a 404, not a 403 existence oracle.
/// </para>
/// </summary>
public sealed class WorkerService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    TenantContext tenantContext,
    ISystemDataScope systemDataScope,
    ILogger<WorkerService> logger)
{
    /// <summary>How long a hashed token prefix may be written to logs (never the plaintext).</summary>
    private const int LogHashPrefixLength = 8;

    public async Task<WorkerTokenIssuedResult> CreateRegistrationTokenAsync(
        string actingUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is not { } organizationId)
        {
            return WorkerTokenIssuedResult.Forbidden(
                "You are not a member of an organization, so there is no organization to mint a token for.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return WorkerTokenIssuedResult.Forbidden("Only an organization Owner can create registration tokens.");
        }

        var plaintext = WorkerTokenCrypto.Generate(WorkerTokenCrypto.RegistrationPrefix);
        var token = new WorkerToken
        {
            OrganizationId = organizationId,
            Kind = WorkerTokenKind.Registration,
            TokenHash = WorkerTokenCrypto.Hash(plaintext),
        };
        context.WorkerTokens.Add(token);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Worker registration token created. OrganizationId={OrganizationId} Hash={TokenHashPrefix}…",
            organizationId,
            token.TokenHash[..LogHashPrefixLength]);
        return WorkerTokenIssuedResult.Success(token.Id, plaintext);
    }

    /// <summary>Registration tokens of the active organization, newest first. No secrets.</summary>
    public async Task<IReadOnlyList<WorkerRegistrationTokenView>> GetRegistrationTokensAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var tokens = await context.WorkerTokens
            .Where(token => token.Kind == WorkerTokenKind.Registration)
            .ToListAsync(cancellationToken);
        return tokens
            .OrderByDescending(token => token.CreatedAt)
            .ThenBy(token => token.Id)
            .Select(token => new WorkerRegistrationTokenView(
                token.Id,
                token.CreatedAt,
                token.UsedAt,
                token.RevokedAt,
                token.LastUsedAt))
            .ToArray();
    }

    /// <summary>Workers of the active organization, ordered by name.</summary>
    public async Task<IReadOnlyList<WorkerView>> GetWorkersAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var workers = await context.Workers.ToListAsync(cancellationToken);
        return workers
            .OrderBy(worker => worker.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(worker => worker.Id)
            .Select(ToView)
            .ToArray();
    }

    /// <summary>
    /// One worker of the active organization, or <see langword="null"/> when the id belongs to
    /// another organization or does not exist (card D-005's detail page renders both as a 404 -
    /// deliberately indistinguishable, like every other tenant-owned read).
    /// </summary>
    public async Task<WorkerView?> GetWorkerAsync(Guid workerId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == workerId,
            cancellationToken);
        return worker is null ? null : ToView(worker);
    }

    private static WorkerView ToView(Worker worker) => new(
        worker.Id,
        worker.Name,
        worker.Os,
        worker.Architecture,
        worker.Version,
        worker.Tags,
        worker.Status,
        worker.CpuCount,
        worker.MaxConcurrency,
        worker.LastHeartbeatAt,
        worker.CreatedAt,
        worker.IsDisabled,
        worker.CanProbe);

    /// <summary>
    /// Disables or re-enables a worker (card D-005, Owner-only). Disable is the reversible
    /// "stop trusting this machine": the claim query (RunService) refuses the worker's next claim
    /// with the ordinary not-assigned answer, runs already in flight continue, and the state is a
    /// plain row update - no tokens are touched, so heartbeats keep arriving and liveness stays the
    /// offline sweeper's judgement. The worker id resolves through the tenant filter, so a foreign
    /// id is the same 404 as an unknown one.
    /// </summary>
    public async Task<WorkerManagementResult> SetDisabledAsync(
        string actingUserId,
        Guid workerId,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return WorkerManagementResult.Forbidden(
                "You are not a member of an organization, so there is no worker to change.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return WorkerManagementResult.Forbidden(
                disabled
                    ? "Only an organization Owner can disable a worker."
                    : "Only an organization Owner can enable a worker.");
        }

        var worker = await context.Workers.SingleOrDefaultAsync(
            candidate => candidate.Id == workerId,
            cancellationToken);
        if (worker is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return WorkerManagementResult.NotFound();
        }

        worker.IsDisabled = disabled;
        worker.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Worker {WorkerId} {State}. OrganizationId={OrganizationId}",
            worker.Id,
            disabled ? "disabled" : "enabled",
            tenantContext.OrganizationId);
        return WorkerManagementResult.Success();
    }

    /// <summary>
    /// Deletes a worker (card D-005, Owner-only) - but only one with <b>no run history</b>: a worker
    /// that executed runs keeps its rows because every run keeps its worker reference (the FK is
    /// SET NULL for schema robustness, and this path refuses any worker the count sees runs for).
    /// The count and the delete share one transaction, so the window between the check and the
    /// commit is sub-millisecond. That makes the guarantee practical, not absolute: a claim that
    /// committed a <c>WorkerId</c> inside exactly that window would still meet the FK's SetNull -
    /// no product path reaches it today (a worker the count shows empty has nothing running, and
    /// the roster's delete targets an idle worker), and the schema's SetNull stays the last line of
    /// defence if one ever does. The worker's tokens cascade with it. A worker with runs is refused
    /// with the count the page names.
    /// </summary>
    public async Task<WorkerDeleteResult> DeleteWorkerAsync(
        string actingUserId,
        Guid workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return WorkerDeleteResult.Forbidden(
                "You are not a member of an organization, so there is no worker to delete.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return WorkerDeleteResult.Forbidden("Only an organization Owner can delete a worker.");
        }

        var worker = await context.Workers.SingleOrDefaultAsync(
            candidate => candidate.Id == workerId,
            cancellationToken);
        if (worker is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return WorkerDeleteResult.NotFound();
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var runCount = await context.BuildRuns.CountAsync(
            run => run.WorkerId == workerId,
            cancellationToken);
        if (runCount > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation(
                "Worker {WorkerId} deletion refused: {RunCount} run(s) keep their worker reference. OrganizationId={OrganizationId}",
                workerId,
                runCount,
                tenantContext.OrganizationId);
            return WorkerDeleteResult.Refused(runCount);
        }

        context.Workers.Remove(worker);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Worker {WorkerId} deleted. OrganizationId={OrganizationId}",
            workerId,
            tenantContext.OrganizationId);
        return WorkerDeleteResult.Success();
    }

    /// <summary>Revokes a pending registration token (Owner-only). Revocation is immediate.</summary>
    public async Task<WorkerManagementResult> RevokeRegistrationTokenAsync(
        string actingUserId,
        Guid tokenId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return WorkerManagementResult.Forbidden(
                "You are not a member of an organization, so there is no token to revoke.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return WorkerManagementResult.Forbidden("Only an organization Owner can revoke registration tokens.");
        }

        var token = await context.WorkerTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == tokenId && candidate.Kind == WorkerTokenKind.Registration,
            cancellationToken);
        if (token is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return WorkerManagementResult.NotFound();
        }

        if (token.RevokedAt is not null)
        {
            return WorkerManagementResult.Invalid(WorkerErrorCodes.TokenAlreadyRevoked);
        }

        if (token.UsedAt is not null)
        {
            return WorkerManagementResult.Invalid(WorkerErrorCodes.TokenAlreadyConsumed);
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // B (second review fix): UsedAt/RevokedAt are EF concurrency tokens (M1), so a revoke
            // that races another revoke - or the burn of the same token - makes this UPDATE match
            // no rows and raises here. Re-read what actually happened and answer exactly like the
            // pre-checks above would have: friendly outcomes only, never a 500.
            return await MapRevocationRaceOutcomeAsync(tokenId, cancellationToken);
        }

        logger.LogInformation(
            "Worker registration token revoked. OrganizationId={OrganizationId} Hash={TokenHashPrefix}…",
            token.OrganizationId,
            token.TokenHash[..LogHashPrefixLength]);
        return WorkerManagementResult.Success();
    }

    /// <summary>
    /// Re-reads a registration token after a lost revocation race and maps its current state to
    /// the same outcomes the pre-checks in <see cref="RevokeRegistrationTokenAsync"/> produce:
    /// another revoke wins → token-already-revoked, the burn wins → token-already-consumed, the
    /// row gone or invisible → the same 404 the pre-check gives an unknown id.
    /// </summary>
    private async Task<WorkerManagementResult> MapRevocationRaceOutcomeAsync(
        Guid tokenId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var token = await context.WorkerTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == tokenId && candidate.Kind == WorkerTokenKind.Registration,
            cancellationToken);

        // A conflict whose re-read still shows a pending token is not reachable (the concurrency
        // columns are exactly UsedAt/RevokedAt); the defensive answer is the pre-check's 404.
        return token switch
        {
            null or { RevokedAt: null, UsedAt: null } => WorkerManagementResult.NotFound(),
            { RevokedAt: not null } => WorkerManagementResult.Invalid(WorkerErrorCodes.TokenAlreadyRevoked),
            _ => WorkerManagementResult.Invalid(WorkerErrorCodes.TokenAlreadyConsumed),
        };
    }

    /// <summary>
    /// Registers (or re-registers, by name) a worker with a single-use registration token. The token
    /// selects the tenant; a cookie is irrelevant to this path.
    /// </summary>
    public async Task<WorkerRegistrationResult> RegisterAsync(
        string? plaintextToken,
        WorkerRegistrationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Authenticate before anything else (review fix): the 401-vs-400 split must not leak
        //    payload validation to a tokenless caller - an unauthenticated probe would otherwise
        //    learn which payloads the contract accepts from the 400 it gets back.
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return WorkerRegistrationResult.InvalidToken();
        }

        var hash = WorkerTokenCrypto.Hash(plaintextToken);
        WorkerToken? registrationToken;
        using var scope = systemDataScope.BeginScope();
        {
            await using var lookupContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            var candidates = await lookupContext.SystemQueryAsync<WorkerToken>(
                token => token.TokenHash == hash && token.Kind == WorkerTokenKind.Registration,
                cancellationToken);
            registrationToken = candidates.FirstOrDefault();
        }

        if (registrationToken is null
            || registrationToken.RevokedAt is not null
            || registrationToken.UsedAt is not null)
        {
            logger.LogWarning(
                "Worker registration rejected. Hash={TokenHashPrefix}… Reason={Reason}",
                hash[..LogHashPrefixLength],
                registrationToken is null ? "unknown-token" : registrationToken.UsedAt is not null ? "burned" : "revoked");
            return WorkerRegistrationResult.InvalidToken();
        }

        // 2. The token selects the tenant - never a cookie, header or URL value.
        tenantContext.OrganizationId = registrationToken.OrganizationId;

        // 3. Then validate the payload: a rejected 400 is only ever shown to an authenticated
        //    caller. The token may already exist as a Worker row (same-name update path), which
        //    is why name/tags read better here than on the raw input.
        var errors = ValidateRegistration(input, out var name, out var os, out var architecture, out var version, out var tags, out var cpuCount, out var maxConcurrency);
        if (errors.Count > 0)
        {
            return WorkerRegistrationResult.Invalid(errors[0]);
        }

        // 4. Burn the token, update/create the worker and issue the worker token atomically.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var persistedToken = await context.WorkerTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == registrationToken.Id, cancellationToken);
        if (persistedToken is null
            || persistedToken.UsedAt is not null
            || persistedToken.RevokedAt is not null)
        {
            // The tenant-filtered re-read is defence in depth; the guard above already checked.
            return WorkerRegistrationResult.InvalidToken();
        }

        var worker = await context.Workers.SingleOrDefaultAsync(
            candidate => candidate.Name == name, cancellationToken);

        // I-005: the capability is what this registration says, never what the old row said. Absent is
        // false, so a pre-probe binary re-registering over a probe-capable row stops being handed probes
        // instead of inheriting a promise it cannot keep.
        var canProbe = input.CanProbe == true;
        if (worker is null)
        {
            worker = new Worker
            {
                OrganizationId = tenantContext.OrganizationId.Value,
                Name = name,
                Os = os,
                Architecture = architecture,
                Version = version,
                Tags = tags,
                CpuCount = cpuCount,
                MaxConcurrency = maxConcurrency,
                CanProbe = canProbe,
            };
            context.Workers.Add(worker);
        }
        else
        {
            worker.Os = os;
            worker.Architecture = architecture;
            worker.Version = version;
            worker.Tags = tags;
            worker.CpuCount = cpuCount;
            worker.MaxConcurrency = maxConcurrency;
            worker.CanProbe = canProbe;
        }

        // Re-registration replaces identity: previous active worker tokens stop working immediately.
        var activeTokens = await context.WorkerTokens
            .Where(token => token.WorkerId == worker.Id
                && token.Kind == WorkerTokenKind.Worker
                && token.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var stale in activeTokens)
        {
            stale.RevokedAt = DateTimeOffset.UtcNow;
        }

        var now = DateTimeOffset.UtcNow;
        worker.Status = WorkerStatus.Online;
        worker.LastHeartbeatAt = now;
        persistedToken.UsedAt = now;

        var workerPlaintext = WorkerTokenCrypto.Generate(WorkerTokenCrypto.WorkerPrefix);
        context.WorkerTokens.Add(new WorkerToken
        {
            OrganizationId = tenantContext.OrganizationId.Value,
            Kind = WorkerTokenKind.Worker,
            WorkerId = worker.Id,
            TokenHash = WorkerTokenCrypto.Hash(workerPlaintext),
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // M1 (review fix): UsedAt/RevokedAt are EF concurrency tokens, so a concurrent
            // registration burning the same single-use token (or a revoke racing the burn) makes
            // this UPDATE match no rows. The loser of that race is, from the protocol's point of
            // view, holding an invalid token: 401, nothing written (the transaction rolls back).
            return WorkerRegistrationResult.InvalidToken();
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Workers"))
        {
            // Two concurrent first-registrations with the same name: the index decides, we explain.
            return WorkerRegistrationResult.Invalid(WorkerErrorCodes.NameDuplicate);
        }

        logger.LogInformation(
            "Worker registered. OrganizationId={OrganizationId} WorkerId={WorkerId} Name={WorkerName}",
            tenantContext.OrganizationId,
            worker.Id,
            worker.Name);
        return WorkerRegistrationResult.Success(worker.Id, worker.Name, workerPlaintext);
    }

    /// <summary>
    /// Records a heartbeat from a valid worker token: refreshes <c>LastHeartbeatAt</c>, the
    /// capability fields present in the payload and the token's <c>LastUsedAt</c>.
    /// </summary>
    public async Task<WorkerHeartbeatResult> HeartbeatAsync(
        string? plaintextToken,
        WorkerHeartbeatInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Authenticate before validating the payload (same probing argument as RegisterAsync).
        var workerToken = await AuthenticateWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null)
        {
            return WorkerHeartbeatResult.InvalidToken();
        }

        // Only now validate the payload: a rejected 400 is only ever shown to an authenticated
        // caller (review fix). A malformed heartbeat from a valid token is a client bug, not an
        // auth failure.
        var errors = ValidateHeartbeat(input, out var os, out var architecture, out var version, out var tags, out var cpuCount, out var maxConcurrency);
        if (errors.Count > 0)
        {
            return new WorkerHeartbeatResult(WorkerOperationStatus.InvalidInput);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers.SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.WorkerId, cancellationToken);

        // The tenant-side re-read rechecks revocation (review fix): the token may have been
        // revoked between the system-scope lookup above and this query.
        var tokenRow = await context.WorkerTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.Id && candidate.RevokedAt == null, cancellationToken);
        if (worker is null || tokenRow is null)
        {
            return WorkerHeartbeatResult.InvalidToken();
        }

        var now = DateTimeOffset.UtcNow;
        worker.LastHeartbeatAt = now;
        worker.Status = WorkerStatus.Online;
        if (os is not null)
        {
            worker.Os = os;
        }

        if (architecture is not null)
        {
            worker.Architecture = architecture;
        }

        if (version is not null)
        {
            worker.Version = version;
        }

        if (tags is not null)
        {
            worker.Tags = tags;
        }

        if (cpuCount is not null)
        {
            worker.CpuCount = cpuCount.Value;
        }

        if (maxConcurrency is not null)
        {
            worker.MaxConcurrency = maxConcurrency.Value;
        }

        // I-005: refresh only when the payload says something. An old Worker omits the field, and
        // clearing a capability because a heartbeat did not repeat it would make probes flap.
        if (input.CanProbe is { } canProbe)
        {
            worker.CanProbe = canProbe;
        }

        tokenRow.LastUsedAt = now;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A revoke committed between the re-read and this write (RevokedAt is a concurrency
            // token, so its UPDATE matched no rows): the token is dead - 401, not a 500.
            return WorkerHeartbeatResult.InvalidToken();
        }

        // F-009's fallback signal channel: every Running run of this worker with a pending cancel
        // request rides the heartbeat response (one heartbeat period at most of added latency).
        // The SignalR cancelRun signal is the primary channel; the two carry the same payload.
        var pendingCancels = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.WorkerId == worker.Id
                && run.Status == BuildRunStatus.Running
                && run.CancelRequestedAt != null)
            .OrderBy(run => run.Id)
            .Select(run => new { run.Id, run.CancelReason })
            .ToListAsync(cancellationToken);

        return WorkerHeartbeatResult.Success(
            worker.Id,
            now,
            pendingCancels
                .Select(run => new WorkerCancelRequest(run.Id, run.CancelReason))
                .ToArray());
    }

    /// <summary>
    /// Authenticates a plaintext worker token (<c>zwrk_…</c>): SHA-256 it, look it up by
    /// <c>TokenHash</c> equality through <see cref="ZenkinsDbContext.SystemQueryAsync{TEntity}"/>
    /// inside an <see cref="ISystemDataScope"/> (no tenant exists yet), then let the token row
    /// select the tenant. Returns the token row - with <see cref="TenantContext.OrganizationId"/>
    /// already set - or <c>null</c> for a missing/unknown/revoked token (the caller answers a bare
    /// 401). Authentication must run before any payload validation, so a tokenless caller cannot
    /// probe the contract through the 400-vs-401 difference (F-004 review fix; shared with the
    /// F-006 run endpoints, which authenticate the same way).
    /// </summary>
    public async Task<WorkerToken?> AuthenticateWorkerAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return null;
        }

        var hash = WorkerTokenCrypto.Hash(plaintextToken);
        WorkerToken? workerToken;
        using var scope = systemDataScope.BeginScope();
        {
            await using var lookupContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            var candidates = await lookupContext.SystemQueryAsync<WorkerToken>(
                token => token.TokenHash == hash && token.Kind == WorkerTokenKind.Worker,
                cancellationToken);
            workerToken = candidates.FirstOrDefault();
        }

        if (workerToken is null || workerToken.RevokedAt is not null)
        {
            logger.LogWarning(
                "Worker request rejected. Hash={TokenHashPrefix}… Reason={Reason}",
                hash[..LogHashPrefixLength],
                workerToken is null ? "unknown-token" : "revoked");
            return null;
        }

        // The token row - never a cookie, header or URL value - selects the tenant.
        tenantContext.OrganizationId = workerToken.OrganizationId;
        return workerToken;
    }

    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);

    private static List<string> ValidateRegistration(
        WorkerRegistrationInput input,
        out string name,
        out string os,
        out string architecture,
        out string version,
        out string tags,
        out int cpuCount,
        out int maxConcurrency)
    {
        var errors = new List<string>();

        var trimmedName = input.Name?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            errors.Add(WorkerErrorCodes.NameRequired);
        }
        else if (trimmedName.Length > 200)
        {
            errors.Add(WorkerErrorCodes.NameTooLong);
        }
        else if (trimmedName.Any(char.IsControl))
        {
            errors.Add(WorkerErrorCodes.NameInvalid);
        }

        os = ValidateText(input.Os, 32, errors);
        architecture = ValidateText(input.Architecture, 32, errors);
        version = ValidateText(input.Version, 64, errors);

        // B1 (review fix): tags are optional per PRD §3.4.4, exactly as in the heartbeat
        // validation - a worker started without tags registers with an empty tag list instead of
        // a 400 it would retry forever.
        tags = ValidateOptionalText(input.Tags, 400, errors) ?? string.Empty;

        cpuCount = input.CpuCount ?? 0;
        if (cpuCount < 1)
        {
            errors.Add(WorkerErrorCodes.FieldInvalid);
        }

        maxConcurrency = input.MaxConcurrency ?? 0;
        if (maxConcurrency < 1)
        {
            errors.Add(WorkerErrorCodes.FieldInvalid);
        }

        name = trimmedName ?? string.Empty;
        return errors;
    }

    private static List<string> ValidateHeartbeat(
        WorkerHeartbeatInput input,
        out string? os,
        out string? architecture,
        out string? version,
        out string? tags,
        out int? cpuCount,
        out int? maxConcurrency)
    {
        var errors = new List<string>();

        os = ValidateOptionalText(input.Os, 32, errors);
        architecture = ValidateOptionalText(input.Architecture, 32, errors);
        version = ValidateOptionalText(input.Version, 64, errors);
        tags = ValidateOptionalText(input.Tags, 400, errors);

        cpuCount = input.CpuCount;
        if (cpuCount is < 1)
        {
            errors.Add(WorkerErrorCodes.FieldInvalid);
        }

        maxConcurrency = input.MaxConcurrency;
        if (maxConcurrency is < 1)
        {
            errors.Add(WorkerErrorCodes.FieldInvalid);
        }

        return errors;
    }

    private static string ValidateText(string? value, int maxLength, List<string> errors)
    {
        var trimmed = value?.Trim();
        if (trimmed is null || trimmed.Length == 0)
        {
            errors.Add(WorkerErrorCodes.FieldInvalid);
            return string.Empty;
        }

        if (trimmed.Length > maxLength || trimmed.Any(char.IsControl))
        {
            errors.Add(WorkerErrorCodes.FieldInvalid);
        }

        return trimmed;
    }

    private static string? ValidateOptionalText(string? value, int maxLength, List<string> errors)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength || trimmed.Any(char.IsControl))
        {
            errors.Add(WorkerErrorCodes.FieldInvalid);
        }

        return trimmed;
    }
}
