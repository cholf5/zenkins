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
}
