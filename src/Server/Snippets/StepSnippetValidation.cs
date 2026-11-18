using Zenkins.Shared.Snippets;

namespace Zenkins.Server.Snippets;

/// <summary>The form fields a snippet validation error belongs to (the stable codes below map to them).</summary>
public static class SnippetFields
{
    public const string Name = "Name";
    public const string Kind = "Kind";
    public const string Body = "Body";
    public const string Description = "Description";
}

/// <summary>
/// The stable validation codes a snippet form redirects with (card F-035), and their sentences. Same shape as
/// the project/secret vocabularies: the code is what travels in the query string, the sentence is what the page
/// renders, and an unknown code still reads as itself instead of blank.
/// </summary>
public static class SnippetErrorCodes
{
    public const string NameRequired = "name-required";
    public const string NameTooLong = "name-too-long";
    public const string NameDuplicate = "name-duplicate";
    public const string KindInvalid = "kind-invalid";
    public const string BodyRequired = "body-required";
    public const string BodyTooLong = "body-too-long";
    public const string DescriptionTooLong = "description-too-long";

    /// <summary>The field a code belongs to, or <see langword="null"/> for an unknown code.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameDuplicate => SnippetFields.Name,
        KindInvalid => SnippetFields.Kind,
        BodyRequired or BodyTooLong => SnippetFields.Body,
        DescriptionTooLong => SnippetFields.Description,
        _ => null,
    };

    /// <summary>The sentence the page renders for a code; the raw code for one this build does not know.</summary>
    public static string MessageFor(string? code) => code switch
    {
        NameRequired => "A snippet needs a name.",
        NameTooLong => $"A snippet name is at most {StepSnippetLimits.MaxNameLength} characters.",
        NameDuplicate => "This organization already has a snippet with that name.",
        KindInvalid => "Choose a known snippet kind.",
        BodyRequired => "A snippet needs a body.",
        BodyTooLong => "The body is longer than the text it feeds allows (32 KiB for a script step, 64 KiB for a Lua fragment).",
        DescriptionTooLong => $"The description is at most {StepSnippetLimits.MaxDescriptionLength} characters.",
        _ => code ?? "The snippet was refused.",
    };
}

/// <summary>One rejected snippet field (the code and the field it belongs to).</summary>
public sealed record SnippetFieldError(string Field, string Code);

/// <summary>The raw values a snippet form submits (card F-035), before trimming and validation.</summary>
public sealed record SnippetInput(string? Name, string? Kind, string? Body, string? Description);

/// <summary>
/// Validates a snippet form (card F-035). The body is bounded like the text it feeds - a script step like a
/// Freestyle script, a Lua fragment like an inline pipeline script - so a snippet can never hold text the
/// target editor or the envelope would refuse.
/// </summary>
public static class StepSnippetValidation
{
    /// <summary>
    /// Validates and normalises the form. Returns the field errors (empty when acceptable) and the values a
    /// row stores: the trimmed name/description, the known kind and the body exactly as submitted (a body's
    /// whitespace is the author's text, not a form artefact).
    /// </summary>
    public static IReadOnlyList<SnippetFieldError> Validate(
        SnippetInput input,
        out string name,
        out string kind,
        out string body,
        out string? description)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = new List<SnippetFieldError>();

        name = input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            errors.Add(new SnippetFieldError(SnippetFields.Name, SnippetErrorCodes.NameRequired));
        }
        else if (name.Length > StepSnippetLimits.MaxNameLength)
        {
            errors.Add(new SnippetFieldError(SnippetFields.Name, SnippetErrorCodes.NameTooLong));
        }

        kind = input.Kind?.Trim() ?? string.Empty;
        if (!StepSnippetKinds.IsKnown(kind))
        {
            errors.Add(new SnippetFieldError(SnippetFields.Kind, SnippetErrorCodes.KindInvalid));
        }

        body = input.Body ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            errors.Add(new SnippetFieldError(SnippetFields.Body, SnippetErrorCodes.BodyRequired));
        }
        else if (StepSnippetKinds.IsKnown(kind) && body.Length > StepSnippetKinds.MaxBodyChars(kind))
        {
            errors.Add(new SnippetFieldError(SnippetFields.Body, SnippetErrorCodes.BodyTooLong));
        }

        description = input.Description?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            description = null;
        }
        else if (description.Length > StepSnippetLimits.MaxDescriptionLength)
        {
            errors.Add(new SnippetFieldError(SnippetFields.Description, SnippetErrorCodes.DescriptionTooLong));
        }

        return errors;
    }
}
