using Zenkins.Server.Localization;
using System.Text.RegularExpressions;
using Zenkins.Shared.Secrets;

namespace Zenkins.Server.Secrets;

/// <summary>
/// Field names as the secrets form and the service refer to them. Constants so the endpoint, the page
/// and the tests cannot drift apart on spelling.
/// </summary>
public static class SecretFields
{
    public const string Name = "Name";
    public const string Value = "Value";

    /// <summary>The kind selector (card F-040): <c>text</c> / <c>usernamePassword</c> / <c>file</c>.</summary>
    public const string Kind = "Kind";

    /// <summary>The username half of a pair secret (card F-040).</summary>
    public const string Username = "Username";

    /// <summary>The password half of a pair secret (card F-040).</summary>
    public const string Password = "Password";
}

/// <summary>
/// Stable validation/outcome codes for secret mutations. The service returns the code, the endpoint
/// puts it into the redirect query and the page maps it back to the field and the message (the same
/// PRG shape F-002/F-003 use).
/// </summary>
public static class SecretErrorCodes
{
    public const string NameRequired = "name-required";
    public const string NameTooLong = "name-too-long";
    public const string NameInvalid = "name-invalid";
    public const string NameDuplicate = "name-duplicate";

    public const string ValueRequired = "value-required";
    public const string ValueTooLong = "value-too-long";

    /// <summary>The submitted kind is not one this build knows (card F-040).</summary>
    public const string KindInvalid = "kind-invalid";

    /// <summary>A pair secret was submitted without its username half.</summary>
    public const string UsernameRequired = "username-required";

    /// <summary>A pair secret was submitted without its password half.</summary>
    public const string PasswordRequired = "password-required";

    /// <summary>A file secret's content is over <see cref="SecretFileLimits.MaxBytes"/>.</summary>
    public const string FileTooLarge = "file-too-large";

    /// <summary>The form field the code belongs to, or <see langword="null"/> for an unknown code.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameInvalid or NameDuplicate => SecretFields.Name,
        ValueRequired or ValueTooLong or FileTooLarge => SecretFields.Value,
        KindInvalid => SecretFields.Kind,
        UsernameRequired => SecretFields.Username,
        PasswordRequired => SecretFields.Password,
        _ => null,
    };

    /// <summary>The message rendered next to the field (and in the form-level alert).</summary>
    /// <summary>The message the page renders for a code, in the UI's language.</summary>
    public static string? MessageFor(string? code, Localizer L10n) => code switch
    {
        NameRequired => L10n["Enter a secret name."],
        NameTooLong =>
            L10n["The secret name must be at most {0} characters.", SecretValidation.MaxNameLength],
        NameInvalid =>
            L10n["Use letters, digits and underscores only, starting with a letter or an underscore. The name must be usable as an environment variable (for example NUGET_TOKEN)."],
        NameDuplicate => L10n["A secret with this name already exists in this organization."],
        ValueRequired => L10n["Enter the secret value."],
        ValueTooLong =>
            L10n["The secret value must be at most {0} characters.", SecretValidation.MaxValueLength],
        KindInvalid =>
            L10n["Choose one of the supported kinds: a text value, a username/password pair, or a file."],
        UsernameRequired => L10n["Enter the username half of this secret."],
        PasswordRequired => L10n["Enter the password half of this secret."],
        FileTooLarge =>
            L10n["A file secret must be at most {0} KiB of content.", SecretFileLimits.MaxBytes / 1024],
        _ => null,
    };
}

/// <summary>
/// Server-side validation of a secret's name and value (card F-012, PRD §3.8). The form's HTML
/// attributes are a convenience; <see cref="SecretService"/> re-runs every rule.
/// <para>
/// <b>Name.</b> <c>^[A-Za-z_][A-Za-z0-9_]{0,63}$</c>: the name is the identity, unique per
/// organization, case-sensitive and env-variable compatible, because F-015 exposes it as an
/// environment variable and <c>secret("NUGET_TOKEN")</c> must be unambiguous. The submitted value is
/// trimmed to the canonical name first (a stray space from a copy-paste must not become part of the
/// identity); anything that is not the allow-list afterwards is rejected by rule, not by trimming.
/// </para>
/// <para>
/// <b>Value.</b> Opaque: never trimmed, never logged, never hashed, and only required and bounded in
/// length. A secret may legitimately contain leading/trailing whitespace or newlines (a private key
/// block), so normalising it would corrupt it.
/// </para>
/// </summary>
public static class SecretValidation
{
    /// <summary>Longest accepted name (the regex allows the same bound).</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Longest accepted value: generous for tokens and private keys (F-013 stores Git credentials as
    /// secrets) while keeping one form field from being an unbounded row.
    /// </summary>
    public const int MaxValueLength = 64 * 1024;

    private static readonly Regex NamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Validates and normalises a secret name. Returns a <see cref="SecretErrorCodes"/> value, or
    /// <see langword="null"/> when <paramref name="normalized"/> is acceptable.
    /// </summary>
    public static string? ValidateName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return SecretErrorCodes.NameRequired;
        }

        if (normalized.Length > MaxNameLength)
        {
            return SecretErrorCodes.NameTooLong;
        }

        return NamePattern.IsMatch(normalized) ? null : SecretErrorCodes.NameInvalid;
    }

    /// <summary>
    /// Validates a secret value without altering it. Returns a <see cref="SecretErrorCodes"/> value,
    /// or <see langword="null"/> when <paramref name="normalized"/> is acceptable.
    /// </summary>
    public static string? ValidateValue(string? value, out string normalized)
    {
        normalized = value ?? string.Empty;

        if (normalized.Length == 0)
        {
            return SecretErrorCodes.ValueRequired;
        }

        return normalized.Length > MaxValueLength ? SecretErrorCodes.ValueTooLong : null;
    }

    /// <summary>
    /// Validates the kind a form submitted (card F-040). An <b>absent</b> kind is <c>text</c>, which is what
    /// every caller written before the kinds existed means; anything present but unknown is refused rather
    /// than quietly treated as text, because a mistyped kind would silently store a differently shaped value.
    /// </summary>
    public static string? ValidateKind(string? kind, out string normalized)
    {
        normalized = kind?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            normalized = SecretKinds.Default;
            return null;
        }

        return SecretKinds.IsKnown(normalized) ? null : SecretErrorCodes.KindInvalid;
    }

    /// <summary>
    /// Validates one half of a username/password pair (card F-040). Both halves are opaque and untrimmed like a
    /// text value; the bound is the same one, so a long token used as a password fits.
    /// </summary>
    /// <param name="missing">The code to answer when the half is absent.</param>
    public static string? ValidatePairHalf(string? value, string missing, out string normalized)
    {
        normalized = value ?? string.Empty;

        if (normalized.Length == 0)
        {
            return missing;
        }

        return normalized.Length > MaxValueLength ? SecretErrorCodes.ValueTooLong : null;
    }

    /// <summary>
    /// Validates a <b>file</b> secret's content (card F-040): untrimmed like every other value - a key file's
    /// leading and trailing newlines matter - and bounded in <b>UTF-8 bytes</b>, because that is the quantity
    /// the Worker writes to disk. Measured here, at save time, so a stored file secret is always deliverable.
    /// </summary>
    public static string? ValidateFileContent(string? value, out string normalized)
    {
        normalized = value ?? string.Empty;

        if (normalized.Length == 0)
        {
            return SecretErrorCodes.ValueRequired;
        }

        return System.Text.Encoding.UTF8.GetByteCount(normalized) > SecretFileLimits.MaxBytes
            ? SecretErrorCodes.FileTooLarge
            : null;
    }
}
