using System.Globalization;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Lua;
using Zenkins.Shared.Workers;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// A pipeline definition that cannot be executed (card F-014): the file is missing, the script did not
/// compile or raised, or the table it declared is not a valid v1 spec. <see cref="FailureReason"/> is
/// one of <see cref="PipelineFailureReasons"/>' definition-phase values, so the run's terminal state
/// needs no string matching anywhere else.
/// <para>
/// The message is already the run-log line: for a spec rejection it names the offending field path
/// (<c>pipeline.steps[2].run must hold at least one argument.</c>), for a syntax error it is the
/// engine's <c>source:(line,col)</c> line, and for a missing file it names the path inside the
/// repository. Nothing here spawns a process - a definition that fails executes nothing.
/// </para>
/// </summary>
public sealed class PipelineDefinitionException(string message, string failureReason, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The <see cref="PipelineFailureReasons"/> value the run is failed with.</summary>
    public string FailureReason { get; } = failureReason;
}

/// <summary>
/// Builds a validated <see cref="PipelineSpec"/> from the table a <c>pipeline.lua</c> declared (card
/// F-014; the schema is v3 since card F-030 added the optional per-step <c>stage</c>).
/// <para>
/// <b>Strict and field-addressed.</b> Every unknown key is refused at both levels, every required
/// field is checked for the exact shape the executor consumes, and every message names the path it is
/// about (<c>pipeline.steps[3].continue_on_error</c>), because "invalid pipeline" without a path is
/// useless to the person who wrote it.
/// </para>
/// <para>
/// <b>Shapes.</b> The seam distinguishes a list from a map (<see cref="LuaHostTable.IsSequence"/>), so
/// the reader can be exact: <c>steps</c>, <c>stages</c>, <c>run</c>, <c>shell</c> and
/// <c>triggers.branches</c> are lists, <c>env</c>, a stage entry and the pipeline/step/triggers tables
/// themselves are maps. An empty table arrives
/// as an empty list (the seam's neutral answer), so every map field accepts an empty list as "nothing
/// declared" - which is exactly what an empty table means in Lua.
/// </para>
/// </summary>
public static class PipelineSpecReader
{
    private const string RunKey = "run";
    private const string ShellKey = "shell";

    private static readonly string[] PipelineKeys = ["name", "env", "steps", "stages", "post", "triggers"];
    private static readonly string[] StepKeys =
        ["name", "run", "shell", "env", "continue_on_error", "when", "artifacts", "timeout", "retry", "runs_on", "stage"];
    private static readonly string[] StageKeys = ["name", "steps"];
    private static readonly string[] PostKeys = ["success", "failure", "always"];
    private static readonly string[] WhenKeys = ["branch", "platform", "env"];
    private static readonly string[] TriggerKeys = ["branches"];

    /// <summary>
    /// Validates the definition table and returns the spec it describes.
    /// </summary>
    /// <exception cref="PipelineDefinitionException">
    /// The value is not a valid spec; the message names the field path and
    /// <see cref="PipelineDefinitionException.FailureReason"/> is <c>pipeline-invalid</c>.
    /// </exception>
    public static PipelineSpec Read(LuaHostValue value)
    {
        if (value.Kind != LuaHostValueKind.Table || value.Table.IsSequence)
        {
            throw Invalid("the definition must be a table with named fields: call pipeline { name = ..., steps = { ... } }.");
        }

        var root = value.Table;
        if (root.Entries.Count == 0)
        {
            throw Invalid("the definition is empty; pipeline { name = ..., steps = { ... } } is required.");
        }

        RejectUnknownKeys(root, "pipeline", PipelineKeys);

        var name = ReadName(root, "pipeline.name");
        var env = ReadEnv(root, "pipeline.env");

        // One name registry across the whole definition (card F-031): a post step's frozen `post/…` name must
        // be unique against the main list's too, or a run's step records would be ambiguous.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = ReadSteps(root, names);
        var post = ReadPost(root, names);
        var triggers = ReadTriggers(root);

        return new PipelineSpec(PipelineSpecSchema.Version, name, steps, env, triggers, Post: post);
    }

