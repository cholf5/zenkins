using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Organizations;

/// <summary>Why an organization mutation did or did not happen.</summary>
public enum OrganizationMutationStatus
{
    Success = 0,

    /// <summary>The caller is not an Owner of the active organization (or there is no active organization).</summary>
    Forbidden = 1,

    /// <summary>The target member/organization does not exist in the active organization.</summary>
    NotFound = 2,

    /// <summary>Input failed validation (empty name, bad email, unknown role).</summary>
    InvalidInput = 3,

    /// <summary>The change would leave the organization without an Owner.</summary>
    LastOwner = 4,

    /// <summary>Invitation target: no account with that exact email address exists.</summary>
    UserNotFound = 5,

    /// <summary>Invitation target already has a membership in the active organization.</summary>
    AlreadyMember = 6,
}

/// <summary>
/// Outcome of a mutation, with a message safe to show to the user and a stable code the HTTP
/// endpoints put in the redirect query (the UI maps it back to the same text).
/// </summary>
public sealed record OrganizationMutationResult(
    OrganizationMutationStatus Status,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool Succeeded => Status == OrganizationMutationStatus.Success;

    public static OrganizationMutationResult Success() => new(OrganizationMutationStatus.Success);

    public static OrganizationMutationResult Failure(
        OrganizationMutationStatus status,
        string errorCode,
        string message) => new(status, errorCode, message);
}

/// <summary>One row of the members list.</summary>
/// <param name="MembershipId">Membership row id, which is what role/remove actions address.</param>
/// <param name="Name">Account name (Identity <c>UserName</c>; registrations set it to the email).</param>
/// <param name="Email">Account email, or a placeholder when the account has none.</param>
/// <param name="Role">Role inside the active organization.</param>
/// <param name="JoinedAt">When the membership was created (UTC).</param>
public sealed record OrganizationMemberView(
    Guid MembershipId,
    string Name,
    string Email,
    OrganizationRole Role,
    DateTimeOffset JoinedAt);

