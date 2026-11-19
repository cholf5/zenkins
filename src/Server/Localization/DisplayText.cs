using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Localization;

/// <summary>
/// Small display helpers for values that are stored as closed codes and shown as words. They live
/// next to the localizer because a code's word is UI copy, not data: the stored <c>Owner</c> and the
/// page's "所有者" must stay the same thing.
/// </summary>
public static class DisplayText
{
    /// <summary>The organization role's word: <c>Owner</c> or <c>Member</c>.</summary>
    public static string Role(OrganizationRole role, Localizer L10n) =>
        role == OrganizationRole.Owner ? L10n["Owner"] : L10n["Member"];
}