    /// <summary>
    /// Reads the pipeline's step list, declared in one of the two spellings the schema allows (card F-030):
    /// <c>steps = { … }</c> - the flat list every v1/v2 definition uses, unchanged - or
    /// <c>stages = { { name = "Build", steps = { … } }, … }</c>, the sugar form that assigns a stage name
    /// to the steps it contains. Both produce <b>one flat list in declaration order</b>: the frozen spec
    /// has a single step index, so the executor, the step reports and the run page keep one shape.
    /// <para>
    /// <b>The ordered list is the only stages form.</b> A Lua map (<c>stages = { Build = … }</c>) has no
    /// declaration order the host could trust, and stages are read in the order they appear - so the map
    /// form is refused with a message that shows the list form instead of silently reordering a pipeline.
    /// </para>
    /// </summary>
    private static PipelineStep[] ReadSteps(LuaHostTable root, Dictionary<string, string> names)
    {
        var hasSteps = TryGet(root, "steps", out var stepsValue) && !stepsValue.IsNil;
        var hasStages = TryGet(root, "stages", out var stagesValue) && !stagesValue.IsNil;

        if (hasSteps && hasStages)
        {
            throw Invalid(
                "pipeline must declare either steps or stages, not both: stages assigns the stage name to the "
                + "steps it contains, so a pipeline that uses it declares no top-level steps.");
        }

        // One name registry across both spellings: a duplicate is a duplicate wherever it was declared.
        var steps = hasStages
            ? ReadStageSteps(stagesValue, names)
            : ReadFlatSteps(stepsValue, names);

        // Card F-033: a run executes on one Worker, so the union of the steps' requirements must be one Worker's
        // worth. A contradiction is a definition mistake and is refused here, by path, naming both steps.
        var requirementError = PipelineSpecSchema.ValidateWorkerRequirements(steps);
        if (requirementError is not null)
        {
            throw Invalid(requirementError);
        }

        return steps;
    }

