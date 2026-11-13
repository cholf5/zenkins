using System.Text.RegularExpressions;

namespace Zenkins.Server.Secrets;

/// <summary>
/// Field names as the secrets form and the service refer to them. Constants so the endpoint, the page
/// and the tests cannot drift apart on spelling.
/// </summary>
public static class SecretFields
{
    public const string Name = "Name";
    public const string Value = "Value";
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

    /// <summary>The form field the code belongs to, or <see langword="null"/> for an unknown code.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameInvalid or NameDuplicate => SecretFields.Name,
        ValueRequired or ValueTooLong => SecretFields.Value,
        _ => null,
    };

    /// <summary>The message rendered next to the field (and in the form-level alert).</summary>
    public static string? MessageFor(string? code) => code switch
    {
        NameRequired => "Enter a secret name.",
        NameTooLong =>
            $"The secret name must be at most {SecretValidation.MaxNameLength} characters.",
        NameInvalid =>
            "Use letters, digits and underscores only, starting with a letter or an underscore. "
            + "The name must be usable as an environment variable (for example NUGET_TOKEN).",
        NameDuplicate => "A secret with this name already exists in this organization.",
        ValueRequired => "Enter the secret value.",
        ValueTooLong =>
            $"The secret value must be at most {SecretValidation.MaxValueLength} characters.",
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
}
