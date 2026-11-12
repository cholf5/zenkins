using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.LogStreaming;

/// <summary>A validated subscription ticket: who may watch which organization's logs, until when.</summary>
public sealed record RunLogSubscriptionTicket(string UserId, Guid OrganizationId, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints and validates the short-lived subscription tickets the run detail page hands to the
/// interactive log panel (card F-008).
/// <para>
/// <b>Why tickets instead of the cookie:</b> the log panel runs in the interactive server circuit
/// and connects to the browser hub with a server-side <see cref="Microsoft.AspNetCore.SignalR.Client.HubConnection"/>
/// in the same process. A circuit does not hold (and must not replay) the authentication cookie, so
/// the hub would have nothing to authenticate. Instead the page - which has the full session -
/// mints a data-protected ticket binding the user and their active organization; the connection
/// presents it as a bearer token. The ticket is unforgeable (data protection), self-expiring, and
/// carries no secret: it authorizes nothing but "subscribe to log groups of my own organization".
/// Joining a group re-validates the membership against the database, so a ticket minted before a
/// membership revocation stops working at the next join.
/// </para>
/// </summary>
public sealed class RunLogAccess(
    IDataProtectionProvider dataProtectionProvider,
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    TenantContext tenantContext,
    RunLogOptions options)
{
    private const string ProtectionPurpose = "Zenkins.RunLogSubscription.v1";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectionPurpose);

    /// <summary>
    /// Mints a ticket for the given user and organization. Called by the run detail page with the
    /// values from the resolved session (never from request parameters), so no membership check is
    /// needed here - the caller is the server's own page render.
    /// </summary>
    public string MintTicket(string userId, Guid organizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var expiresAt = DateTimeOffset.UtcNow.Add(options.TicketLifetime);
        var payload = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"v1|{userId}|{organizationId}|{expiresAt.ToUnixTimeSeconds()}");
        return _protector.Protect(payload);
    }

    /// <summary>
    /// Validates a presented ticket: data-protection unprotect (forged or garbled → null), strict
    /// payload parse and expiry check. Returns null for any failure - callers answer 401.
    /// </summary>
    public RunLogSubscriptionTicket? ValidateTicket(string? protectedTicket)
    {
        if (string.IsNullOrEmpty(protectedTicket))
        {
            return null;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(protectedTicket);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }

        var parts = payload.Split('|');
        if (parts.Length != 4 || parts[0] != "v1"
            || !Guid.TryParse(parts[2], out var organizationId)
            || !long.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var expiresAtSeconds))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds);
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return new RunLogSubscriptionTicket(parts[1], organizationId, expiresAt);
    }

    /// <summary>
    /// The join-time authorization: the caller's membership of the ticket's organization is
    /// re-checked against the database (a revoked member cannot join with an old ticket), and the
    /// run must belong to that same organization - a foreign run id is indistinguishable from a
    /// missing one (both are a plain rejection, no existence oracle).
    /// </summary>
    public async Task<bool> CanJoinRunAsync(
        string userId,
        Guid organizationId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // The ticket's organization - never anything client-supplied - selects the tenant, so both
        // queries below run through the regular tenant filter (foreign rows simply do not exist).
        tenantContext.OrganizationId = organizationId;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var isMember = await context.OrganizationMembers.AnyAsync(
            member => member.UserId == userId, cancellationToken);
        if (!isMember)
        {
            return false;
        }

        return await context.BuildRuns.AnyAsync(run => run.Id == runId, cancellationToken);
    }
}
