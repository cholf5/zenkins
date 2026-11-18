using System.Text.Json;
using System.Text.Json.Serialization;

using Zenkins.Shared.Workers;

namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The frozen shape of a validated pipeline definition (card F-014, spec schema of
/// <c>docs/lua-runtime.md</c> §6.1; v2 adds the Freestyle <c>script</c> step, card F-020, and v3 the
/// optional per-step <c>stage</c>, card F-030).
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
/// <para>
/// <b>Secret references are recorded here (card D-006).</b> The frozen spec is the only description of a
/// run that outlives the repository, so the secret names a definition references are captured into
/// <see cref="SecretReferences"/> at definition time - where the <c>secret()</c> calls happen - instead of
/// being re-derived by parsing code later. The field is <b>tri-state on purpose</b>:
/// <list type="bullet">
/// <item><see langword="null"/> - the spec was frozen before capture existed, so what it references is
/// <b>unknown</b>; the page says exactly that instead of pretending zero (TC-003);</item>
/// <item>an empty array - capture ran and the pipeline references no secret, which is what makes a
/// delete safe to offer;</item>
/// <item>a non-empty array - the names, never their values (F-012's invariant).</item>
/// </list>
/// The names are recorded only: no value, no ciphertext and no hash is ever part of a spec.
/// </para>
/// </summary>
public sealed record PipelineSpec(
    int Version,
    string Name,
    PipelineStep[] Steps,
    SortedDictionary<string, string>? Env = null,
    PipelineTriggers? Triggers = null,
    string[]? SecretReferences = null);

/// <summary>
/// One step of a frozen spec. Exactly one of <see cref="Run"/> / <see cref="Shell"/> / <see cref="Script"/>
/// is set. <see cref="Run"/> and <see cref="Shell"/> are pure argv whose first element is the program
/// (<b>never</b> a shell command line: PRD §8 constraint 6 - no quoting interpretation exists anywhere in
/// the pipeline). <see cref="Script"/> is a multi-line shell text (card F-020): the Worker resolves its
/// references, writes it to a per-step file in the run's <c>temp/</c> and executes that file with the
/// platform shell. A <c>pipeline.lua</c> never produces <see cref="Script"/> - it is the frozen spelling
/// of a Freestyle job's step, configured in the Server UI and frozen into the spec there.
/// <para>
/// <see cref="Env"/>, <see cref="ContinueOnError"/> and <see cref="When"/> are frozen by the definition
/// phase and honoured by the C# executor (F-015): the step list is data, so a script cannot skip the
/// sequencing, the conditions or the environment composition.
/// <para>
/// <b>Two optional knobs, both additive (card F-032).</b> <see cref="TimeoutSeconds"/> bounds the step on
/// its own and <see cref="Retry"/> lets a failed or timed-out attempt run again; both are absent by default,
/// which is exactly the pre-F-032 behaviour, and the schema version stays put because a reader that does
/// not know them simply executes the step as it always did.
/// </para>
/// <para>
/// <b>The stage is presentation only, frozen with the step (card F-030, schema v3).</b> <see cref="Stage"/> is
/// the name of the group the run page shows the step under (the Jenkins-style Build → Test → Package read),
/// not an execution concept: the executor still runs the flat <see cref="PipelineSpec.Steps"/> list in
/// order. It is frozen here, on the step, rather than derived from the step's name at render time, because a
/// later rename must never rewrite what an old run displays.
/// </para>
/// </summary>
public sealed record PipelineStep(
    string Name,
    string[]? Run = null,
    string[]? Shell = null,
    string? Script = null,
    SortedDictionary<string, string>? Env = null,
    bool ContinueOnError = false,
    PipelineCondition? When = null,
    string[]? Artifacts = null,
    int? TimeoutSeconds = null,
    int? Retry = null,
    string? RunsOn = null,
    string? Requirement = null,
    string? Stage = null)
{
    /// <summary>
    /// Card F-030: whether this step belongs to a named stage. A step without one renders outside every
    /// stage section - never folded into the previous stage, which would attribute it to a group its author
    /// never chose.
    /// </summary>
    [JsonIgnore]
    public bool HasStage => !string.IsNullOrWhiteSpace(Stage);
    /// <summary>
    /// Card F-033: what this step needs from the Worker, as the author wrote it (<c>"linux &amp;&amp; arm64"</c> or
    /// <c>"linux,arm64"</c>), kept for the page. <see cref="Requirement"/> is the same thing normalized to DNF,
    /// which is what the run's union and the claim loop read; both are null when the step asks for nothing.
    /// </summary>
    [JsonIgnore]
    public bool HasWorkerRequirement => !string.IsNullOrWhiteSpace(Requirement);

    /// <summary>
    /// Whether this step declares its own execution-time limit (card F-032). The limit itself is
    /// <see cref="TimeoutSeconds"/>; an absent value means the step has none of its own and is bounded only
    /// by the run's deadline, exactly as before this card.
    /// </summary>
    [JsonIgnore]
    public bool HasOwnTimeout => TimeoutSeconds is not null;

    /// <summary>
    /// How many attempts this step may make (card F-032): <see cref="Retry"/> when declared, otherwise one.
    /// A failed or timed-out attempt is retried up to this count inside <b>one</b> step record; a
    /// cancellation is never retried.
    /// </summary>
    [JsonIgnore]
    public int AttemptCount => Retry ?? 1;

    /// <summary>This step's own limit as a span, or <see langword="null"/> when it declares none.</summary>
    [JsonIgnore]
    public TimeSpan? OwnTimeout =>
        TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Whether the step declares an execution-time condition (<c>when = { … }</c>).</summary>
    [JsonIgnore]
    public bool HasCondition => When is { } condition && !condition.IsEmpty;

    /// <summary>Whether the step was declared with <c>shell</c> rather than <c>run</c>.</summary>
    [JsonIgnore]
    public bool IsShell => Shell is not null;

    /// <summary>Whether the step was declared with a multi-line <c>script</c> (card F-020, Freestyle only).</summary>
    [JsonIgnore]
    public bool IsScript => Script is not null;

    /// <summary>
    /// The argv actually executed: <c>run</c>, or <c>shell</c>'s program + argv. Empty for a
    /// <see cref="Script"/> step - its command line is built at execution time (shell + step file).
    /// </summary>
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
    /// <summary>
    /// The schema version newly frozen specs carry (v3, card F-030: the optional per-step <c>stage</c>).
    /// An unknown version is refused, never guessed; v1 documents (frozen before F-020) and v2 documents
    /// (before F-030) stay readable and render exactly as they did.
    /// </summary>
    public const int Version = 3;

    /// <summary>The schema versions <see cref="PipelineSpecJson.TryRead"/> accepts. Only v3 is produced today.</summary>
    public static readonly int[] AcceptedVersions = [1, 2, 3];

    /// <summary>
    /// An upper bound on a stage name (card F-030). A stage name is a section header a person reads, not a
    /// path or a channel; 64 characters is enough for "Integration tests (linux)" and keeps a run page's
    /// header column bounded.
    /// </summary>
    public const int MaxStageLength = 64;

    /// <summary>
    /// An upper bound on one <c>script</c> step's text (card F-020). A Freestyle step is a hand-written
    /// command sequence, not a data channel; the bound keeps the frozen JSON bounded and the temp file
    /// reasonable.
    /// </summary>
    public const int MaxScriptChars = 32_768;

    /// <summary>
    /// An upper bound on the entries of one <c>when.env</c> map (card F-015). A condition is a hand-written
    /// comparison, not a data channel; the bound keeps the frozen JSON bounded.
    /// </summary>
    public const int MaxConditionEnvEntries = 32;

    /// <summary>An upper bound on the steps of one pipeline (§6.1's 1..200).</summary>
    public const int MaxSteps = 200;

    /// <summary>
    /// The lower bound of a step's own timeout (card F-032): one second. Below that the platform's own
    /// process start-up dominates the measurement, so the limit would kill steps that never had a chance.
    /// </summary>
    public const int MinStepTimeoutSeconds = 1;

    /// <summary>
    /// The upper bound of a step's own timeout (card F-032): one day. A step bounded by anything longer is
    /// not the step's problem any more - the run's own deadline is minutes.
    /// </summary>
    public const int MaxStepTimeoutSeconds = 86_400;

    /// <summary>
    /// The lower bound of a declared retry count (card F-032): one attempt, which is also what an absent
    /// <c>retry</c> means. Declaring one would be a step that never runs at all.
    /// </summary>
    public const int MinStepRetry = 1;

    /// <summary>
    /// The upper bound of a declared retry count (card F-032): five attempts. A flaky step is not a rebuild
    /// loop, and the executor's attempt loop must stay bounded.
    /// </summary>
    public const int MaxStepRetry = 5;

    /// <summary>
    /// Card F-033: the requirement a run needs is the union of its steps' <c>runs_on</c>. Two steps that cannot
    /// share one Worker are a definition mistake - a run would wait for a machine that does not exist - so this
    /// is checked where the definition is read, and the message names <b>both steps and the atoms that clash</b>
    /// (<c>pipeline.steps[1] asks for windows, pipeline.steps[2] asks for linux</c>).
    /// <para>
    /// "Cannot share one Worker" is judged by the requirement algebra, not by a list of exclusive labels: it is
    /// the case where the union of two steps' requirements is a contradiction (<c>linux</c> and <c>!linux</c>, or
    /// the same atom both ways). Whether a real machine carries two labels at once is the tag catalogue's
    /// question, asked at claim time; a definition is refused only for what can never hold.
    /// </para>
    /// </summary>
    public static string? ValidateWorkerRequirements(IReadOnlyList<PipelineStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var resolved = new List<(string Path, WorkerRequirement Requirement)>();
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var path = $"pipeline.steps[{index + 1}]";

            WorkerRequirement requirement;
            if (!string.IsNullOrWhiteSpace(step.Requirement))
            {
                // A frozen spec carries the normalized form; if it does not parse, the row is corrupt and says so
                // rather than being silently read as "no requirement".
                if (!WorkerRequirement.TryParse(step.Requirement, out requirement, out var parseError))
                {
                    return $"{path}.runs_on {parseError}";
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.RunsOn))
            {
                if (!WorkerRequirement.TryParse(step.RunsOn, out requirement, out var parseError))
                {
                    return $"{path}.runs_on {parseError}";
                }
            }
            else
            {
                continue;
            }

            // Pairwise: only the pair that actually clashes is named, which is what an author needs to fix.
            foreach (var (earlierPath, earlier) in resolved)
            {
                var union = earlier.And(requirement);
                if (union is null)
                {
                    return $"{earlierPath} and {path} need more than {WorkerRequirement.MaxDisjuncts} "
                        + "alternatives together; simplify one of them.";
                }

                if (union.IsContradiction)
                {
                    // The reason, not just the verdict: the union has no alternative left that one Worker
                    // could satisfy, and the atoms both steps name are what an author looks at first.
                    return $"{earlierPath} asks for '{earlier.Expression}' and {path} asks for "
                        + $"'{requirement.Expression}'; no single Worker can satisfy both - every combination "
                        + "of their alternatives contradicts itself "
                        + $"({string.Join(", ", earlier.Atoms.Intersect(requirement.Atoms, StringComparer.OrdinalIgnoreCase))}).";
                }
            }

            resolved.Add((path, requirement));
        }

        return null;
    }

    /// <summary>
    /// Validates a declared stage name (card F-030): non-empty, at most <see cref="MaxStageLength"/>
    /// characters, and with no leading or trailing whitespace. The shape rules live here because the Lua
    /// reader, the Freestyle builder and the Server's envelope gate all ask the same question - a second copy
    /// is how two sides start accepting different stages.
    /// </summary>
    /// <param name="stage">The declared name, or <see langword="null"/> when the step declares none.</param>
    /// <param name="error">A one-line reason, without the field path (the caller prefixes its own).</param>
    public static bool TryValidateStageName(string? stage, out string? error)
    {
        error = null;
        if (stage is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(stage))
        {
            error = "must be a non-empty string.";
            return false;
        }

        if (stage.Length > MaxStageLength)
        {
            error = $"must be at most {MaxStageLength} characters.";
            return false;
        }

        if (!string.Equals(stage, stage.Trim(), StringComparison.Ordinal))
        {
            error = "must not start or end with whitespace.";
            return false;
        }

        return true;
    }

    /// <summary>An upper bound on the argv length of one step.</summary>
    public const int MaxArguments = 64;

    /// <summary>An upper bound on a pipeline or step name (mirrors the 200-character name rule).</summary>
    public const int MaxNameLength = 200;

    /// <summary>An upper bound on a secret name (mirrors <c>SecretValidation.MaxNameLength</c>).</summary>
    public const int MaxSecretNameLength = 64;

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

    /// <summary>
    /// Whether <paramref name="name"/> has the <b>shape</b> a reference target must have: an
    /// environment-variable-shaped name. Deliberately no length bound - this is the predicate
    /// <c>env("NAME")</c>, <c>secret("NAME")</c> and the <c>${env:…}</c>/<c>${secret:…}</c> scanner have always
    /// used, and D-006 left that behaviour untouched.
    /// </summary>
    public static bool IsReferenceNameShape(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            var isLetter = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
            var isDigit = character is >= '0' and <= '9';

            // A name is environment-variable shaped: a letter or underscore first, then letters,
            // digits and underscores.
            if (!isLetter && !(i > 0 && isDigit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a name a <b>stored secret</b> could have (card D-006): the
    /// reference shape <b>and</b> the 64-character bound <c>SecretValidation</c> enforces. Only the recorded
    /// reference list uses this; <c>env()</c> keeps the unbounded shape rule, so a long environment variable
    /// name stays as legal as it was before this card.
    /// </summary>
    public static bool IsSecretReferenceName(string? name) =>
        name is not null && name.Length <= MaxSecretNameLength && IsReferenceNameShape(name);

    /// <summary>
    /// The recorded reference list, <b>normalised rather than refused</b> (card D-006): duplicates collapse,
    /// and an entry no stored secret could ever have (a bad shape, or over the name bound) is dropped. Dirty
    /// metadata must never fail a whole run - the definition already produced a valid step list, and the
    /// reference list is advisory metadata about names, not a value channel.
    /// <para>
    /// <b>A recorded entry is a reference, not a bare name (card F-040).</b> <c>NAME</c>, <c>NAME.username</c>,
    /// <c>NAME.password</c> and <c>file:NAME</c> are the four forms, and the exact form is what gets stored - so
    /// the Secrets page can say which half a pipeline used, and a file secret is not recorded as if it were a
    /// plain value. The grammar lives in <see cref="SecretReference.TryParseRecorded"/>; this method only
    /// normalises the list.
    /// </para>
    /// <para>
    /// <see langword="null"/> stays <see langword="null"/>, because that is the answer "this spec predates
    /// capture" - it must never be rewritten into "captured, nothing" (TC-003).
    /// </para>
    /// </summary>
    public static string[]? NormalizeSecretReferences(string[]? secretReferences)
    {
        if (secretReferences is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<string>(secretReferences.Length);
        foreach (var entry in secretReferences)
        {
            if (SecretReference.TryParseRecorded(entry, out var reference) && seen.Add(reference.Recorded))
            {
                references.Add(reference.Recorded);
            }
        }

        return [.. references];
    }
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
    /// Reads and defensively validates a frozen spec: parseable JSON, a schema version in
    /// <see cref="PipelineSpecSchema.AcceptedVersions"/>, a bounded size, 1..200 steps with non-empty
    /// unique names, and exactly one of <c>run</c>/<c>shell</c>/<c>script</c> per step. Returns
    /// <see langword="false"/> with a one-line reason when a rule fails.
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
            error = "the pipeline spec is not valid JSON for the frozen schema.";
            return false;
        }

        if (parsed is null)
        {
            error = "the pipeline spec is null.";
            return false;
        }

        if (Array.IndexOf(PipelineSpecSchema.AcceptedVersions, parsed.Version) < 0)
        {
            error = $"pipeline.version must be one of {string.Join(" or ", PipelineSpecSchema.AcceptedVersions)}.";
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

        var requirementError = PipelineSpecSchema.ValidateWorkerRequirements(parsed.Steps);
        if (requirementError is not null)
        {
            error = requirementError;
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

            var declared = (step.Run is null ? 0 : 1)
                + (step.Shell is null ? 0 : 1)
                + (step.Script is null ? 0 : 1);
            if (declared != 1)
            {
                error = $"{path} must declare exactly one of run, shell or script.";
                return false;
            }

            if (step.Script is { } script)
            {
                // F-020: a script step carries text instead of argv - non-empty and bounded, or it is a
                // mistake the executor could only fail later and less clearly.
                if (string.IsNullOrWhiteSpace(script))
                {
                    error = $"{path}.script must be a non-empty script text.";
                    return false;
                }

                if (script.Length > PipelineSpecSchema.MaxScriptChars)
                {
                    error = $"{path}.script must be at most {PipelineSpecSchema.MaxScriptChars} characters.";
                    return false;
                }
            }
            else if (step.Argv.Length == 0)
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

            // F-030: the stage is frozen on the step, so the envelope checks the shape the page will render -
            // an empty or whitespace-padded header is a definition mistake, not data to trim later.
            if (!PipelineSpecSchema.TryValidateStageName(step.Stage, out var stageError))
            {
                error = $"{path}.stage {stageError}";
                return false;
            }

            // The stage is v3's field. A v1/v2 document that carries one contradicts the version it was frozen
            // under, so it is refused instead of being rendered as if the field had always been there.
            if (step.Stage is not null && parsed.Version < 3)
            {
                error = $"{path}.stage requires pipeline.version 3 (this spec is v{parsed.Version}).";
                return false;
            }

            // F-032: the two step knobs are bounded here too. The Worker's definition phase refuses them
            // with the same bounds and a field path; this is the Server's defensive gate, so a buggy or
            // hostile worker cannot freeze a limit the executor would have to guess about (a zero-second
            // timeout, or a six-thousand-attempt loop).
            if (!TryReadStepLimit(
                    step.TimeoutSeconds,
                    PipelineSpecSchema.MinStepTimeoutSeconds,
                    PipelineSpecSchema.MaxStepTimeoutSeconds,
                    $"{path}.timeoutSeconds",
                    out error)
                || !TryReadStepLimit(
                    step.Retry,
                    PipelineSpecSchema.MinStepRetry,
                    PipelineSpecSchema.MaxStepRetry,
                    $"{path}.retry",
                    out error))
            {
                return false;
            }
        }

        // D-006: the recorded secret references are names only. They are normalised, never refused: a bad
        // entry must not turn a valid definition into an unreadable spec (see NormalizeSecretReferences).
        // Null stays null - that is "recorded before capture", i.e. unknown, and it must stay optional.
        var secretReferences = PipelineSpecSchema.NormalizeSecretReferences(parsed.SecretReferences);

        spec = secretReferences is null
            ? parsed
            : parsed with { SecretReferences = secretReferences };
        return true;
    }

    /// <summary>
    /// The envelope check of one of a step's two numeric knobs (card F-032). Absent is the default (no own
    /// timeout, one attempt); a declared value must be inside its bounds - a limit the executor could not
    /// honour is a definition mistake, never data stored for later interpretation.
    /// </summary>
    internal static bool TryReadStepLimit(int? value, int min, int max, string path, out string? error)
    {
        error = null;
        if (value is null)
        {
            return true;
        }

        if (value.Value < min || value.Value > max)
        {
            error = $"{path} must be between {min} and {max} (got {value.Value}).";
            return false;
        }

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
