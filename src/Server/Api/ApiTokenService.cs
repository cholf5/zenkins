using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Api;

/// <summary>Outcome of a token-management action. The HTTP layer maps each value to one status code.</summary>
public enum ApiTokenOperationStatus
{
    Success = 0,

    /// <summary>The request is malformed (a blank name, an unknown scope); nothing was written.</summary>
    InvalidInput = 1,

    /// <summary>The token id does not exist in the active organization (also the foreign case).</summary>
    NotFound = 2,

    /// <summary>The caller is not an Owner of the active organization.</summary>
    Forbidden = 3,
}

/// <summary>One minted token as the management page lists it. Never a hash, never a plaintext.</summary>
public sealed record ApiTokenView(
    Guid Id,
    string Name,
    string[] Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>Whether the token still authenticates (not revoked, not expired).</summary>
    public bool IsActive => RevokedAt is null && (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow);
}

/// <summary>The result of minting a token: the plaintext is returned exactly once, never stored.</summary>
public sealed record ApiTokenIssuedResult(
    ApiTokenOperationStatus Status,
    Guid? TokenId = null,
    string? Name = null,
    string? Plaintext = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public static ApiTokenIssuedResult Success(Guid tokenId, string name, string plaintext) =>
        new(ApiTokenOperationStatus.Success, tokenId, name, plaintext);

    public static ApiTokenIssuedResult Invalid(string errorCode) =>
        new(ApiTokenOperationStatus.InvalidInput, ErrorCode: errorCode);

    public static ApiTokenIssuedResult Forbidden(string message) =>
        new(ApiTokenOperationStatus.Forbidden, Message: message);
}

/// <summary>The result of revoking a token.</summary>
public sealed record ApiTokenActionResult(
    ApiTokenOperationStatus Status,
    string? ErrorCode = null,
    string? Message = null)
{
    public static ApiTokenActionResult Success() => new(ApiTokenOperationStatus.Success);

    public static ApiTokenActionResult Invalid(string errorCode) =>
        new(ApiTokenOperationStatus.InvalidInput, errorCode);

    public static ApiTokenActionResult NotFound() => new(ApiTokenOperationStatus.NotFound);

    public static ApiTokenActionResult Forbidden(string message) =>
        new(ApiTokenOperationStatus.Forbidden, Message: message);
}

/// <summary>
/// The identity a resolved API token presents to the rest of a request (card F-027). A plain value, not
/// an entity: the authentication path's context is disposed before any endpoint runs, so nothing here
/// may be a tracked row.
/// </summary>
/// <param name="Id">The token row's id (the rate limiter's bucket and the idempotency key's scope).</param>
/// <param name="OrganizationId">The tenant the token selects - never a client-supplied value.</param>
/// <param name="Name">The Owner's label, used only for attribution and logs.</param>
/// <param name="Scopes">The scopes the token carries.</param>
public sealed record ApiTokenIdentity(Guid Id, Guid OrganizationId, string Name, string[] Scopes)
{
    /// <summary>Whether this token carries <paramref name="scope"/>.</summary>
    public bool HasScope(string scope) => Array.IndexOf(Scopes, scope) >= 0;
}

