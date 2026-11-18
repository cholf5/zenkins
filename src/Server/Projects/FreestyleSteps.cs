using System.Text.Json;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Workers;

namespace Zenkins.Server.Projects;

/// <summary>
/// One Freestyle step as the steps editor submits it (card F-020): a serialized draft, not a spec. The
/// Server - never the browser - builds the frozen <see cref="PipelineSpec"/> from these drafts, so the
/// schema version, the step names, the secret-reference capture and every bound are re-derived here and
/// the stored JSON is exactly what <see cref="PipelineSpecJson.TryRead"/> accepted. The text-shaped
/// fields mirror the editor's editors: <paramref name="EnvLines"/> is one <c>NAME=value</c> per line,
/// <paramref name="ArtifactsText"/> a comma-separated path list - both parsed server-side, so the
/// parsing is unit-tested rather than trusted to the browser.
/// </summary>
/// <param name="Name">The step's display name; must be unique and non-empty (the envelope re-checks).</param>
/// <param name="Script">The multi-line script text; the only command form a Freestyle step has (the
/// editor does not offer argv steps, and the builder never reads such a field at all).</param>
/// <param name="EnvLines">The step's declared environment, one <c>NAME=value</c> per line; blank lines
/// and <c>#</c> comments are skipped, a duplicate name is refused.</param>
/// <param name="ContinueOnError">Whether the run continues when this step fails (F-015).</param>
/// <param name="ArtifactsText">The comma-separated paths this step publishes after exiting 0 (F-016),
/// resolved inside the run's workspace for a Freestyle run.</param>
/// <param name="TimeoutSeconds">The step's own limit in seconds as the editor's text field carries it
/// (card F-032); blank means "no limit of its own". Parsed and bounded server-side, never trusted to the
/// browser - the same discipline <paramref name="EnvLines"/> and <paramref name="ArtifactsText"/> get.</param>
/// <param name="Retry">The step's attempt count as the editor's text field carries it (card F-032); blank
/// means one attempt.</param>
public sealed record FreestyleStepDraft(
    string? Name,
    string? Script,
    string? EnvLines,
    bool ContinueOnError,
    string? ArtifactsText,
    string? TimeoutSeconds = null,
    string? Retry = null,
    string? RunsOn = null);

/// <summary>The steps editor's form payload: the drafts, serialized as JSON by the page.</summary>
public sealed record FreestyleStepsInput(string? StepsJson);

/// <summary>
/// Builds and validates the Freestyle spec (card F-020). One routine, used by the save endpoint and
/// re-run by nothing else: the drafts become <see cref="PipelineStep"/>s, the secret names their script
/// texts and env values reference are captured here (the Server-side counterpart of the Worker's
/// definition-phase capture - the same <see cref="StepReference"/> scanner, so "what the Server
/// recorded" and "what the Worker substitutes" cannot drift), and the envelope check has the final word.
/// <para>
/// <b>Errors are the envelope's, verbatim.</b> A refused draft list surfaces the TryRead reason
/// (<c>pipeline.steps[2].name must be a non-empty string.</c>) rather than a parallel error taxonomy:
/// the same message the Worker would produce for a bad definition, and the same one the run log shows.
/// </para>
/// </summary>
public static class FreestyleSteps
{
    /// <summary>The serialization of the drafts the editor posts (camelCase on the wire).</summary>
    public static readonly JsonSerializerOptions DraftsOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the frozen spec JSON from the drafts. Returns <see langword="null"/> with a one-line
    /// reason when anything is refused; nothing is written by this class.
    /// </summary>
    public static string? BuildSpecJson(string projectName, FreestyleStepsInput input, out string? error)
    {
        error = null;
        var drafts = TryDeserialize(input.StepsJson, out error);
        if (drafts is null)
        {
            return null;
        }

        if (drafts.Length == 0)
        {
            error = "pipeline.steps must declare at least one step.";
            return null;
        }

        var steps = new PipelineStep[drafts.Length];
        var secretNames = new List<string>();
        for (var i = 0; i < drafts.Length; i++)
        {
            var draft = drafts[i];
            var path = $"pipeline.steps[{i + 1}]";

            // The draft's text fields become the declared shapes; the individual field rules
            // (non-empty name, bounded script, artifact paths) are the envelope's, applied below by
            // TryRead and the upload path.
            if (ParseEnvLines(draft.EnvLines, path, out var env, out error) is false)
            {
                return null;
            }

            var artifacts = ParseArtifactsText(draft.ArtifactsText);
            if (!ParseStepLimit(
                    draft.TimeoutSeconds,
                    $"{path}.timeoutSeconds",
                    PipelineSpecSchema.MinStepTimeoutSeconds,
                    PipelineSpecSchema.MaxStepTimeoutSeconds,
                    out var timeoutSeconds,
                    out error)
                || !ParseStepLimit(
                    draft.Retry,
                    $"{path}.retry",
                    PipelineSpecSchema.MinStepRetry,
                    PipelineSpecSchema.MaxStepRetry,
                    out var retry,
                    out error))
            {
                return null;
            }

            // Card F-033: one parser for the requirement, whatever the editor's field holds - the same
            // WorkerRequirement the Lua reader and the claim loop use, never a second splitter here. A
            // blank field means "no requirement", which is not the empty expression.
            var runsOn = draft.RunsOn?.Trim();
            WorkerRequirement? requirement = null;
            if (!string.IsNullOrWhiteSpace(runsOn))
            {
                if (!WorkerRequirement.TryParse(runsOn, out var parsed, out var parseError))
                {
                    error = $"{path}.runsOn {parseError}";
                    return null;
                }

                if (parsed.IsContradiction)
                {
                    error = $"{path}.runsOn can never be satisfied: '{runsOn}'.";
                    return null;
                }

                requirement = parsed;
            }

            steps[i] = new PipelineStep(
                draft.Name?.Trim() ?? string.Empty,
                Script: draft.Script ?? string.Empty,
                Env: env,
                ContinueOnError: draft.ContinueOnError,
                Artifacts: artifacts,
                TimeoutSeconds: timeoutSeconds,
                Retry: retry,
                RunsOn: string.IsNullOrWhiteSpace(runsOn) ? null : runsOn,
                Requirement: requirement?.StorageForm);

            // The Server-side capture (D-006's semantics, extended by F-040): the exact reference form and
            // never a value - scanned from the same fields the Worker will substitute (the script text and the
            // step's env values). The form matters: a step that reads ${secretFile:KEY} or a pair's .password
            // is a use of that secret, and recording only the bare name would make the Secrets page's "which
            // pipelines use this" either lie or lose the half that was used.
            secretNames.AddRange(StepReference.ReferencesIn(draft.Script));
            if (env is not null)
            {
                foreach (var (_, value) in env)
                {
                    secretNames.AddRange(StepReference.ReferencesIn(value));
                }
            }
        }

        // Card F-033: the same cross-step rule the Lua reader applies, at the one place a Freestyle definition
        // is built - a run executes on one Worker, so two steps whose requirements can never hold together are
        // refused here, by step path, instead of becoming a run that waits for a machine that cannot exist.
        var requirementError = PipelineSpecSchema.ValidateWorkerRequirements(steps);
        if (requirementError is not null)
        {
            error = requirementError;
            return null;
        }

        var spec = new PipelineSpec(
            PipelineSpecSchema.Version,
            projectName,
            steps,
            SecretReferences: PipelineSpecSchema.NormalizeSecretReferences([.. secretNames]));

        if (!PipelineSpecJson.TryRead(PipelineSpecJson.Serialize(spec), out var validated, out var envelopeError)
            || validated is null)
        {
            error = envelopeError;
            return null;
        }

        return PipelineSpecJson.Serialize(validated);
    }

