using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The frozen shape of a validated <c>pipeline.lua</c> definition (card F-014, spec schema v1 of
/// <c>docs/lua-runtime.md</c> §6.1).
/// <para>
/// <b>Two phases, one record.</b> The Worker runs the script once (the definition phase), validates
/// the table it built and serialises it into this record; the Server stores that JSON verbatim as
/// <c>BuildRun.PipelineSpecJson</c>, renders the step names from it and hands it back on the run page.
/// Execution reads the frozen record, never the script (§6.1: "the host validates it, serialises it,
/// and executes the steps in C#"), so a later commit can never change what a run did.
/// </para>
/// <para>
/// <b>Provider- and engine-neutral.</b> This assembly is shared by the Server and the plain console
/// Worker, so the record carries no Lua, no EF Core and no ASP.NET type - only what both sides must
/// agree on: the schema version, the pipeline name, the step list and the fields F-015/F-017 will
/// start honouring (<c>env</c>, <c>continueOnError</c>, <c>triggers</c>).
/// </para>
/// </summary>
public sealed record PipelineSpec(
    int Version,
    string Name,
    PipelineStep[] Steps,
    SortedDictionary<string, string>? Env = null,
    PipelineTriggers? Triggers = null);

/// <summary>
/// One step of a frozen spec. Exactly one of <see cref="Run"/> / <see cref="Shell"/> is set, and its
/// first element is the program (pure argv, <b>never</b> a shell command line: PRD §8 constraint 6 -
/// no quoting interpretation exists anywhere in the pipeline).
/// <para>
/// <see cref="Env"/>, <see cref="ContinueOnError"/> and <see cref="When"/> are frozen by the definition
/// phase and honoured by the C# executor (F-015): the step list is data, so a script cannot skip the
/// sequencing, the conditions or the environment composition.
/// </para>
/// </summary>
public sealed record PipelineStep(
    string Name,
    string[]? Run = null,
    string[]? Shell = null,
    SortedDictionary<string, string>? Env = null,
    bool ContinueOnError = false,
    PipelineCondition? When = null,
    string[]? Artifacts = null)
{
    /// <summary>Whether the step declares an execution-time condition (<c>when = { … }</c>).</summary>
    [JsonIgnore]
    public bool HasCondition => When is { } condition && !condition.IsEmpty;

    /// <summary>Whether the step was declared with <c>shell</c> rather than <c>run</c>.</summary>
    [JsonIgnore]
    public bool IsShell => Shell is not null;

    /// <summary>The argv actually executed: <c>run</c>, or <c>shell</c>'s program + argv.</summary>
    [JsonIgnore]
    public string[] Argv => Run ?? Shell ?? [];

    /// <summary>
    /// The paths this step publishes as run artifacts (<c>artifacts = { "bin/Release" }</c>), the
    /// declarative spelling of PRD §3.7's <c>artifact("bin/Release")</c> (card F-016). The C# executor
    /// publishes them after the step's process exits 0, so a failing step publishes nothing; each path
    /// is resolved inside <c>work/{run-id}/repository</c> by the shared
    /// <see cref="Zenkins.Shared.Artifacts.ArtifactPathRules"/> and re-validated by the Server.
    /// </summary>
    [JsonIgnore]
    public string[] ArtifactPaths => Artifacts ?? [];
}

/// <summary>The optional <c>triggers</c> table. Informational for a manual run; F-017 reads it.</summary>
public sealed record PipelineTriggers(string[]? Branches = null);

/// <summary>
/// A step's execution-time condition (card F-015): <c>when = { branch = "main", platform = "linux",
/// env = { K = "v" } }</c>.
/// <para>
/// <b>Declared, not executable.</b> A definition-time condition is plain Lua (<c>if is_linux() then
/// steps[#steps+1] = … end</c>); this field is the other half - a condition the <b>C#</b> executor
/// evaluates against the run's frozen facts (the branch it checked out, the worker's OS, the step's
/// effective environment) immediately before the step would start. A script cannot make it do
/// anything a step could not, and a skipped step is recorded rather than omitted, so the run page
/// never shows a step that silently vanished.
/// </para>
/// <para>
/// <b>All declared keys must match.</b> An absent key is not a condition; an unknown key is a
/// definition error (<see cref="PipelineSpecSchema"/> refuses it, and this type refuses it on the
/// wire too), because a typo like <c>brnach = "main"</c> would otherwise silently never match.
/// </para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PipelineCondition(
    string? Branch = null,
    string? Platform = null,
    SortedDictionary<string, string>? Env = null)
{
    /// <summary>The platforms <c>when.platform</c> may name (the same names <c>is_*()</c> use).</summary>
    public static IReadOnlyList<string> Platforms { get; } = ["linux", "macos", "windows"];

    /// <summary>Whether nothing is declared (the step always runs).</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Branch)
        && string.IsNullOrEmpty(Platform)
        && (Env is null || Env.Count == 0);
}