/// <summary>
/// Organization API tokens (card F-027): mint, list, revoke, and the authentication path that turns a
/// plaintext bearer token into an <see cref="ApiTokenIdentity"/> plus the request's tenant.
/// <para>
/// <b>Minting is Owner-only</b> (the F-004 worker-token rule): the token is a standing authority for the
/// whole organization, so only an account that may already change the organization may create one.
/// Listing and revoking follow the same rule.
/// </para>
/// <para>
/// <b>Authentication is the token row's.</b> The hash lookup runs through the system scope (a token is
/// resolved before a tenant exists), and the resolved row - never a cookie, header value or URL - sets
/// <see cref="TenantContext.OrganizationId"/>. Every failure (missing, unknown, revoked, expired) is the
/// same <see langword="null"/>, which the endpoint layer answers as one bare 401.
/// </para>
/// </summary>
public sealed class ApiTokenService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    TenantContext tenantContext,
    ISystemDataScope systemDataScope,
    ILogger<ApiTokenService> logger)
{
    /// <summary>
    /// Mints one token: the plaintext is generated here, only the hash is stored, and the answer carries
    /// the plaintext - this is the only time it exists outside the caller's memory.
    /// </summary>
    public async Task<ApiTokenIssuedResult> CreateAsync(
        string actingUserId,
        string? name,
        IReadOnlyCollection<string>? scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is not { } organizationId)
        {
            return ApiTokenIssuedResult.Forbidden(
                "You are not a member of an organization, so there is no organization to mint a token for.");
        }

        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length == 0)
        {
            return ApiTokenIssuedResult.Invalid(ApiErrorCodes.NameRequired);
        }

        if (trimmedName.Length > ApiTokenLimits.MaxNameLength || trimmedName.Any(char.IsControl))
        {
            return ApiTokenIssuedResult.Invalid(ApiErrorCodes.NameInvalid);
        }

        var requested = scopes?.Where(scope => !string.IsNullOrWhiteSpace(scope)).ToArray() ?? [];
        if (requested.Length == 0)
        {
            return ApiTokenIssuedResult.Invalid(ApiErrorCodes.ScopeRequired);
        }

        if (requested.Any(scope => !ApiTokenScopes.IsValid(scope)))
        {
            return ApiTokenIssuedResult.Invalid(ApiErrorCodes.ScopeInvalid);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return ApiTokenIssuedResult.Forbidden("Only an organization Owner can create API tokens.");
        }

        var plaintext = ApiTokenCrypto.Generate();
        var token = new ApiToken
        {
            OrganizationId = organizationId,
            Name = trimmedName,
            TokenHash = ApiTokenCrypto.Hash(plaintext),
            Scopes = ApiTokenScopes.Format(requested),
            CreatedByUserId = actingUserId,
        };
        context.ApiTokens.Add(token);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "ApiTokens"))
        {
            // The label is unique per organization (the index, not a pre-flight read, is the guard).
            return ApiTokenIssuedResult.Invalid(ApiErrorCodes.NameTaken);
        }

        logger.LogInformation(
            "API token created. OrganizationId={OrganizationId} TokenId={TokenId} Scopes={Scopes} Hash={TokenHashPrefix}… ByUserId={UserId}",
            organizationId,
            token.Id,
            token.Scopes,
            token.TokenHash[..ApiTokenCrypto.LogHashPrefixLength],
            actingUserId);

        return ApiTokenIssuedResult.Success(token.Id, token.Name, plaintext);
    }

    /// <summary>
    /// The active organization's tokens, newest first. Never a hash, never a plaintext.
    /// <para>
    /// Owner-only, checked here rather than only on the page: a Member gets an empty list, exactly like
    /// every other refusal on this surface (the page renders its "Owner access required" card from the
    /// same answer). A non-member is refused by the tenant check above it - there is no organization to
    /// list tokens for.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ApiTokenView>> GetTokensAsync(
        string actingUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return [];
        }

        var tokens = await context.ApiTokens.ToListAsync(cancellationToken);
        return tokens
            .OrderByDescending(token => token.CreatedAt)
            .ThenBy(token => token.Id)
            .Select(token => new ApiTokenView(
                token.Id,
                token.Name,
                ApiTokenScopes.Parse(token.Scopes),
                token.CreatedAt,
                token.LastUsedAt,
                token.ExpiresAt,
                token.RevokedAt))
            .ToArray();
    }

    /// <summary>
    /// Revokes one token, permanently. A foreign or unknown id is the same <see cref="ApiTokenOperationStatus.NotFound"/>
    /// (no existence oracle); an already-revoked token is a refusal that changes nothing.
    /// </summary>
    public async Task<ApiTokenActionResult> RevokeAsync(
        string actingUserId,
        Guid tokenId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return ApiTokenActionResult.Forbidden(
                "You are not a member of an organization, so there is no token to revoke.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return ApiTokenActionResult.Forbidden("Only an organization Owner can revoke API tokens.");
        }

        var token = await context.ApiTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == tokenId,
            cancellationToken);
        if (token is null)
        {
            return ApiTokenActionResult.NotFound();
        }

        if (token.RevokedAt is not null)
        {
            return ApiTokenActionResult.Invalid(ApiErrorCodes.TokenAlreadyRevoked);
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "API token revoked. OrganizationId={OrganizationId} TokenId={TokenId} ByUserId={UserId}",
            token.OrganizationId,
            token.Id,
            actingUserId);

        return ApiTokenActionResult.Success();
    }

    /// <summary>
    /// Resolves a plaintext bearer token to its identity and selects the request's tenant, or
    /// <see langword="null"/> for every failure. The lookup runs through the system scope (no tenant
    /// exists yet); the resolved row then sets <see cref="TenantContext.OrganizationId"/>, and the
    /// token's last-used mark is written at most once a minute so a polling client cannot turn every
    /// read into a write.
    /// </summary>
    public async Task<ApiTokenIdentity?> AuthenticateAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return null;
        }

        var hash = ApiTokenCrypto.Hash(plaintextToken);
        ApiToken? token;
        using (systemDataScope.BeginScope())
        {
            await using var lookupContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            var candidates = await lookupContext.SystemQueryAsync<ApiToken>(
                candidate => candidate.TokenHash == hash,
                cancellationToken);
            token = candidates.FirstOrDefault();
        }

        var now = DateTimeOffset.UtcNow;
        if (token is null || token.RevokedAt is not null || token.ExpiresAt is { } expiry && expiry <= now)
        {
            logger.LogWarning(
                "API request rejected. Hash={TokenHashPrefix}… Reason={Reason}",
                hash[..ApiTokenCrypto.LogHashPrefixLength],
                token is null ? "unknown-token"
                    : token.RevokedAt is not null ? "revoked"
                    : "expired");
            return null;
        }

        // The token row - never a cookie, header or URL value - selects the tenant.
        tenantContext.OrganizationId = token.OrganizationId;

        if (token.LastUsedAt is null || now - token.LastUsedAt.Value >= TimeSpan.FromMinutes(1))
        {
            await TouchLastUsedAsync(token.Id, now, cancellationToken);
        }

        return new ApiTokenIdentity(
            token.Id,
            token.OrganizationId,
            token.Name,
            ApiTokenScopes.Parse(token.Scopes));
    }

    /// <summary>
    /// The run an <c>Idempotency-Key</c> already queued for this token, or <see langword="null"/> when
    /// the key is new. Read through the tenant filter, so a key of another organization's token is
    /// simply not found (the (token, key) index is already scoped by the token id).
    /// </summary>
    public async Task<Guid?> FindIdempotentRunAsync(
        Guid apiTokenId,
        string key,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ApiIdempotencyRecords
            .AsNoTracking()
            .Where(record => record.ApiTokenId == apiTokenId && record.Key == key)
            .Select(record => (Guid?)record.RunId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Remembers that <paramref name="key"/> queued <paramref name="runId"/>. The unique
    /// <c>(ApiTokenId, Key)</c> index is the guard: when a concurrent post with the same key won, this
    /// call answers the run that won instead of pretending to have stored anything.
    /// </summary>
    /// <returns>The run id the key is now bound to.</returns>
    public async Task<Guid> RememberIdempotentRunAsync(
        Guid apiTokenId,
        string key,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.ApiIdempotencyRecords.Add(new ApiIdempotencyRecord
        {
            ApiTokenId = apiTokenId,
            Key = key,
            RunId = runId,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return runId;
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "ApiIdempotencyRecords"))
        {
            // The key was claimed between the lookup and this insert: the stored binding is the answer.
            context.ChangeTracker.Clear();
            return await context.ApiIdempotencyRecords
                .AsNoTracking()
                .Where(record => record.ApiTokenId == apiTokenId && record.Key == key)
                .Select(record => record.RunId)
                .SingleAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Stamps the token's last-used instant. The mark is <b>best effort and never vetoes the request</b>:
    /// the token has already authenticated by the time this runs, so no write failure here - a concurrent
    /// revoke losing the row, a SQLite busy timeout, a full disk - may turn an authenticated call into a
    /// 500. Any <see cref="DbUpdateException"/> (the concurrency one included) is logged as a warning and
    /// swallowed; a revoke that won the row still takes effect on the token's next request.
    /// </summary>
    private async Task TouchLastUsedAsync(Guid tokenId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var token = await context.ApiTokens.SingleOrDefaultAsync(
                candidate => candidate.Id == tokenId,
                cancellationToken);
            if (token is null)
            {
                return;
            }

            token.LastUsedAt = now;
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure)
        {
            // Best effort by contract: the request proceeds, and the next one retries the mark.
            logger.LogWarning(
                failure,
                "Stamping the API token's last-used time failed; the authenticated request proceeds. TokenId={TokenId}",
                tokenId);
        }
    }

    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);
}
