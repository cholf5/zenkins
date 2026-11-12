using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Auth;

/// <summary>One organization the signed-in user belongs to, plus their role in it.</summary>
public sealed record UserOrganization(Guid OrganizationId, string Name, OrganizationRole Role);

/// <summary>
/// Reads the organizations a user belongs to. This lookup happens <b>before</b> a tenant is
/// selected (the user's membership is what selects it), and
/// <see cref="OrganizationMember"/> is tenant-filtered, so it runs inside
/// <see cref="ISystemDataScope"/> - the escape hatch I-002 designated for exactly this case.
/// <para>
/// It reads only the caller's own membership rows and returns organization names; it never exposes
/// another user's data. F-002's organization switcher reuses this shape and replaces the
/// "pick a default" rule with an explicit selection.
/// </para>
/// </summary>
public sealed class UserOrganizationDirectory(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ISystemDataScope systemDataScope)
{
    /// <summary>
    /// The organization this request/circuit should act as: Owner memberships first, then the
    /// earliest membership, then organization id so the choice is deterministic.
    /// Returns <see langword="null"/> for a user who is not a member of any organization.
    /// </summary>
    public async Task<UserOrganization?> FindDefaultForUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var memberships = await context.SystemQueryAsync<OrganizationMember>(
            member => member.UserId == userId,
            cancellationToken);
        if (memberships.Count == 0)
        {
            return null;
        }

        var organizationIds = memberships.Select(member => member.OrganizationId).ToArray();
        var organizations = await context.Organizations
            .Where(organization => organizationIds.Contains(organization.Id))
            .ToDictionaryAsync(organization => organization.Id, cancellationToken);

        var selected = memberships
            .Where(member => organizations.ContainsKey(member.OrganizationId))
            .OrderByDescending(member => member.Role == OrganizationRole.Owner)
            .ThenBy(member => member.CreatedAt)
            .ThenBy(member => member.OrganizationId)
            .FirstOrDefault();

        return selected is null
            ? null
            : new UserOrganization(selected.OrganizationId, organizations[selected.OrganizationId].Name, selected.Role);
    }
}
