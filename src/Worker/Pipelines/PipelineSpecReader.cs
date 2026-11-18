using System.Globalization;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Lua;

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
/// F-014, spec schema v1).
/// <para>
/// <b>Strict and field-addressed.</b> Every unknown key is refused at both levels, every required
/// field is checked for the exact shape the executor consumes, and every message names the path it is
/// about (<c>pipeline.steps[3].continue_on_error</c>), because "invalid pipeline" without a path is
/// useless to the person who wrote it.
/// </para>
/// <para>
/// <b>Shapes.</b> The seam distinguishes a list from a map (<see cref="LuaHostTable.IsSequence"/>), so
/// the reader can be exact: <c>steps</c>, <c>run</c>, <c>shell</c> and <c>triggers.branches</c> are
/// lists, <c>env</c> and the pipeline/step/triggers tables themselves are maps. An empty table arrives
/// as an empty list (the seam's neutral answer), so every map field accepts an empty list as "nothing
/// declared" - which is exactly what an empty table means in Lua.
/// </para>
/// </summary>
public static class PipelineSpecReader
{
    private const string RunKey = "run";
    private const string ShellKey = "shell";

    private static readonly string[] PipelineKeys = ["name", "env", "steps", "triggers"];
    private static readonly string[] StepKeys =
        ["name", "run", "shell", "env", "continue_on_error", "when", "artifacts", "timeout", "retry"];
    private static readonly string[] WhenKeys = ["branch", "platform", "env"];
    private static readonly string[] TriggerKeys = ["branches"];

    /// <summary>
    /// Validates the definition table and returns the spec it describes.
    /// </summary>
    /// <exception cref="PipelineDefinitionException">
    /// The value is not a valid v1 spec; the message names the field path and
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
        var steps = ReadSteps(root);
        var triggers = ReadTriggers(root);

        return new PipelineSpec(PipelineSpecSchema.Version, name, steps, env, triggers);
    }

    private static PipelineStep[] ReadSteps(LuaHostTable root)
    {
        if (!TryGet(root, "steps", out var stepsValue) || stepsValue.IsNil)
        {
            throw Invalid("pipeline.steps is required: declare at least one step.");
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
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var path = $"pipeline.steps[{i + 1}]";
            var item = stepsTable.Items[i];
            if (item.Kind != LuaHostValueKind.Table || item.Table.IsSequence)
            {
                throw Invalid($"{path} must be a table with name and run (or shell), got {Describe(item)}.");
            }

            var table = item.Table;
            RejectUnknownKeys(table, path, StepKeys);

            var stepName = ReadName(table, path + ".name");
            if (names.TryGetValue(stepName, out var firstIndex))
            {
                throw Invalid(
                    $"{path}.name '{stepName}' is already used by {FirstStepPath(firstIndex)}; step names must be unique.");
            }

            names.Add(stepName, i + 1);

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

            steps[i] = new PipelineStep(
                stepName,
                Run: run,
                Shell: shell,
                Env: stepEnv,
                ContinueOnError: continueOnError,
                When: when,
                Artifacts: artifactPaths,
                TimeoutSeconds: stepTimeout,
                Retry: stepRetry);
        }

        return steps;
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

    private static string FirstStepPath(int index) => $"pipeline.steps[{index}]";

    private static PipelineDefinitionException Invalid(string message) =>
        new(message, PipelineFailureReasons.PipelineInvalid);
}
