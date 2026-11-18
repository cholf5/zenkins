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

            // F-034: a parameter is a layer of the step's environment, and the ZENKINS_* namespace is the
            // server's own record of the run. Refusing the name at save time is what keeps the metadata
            // unspoofable now that it is the *lowest* layer (PRD §3.11.3): before the reorder the value
            // merely lost by sort order, which was correct by accident and silent either way.
            if (ServerEnvironmentNames.IsReserved(name))
            {
                error = $"{path}.name '{name}' is reserved: "
                    + $"{string.Join(", ", ServerEnvironmentNames.ReservedPrefixes)} "
                    + "is the namespace of Zenkins' own run metadata (ZENKINS_RUN_ID, ZENKINS_BUILD_NUMBER, …). "
                    + "Choose another name.";
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
            else if (type == ProjectParameterTypes.Password && defaultValue is not null)
            {
                // Card F-041: a password is an input to one run, not project state. A default would put the
                // plaintext on the project row (in the definition JSON, unencrypted) - exactly the thing this
                // type exists to avoid - so the definition refuses one outright instead of storing it quietly.
                error = $"{path}.default must be empty for a password parameter: a password is typed per run, "
                    + "never stored on the project. A trigger that supplies none (a schedule, a poll, an API "
                    + "call) resolves it to the empty string.";
                return [];
            }
            else if (type == ProjectParameterTypes.File && defaultValue is not null)
            {
                // A file parameter is an upload, not a value typed once and reused: a "default" would be a file
                // stored on the project row, which is not what an input to one build is.
                error = $"{path}.default must be empty for a file parameter: the file is uploaded with each "
                    + "trigger. A trigger that uploads none (a schedule, a poll, an API call) passes an empty "
                    + "value.";
                return [];
            }
            else if (type == ProjectParameterTypes.Run && defaultValue is not null)
            {
                // A run selection defaults to the project's most recent successful run, which is a fact of the
                // moment rather than of the definition: a number written here would rot the first time that
                // build is deleted, and a default that names #7 means nothing in a project with two runs.
                error = $"{path}.default must be empty for a run parameter: it resolves at trigger time to "
                    + "the project's most recent successful run (the trigger form prefills the same one).";
                return [];
            }
            else if (defaultValue is { } nonChoiceDefault)
            {
                var bound = type switch
                {
                    ProjectParameterTypes.Text => ProjectParameterLimits.MaxTextValueLength,
                    ProjectParameterTypes.GitRef => ProjectParameterLimits.MaxGitRefLength,
                    _ => ProjectParameterLimits.MaxStringValueLength,
                };
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

                if (type == ProjectParameterTypes.GitRef
                    && !ProjectParameterRules.IsValidGitRef(nonChoiceDefault, out _))
                {
                    error = $"{path}.default is not a usable git ref: no whitespace, no leading '-', at most "
                        + $"{ProjectParameterLimits.MaxGitRefLength} characters.";
                    return [];
                }
            }

            definitions[i] = new ProjectParameter(name, type, defaultValue, choices, description.Length == 0 ? null : description);
        }

        // Card F-041: a `run` parameter injects two extra variables, `<NAME>_RUN_ID` and `<NAME>_COMMIT`. A
        // parameter that happens to be called `<NAME>_RUN_ID` would then be silently overwritten by the
        // convenience variable - the same "nothing silently wins" rule F-034's reserved namespace follows -
        // so the collision is refused here, where both names are known, rather than at some later trigger.
        foreach (var definition in definitions)
        {
            if (!ProjectParameterTypes.IsRunSelection(definition.Type))
            {
                continue;
            }

            foreach (var suffix in new[] { "_RUN_ID", "_COMMIT" })
            {
                if (names.Contains(definition.Name + suffix))
                {
                    error = $"'{definition.Name + suffix}' is generated by the run parameter "
                        + $"'{definition.Name}' (it injects the selected run's id and commit), so no parameter "
                        + "may be declared under that name.";
                    return [];
                }
            }
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

            case ProjectParameterTypes.Password:
                // Card F-041: never normalised. A password with a leading or trailing space, or with a newline
                // in it, is a password - trimming it would corrupt the value the step has to reproduce.
                if (raw.Length > ProjectParameterLimits.MaxPasswordValueLength)
                {
                    error = RunErrorCodes.ParameterInvalid;
                    return false;
                }

                normalized = raw;
                return true;

            case ProjectParameterTypes.Run:
                // The shape only - whether the run exists is the Server's trigger path's question (it needs
                // the database, which this static rule does not have).
                if (!ProjectParameterRules.IsRunNumber(raw.Trim(), out _))
                {
                    error = RunErrorCodes.ParameterInvalid;
                    return false;
                }

                normalized = raw.Trim();
                return true;

            case ProjectParameterTypes.File:
                // The value of a file parameter is produced by the trigger (an upload), never typed: a supplied
                // string would be a path the server did not write, so it is a type mismatch rather than a value.
                if (raw.Length > 0)
                {
                    error = RunErrorCodes.ParameterInvalid;
                    return false;
                }

                normalized = string.Empty;
                return true;

            case ProjectParameterTypes.GitRef:
                if (!ProjectParameterRules.IsValidGitRef(raw.Trim(), out var gitRef))
                {
                    error = RunErrorCodes.ParameterInvalid;
                    return false;
                }

                normalized = gitRef;
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

        // Card B-006: a repeated entry is refused, the same discipline the artifact list follows
        // (`PipelineSpec.TryReadArtifactPaths`) - a duplicate is an authoring mistake, not something to
        // store and discover from the rendered form. Collapsing it silently would also be wrong: the
        // declared order is the order the trigger form renders, and the first entry is the value a
        // trigger without a supplied one resolves to, so dropping an entry can change what a run gets.
        // The comparison is ordinal, because a choice value is compared ordinally when the trigger's
        // value is matched (`TryNormalizeForTrigger`): 'fast' and 'Fast' are two distinct legal values,
        // and refusing them would be a rule the resolver disagrees with.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < choices.Length; i++)
        {
            if (!seen.Add(choices[i]))
            {
                error = $"{path}.choices[{i + 1}] '{choices[i]}' is declared twice.";
                return [];
            }
        }

        return choices;
    }
}
