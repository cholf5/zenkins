using Zenkins.Server.Secrets;

namespace Zenkins.Server.Environments;

/// <summary>
/// The names the server itself owns in a step's environment (cards F-007/F-015/F-021/F-034).
/// <para>
/// <b>One set, one owner.</b> The worker writes this namespace into every step's environment after the
/// run's own layers, and it is the record of <em>this</em> run: a variable that shadows
/// <c>ZENKINS_RUN_ID</c> would make a step read a different run than the one it executes. The prefix is
/// therefore refused when an organization variable is saved - the author gets an error naming the
/// collision instead of discovering at run time that the value silently lost.
/// </para>
/// <para>
/// The comparison is case-insensitive because an environment variable name is case-insensitive on
/// Windows; refusing <c>zenkins_run_id</c> there would be the same shadowing under a different spelling.
/// </para>
/// </summary>
public static class ServerEnvironmentNames
{
    /// <summary>The prefixes the server-provided metadata occupies; any name starting with one is reserved.</summary>
    public static readonly string[] ReservedPrefixes = ["ZENKINS_"];

    /// <summary>Whether <paramref name="name"/> is inside a reserved prefix.</summary>
    public static bool IsReserved(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var prefix in ReservedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Field names of the organization environment form, constants so the page, the endpoint and the
/// tests cannot drift apart on spelling (the same discipline <see cref="SecretFields"/> follows).
/// </summary>
public static class OrganizationEnvironmentFields
{
    public const string Name = "Name";
    public const string Value = "Value";
}

/// <summary>
/// Stable validation/outcome codes for organization environment mutations. The service returns the
/// code, the endpoint puts it into the redirect query and the page maps it back to the field and the
/// message (the PRG shape F-002/F-003/F-012 use).
/// <para>
/// The three name-shape codes are <b>the secret codes themselves</b> (the constants reference
/// <see cref="SecretErrorCodes"/>), because the rule is the same rule - only the message differs.
/// </para>
/// </summary>
public static class OrganizationEnvironmentErrorCodes
{
    public const string NameRequired = SecretErrorCodes.NameRequired;
    public const string NameTooLong = SecretErrorCodes.NameTooLong;
    public const string NameInvalid = SecretErrorCodes.NameInvalid;
    public const string NameDuplicate = "name-duplicate";

    /// <summary>A name inside a reserved prefix (the server's own <c>ZENKINS_*</c> metadata).</summary>
    public const string NameReserved = "name-reserved";

    public const string ValueRequired = "value-required";
    public const string ValueTooLong = "value-too-long";

    /// <summary>The organization already holds the maximum number of variables.</summary>
    public const string LimitReached = "limit-reached";

    /// <summary>The form field the code belongs to, or <see langword="null"/> for a form-level refusal.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameInvalid or NameDuplicate or NameReserved =>
            OrganizationEnvironmentFields.Name,
        ValueRequired or ValueTooLong => OrganizationEnvironmentFields.Value,

        // The count cap is about the organization's list, not about one field: the page shows it as a
        // form-level alert.
        LimitReached => null,
        _ => null,
    };

    /// <summary>The message rendered next to the field (and in the form-level alert).</summary>
    public static string? MessageFor(string? code) => code switch
    {
        NameRequired => "Enter a variable name.",
        NameTooLong =>
            $"The variable name must be at most {OrganizationEnvironmentValidation.MaxNameLength} characters.",
        NameInvalid =>
            "Use letters, digits and underscores only, starting with a letter or an underscore. "
            + "The name must be usable as an environment variable (for example REGISTRY_HOST).",
        NameDuplicate => "A variable with this name already exists in this organization.",

        // AC3: the collision is named, not silently resolved in the metadata's favour.
        NameReserved =>
            $"The prefix {string.Join(", ", ServerEnvironmentNames.ReservedPrefixes)} is reserved for "
            + "Zenkins' own run metadata (ZENKINS_RUN_ID, ZENKINS_BUILD_NUMBER, …). Choose another name.",

        ValueRequired => "Enter the variable's value.",
        ValueTooLong =>
            $"The value must be at most {OrganizationEnvironmentValidation.MaxValueLength} characters.",
        LimitReached =>
            $"An organization can hold at most {OrganizationEnvironmentValidation.MaxVariables} environment "
            + "variables. Delete one you no longer need, then save this one.",
        _ => null,
    };
}

/// <summary>
/// Server-side validation of an organization environment variable (card F-034, PRD §3.1.1).
/// <para>
/// <b>The name rule is reused, not rewritten.</b> <see cref="ValidateName"/> delegates to
/// <see cref="SecretValidation.ValidateName"/>, so "the same name rules as a secret" is one
/// implementation: <c>^[A-Za-z_][A-Za-z0-9_]{0,63}$</c> after trimming. On top of that shape, a name
/// inside a <see cref="ServerEnvironmentNames"/> prefix is refused (<c>name-reserved</c>).
/// </para>
/// <para>
/// <b>Values are configuration, not credentials.</b> They are not trimmed (a Windows path may end in a
/// backslash and a value may legitimately contain spaces), are shown wherever they are used, and are
/// bounded in length only - the exact opposite of <see cref="SecretValidation"/>'s write-only handling,
/// which is why the two validators are separate files that share the name rule.
/// </para>
/// </summary>
public static class OrganizationEnvironmentValidation
{
    /// <summary>Longest accepted name: the stored-secret bound (the shape regex allows the same).</summary>
    public const int MaxNameLength = SecretValidation.MaxNameLength;

    /// <summary>
    /// Longest accepted value, 4 KiB. Generous for a URL, a path list or a proxy setting, and small
    /// enough that the whole snapshot (at most <see cref="MaxVariables"/> of them) stays a sane JSON
    /// document on the run row and in the claim.
    /// </summary>
    public const int MaxValueLength = 4 * 1024;

    /// <summary>
    /// How many variables one organization may hold. The cap exists because every variable is copied
    /// onto every run's snapshot and into every claim: it is configuration, but it must not become a
    /// data channel.
    /// </summary>
    public const int MaxVariables = 100;

    /// <summary>
    /// Validates and normalises a variable name. Returns an
    /// <see cref="OrganizationEnvironmentErrorCodes"/> value, or <see langword="null"/> when
    /// <paramref name="normalized"/> is acceptable.
    /// </summary>
    public static string? ValidateName(string? value, out string normalized)
    {
        // The shape is the secret rule verbatim (same trim, same regex, same 64-character bound).
        if (SecretValidation.ValidateName(value, out normalized) is { } shapeError)
        {
            return shapeError;
        }

        return ServerEnvironmentNames.IsReserved(normalized)
            ? OrganizationEnvironmentErrorCodes.NameReserved
            : null;
    }

    /// <summary>
    /// Validates a variable value without altering it. Returns an
    /// <see cref="OrganizationEnvironmentErrorCodes"/> value, or <see langword="null"/> when
    /// <paramref name="normalized"/> is acceptable.
    /// </summary>
    public static string? ValidateValue(string? value, out string normalized)
    {
        normalized = value ?? string.Empty;

        if (normalized.Length == 0)
        {
            return OrganizationEnvironmentErrorCodes.ValueRequired;
        }

        return normalized.Length > MaxValueLength ? OrganizationEnvironmentErrorCodes.ValueTooLong : null;
    }
}