    /// <summary>
    /// Reads the optional <c>post = { always = { … }, success = { … }, failure = { … } }</c> table
    /// (card F-031). Each list holds ordinary step tables; their frozen names get the
    /// <see cref="PipelineSpecSchema.PostNamePrefix"/>, which is what keeps a post step's record and log
    /// unambiguous. The order of the three keys does not matter: the executor, not the table, decides which
    /// set runs from the run's own outcome.
    /// </summary>
    private static PipelinePost? ReadPost(LuaHostTable root, Dictionary<string, string> names)
    {
        if (!TryGet(root, "post", out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind == LuaHostValueKind.Table && value.Table.IsSequence && value.Table.Items.Count == 0)
        {
            // An empty table is Lua for "nothing declared" everywhere else (env, triggers), so it is the same
            // here: no post phase rather than an empty one.
            return null;
        }

        if (value.Kind != LuaHostValueKind.Table || value.Table.IsSequence)
        {
            throw Invalid(
                "pipeline.post must be a table ({ always = { … }, success = { … }, failure = { … } }), got "
                + Describe(value) + ".");
        }

        var post = value.Table;
        RejectUnknownKeys(post, "pipeline.post", PostKeys);

        var success = ReadPostList(post, PipelinePostOutcomes.Success, names);
        var failure = ReadPostList(post, PipelinePostOutcomes.Failure, names);
        var always = ReadPostList(post, PipelinePostOutcomes.Always, names);
        if (success is null && failure is null && always is null)
        {
            throw Invalid("pipeline.post must declare at least one of always, success or failure.");
        }

        return new PipelinePost(success, failure, always);
    }

    /// <summary>
    /// Reads one post outcome list. Absent and an empty table both mean "this set declares nothing" - the
    /// same reading every other list field gets (an empty Lua table has no shape of its own).
    /// </summary>
    private static PipelineStep[]? ReadPostList(LuaHostTable post, string key, Dictionary<string, string> names)
    {
        var path = $"pipeline.post.{key}";
        if (!TryGet(post, key, out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind != LuaHostValueKind.Table || !value.Table.IsSequence)
        {
            throw Invalid($"{path} must be a list of steps, not a map.");
        }

        var items = value.Table.Items;
        if (items.Count == 0)
        {
            return null;
        }

        var steps = new PipelineStep[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            steps[i] = ReadStep(items[i], $"{path}[{i + 1}]", inheritedStage: null, names, isPost: true);
        }

        // F-033's cross-step rule applies to a post list too: one Worker executes the run, so two post steps
        // whose requirements can never hold together are a definition mistake, named by their own paths.
        var requirementError = PipelineSpecSchema.ValidateWorkerRequirements(steps, path);
        if (requirementError is not null)
        {
            throw Invalid(requirementError);
        }

        return steps;
    }

    /// <summary>The flat <c>steps = { … }</c> form: the v1 spelling, read exactly as before F-030.</summary>
    private static PipelineStep[] ReadFlatSteps(LuaHostValue stepsValue, Dictionary<string, string> names)
    {
        if (stepsValue.IsNil)
        {
            throw Invalid("pipeline.steps is required: declare at least one step (or a stages list).");
        }

        if (stepsValue.Kind != LuaHostValueKind.Table)
        {
            throw Invalid("pipeline.steps must be a list of steps, got " + Describe(stepsValue) + ".");
        }

        var stepsTable = stepsValue.Table;
        var count = stepsTable.IsSequence ? stepsTable.Items.Count : stepsTable.Entries.Count;
        if (count == 0)
        {
            throw Invalid("pipeline.steps must declare at least one step.");
        }

        if (count > PipelineSpecSchema.MaxSteps)
        {
            throw Invalid($"pipeline.steps must declare at most {PipelineSpecSchema.MaxSteps} steps (found {count}).");
        }

        if (!stepsTable.IsSequence)
        {
            throw Invalid("pipeline.steps must be a list of step tables, not a map.");
        }

        var steps = new PipelineStep[count];
        for (var i = 0; i < count; i++)
        {
            steps[i] = ReadStep(
                stepsTable.Items[i], $"pipeline.steps[{i + 1}]", inheritedStage: null, names);
        }

        return steps;
    }

    /// <summary>
    /// The <c>stages = { … }</c> sugar form (card F-030): an ordered list of
    /// <c>{ name = "Build", steps = { … } }</c> entries. Each entry's name is written onto every step it
    /// contains, in declaration order, so the frozen spec carries the name on the step and a later reader
    /// never has to reconstruct it. An entry without a <c>name</c> declares steps that belong to no stage
    /// (they render outside every section), which is how a pipeline mixes staged and unstaged steps without
    /// leaving the sugar form.
    /// </summary>
    private static PipelineStep[] ReadStageSteps(LuaHostValue stagesValue, Dictionary<string, string> names)
    {
        if (stagesValue.Kind != LuaHostValueKind.Table || !stagesValue.Table.IsSequence)
        {
            throw Invalid(
                "pipeline.stages must be a list of stage tables ({ { name = \"Build\", steps = { … } } }), got "
                + Describe(stagesValue) + ". A map has no declaration order, and stages are read in order.");
        }

        var stageItems = stagesValue.Table.Items;
        if (stageItems.Count == 0)
        {
            throw Invalid("pipeline.stages must declare at least one stage.");
        }

        var steps = new List<PipelineStep>();
        for (var stageIndex = 0; stageIndex < stageItems.Count; stageIndex++)
        {
            var path = $"pipeline.stages[{stageIndex + 1}]";
            var stageItem = stageItems[stageIndex];
            if (stageItem.Kind != LuaHostValueKind.Table || stageItem.Table.IsSequence)
            {
                throw Invalid($"{path} must be a stage table ({{ name = \"Build\", steps = {{ … }} }}), got "
                    + Describe(stageItem) + ".");
            }

            var stageTable = stageItem.Table;
            RejectUnknownKeys(stageTable, path, StageKeys);
            var stageName = ReadStageName(stageTable, path);

            if (!TryGet(stageTable, "steps", out var stageStepsValue) || stageStepsValue.IsNil)
            {
                throw Invalid($"{path}.steps is required: a stage holds at least one step.");
            }

            if (stageStepsValue.Kind != LuaHostValueKind.Table || !stageStepsValue.Table.IsSequence)
            {
                throw Invalid($"{path}.steps must be a list of step tables, not a map.");
            }

            var items = stageStepsValue.Table.Items;
            if (items.Count == 0)
            {
                throw Invalid($"{path}.steps must declare at least one step.");
            }

            for (var i = 0; i < items.Count; i++)
            {
                steps.Add(ReadStep(items[i], $"{path}.steps[{i + 1}]", stageName, names));
            }
        }

        if (steps.Count > PipelineSpecSchema.MaxSteps)
        {
            throw Invalid(
                $"pipeline.stages must declare at most {PipelineSpecSchema.MaxSteps} steps in total (found {steps.Count}).");
        }

        return [.. steps];
    }

    /// <summary>
    /// Reads one step table. <paramref name="inheritedStage"/> is the name the containing stage entry
    /// assigned (card F-030); a step that also declares its own <c>stage</c> inside a named entry is refused
    /// rather than silently overridden, because "which of the two names did the author mean" is not a
    /// question a frozen record should leave open.
    /// </summary>
    private static PipelineStep ReadStep(
        LuaHostValue item,
        string path,
        string? inheritedStage,
        Dictionary<string, string> names,
        bool isPost = false)
    {
        if (item.Kind != LuaHostValueKind.Table || item.Table.IsSequence)
        {
            throw Invalid($"{path} must be a table with name and run (or shell), got {Describe(item)}.");
        }

        var table = item.Table;
        RejectUnknownKeys(table, path, StepKeys);

        var declaredName = ReadName(table, path + ".name");
        string stepName;
        if (isPost)
        {
            // F-031: the frozen record's name carries the prefix, so a step record and a log line can never be
            // mistaken for a main step's. The author writes the plain name; writing the prefix again is a
            // mistake worth naming, not a silently doubled prefix.
            if (declaredName.StartsWith(PipelineSpecSchema.PostNamePrefix, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"{path}.name must not start with '{PipelineSpecSchema.PostNamePrefix}' - a post step's "
                    + "record name gets that prefix automatically; declare the plain name.");
            }

            stepName = PipelineSpecSchema.PostNamePrefix + declaredName;
        }
        else
        {
            stepName = declaredName;
        }

        if (names.TryGetValue(stepName, out var firstPath))
        {
            throw Invalid(
                $"{path}.name '{declaredName}' is already used by {firstPath}; step names must be unique.");
        }

        names.Add(stepName, path);

        var run = ReadArgv(table, path, RunKey);
        var shell = ReadArgv(table, path, ShellKey);
        if ((run is null) == (shell is null))
        {
            throw Invalid($"{path} must declare exactly one of run or shell.");
        }

        var stepEnv = ReadEnv(table, path + ".env");
        var continueOnError = ReadContinueOnError(table, path);
        var when = ReadWhen(table, path);
        var artifactPaths = ReadArtifactPaths(table, path);
        var stepTimeout = ReadStepLimit(
            table,
            "timeout",
            path,
            PipelineSpecSchema.MinStepTimeoutSeconds,
            PipelineSpecSchema.MaxStepTimeoutSeconds);
        var stepRetry = ReadStepLimit(
            table,
            "retry",
            path,
            PipelineSpecSchema.MinStepRetry,
            PipelineSpecSchema.MaxStepRetry);
        var runsOn = ReadRunsOn(table, path);
        var declaredStage = ReadStage(table, path);

        if (inheritedStage is not null && declaredStage is not null)
        {
            throw Invalid(
                $"{path}.stage is assigned by the stage entry that contains it; declare the stage in one place "
                + $"({inheritedStage}).");
        }

        // A post step renders in the post section, never inside a stage: a stage on it is configuration the
        // page cannot honour, so it is refused here as well as by the envelope.
        if (isPost && declaredStage is not null)
        {
            throw Invalid(
                $"{path}.stage must not be declared: post steps render in the post section, not a stage.");
        }

        return new PipelineStep(
            stepName,
            Run: run,
            Shell: shell,
            Env: stepEnv,
            ContinueOnError: continueOnError,
            When: when,
            Artifacts: artifactPaths,
            TimeoutSeconds: stepTimeout,
            Retry: stepRetry,
            RunsOn: runsOn.Raw,
            Requirement: runsOn.Normalized,
            Stage: declaredStage ?? inheritedStage);
    }

    /// <summary>
    /// Reads a step's own <c>stage = "Build"</c> (card F-030) and validates the shape the run page renders -
    /// non-empty, bounded, unpadded - with the field path, like every other declared field.
    /// </summary>
    private static string? ReadStage(LuaHostTable table, string path)
    {
        if (!TryGet(table, "stage", out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind != LuaHostValueKind.String)
        {
            throw Invalid($"{path}.stage must be a string, got {Describe(value)}.");
        }

        if (!PipelineSpecSchema.TryValidateStageName(value.Text, out var error))
        {
            throw Invalid($"{path}.stage {error}");
        }

        return value.Text;
    }

    /// <summary>Reads a stage entry's optional <c>name</c> with the same shape rules as a step's stage.</summary>
    private static string? ReadStageName(LuaHostTable table, string path)
    {
        if (!TryGet(table, "name", out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind != LuaHostValueKind.String)
        {
            throw Invalid($"{path}.name must be a string, got {Describe(value)}.");
        }

        if (!PipelineSpecSchema.TryValidateStageName(value.Text, out var error))
        {
            throw Invalid($"{path}.name {error}");
        }

        return value.Text;
    }

    /// <summary>
    /// Reads a step's Worker requirement (card F-033): <c>runs_on = { "linux", "arm64" }</c> (all must match),
    /// <c>runs_on = "linux &amp;&amp; (arm64 || x64)"</c>, or absent for "any Worker". The raw text is kept for the
    /// page and the normalized DNF is what the run's union and the claim loop read.
    /// </summary>
    private static (string? Raw, string? Normalized) ReadRunsOn(LuaHostTable table, string path)
    {
        if (!TryGet(table, "runs_on", out var value) || value.IsNil)
        {
            return (null, null);
        }

        string text;
        if (value.Kind == LuaHostValueKind.String)
        {
            text = value.Text;
        }
        else if (value.Kind == LuaHostValueKind.Table && value.Table.IsSequence)
        {
            // The table form is the conjunction its entries spell out, in the same comma syntax the trigger's
            // tag field already uses - one spelling, one parser.
            var entries = new List<string>();
            foreach (var entry in value.Table.Items)
            {
                if (entry.Kind != LuaHostValueKind.String)
                {
                    throw Invalid($"{path}.runs_on must be a string or a list of strings, got a list holding "
                        + Describe(entry) + ".");
                }

                entries.Add(entry.Text);
            }

            text = string.Join(',', entries);
        }
        else
        {
            throw Invalid($"{path}.runs_on must be a string or a list of strings, got {Describe(value)}.");
        }

        if (!WorkerRequirement.TryParse(text, out var requirement, out var error))
        {
            throw Invalid($"{path}.runs_on {error}");
        }

        if (requirement.IsContradiction)
        {
            throw Invalid($"{path}.runs_on can never be satisfied: '{text}'.");
        }

        return (text, requirement.StorageForm);
    }

    private static string[]? ReadArgv(LuaHostTable table, string path, string key)
    {
        if (!TryGet(table, key, out var value) || value.IsNil)
        {
            return null;
        }

        var argvPath = path + "." + key;
        if (value.Kind != LuaHostValueKind.Table)
        {
            throw Invalid($"{argvPath} must be a list of strings, got {Describe(value)}.");
        }

        var argvTable = value.Table;
        if (!argvTable.IsSequence)
        {
            throw Invalid($"{argvPath} must be a list of strings, not a map.");
        }

        var items = argvTable.Items;
        if (items.Count == 0)
        {
            throw Invalid($"{argvPath} must hold at least one argument.");
        }

        if (items.Count > PipelineSpecSchema.MaxArguments)
        {
            throw Invalid($"{argvPath} must hold at most {PipelineSpecSchema.MaxArguments} arguments (found {items.Count}).");
        }

        var argv = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var elementPath = $"{argvPath}[{i + 1}]";
            if (items[i].Kind != LuaHostValueKind.String)
            {
                throw Invalid($"{elementPath} must be a string, got {Describe(items[i])}.");
            }

            if (items[i].Text.Length == 0)
            {
                throw Invalid($"{elementPath} is empty; every argument must be a non-empty string.");
            }

            argv[i] = items[i].Text;
        }

        return argv;
    }

    /// <summary>
    /// Reads a step's declared artifact paths (card F-016): <c>artifacts = { "bin/Release" }</c>, the
    /// declarative spelling of PRD §3.7's <c>artifact("bin/Release")</c>. Strict about shape - a list of
    /// non-empty strings, bounded in number - but deliberately <b>not</b> about the paths themselves: those
    /// are validated when the executor publishes them, by the same shared rules the server applies, so a
    /// traversal fails the step that declared it (with the path named) instead of the whole definition.
    /// </summary>
    private static string[]? ReadArtifactPaths(LuaHostTable table, string path)
    {
        var artifactsPath = path + ".artifacts";
        if (!TryGet(table, "artifacts", out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind != LuaHostValueKind.Table || !value.Table.IsSequence)
        {
            throw Invalid($"{artifactsPath} must be a list of paths ({{ \"bin/Release\" }}), got {Describe(value)}.");
        }

        var items = value.Table.Items;
        if (items.Count == 0)
        {
            throw Invalid($"{artifactsPath} must hold at least one path.");
        }

        if (items.Count > PipelineSpecSchema.MaxArtifactsPerStep)
        {
            throw Invalid(
                $"{artifactsPath} must hold at most {PipelineSpecSchema.MaxArtifactsPerStep} paths "
                + $"(found {items.Count}).");
        }

        var paths = new string[items.Count];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var elementPath = $"{artifactsPath}[{i + 1}]";
            if (items[i].Kind != LuaHostValueKind.String)
            {
                throw Invalid($"{elementPath} must be a string, got {Describe(items[i])}.");
            }

            if (items[i].Text.Length == 0)
            {
                throw Invalid($"{elementPath} is empty; every artifact path must be a non-empty string.");
            }

            // The same path twice in one step would make the run's artifact names ambiguous (create would answer
            // a 409 mid-run). It is a definition mistake, so it is refused here, with the field path.
            if (seen.TryGetValue(items[i].Text, out var firstIndex))
            {
                throw Invalid(
                    $"{elementPath} '{items[i].Text}' is already declared by {artifactsPath}[{firstIndex}]; "
                    + "a step declares each artifact once.");
            }

            seen.Add(items[i].Text, i + 1);
            paths[i] = items[i].Text;
        }

        return paths;
    }

    private static SortedDictionary<string, string>? ReadEnv(LuaHostTable table, string path)
    {
        if (!TryGet(table, "env", out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind != LuaHostValueKind.Table)
        {
            throw Invalid($"{path} must be a map of names to string values, got {Describe(value)}.");
        }

        var envTable = value.Table;
        if (envTable.IsSequence && envTable.Items.Count == 0)
        {
            // An empty table is Lua for "no entries"; the seam cannot tell a list from a map there.
            return null;
        }

        if (envTable.IsSequence)
        {
            throw Invalid($"{path} must be a map of names to string values, not a list.");
        }

        // Sorted, so the frozen JSON of one definition is byte-stable across runs and hosts.
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, envValue) in envTable.Entries)
        {
            if (envValue.Kind != LuaHostValueKind.String)
            {
                throw Invalid($"{path}.{key} must be a string, got {Describe(envValue)}.");
            }

            env[key] = envValue.Text;
        }

        return env.Count == 0 ? null : env;
    }

    /// <summary>
    /// Reads one of a step's two numeric knobs (card F-032): <c>timeout = 600</c> (seconds) and
    /// <c>retry = 2</c> (attempts). Both are optional and bounded; an out-of-range or non-integer value is a
    /// definition error with the field path, exactly like every other declared field - the executor must
    /// never have to guess at a limit.
    /// </summary>
    private static int? ReadStepLimit(LuaHostTable table, string key, string path, int min, int max)
    {
        if (!TryGet(table, key, out var value) || value.IsNil)
        {
            return null;
        }

        var limitPath = $"{path}.{key}";
        if (value.Kind != LuaHostValueKind.Number)
        {
            throw Invalid($"{limitPath} must be an integer, got {Describe(value)}.");
        }

        var number = value.Number;
        if (number != Math.Floor(number) || number < min || number > max)
        {
            throw Invalid($"{limitPath} must be a whole number between {min} and {max}, got {number}.");
        }

        return (int)number;
    }

    private static bool ReadContinueOnError(LuaHostTable table, string path)
    {
        if (!TryGet(table, "continue_on_error", out var value) || value.IsNil)
        {
            return false;
        }

        if (value.Kind != LuaHostValueKind.Boolean)
        {
            throw Invalid($"{path}.continue_on_error must be true or false, got {Describe(value)}.");
        }

        return value.Boolean;
    }

    /// <summary>
    /// Reads a step's execution-time condition (card F-015): <c>when = { branch = …, platform = …, env = … }</c>.
    /// Strict like everything else here - an unknown key is refused by field path (a typo like
    /// <c>brnach</c> would otherwise silently never match), a platform the executor does not know is refused,
    /// and an empty table is refused because a condition that declares nothing is a mistake, not "always".
    /// </summary>
    private static PipelineCondition? ReadWhen(LuaHostTable table, string path)
    {
        var whenPath = path + ".when";
        if (!TryGet(table, "when", out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind != LuaHostValueKind.Table)
        {
            throw Invalid($"{whenPath} must be a table ({{ branch = \"main\" }}), got {Describe(value)}.");
        }

        if (value.Table.IsSequence)
        {
            // An empty table arrives as an empty list: a condition that declares nothing.
            if (value.Table.Items.Count == 0)
            {
                throw Invalid($"{whenPath} must declare at least one of branch, platform or env.");
            }

            throw Invalid($"{whenPath} must be a table with named fields, not a list.");
        }

        var when = value.Table;
        RejectUnknownKeys(when, whenPath, WhenKeys);

        var branch = ReadOptionalString(when, whenPath + ".branch");
        var platform = ReadOptionalString(when, whenPath + ".platform");
        if (platform is not null && !PipelineCondition.Platforms.Contains(platform, StringComparer.Ordinal))
        {
            throw Invalid(
                $"{whenPath}.platform must be one of {string.Join(", ", PipelineCondition.Platforms)} "
                + $"(got \"{platform}\").");
        }

        var env = ReadEnv(when, whenPath);
        var condition = new PipelineCondition(branch, platform, env);
        if (condition.IsEmpty)
        {
            throw Invalid($"{whenPath} must declare at least one of branch, platform or env.");
        }

        return condition;
    }

    /// <summary>A declared string field that may be absent; present but not a non-empty string is refused.</summary>
    private static string? ReadOptionalString(LuaHostTable table, string path)
    {
        var key = path[(path.LastIndexOf('.') + 1)..];
        if (!TryGet(table, key, out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind != LuaHostValueKind.String || value.Text.Length == 0)
        {
            throw Invalid($"{path} must be a non-empty string, got {Describe(value)}.");
        }

        return value.Text;
    }

    private static PipelineTriggers? ReadTriggers(LuaHostTable root)
    {
        if (!TryGet(root, "triggers", out var value) || value.IsNil)
        {
            return null;
        }

        if (value.Kind != LuaHostValueKind.Table || value.Table.IsSequence)
        {
            // An empty table arrives as an empty list: "no triggers declared".
            if (value.Kind == LuaHostValueKind.Table && value.Table.Items.Count == 0)
            {
                return null;
            }

            throw Invalid("pipeline.triggers must be a table ({ branches = { \"main\" } }), got " + Describe(value) + ".");
        }

        var triggers = value.Table;
        RejectUnknownKeys(triggers, "pipeline.triggers", TriggerKeys);

        if (!TryGet(triggers, "branches", out var branchesValue) || branchesValue.IsNil)
        {
            return new PipelineTriggers();
        }

        if (branchesValue.Kind != LuaHostValueKind.Table || !branchesValue.Table.IsSequence)
        {
            throw Invalid("pipeline.triggers.branches must be a list of branch names.");
        }

        var branches = new string[branchesValue.Table.Items.Count];
        for (var i = 0; i < branches.Length; i++)
        {
            var item = branchesValue.Table.Items[i];
            if (item.Kind != LuaHostValueKind.String || item.Text.Length == 0)
            {
                throw Invalid($"pipeline.triggers.branches[{i + 1}] must be a non-empty string.");
            }

            branches[i] = item.Text;
        }

        return new PipelineTriggers(branches);
    }

    private static string ReadName(LuaHostTable table, string path)
    {
        if (!TryGet(table, path[(path.LastIndexOf('.') + 1)..], out var value) || value.IsNil)
        {
            throw Invalid($"{path} is required.");
        }

        if (value.Kind != LuaHostValueKind.String)
        {
            throw Invalid($"{path} must be a string, got {Describe(value)}.");
        }

        if (value.Text.Length == 0)
        {
            throw Invalid($"{path} must be a non-empty string.");
        }

        if (value.Text.Length > PipelineSpecSchema.MaxNameLength)
        {
            throw Invalid($"{path} must be at most {PipelineSpecSchema.MaxNameLength} characters.");
        }

        return value.Text;
    }

    private static void RejectUnknownKeys(LuaHostTable table, string path, string[] allowed)
    {
        if (table.IsSequence)
        {
            return;
        }

        foreach (var (key, _) in table.Entries)
        {
            if (Array.IndexOf(allowed, key) < 0)
            {
                throw Invalid(
                    $"{path} has an unknown key '{key}'; allowed keys are {string.Join(", ", allowed)}.");
            }
        }
    }

    private static bool TryGet(LuaHostTable table, string key, out LuaHostValue value)
    {
        foreach (var (entryKey, entryValue) in table.Entries)
        {
            if (string.Equals(entryKey, key, StringComparison.Ordinal))
            {
                value = entryValue;
                return true;
            }
        }

        value = LuaHostValue.Nil;
        return false;
    }

    private static string Describe(LuaHostValue value) => value.Kind switch
    {
        LuaHostValueKind.Nil => "nil",
        LuaHostValueKind.Table => value.Table.IsSequence ? "a list" : "a table",
        LuaHostValueKind.Number => "a number (" + value.Number.ToString("R", CultureInfo.InvariantCulture) + ")",
        LuaHostValueKind.Boolean => "a boolean",
        _ => "a string",
    };

    private static PipelineDefinitionException Invalid(string message) =>
        new(message, PipelineFailureReasons.PipelineInvalid);
}
