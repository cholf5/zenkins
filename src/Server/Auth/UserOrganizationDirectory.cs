using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Auth;

/// <summary>One organization the signed-in user belongs to, plus their role and when they joined.</summary>
public sealed record UserOrganization(
    Guid OrganizationId,
    string Name,
    OrganizationRole Role,
    DateTimeOffset JoinedAt);

/// <summary>
/// The resolved organization state of one user: the <see cref="Active"/> organization the request
/// must act as, and every <see cref="Memberships"/> row the switcher may offer.
/// </summary>
public sealed record UserOrganizations(
    UserOrganization? Active,
    IReadOnlyList<UserOrganization> Memberships);

/// <summary>
/// The single source of truth for "which organization is this user acting as" (F-002).
/// <para>
/// <b>Resolution order</b> (implemented by <see cref="ResolveAsync"/>):
/// <list type="number">
/// <item>the selection persisted on the user row (<see cref="User.ActiveOrganizationId"/>), but only
/// if a membership for that organization still exists;</item>
/// <item>otherwise a deterministic valid membership of theirs (Owner first, then earliest
/// membership, then organization id), which is then persisted so the next request is stable;</item>
/// <item>otherwise no organization.</item>
/// </list>
/// A cookie, query string, header or form value never selects the tenant directly. The only write of
/// the persisted selection is <see cref="SwitchAsync"/>, which rejects an organization the user is
/// not a member of; resolution re-checks it on every request anyway, so a stale selection (membership
/// removed, organization deleted) falls back instead of being honoured.
/// </para>
/// <para>
/// <see cref="OrganizationMember"/> is tenant-filtered and no tenant exists yet when this runs, so
/// every membership read happens inside <see cref="ISystemDataScope"/> - the escape hatch I-002
/// designated for exactly this resolution path. It reads only the caller's own membership rows and
/// organization names; it never enumerates users and never exposes another user's data.
/// </para>
/// </summary>
public sealed class UserOrganizationDirectory(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ISystemDataScope systemDataScope)
{
    /// <summary>
    /// Resolves the active organization and the membership list for <paramref name="userId"/>.
    /// The returned <see cref="UserOrganizations.Active"/> is <see langword="null"/> for a user who
    /// is not a member of any organization.
    /// </summary>
    public async Task<UserOrganizations> ResolveAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var memberships = await ReadMembershipsAsync(context, userId, cancellationToken);
        if (memberships.Count == 0)
        {
            var userWithoutMemberships = await context.Users.FindAsync([userId], cancellationToken);
            if (userWithoutMemberships?.ActiveOrganizationId is not null)
            {
                // The last membership disappeared (removed, or its organization was deleted): drop the
                // stale selection instead of leaving a value that would have to be rejected forever.
                userWithoutMemberships.ActiveOrganizationId = null;
                await context.SaveChangesAsync(cancellationToken);
            }

            return new UserOrganizations(null, []);
        }

        var user = await context.Users.FindAsync([userId], cancellationToken);

        var selected = user?.ActiveOrganizationId is { } selectedId
            ? memberships.FirstOrDefault(membership => membership.OrganizationId == selectedId)
            : null;

        if (selected is null)
        {
            if (user is not null)
            {
                // Persist the fallback: the active organization is server-side state, so it must not
                // silently change between two requests just because the previous choice became stale.
                selected = PickDefault(memberships);
                user.ActiveOrganizationId = selected.OrganizationId;
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                // A principal with memberships but no user row cannot be persisted; still resolve the
                // default so the request can render, without writing anything.
                selected = PickDefault(memberships);
            }
        }

        return new UserOrganizations(selected, OrderForSwitcher(memberships));
    }

    /// <summary>
    /// Persists <paramref name="organizationId"/> as the user's active organization, but only when a
    /// membership exists. Returns the selected organization, or <see langword="null"/> when the user
    /// does not belong to it (the caller must reject the request and leave the selection untouched).
    /// </summary>
    public async Task<UserOrganization?> SwitchAsync(
        string userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var memberships = await ReadMembershipsAsync(context, userId, cancellationToken);
        var target = memberships.FirstOrDefault(membership => membership.OrganizationId == organizationId);
        if (target is null)
        {
            // Not a member: reject before touching the row. This is the server-side guard that makes
            // a forged switch a no-op, regardless of what the browser submitted.
            return null;
        }

        var user = await context.Users.FindAsync([userId], cancellationToken);
        if (user is null)
        {
            return null;
        }

        if (user.ActiveOrganizationId != organizationId)
        {
            user.ActiveOrganizationId = organizationId;
            await context.SaveChangesAsync(cancellationToken);
        }

        return target;
    }

    /// <summary>
    /// Lists only the caller's own memberships. Used by the management pages to render the switcher
    /// without a second resolution round-trip when the active organization is not needed.
    /// </summary>
    public async Task<IReadOnlyList<UserOrganization>> ListForUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return OrderForSwitcher(await ReadMembershipsAsync(context, userId, cancellationToken));
    }

    private static async Task<List<UserOrganization>> ReadMembershipsAsync(
        ZenkinsDbContext context,
        string userId,
        CancellationToken cancellationToken)
    {
        var memberships = await context.SystemQueryAsync<OrganizationMember>(
            member => member.UserId == userId,
            cancellationToken);
        if (memberships.Count == 0)
        {
            return [];
        }

        var organizationIds = memberships.Select(member => member.OrganizationId).ToArray();
        var organizations = await context.Organizations
            .Where(organization => organizationIds.Contains(organization.Id))
            .ToDictionaryAsync(organization => organization.Id, cancellationToken);

        return memberships
            .Where(member => organizations.ContainsKey(member.OrganizationId))
            .Select(member => new UserOrganization(
                member.OrganizationId,
                organizations[member.OrganizationId].Name,
                member.Role,
                member.CreatedAt))
            .ToList();
    }

    /// <summary>
    /// Deterministic fallback: Owner memberships first, then the earliest membership, then
    /// organization id. The rule is stable across processes, so a stale selection cannot make the
    /// active organization flip between two equally valid choices.
    /// </summary>
    private static UserOrganization PickDefault(IReadOnlyList<UserOrganization> memberships) =>
        memberships
            .OrderByDescending(membership => membership.Role == OrganizationRole.Owner)
            .ThenBy(membership => membership.JoinedAt)
            .ThenBy(membership => membership.OrganizationId)
            .First();

    private static IReadOnlyList<UserOrganization> OrderForSwitcher(
        IReadOnlyList<UserOrganization> memberships) =>
        memberships
            .OrderBy(membership => membership.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(membership => membership.OrganizationId)
            .ToArray();
}
