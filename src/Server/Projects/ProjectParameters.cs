using System.Text.Json;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;

namespace Zenkins.Server.Projects;

/// <summary>
/// One build parameter as the parameters editor submits it (card F-021): a serialized draft, not a
/// definition. The Server parses the choice list (a comma-separated line, like the steps editor's
/// artifacts) and validates every rule here, so the stored JSON is a definition the trigger form and
/// the value resolver can trust.
/// </summary>
/// <param name="Name">Env-variable shaped; the environment variable the steps read.</param>
/// <param name="Type">One of <see cref="ProjectParameterTypes"/>.</param>
/// <param name="DefaultValue">The editor's default field; empty means "no default".</param>
/// <param name="ChoicesText">Comma-separated choices for a choice parameter; ignored otherwise.</param>
/// <param name="Description">The hint the trigger form renders.</param>
public sealed record ProjectParameterDraft(
    string? Name,
    string? Type,
    string? DefaultValue,
    string? ChoicesText,
    string? Description);

/// <summary>The parameters editor's form payload: the drafts, serialized as JSON by the page.</summary>
public sealed record ProjectParametersInput(string? ParametersJson);

/// <summary>
/// The build-parameter catalog (card F-021, PRD §3.11): validation of the definitions a project
/// saves, and the resolution of a trigger's supplied values against them.
/// <para>
/// <b>Parameters are not secrets.</b> A parameter value is configuration the person triggering
/// types on purpose - it is visible on the run page and carried in the claim payload, never
/// collected, never redacted; a sensitive value belongs in the Secrets store, and the trigger form
/// says so. The resolved values are a run snapshot: a later definition edit never moves a queued or
/// running run, exactly like the branch and the frozen spec.
/// </para>
/// </summary>
public static class ProjectParameters
{
    /// <summary>The serialization of the drafts the editor posts (camelCase on the wire).</summary>
    public static readonly JsonSerializerOptions DraftsOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses the drafts and returns the definitions JSON to store, or <see langword="null"/> with a
    /// one-line reason. An empty list is a valid answer - it clears the project's parameters.
    /// </summary>
    public static string? BuildDefinitionsJson(ProjectParametersInput input, out string? error)
    {
        error = null;
        ProjectParameterDraft[]? drafts;
        try
        {
            drafts = string.IsNullOrWhiteSpace(input.ParametersJson)
                ? []
                : JsonSerializer.Deserialize<ProjectParameterDraft[]>(input.ParametersJson, DraftsOptions);
        }
        catch (JsonException)
        {
            error = "the parameter list is not valid JSON for the parameters editor.";
            return null;
        }

        var definitions = Validate(drafts ?? [], out error);
        if (error is not null)
        {
            return null;
        }

        return definitions.Length == 0
            ? null
            : JsonSerializer.Serialize(definitions, ProjectParameterJsonContext.Default.ProjectParameterArray);
    }

    /// <summary>
    /// Validates a draft list against the shared limits (card F-021). Every rule here is re-checked
    /// by nobody else - this is the single authority for what a definition may look like, so the
    /// trigger form can render it and the value resolver can enforce it without re-deriving.
    /// </summary>
    public static ProjectParameter[] Validate(ProjectParameterDraft[] drafts, out string? error)
    {
        error = null;
        if (drafts.Length > ProjectParameterLimits.MaxParametersPerProject)
        {
            error = $"A project declares at most {ProjectParameterLimits.MaxParametersPerProject} parameters.";
            return [];
        }

        var definitions = new ProjectParameter[drafts.Length];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < drafts.Length; i++)
        {
            var draft = drafts[i];
            var path = $"parameters[{i + 1}]";

            var name = draft.Name?.Trim() ?? string.Empty;
            if (!PipelineSpecSchema.IsReferenceNameShape(name))
            {
                error = $"{path}.name must be an environment-variable-shaped name (letters, digits and "
                    + "underscores, starting with a letter or an underscore), for example NUGET_TOKEN.";
                return [];
            }

            if (name.Length > ProjectParameterLimits.MaxNameLength)
            {
                error = $"{path}.name must be at most {ProjectParameterLimits.MaxNameLength} characters.";
                return [];
            }

            if (!names.Add(name))
            {
                error = $"{path}.name '{name}' is declared more than once; a parameter name is unique.";
                return [];
            }

            var type = draft.Type?.Trim() ?? string.Empty;
            if (!ProjectParameterTypes.IsValid(type))
            {
                error = $"{path}.type must be one of {string.Join(", ", ProjectParameterTypes.All)}.";
                return [];
            }

            var description = draft.Description?.Trim() ?? string.Empty;
            if (description.Length > ProjectParameterLimits.MaxDescriptionLength)
            {
                error = $"{path}.description must be at most {ProjectParameterLimits.MaxDescriptionLength} characters.";
                return [];
            }

            string? defaultValue = draft.DefaultValue?.Trim() is { Length: > 0 } value ? value : null;
            string[]? choices = null;
            if (type == ProjectParameterTypes.Choice)
            {
                choices = ParseChoicesText(draft.ChoicesText, path, out error);
                if (error is not null)
                {
                    return [];
                }

                if (defaultValue is { } chosen && !choices!.Contains(chosen, StringComparer.Ordinal))
                {
                    error = $"{path}.default must be one of the declared choices (got '{chosen}').";
                    return [];
                }
            }
            else if (defaultValue is { } nonChoiceDefault)
            {
                var bound = type == ProjectParameterTypes.Text
                    ? ProjectParameterLimits.MaxTextValueLength
                    : ProjectParameterLimits.MaxStringValueLength;
                if (nonChoiceDefault.Length > bound)
                {
                    error = $"{path}.default must be at most {bound} characters for a {type} parameter.";
                    return [];
                }

                if (type == ProjectParameterTypes.Boolean
                    && nonChoiceDefault is not ("true" or "false"))
                {
                    error = $"{path}.default must be true or false for a boolean parameter.";
                    return [];
                }
            }

            definitions[i] = new ProjectParameter(name, type, defaultValue, choices, description.Length == 0 ? null : description);
        }

