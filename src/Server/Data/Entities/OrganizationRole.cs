namespace Zenkins.Server.Data.Entities;

/// <summary>
/// Role inside a single organization (PRD §3.1.2). Deliberately not an ASP.NET Identity role:
/// one user can be <see cref="Owner"/> of one organization and <see cref="Member"/> of another.
/// </summary>
public enum OrganizationRole
{
    /// <summary>Full management rights over the organization.</summary>
    Owner = 0,

    /// <summary>May view projects and create/edit pipelines.</summary>
    Member = 1,
}