/// <summary>
/// The schema constants and the JSON contract of <see cref="PipelineSpec"/> (card F-014). Both sides
/// use this one serializer configuration, so the Worker's frozen JSON and the Server's read-back can
/// never disagree on casing or on how an absent optional field is spelled.
/// <para>
/// <b>The Server re-checks the envelope, never the script.</b> The Worker owns the strict validation
/// with field paths (it is the only side holding the Lua table); the Server's
/// <see cref="PipelineSpecJson.TryRead"/> is the defensive gate the issue asks for: it bounds the
/// stored size, refuses an unknown schema version and refuses a step list that is empty or too long,
/// so a buggy or hostile worker cannot store something the page would then have to trust.
/// </para>
/// </summary>
public static class PipelineSpecSchema
{
    /// <summary>The only schema version v1 knows: an unknown version is refused, never guessed.</summary>
    public const int Version = 1;

    /// <summary>
    /// An upper bound on the entries of one <c>when.env</c> map (card F-015). A condition is a hand-written
    /// comparison, not a data channel; the bound keeps the frozen JSON bounded.
    /// </summary>
    public const int MaxConditionEnvEntries = 32;

    /// <summary>An upper bound on the steps of one pipeline (§6.1's 1..200).</summary>
    public const int MaxSteps = 200;

    /// <summary>An upper bound on the argv length of one step.</summary>
    public const int MaxArguments = 64;

    /// <summary>An upper bound on a pipeline or step name (mirrors the 200-character name rule).</summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// An upper bound on the artifacts one step may publish (card F-016). An artifact is a declared
    /// build product, not a filesystem listing: a step publishing dozens of them is a pipeline mistake.
    /// </summary>
    public const int MaxArtifactsPerStep = 16;

    /// <summary>
    /// The size cap on the frozen JSON. A spec is a hand-written step list, so 256 KiB is generous;
    /// the cap exists because the column is <c>text</c> and the page renders it.
    /// </summary>
    public const int MaxJsonBytes = 256 * 1024;
}

/// <summary>
/// The JSON contract of <see cref="PipelineSpec"/>: camelCase, nulls omitted, source-generated so the
/// Worker's AOT/trim-friendly build needs no reflection-based serializer.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PipelineSpec))]
[JsonSerializable(typeof(PipelineStep))]
[JsonSerializable(typeof(PipelineTriggers))]
[JsonSerializable(typeof(PipelineCondition))]
public sealed partial class PipelineJsonContext : JsonSerializerContext;

/// <summary>Serialisation and the Server-side envelope check of the frozen spec (card F-014).</summary>
public static class PipelineSpecJson
{
    /// <summary>Serialises the frozen record. The result is what <c>PipelineSpecJson</c> stores.</summary>
    public static string Serialize(PipelineSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return JsonSerializer.Serialize(spec, PipelineJsonContext.Default.PipelineSpec);
    }

    /// <summary>
    /// Reads and defensively validates a frozen spec: parseable JSON, schema version 1, a bounded
    /// size, 1..200 steps with non-empty unique names, and exactly one of <c>run</c>/<c>shell</c> per
    /// step. Returns <see langword="false"/> with a one-line reason when a rule fails.
    /// </summary>
    public static bool TryRead(string? json, out PipelineSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "the pipeline spec is empty.";
            return false;
        }

        if (json.Length > PipelineSpecSchema.MaxJsonBytes)
        {
            error = $"the pipeline spec exceeds {PipelineSpecSchema.MaxJsonBytes} bytes.";
            return false;
        }