/// <summary>
/// Organization management (card F-002): rename, member list, invite, role change and removal.
/// <para>
/// <b>Every method is Owner-only</b> except <see cref="GetMembersAsync"/> (members may see the list).
/// The guard is evaluated against the <b>active</b> tenant's membership row, server-side, before any
/// mutation; hiding a control in the UI is never the enforcement. Member ids/role values come from
/// the form, but they are looked up through the tenant-filtered <see cref="ZenkinsDbContext"/>, so an
/// id from another organization is indistinguishable from a nonexistent one (<see cref="OrganizationMutationStatus.NotFound"/>).
/// </para>
/// <para>
/// <b>Last-owner invariant:</b> demoting or removing an Owner first counts the remaining Owners inside
/// the same explicit transaction as the write, so the check and the update commit or roll back
/// together. v0.1 does not take row locks; on PostgreSQL two simultaneous role changes by two
/// different Owners could in principle interleave, which is recorded as a known gap rather than
/// claimed as solved.
/// </para>
/// <para>
/// <b>Invitations</b> attach an existing account by exact (normalized) email; v0.1 sends no email.
/// The membership is created immediately, so the invitee can use it on their next request.
/// </para>
/// </summary>
public sealed class OrganizationManagementService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    ILookupNormalizer lookupNormalizer,
    ILogger<OrganizationManagementService> logger)
{
    /// <summary>Maximum organization name length; matches the database column.</summary>
    public const int MaxOrganizationNameLength = 200;

    /// <summary>
    /// Members of the active organization with name, email, role and joined date. Visible to both
    /// roles (PRD §3.1.2 lets Members see the organization); management actions are separate methods.
    /// </summary>
    public async Task<IReadOnlyList<OrganizationMemberView>> GetMembersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Tenant-filtered: only the active organization's memberships are readable here.
        // SQLite cannot ORDER BY a DateTimeOffset, so the (small) member list is ordered in memory.
        var memberships = (await context.OrganizationMembers
                .ToListAsync(cancellationToken))
            .OrderBy(member => member.CreatedAt)
            .ThenBy(member => member.UserId, StringComparer.Ordinal)
            .ToList();
        if (memberships.Count == 0)
        {
            return [];
        }

        // One bounded lookup keyed by the member ids just read - never an enumeration of users.
        var userIds = memberships.Select(member => member.UserId).Distinct().ToArray();
        var users = await context.Users
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);

        return memberships
            .Select(member =>
            {
                users.TryGetValue(member.UserId, out var user);
                return new OrganizationMemberView(
                    member.Id,
                    user?.UserName ?? member.UserId,
                    user?.Email ?? "(no email)",
                    member.Role,
                    member.CreatedAt);
            })
            .ToArray();
    }

    /// <summary>Renames the active organization. Owner-only, guarded server-side.</summary>
    public async Task<OrganizationMutationResult> RenameActiveOrganizationAsync(
        string actingUserId,
        string? name,
        CancellationToken cancellationToken = default)
    {
        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.Forbidden,
                "forbidden",
                "Only an organization Owner can change these settings.");
        }

        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.InvalidInput,
                "invalid-name",
                "Enter an organization name.");
        }

        if (trimmedName.Length > MaxOrganizationNameLength)
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.InvalidInput,
                "invalid-name",
                $"The organization name must be at most {MaxOrganizationNameLength} characters.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.Forbidden,
                "forbidden",
                "Only an organization Owner can change these settings.");
        }

        var organization = await context.Organizations
            .SingleOrDefaultAsync(row => row.Id == activeOrganizationId.Value, cancellationToken);
        if (organization is null)
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.NotFound,
                "organization-not-found",
                "This organization no longer exists.");
        }

        organization.Name = trimmedName;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Organization renamed. OrganizationId={OrganizationId} ByUserId={UserId}",
            organization.Id,
            actingUserId);
        return OrganizationMutationResult.Success();
    }

    /// <summary>
    /// Invites an existing account by exact email (no mail delivery in v0.1). Owner-only. Creates a
    /// <see cref="OrganizationRole.Member"/> membership in the active organization.
    /// </summary>
    public async Task<OrganizationMutationResult> InviteAsync(
        string actingUserId,
        string? email,
        CancellationToken cancellationToken = default)
    {
        var trimmedEmail = email?.Trim();
        if (string.IsNullOrEmpty(trimmedEmail) || !new EmailAddressAttribute().IsValid(trimmedEmail))
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.InvalidInput,
                "invalid-email",
                "Enter the email address of an existing account.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.Forbidden,
                "forbidden",
                "Only an organization Owner can invite members.");
        }

        // Exact match on the normalized email via the unique Identity index; no user enumeration.
        var normalizedEmail = lookupNormalizer.NormalizeEmail(trimmedEmail);
        var user = await context.Users
            .SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, cancellationToken);
        if (user is null)
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.UserNotFound,
                "unknown-email",
                "No account exists with that email address. Invitations in v0.1 attach existing accounts only.");
        }

        var alreadyMember = await context.OrganizationMembers
            .AnyAsync(member => member.UserId == user.Id, cancellationToken);
        if (alreadyMember)
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.AlreadyMember,
                "already-member",
                "That account is already a member of this organization.");
        }

        context.OrganizationMembers.Add(new OrganizationMember
        {
            // The tenant comes from the active request; the data layer rejects any other value.
            OrganizationId = tenantContext.OrganizationId ?? Guid.Empty,
            UserId = user.Id,
            Role = OrganizationRole.Member,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "OrganizationMembers"))
        {
            // Two concurrent invitations: the unique index is the real guard.
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.AlreadyMember,
                "already-member",
                "That account is already a member of this organization.");
        }

        logger.LogInformation(
            "Organization member invited. OrganizationId={OrganizationId} MemberUserId={MemberUserId} ByUserId={UserId}",
            tenantContext.OrganizationId,
            user.Id,
            actingUserId);
        return OrganizationMutationResult.Success();
    }

    /// <summary>
    /// Changes a member's role. Owner-only; refuses to demote the last Owner in the same transaction
    /// as the write.
    /// </summary>
    public async Task<OrganizationMutationResult> ChangeRoleAsync(
        string actingUserId,
        Guid membershipId,
        OrganizationRole role,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role))
        {
            return OrganizationMutationResult.Failure(
                OrganizationMutationStatus.InvalidInput,
                "invalid-role",
                "Choose either Owner or Member.");
        }

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return Forbidden("Only an organization Owner can change roles.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return Forbidden("Only an organization Owner can change roles.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var member = await FindMemberAsync(context, membershipId, cancellationToken);
        if (member is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return NotFound("That member no longer exists.");
        }

        if (member.Role == OrganizationRole.Owner && role != OrganizationRole.Owner)
        {
            var ownerCount = await context.OrganizationMembers
                .CountAsync(candidate => candidate.Role == OrganizationRole.Owner, cancellationToken);
            if (ownerCount <= 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return LastOwner();
            }
        }

        member.Role = role;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Organization member role changed. OrganizationId={OrganizationId} MemberUserId={MemberUserId} Role={Role} ByUserId={UserId}",
            activeOrganizationId,
            member.UserId,
            role,
            actingUserId);
        return OrganizationMutationResult.Success();
    }

    /// <summary>
    /// Removes a member from the active organization. Owner-only; refuses to remove the last Owner in
    /// the same transaction as the delete. Also clears the removed user's server-side selection when
    /// it pointed here, so the next request falls back instead of re-validating a dead selection.
    /// </summary>
    public async Task<OrganizationMutationResult> RemoveMemberAsync(
        string actingUserId,
        Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return Forbidden("Only an organization Owner can remove members.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return Forbidden("Only an organization Owner can remove members.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var member = await FindMemberAsync(context, membershipId, cancellationToken);
        if (member is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return NotFound("That member no longer exists.");
        }

        if (member.Role == OrganizationRole.Owner)
        {
            var ownerCount = await context.OrganizationMembers
                .CountAsync(candidate => candidate.Role == OrganizationRole.Owner, cancellationToken);
            if (ownerCount <= 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return LastOwner();
            }
        }

        context.OrganizationMembers.Remove(member);

        var removedUser = await context.Users.FindAsync([member.UserId], cancellationToken);
        if (removedUser?.ActiveOrganizationId == activeOrganizationId)
        {
            removedUser.ActiveOrganizationId = null;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Organization member removed. OrganizationId={OrganizationId} MemberUserId={MemberUserId} ByUserId={UserId}",
            activeOrganizationId,
            member.UserId,
            actingUserId);
        return OrganizationMutationResult.Success();
    }

    /// <summary>
    /// Owner check against the <b>active</b> tenant. The membership query is tenant-filtered, so this
    /// cannot be satisfied by ownership of a different organization.
    /// </summary>
    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);

    private static Task<OrganizationMember?> FindMemberAsync(
        ZenkinsDbContext context,
        Guid membershipId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.SingleOrDefaultAsync(
            member => member.Id == membershipId,
            cancellationToken);

    private static OrganizationMutationResult Forbidden(string message) =>
        OrganizationMutationResult.Failure(OrganizationMutationStatus.Forbidden, "forbidden", message);

    private static OrganizationMutationResult NotFound(string message) =>
        OrganizationMutationResult.Failure(OrganizationMutationStatus.NotFound, "member-not-found", message);

    private static OrganizationMutationResult LastOwner() =>
        OrganizationMutationResult.Failure(
            OrganizationMutationStatus.LastOwner,
            "last-owner",
            "The last Owner cannot be demoted or removed. Make another member an Owner first.");
}
