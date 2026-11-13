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
/// <see cref="Env"/> and <see cref="ContinueOnError"/> are accepted and frozen now and honoured by
/// F-015; F-014 executes the default behaviour only (inherited environment, stop at the first
/// failure), so a spec written today does not need a v2 tomorrow.
/// </para>
/// </summary>
public sealed record PipelineStep(
    string Name,
    string[]? Run = null,
    string[]? Shell = null,
    SortedDictionary<string, string>? Env = null,
    bool ContinueOnError = false)
{
    /// <summary>Whether the step was declared with <c>shell</c> rather than <c>run</c>.</summary>
    [JsonIgnore]
    public bool IsShell => Shell is not null;

    /// <summary>The argv actually executed: <c>run</c>, or <c>shell</c>'s program + argv.</summary>
    [JsonIgnore]
    public string[] Argv => Run ?? Shell ?? [];
}

/// <summary>The optional <c>triggers</c> table. Informational for a manual run; F-017 reads it.</summary>
public sealed record PipelineTriggers(string[]? Branches = null);

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

    /// <summary>An upper bound on the steps of one pipeline (§6.1's 1..200).</summary>
    public const int MaxSteps = 200;

    /// <summary>An upper bound on the argv length of one step.</summary>
    public const int MaxArguments = 64;

    /// <summary>An upper bound on a pipeline or step name (mirrors the 200-character name rule).</summary>
    public const int MaxNameLength = 200;

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
        }

        spec = parsed;
        return true;
    }
}
