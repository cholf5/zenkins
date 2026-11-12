using System.Text.RegularExpressions;

namespace Zenkins.Server.Projects;

/// <summary>
/// Field names as the project form and the service refer to them. Kept as constants so the
/// endpoint, the pages and the tests cannot drift apart on spelling.
/// </summary>
public static class ProjectFields
{
    public const string Name = "Name";
    public const string RepositoryUrl = "RepositoryUrl";
    public const string DefaultBranch = "DefaultBranch";
    public const string PipelinePath = "PipelinePath";
}

/// <summary>
/// Stable validation codes. The service returns the code, the endpoint puts it in the redirect
/// query and the form page maps it back to the field and the message (the same PRG shape F-002
/// uses for organization validation).
/// </summary>
public static class ProjectErrorCodes
{
    public const string NameRequired = "name-required";
    public const string NameTooLong = "name-too-long";
    public const string NameInvalid = "name-invalid";
    public const string NameDuplicate = "name-duplicate";

    public const string RepositoryUrlRequired = "repository-url-required";
    public const string RepositoryUrlTooLong = "repository-url-too-long";
    public const string RepositoryUrlInvalid = "repository-url-invalid";

    public const string DefaultBranchRequired = "default-branch-required";
    public const string DefaultBranchInvalid = "default-branch-invalid";

    public const string PipelinePathTooLong = "pipeline-path-too-long";
    public const string PipelinePathInvalid = "pipeline-path-invalid";

    /// <summary>The form field the code belongs to, or <see langword="null"/> for an unknown code.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameInvalid or NameDuplicate => ProjectFields.Name,
        RepositoryUrlRequired or RepositoryUrlTooLong or RepositoryUrlInvalid => ProjectFields.RepositoryUrl,
        DefaultBranchRequired or DefaultBranchInvalid => ProjectFields.DefaultBranch,
        PipelinePathTooLong or PipelinePathInvalid => ProjectFields.PipelinePath,
        _ => null,
    };

    /// <summary>The message rendered next to the field (and in the form-level alert).</summary>
    public static string? MessageFor(string? code) => code switch
    {
        NameRequired => "Enter a project name.",
        NameTooLong => $"The project name must be at most {ProjectValidation.MaxNameLength} characters.",
        NameInvalid => "The project name contains characters that are not allowed.",
        NameDuplicate => "A project with this name already exists in this organization.",
        RepositoryUrlRequired => "Enter the Git repository URL.",
        RepositoryUrlTooLong => $"The repository URL must be at most {ProjectValidation.MaxRepositoryUrlLength} characters.",
        RepositoryUrlInvalid =>
            "Only HTTPS or SSH git URLs are supported, for example https://host/owner/repo.git "
            + "or git@host:owner/repo.git. file://, ext:: and other transports are rejected.",
        DefaultBranchRequired => "Enter the default branch.",
        DefaultBranchInvalid => "That is not a valid branch name.",
        PipelinePathTooLong => $"The pipeline path must be at most {ProjectValidation.MaxPipelinePathLength} characters.",
        PipelinePathInvalid =>
            "Enter a relative path inside the repository (for example pipeline.lua). "
            + "Absolute paths, backslashes and '..' are rejected.",
        _ => null,
    };
}

/// <summary>
/// Server-side validation for PRD §3.2 project fields (card F-003). Every rule here is re-checked by
/// <see cref="ProjectService"/>; the HTML form's <c>required</c>/<c>maxlength</c> attributes are a
/// convenience, never the enforcement.
/// <para>
/// <b>Repository URL allow-list.</b> Only <c>https://</c> and <c>ssh://</c> URLs and git's scp-like
/// <c>user@host:path</c> form are accepted. That matters because git can be told to run a transport
/// helper instead of a network protocol: <c>file://</c> reads the server's disk and <c>ext::</c>
/// runs an arbitrary command. An allow-list keeps future git transports out by default instead of
/// chasing a deny-list.
/// </para>
/// </summary>
public static class ProjectValidation
{
    /// <summary>PRD §3.2's default pipeline file name.</summary>
    public const string DefaultPipelinePath = "pipeline.lua";

    public const int MaxNameLength = 200;
    public const int MaxRepositoryUrlLength = 2048;
    public const int MaxDefaultBranchLength = 200;
    public const int MaxPipelinePathLength = 512;

