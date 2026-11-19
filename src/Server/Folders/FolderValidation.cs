using Zenkins.Server.Localization;

namespace Zenkins.Server.Folders;

/// <summary>
/// Field and error-code vocabulary for folders (card F-042). Kept apart from
/// <see cref="Zenkins.Server.Projects.ProjectValidation"/>'s codes so a folder form's refusal cannot be
/// mistaken for a project's, and stable so the POST endpoints and the pages cannot drift on spelling.
/// </summary>
public static class FolderFields
{
    public const string Name = "FolderName";
    public const string Description = "FolderDescription";

    /// <summary>The parent selector on the create/move forms (a folder id, or blank for the root).</summary>
    public const string Parent = "ParentFolderId";

    /// <summary>The target folder on the "move project" form (a folder id, or blank for the root).</summary>
    public const string Folder = "FolderId";
}

/// <summary>Stable validation codes; the page maps each back to its field and message.</summary>
public static class FolderErrorCodes
{
    public const string NameRequired = "folder-name-required";
    public const string NameTooLong = "folder-name-too-long";
    public const string NameInvalid = "folder-name-invalid";

    /// <summary>Another folder with the same name (case-insensitively) already exists among the siblings.</summary>
    public const string NameDuplicate = "folder-name-duplicate";

    public const string DescriptionTooLong = "folder-description-too-long";
    public const string DescriptionInvalid = "folder-description-invalid";

    /// <summary>The posted parent (or the folder being moved into) does not exist in the active organization.</summary>
    public const string ParentNotFound = "folder-parent-not-found";

    /// <summary>A folder can be at most <see cref="FolderValidation.MaxDepth"/> levels below the root.</summary>
    public const string DepthExceeded = "folder-depth-exceeded";

    /// <summary>The move would put a folder inside itself or one of its descendants.</summary>
    public const string Cycle = "folder-cycle";

    /// <summary>The target folder holds projects or child folders - delete or move them first.</summary>
    public const string NotEmpty = "folder-not-empty";

    /// <summary>The posted id is not a folder id (never coerced to "the root").</summary>
    public const string ParentInvalid = "folder-parent-invalid";

    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameInvalid or NameDuplicate => FolderFields.Name,
        DescriptionTooLong or DescriptionInvalid => FolderFields.Description,
        ParentNotFound or ParentInvalid or DepthExceeded or Cycle => FolderFields.Parent,
        _ => null,
    };

    /// <summary>The message the page renders for a code, in the UI's language.</summary>
    public static string? MessageFor(string? code, Localizer L10n) => code switch
    {
        NameRequired => L10n["Enter a folder name."],
        NameTooLong => L10n["The folder name must be at most {0} characters.", FolderValidation.MaxNameLength],
        NameInvalid => L10n["The folder name contains characters that are not allowed."],
        NameDuplicate => L10n["A folder with this name already exists here (names are compared ignoring case)."],
        DescriptionTooLong =>
            L10n["The description must be at most {0} characters.", FolderValidation.MaxDescriptionLength],
        DescriptionInvalid => L10n["The description contains characters that are not allowed."],
        ParentNotFound => L10n["That parent folder does not exist in this organization."],
        ParentInvalid => L10n["That is not a folder id."],
        DepthExceeded =>
            L10n["Folders can be nested at most {0} levels deep.", FolderValidation.MaxDepth],
        Cycle => L10n["A folder cannot be moved inside itself or one of its own subfolders."],
        NotEmpty => L10n["This folder still has projects or subfolders inside - move or delete them first."],
        _ => null,
    };
}

/// <summary>
/// Folder rules (card F-042). Everything here is re-checked by <see cref="FolderService"/>; a page's
/// <c>maxlength</c> is a convenience, never the enforcement.
/// </summary>
public static class FolderValidation
{
    public const int MaxNameLength = 200;

    public const int MaxDescriptionLength = 500;

    /// <summary>
    /// The nesting cap (AC-1's "e.g. 5 levels"): a root folder is depth 1, so a folder at this depth has no
    /// children. <see cref="Zenkins.Server.Data.Entities.Folder.Depth"/> stores it and the model's check
    /// constraint restates the bound, so an out-of-band writer cannot invent a sixth level.
    /// </summary>
    public const int MaxDepth = 5;

    /// <summary>
    /// The case-insensitive identity every name-key column is built from. One spelling, one place: the
    /// folder and project unique indexes compare this value, so "Build" and "build" are one name.
    /// </summary>
    public static string NameKeyFor(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Validates and trims a folder name. Control characters are refused (the name is rendered verbatim
    /// in a breadcrumb and a select list), and the length bound matches the column.
    /// </summary>
    public static string? ValidateName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return FolderErrorCodes.NameRequired;
        }

        if (normalized.Length > MaxNameLength)
        {
            return FolderErrorCodes.NameTooLong;
        }

        return normalized.Any(char.IsControl) ? FolderErrorCodes.NameInvalid : null;
    }

    /// <summary>
    /// Validates and trims the optional description. Newlines and tabs are allowed (it is a note, not a
    /// breadcrumb label); every other control character is refused. Blank means "no description".
    /// </summary>
    public static string? ValidateDescription(string? value, out string? normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            normalized = null;
            return null;
        }

        if (normalized.Length > MaxDescriptionLength)
        {
            return FolderErrorCodes.DescriptionTooLong;
        }

        return normalized.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'))
            ? FolderErrorCodes.DescriptionInvalid
            : null;
    }
}