    /// <summary>
    /// Parses the editor's <c>NAME=value</c> lines into the declared environment (card F-020). Blank
    /// lines and <c>#</c> comments are skipped; both sides of the separator are trimmed (the editor's
    /// line format, like a .env file, is not whitespace-significant); a duplicate name is refused
    /// rather than silently overwritten - a duplicated variable is a config mistake the author wants
    /// to know about, and the frozen spec must mean exactly what the editor showed.
    /// </summary>
    private static bool ParseEnvLines(
        string? envLines,
        string path,
        out SortedDictionary<string, string>? env,
        out string? error)
    {
        env = null;
        error = null;
        if (string.IsNullOrWhiteSpace(envLines))
        {
            return true;
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = envLines.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                error = $"{path}.env line {i + 1} must be NAME=value (an '=' with a name before it).";
                return false;
            }

            var name = line[..separator].Trim();
            if (name.Length == 0)
            {
                error = $"{path}.env line {i + 1} needs a variable name before '='.";
                return false;
            }

            if (parsed.ContainsKey(name))
            {
                error = $"{path}.env declares '{name}' more than once.";
                return false;
            }

            parsed[name] = line[(separator + 1)..].Trim();
        }

        if (parsed.Count == 0)
        {
            return true;
        }

        env = new SortedDictionary<string, string>(parsed, StringComparer.Ordinal);
        return true;
    }

    /// <summary>
    /// Parses the editor's comma-separated artifact paths (card F-020). Empty entries are dropped -
    /// they are what a blank field or a trailing comma yields, not a declared path; the envelope's
    /// duplicate and count checks still apply to what remains.
    /// </summary>
    private static string[]? ParseArtifactsText(string? artifactsText)
    {
        if (string.IsNullOrWhiteSpace(artifactsText))
        {
            return null;
        }

        var paths = artifactsText
            .Split(',')
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .ToArray();
        return paths.Length == 0 ? null : paths;
    }

    /// <summary>
    /// Parses one of the editor's two numeric fields (card F-032). A blank field is "not declared"; anything
    /// else must be a whole number inside the bounds, refused with the same field path and phrasing the
    /// envelope uses, so the editor's error and a worker's definition error read the same way.
    /// </summary>
    private static bool ParseStepLimit(
        string? text,
        string path,
        int min,
        int max,
        out int? value,
        out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < min
            || parsed > max)
        {
            error = $"{path} must be a whole number between {min} and {max} (got '{text.Trim()}').";
            return false;
        }

        value = parsed;
        return true;
    }

    private static FreestyleStepDraft[]? TryDeserialize(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "pipeline.steps must declare at least one step.";
            return null;
        }

        FreestyleStepDraft[]? drafts;
        try
        {
            drafts = JsonSerializer.Deserialize<FreestyleStepDraft[]>(json, DraftsOptions);
        }
        catch (JsonException)
        {
            error = "the step list is not valid JSON for the steps editor.";
            return null;
        }

        return drafts ?? [];
    }
}