        PipelineSpec? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, PipelineJsonContext.Default.PipelineSpec);
        }
        catch (JsonException)
        {
            error = "the pipeline spec is not valid JSON for schema version 1.";
            return false;
        }

        if (parsed is null)
        {
            error = "the pipeline spec is null.";
            return false;
        }

        if (parsed.Version != PipelineSpecSchema.Version)
        {
            error = $"pipeline.version must be {PipelineSpecSchema.Version}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Name))
        {
            error = "pipeline.name must be a non-empty string.";
            return false;
        }

        if (parsed.Steps is null)
        {
            error = "pipeline.steps is required.";
            return false;
        }

        if (parsed.Steps.Length is 0 or > PipelineSpecSchema.MaxSteps)
        {
            error = $"pipeline.steps must hold 1..{PipelineSpecSchema.MaxSteps} steps.";
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < parsed.Steps.Length; i++)
        {
            var step = parsed.Steps[i];
            var path = $"pipeline.steps[{i + 1}]";
            if (string.IsNullOrWhiteSpace(step.Name))
            {
                error = $"{path}.name must be a non-empty string.";
                return false;
            }

            if (!names.Add(step.Name))
            {
                error = $"{path}.name '{step.Name}' is used by more than one step.";
                return false;
            }

            if ((step.Run is null) == (step.Shell is null))
            {
                error = $"{path} must declare exactly one of run or shell.";
                return false;
            }

            if (step.Argv.Length == 0)
            {
                error = $"{path} has an empty argv.";
                return false;
            }

            // F-015: a condition is declared data the executor evaluates; an empty one is a mistake, and
            // so is a key or platform the executor does not know (a typo would never match anything).
            if (step.When is { } condition
                && !TryReadCondition(condition, $"{path}.when", out error))
            {
                return false;
            }

            // F-016: the declared artifact paths are re-validated against the shared path rules by the
            // upload endpoint; here the envelope only refuses a shape the executor could not even try
            // (an empty entry, or more entries than a step may publish).
            if (!TryReadArtifactPaths(step.Artifacts, $"{path}.artifacts", out error))
            {
                return false;
            }
        }

        spec = parsed;
        return true;
    }

    /// <summary>
    /// The envelope check of one step's artifact list (card F-016): a bounded list of non-empty strings.
    /// The <b>path rules</b> are deliberately not duplicated here - they live in
    /// <see cref="Zenkins.Shared.Artifacts.ArtifactPathRules"/> and are applied by both the Worker (before
    /// it sends anything) and this Server's upload endpoint (on every received relative path), which is
    /// what makes the two validations independent implementations of one rule set rather than a copy.
    /// </summary>
    internal static bool TryReadArtifactPaths(string[]? artifacts, string path, out string? error)
    {
        error = null;
        if (artifacts is null)
        {
            return true;
        }

        if (artifacts.Length is 0 or > PipelineSpecSchema.MaxArtifactsPerStep)
        {
            error = $"{path} must hold 1..{PipelineSpecSchema.MaxArtifactsPerStep} paths.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < artifacts.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(artifacts[i]))
            {
                error = $"{path}[{i + 1}] must be a non-empty path.";
                return false;
            }

            if (!seen.Add(artifacts[i]))
            {
                // Ambiguous artifact names are a definition mistake, not something to store and discover at upload.
                error = $"{path}[{i + 1}] '{artifacts[i]}' is declared twice.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The envelope check of one step condition (card F-015). <see cref="PipelineCondition"/> already
    /// refuses an unknown key on the wire; this refuses the shapes a known key may not take, so the
    /// Server never stores a condition the executor would have to guess at.
    /// </summary>
    internal static bool TryReadCondition(PipelineCondition? condition, string path, out string? error)
    {
        error = null;
        if (condition is null)
        {
            return true;
        }

        if (condition.IsEmpty)
        {
            error = $"{path} must declare at least one of branch, platform or env.";
            return false;
        }

        if (condition.Branch is { Length: 0 })
        {
            error = $"{path}.branch must be a non-empty string.";
            return false;
        }

        if (condition.Branch is { } branch && branch.Length > PipelineSpecSchema.MaxNameLength)
        {
            error = $"{path}.branch must be at most {PipelineSpecSchema.MaxNameLength} characters.";
            return false;
        }

        if (condition.Platform is { } platform
            && !PipelineCondition.Platforms.Contains(platform, StringComparer.Ordinal))
        {
            error = $"{path}.platform must be one of {string.Join(", ", PipelineCondition.Platforms)} "
                + $"(got '{platform}').";
            return false;
        }

        if (condition.Env is { } env)
        {
            if (env.Count > PipelineSpecSchema.MaxConditionEnvEntries)
            {
                error = $"{path}.env must hold at most {PipelineSpecSchema.MaxConditionEnvEntries} entries.";
                return false;
            }

            foreach (var (name, value) in env)
            {
                if (name.Length == 0)
                {
                    error = $"{path}.env has an empty variable name.";
                    return false;
                }

                if (value is null)
                {
                    error = $"{path}.env.{name} must be a string.";
                    return false;
                }
            }
        }

        return true;
    }
}
