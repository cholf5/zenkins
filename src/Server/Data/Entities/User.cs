using Microsoft.AspNetCore.Identity;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// The application user (PRD §6 <c>User</c>), an ASP.NET Identity user with a <see cref="string"/>
/// key.
/// <para>
/// Identity roles are deliberately <b>not</b> used: PRD §3.1.2 defines Owner/Member as
/// <em>Organization</em> roles, so the same person can be Owner of one organization and Member of
/// another. Those roles live on <see cref="OrganizationMember"/>.
/// </para>
/// </summary>
public sealed class User : IdentityUser
{
    /// <summary>When the account was created (UTC), stamped once at registration.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The organization this account last selected in the switcher (F-002). The selection is
    /// <b>server-side state</b>, never a cookie or URL value: every request resolves it again and
    /// only honours it while a matching <see cref="OrganizationMember"/> row exists (see
    /// <c>UserOrganizationDirectory</c>). A stale or forged value therefore falls back to a valid
    /// membership instead of being trusted.
    /// </summary>
    public Guid? ActiveOrganizationId { get; set; }
}