        return definitions;
    }

    /// <summary>
    /// Resolves a trigger's supplied values against the definitions (card F-021): every defined
    /// parameter gets exactly one value - the supplied one, or the definition's default, or the
    /// type's neutral default - and a supplied value that names nothing, breaks its type or breaks a
    /// bound is refused rather than passed through. The result is the run's snapshot.
    /// </summary>
    /// <returns>The values, or <see langword="null"/> with a stable
    /// <see cref="RunErrorCodes"/>-shaped reason when refused.</returns>
    public static IReadOnlyDictionary<string, string>? ResolveValues(
        ProjectParameter[]? definitions,
        IReadOnlyDictionary<string, string>? provided,
        out string? error)
    {
        error = null;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var byName = (definitions ?? []).ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);

        if (provided is not null)
        {
            foreach (var (name, raw) in provided)
            {
                if (!byName.TryGetValue(name, out var definition))
                {
                    error = RunErrorCodes.ParameterUnknown;
                    return null;
                }

                if (TryNormalizeForTrigger(definition, raw, out var normalized, out error) is false)
                {
                    return null;
                }

                resolved[name] = normalized;
            }
        }

        foreach (var definition in definitions ?? [])
        {
            if (resolved.ContainsKey(definition.Name))
            {
                continue;
            }

            resolved[definition.Name] = (definition.Type, definition.DefaultValue) switch
            {
                (_, { } declared) => declared,
                (ProjectParameterTypes.Boolean, _) => "false",
                // A saved choice definition always declares at least one entry (Validate refuses an
                // empty list), so the empty fallback only covers a row corrupted after the fact: it
                // resolves to the empty string instead of throwing inside a trigger.
                (ProjectParameterTypes.Choice, _) => definition.Choices is { Length: > 0 } choices
                    ? choices[0]
                    : string.Empty,
                _ => string.Empty,
            };
        }

        return resolved;
    }

    /// <summary>Normalizes one supplied value to what the definition's type accepts.</summary>
    private static bool TryNormalizeForTrigger(
        ProjectParameter definition,
        string raw,
        out string normalized,
        out string? error)
    {
        normalized = string.Empty;
        error = null;

        switch (definition.Type)
        {
            case ProjectParameterTypes.Boolean:
                if (!bool.TryParse(raw.Trim(), out var boolean))
                {
                    error = RunErrorCodes.ParameterInvalid;
                    return false;
                }

                normalized = boolean ? "true" : "false";
                return true;

            case ProjectParameterTypes.Choice:
                if (definition.Choices is null || !definition.Choices.Contains(raw, StringComparer.Ordinal))
                {
                    error = RunErrorCodes.ParameterInvalid;
                    return false;
                }

                normalized = raw;
                return true;

            case ProjectParameterTypes.Text:
                if (raw.Length > ProjectParameterLimits.MaxTextValueLength)
                {
                    error = RunErrorCodes.ParameterInvalid;
                    return false;
                }

                normalized = raw;
                return true;

            default:
                if (raw.Length > ProjectParameterLimits.MaxStringValueLength)
                {
                    error = RunErrorCodes.ParameterInvalid;
                    return false;
                }

                normalized = raw;
                return true;
        }
    }

    private static string[] ParseChoicesText(string? choicesText, string path, out string? error)
    {
        error = null;
        var choices = (choicesText ?? string.Empty)
            .Split(',')
            .Select(choice => choice.Trim())
            .Where(choice => choice.Length > 0)
            .ToArray();

        if (choices.Length == 0)
        {
            error = $"{path}.choices must hold at least one value (comma-separated).";
            return [];
        }

        if (choices.Length > ProjectParameterLimits.MaxChoices)
        {
            error = $"{path}.choices must hold at most {ProjectParameterLimits.MaxChoices} values.";
            return [];
        }

        if (choices.Any(choice => choice.Length > ProjectParameterLimits.MaxStringValueLength))
        {
            error = $"{path}.choices entries must be at most {ProjectParameterLimits.MaxStringValueLength} characters.";
            return [];
        }

        return choices;
    }
}