    // git's scp-like syntax: user@host:path. The path must be relative (git treats a leading '/'
    // as an absolute server path) and may not contain a colon, because a second colon makes the
    // value ambiguous enough that a transport helper could be selected.
    private static readonly Regex ScpLikeRepositoryUrl = new(
        "^(?<user>[A-Za-z0-9][A-Za-z0-9._-]*)@(?<host>[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?)"
        + ":(?<path>(?!/)(?!-)[^\\s:]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Validates and trims the project name. Returns a <see cref="ProjectErrorCodes"/> value or null.</summary>
    public static string? ValidateName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return ProjectErrorCodes.NameRequired;
        }

        if (normalized.Length > MaxNameLength)
        {
            return ProjectErrorCodes.NameTooLong;
        }

        return normalized.Any(char.IsControl) ? ProjectErrorCodes.NameInvalid : null;
    }

    /// <summary>
    /// Validates and trims the repository URL. Only HTTPS, SSH and the scp-like SSH form pass; every
    /// other scheme (including <c>file</c> and <c>ext</c>) is rejected with one stable message.
    /// </summary>
    public static string? ValidateRepositoryUrl(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return ProjectErrorCodes.RepositoryUrlRequired;
        }

        if (normalized.Length > MaxRepositoryUrlLength)
        {
            return ProjectErrorCodes.RepositoryUrlTooLong;
        }

        // No whitespace or control characters anywhere. This alone rejects git's transport helper
        // form ("ext::sh -c ..."), which needs a command line to be useful, and it keeps the value
        // safe to log and to hand to a process argument later.
        if (normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return ProjectErrorCodes.RepositoryUrlInvalid;
        }

        // A leading dash would be parsed as an option by git if the value were ever passed through
        // unguarded; reject it instead of relying on a later "--" separator.
        if (normalized[0] == '-')
        {
            return ProjectErrorCodes.RepositoryUrlInvalid;
        }

        if (normalized.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                return ProjectErrorCodes.RepositoryUrlInvalid;
            }

            // The allow-list: only these two schemes can reach a repository. file:// is a read of
            // the server filesystem and git:// is unauthenticated and unencrypted, so neither is
            // accepted even though git supports them.
            var schemeIsSupported =
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase);
            if (!schemeIsSupported)
            {
                return ProjectErrorCodes.RepositoryUrlInvalid;
            }

            if (string.IsNullOrWhiteSpace(uri.Host)
                || string.IsNullOrEmpty(uri.AbsolutePath)
                || uri.AbsolutePath == "/"
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0)
            {
                return ProjectErrorCodes.RepositoryUrlInvalid;
            }

            return null;
        }

        // No scheme: the only remaining supported shape is git's scp-like SSH syntax.
        return ScpLikeRepositoryUrl.IsMatch(normalized)
            ? null
            : ProjectErrorCodes.RepositoryUrlInvalid;
    }

    /// <summary>
    /// Validates and trims the default branch. These are git ref-shape checks, not a full
    /// <c>git check-ref-format</c> implementation: they reject the values that turn into paths
    /// (<c>..</c>, leading slash) or command-line options (leading dash) and the characters git
    /// itself forbids in a ref.
    /// </summary>
    public static string? ValidateDefaultBranch(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return ProjectErrorCodes.DefaultBranchRequired;
        }

        if (normalized.Length > MaxDefaultBranchLength)
        {
            return ProjectErrorCodes.DefaultBranchInvalid;
        }

        if (normalized[0] is '/' or '-'
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Contains('\\')
            || normalized.Contains("//", StringComparison.Ordinal)
            || normalized.Contains("@{", StringComparison.Ordinal)
            || normalized.IndexOfAny(['~', '^', ':', '?', '*', '[', '\u007f']) >= 0
            || normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || normalized.EndsWith('/')
            || normalized.EndsWith('.')
            || normalized.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectErrorCodes.DefaultBranchInvalid;
        }

        return null;
    }

    /// <summary>
    /// Validates the pipeline path and applies the PRD default when it is blank. A repository-relative
    /// path only: no leading slash, no drive/UNC prefix, no backslashes and no <c>..</c> segment.
    /// </summary>
    public static string? ValidatePipelinePath(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            normalized = DefaultPipelinePath;
        }

        if (normalized.Length > MaxPipelinePathLength)
        {
            return ProjectErrorCodes.PipelinePathTooLong;
        }

        if (normalized[0] is '/' or '\\'
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Contains('\\')
            || normalized.Contains("//", StringComparison.Ordinal)
            || normalized.EndsWith('/')
            || normalized.Any(char.IsControl)
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            return ProjectErrorCodes.PipelinePathInvalid;
        }

        return null;
    }
}
