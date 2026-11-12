using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
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
    int? MaxConcurrency);

/// <summary>The raw heartbeat payload. Every capability field is optional (refresh when present).</summary>
public sealed record WorkerHeartbeatInput(
    string? Os,
    string? Architecture,
    string? Version,
    string? Tags,
    int? CpuCount,
    int? MaxConcurrency);

/// <summary>A worker as the /workers page shows it.</summary>
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
    DateTimeOffset CreatedAt);

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

/// <summary>Outcome of a heartbeat: enough for the worker to keep its loop running.</summary>
public sealed record WorkerHeartbeatResult(
    WorkerOperationStatus Status,
    Guid? WorkerId = null,
    DateTimeOffset? LastHeartbeatAt = null)
{
    public static WorkerHeartbeatResult Success(Guid workerId, DateTimeOffset lastHeartbeatAt) =>
        new(WorkerOperationStatus.Success, workerId, lastHeartbeatAt);

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
            .Select(worker => new WorkerView(
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
                worker.CreatedAt))
            .ToArray();
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
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return WorkerHeartbeatResult.InvalidToken();
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
                "Worker heartbeat rejected. Hash={TokenHashPrefix}… Reason={Reason}",
                hash[..LogHashPrefixLength],
                workerToken is null ? "unknown-token" : "revoked");
            return WorkerHeartbeatResult.InvalidToken();
        }

        tenantContext.OrganizationId = workerToken.OrganizationId;

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

        return WorkerHeartbeatResult.Success(worker.Id, now);
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
