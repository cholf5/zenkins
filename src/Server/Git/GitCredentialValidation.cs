using Zenkins.Server.Secrets;

namespace Zenkins.Server.Git;

/// <summary>Field names the Git credential forms and the service refer to.</summary>
public static class GitCredentialFields
{
    public const string Name = "Name";
    public const string Kind = "Kind";
    public const string Value = "Value";
    public const string UserName = "UserName";
}

/// <summary>Stable validation/outcome codes for a Git credential mutation.</summary>
public static class GitCredentialErrorCodes
{
    public const string NameRequired = "name-required";
    public const string NameTooLong = "name-too-long";
    public const string NameInvalid = "name-invalid";
    public const string NameDuplicate = "name-duplicate";

    public const string KindInvalid = "kind-invalid";

    public const string ValueRequired = "value-required";
    public const string ValueTooLong = "value-too-long";

    /// <summary>The value does not look like what the kind claims (a token with line breaks, or a key
    /// without a BEGIN/PRIVATE KEY header).</summary>
    public const string ValueShape = "value-shape";

    public const string UserNameTooLong = "user-name-too-long";
    public const string UserNameInvalid = "user-name-invalid";
    public const string FieldInvalid = "field-invalid";

    /// <summary>The field a code belongs to, or <see langword="null"/> for an unknown code.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameInvalid or NameDuplicate => GitCredentialFields.Name,
        KindInvalid => GitCredentialFields.Kind,
        ValueRequired or ValueTooLong or ValueShape => GitCredentialFields.Value,
        UserNameTooLong or UserNameInvalid => GitCredentialFields.UserName,
        _ => null,
    };

    /// <summary>The message rendered next to the field.</summary>
    public static string? MessageFor(string? code) => code switch
    {
        NameRequired => "Enter a credential name.",
        NameTooLong =>
            $"The credential name must be at most {SecretValidation.MaxNameLength} characters.",
        NameInvalid =>
            "Use letters, digits and underscores only, starting with a letter or an underscore.",
        NameDuplicate => "A Git credential with this name already exists in this organization.",
        KindInvalid => "Choose 'HTTPS token' or 'SSH key'.",
        ValueRequired => "Enter the credential value.",
        ValueTooLong =>
            $"The credential value must be at most {SecretValidation.MaxValueLength} characters.",
        ValueShape =>
            "An HTTPS token must be a single line; an SSH key must be a PRIVATE KEY block "
            + "starting with '-----BEGIN'.",
        UserNameTooLong => "The user name must be at most 200 characters.",
        UserNameInvalid => "The user name must not contain control characters or line breaks.",
        _ => null,
    };
}

/// <summary>
/// Server-side validation of a Git credential (card F-013). The name reuses F-012's rules verbatim -
/// the two stores are referenced from the same project settings, so a name must mean the same thing in
/// both - while the value rules are kind-aware, because a token and a private key have different
/// failure modes.
/// </summary>
public static class GitCredentialValidation
{
    /// <summary>Longest accepted <see cref="GitCredential.UserName"/>.</summary>
    public const int MaxUserNameLength = 200;

    /// <summary>
    /// Shortest accepted value. A short value is almost always a misconfiguration (a truncated paste) and
    /// a real credential is longer than this. The worker's log filter redacts every non-empty value
    /// regardless of length (F-015 removed its old four-character threshold), so the rule here is about
    /// rejecting a broken credential, not about what the filter can protect.
    /// </summary>
    public const int MinValueLength = 8;

    /// <summary>Validates the name with F-012's exact rules (trimmed, env-var shaped, ≤ 64 chars).</summary>
    public static string? ValidateName(string? value, out string normalized) =>
        SecretValidation.ValidateName(value, out normalized);

    /// <summary>Validates the kind discriminator.</summary>
    public static string? ValidateKind(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized is GitCredentialKindTexts.HttpsToken or GitCredentialKindTexts.SshKey
            ? null
            : GitCredentialErrorCodes.KindInvalid;
    }

    /// <summary>
    /// Validates the value against its kind. The value itself is never trimmed: leading whitespace is
    /// meaningful in a private key block, and a token with a stray newline is a paste error the user
    /// must see rather than a value the server silently repairs.
    /// </summary>
    public static string? ValidateValue(string? kind, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return GitCredentialErrorCodes.ValueRequired;
        }

        if (value.Length > SecretValidation.MaxValueLength)
        {
            return GitCredentialErrorCodes.ValueTooLong;
        }

        if (value.Length < MinValueLength)
        {
            return GitCredentialErrorCodes.ValueShape;
        }

        return kind switch
        {
            // A token is one line: a newline would break the ask-pass helper (and make the token
            // impossible to deliver without a quoting hazard).
            GitCredentialKindTexts.HttpsToken =>
                value.AsSpan().IndexOfAny('\r', '\n') >= 0 || value.Contains('\0', StringComparison.Ordinal)
                    ? GitCredentialErrorCodes.ValueShape
                    : null,

            // An SSH key is a PEM/OpenSSH block: require the header so a token pasted into the key
            // field fails here rather than in ssh's parser on the worker.
            GitCredentialKindTexts.SshKey =>
                value.TrimStart().StartsWith("-----BEGIN ", StringComparison.Ordinal)
                && value.Contains("PRIVATE KEY", StringComparison.Ordinal)
                    ? null
                    : GitCredentialErrorCodes.ValueShape,

            _ => GitCredentialErrorCodes.KindInvalid,
        };
    }

    /// <summary>Validates the optional user name (an account label, not a secret).</summary>
    public static string? ValidateUserName(string? value, out string? normalized)
    {
        normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            normalized = null;
            return null;
        }

        if (normalized.Length > MaxUserNameLength)
        {
            return GitCredentialErrorCodes.UserNameTooLong;
        }

        return normalized.Any(character => char.IsControl(character))
            ? GitCredentialErrorCodes.UserNameInvalid
            : null;
    }
}

/// <summary>The kind discriminators as stored in <see cref="GitCredential.Kind"/>.</summary>
public static class GitCredentialKindTexts
{
    public const string HttpsToken = "HttpsToken";
    public const string SshKey = "SshKey";
}

/// <summary>
/// Validation of the commit a worker reports with its <c>accepted</c> report (card F-013). The server
/// accepts only an object name - a full SHA-1 (40 hex) or SHA-256 (64 hex) - never a branch, a tag or
/// a revision expression: <see cref="Zenkins.Server.Data.Entities.BuildRun.GitCommit"/> records what
/// was built, so a moving reference would make it meaningless.
/// </summary>
public static class GitCommitValidation
{
    /// <summary>Longest accepted value (a SHA-256 object name).</summary>
    public const int MaxLength = 64;

    /// <summary>The error code for a malformed report; the worker gives the run up on a 400.</summary>
    public const string InvalidCode = "git-commit-invalid";

    /// <summary>Whether the text is a full lower-case hex object name of 40 or 64 characters.</summary>
    public static bool IsValid(string? commit) =>
        commit is { Length: 40 or 64 }
        && commit.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
