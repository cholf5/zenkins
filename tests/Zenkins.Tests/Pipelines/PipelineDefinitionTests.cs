using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker;
using Zenkins.Worker.Lua;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-014's definition phase against the real F-011 sandbox: the prelude is shipped in the worker
/// binary, an I/O primitive called while describing is a clear positioned error (not a null reference),
/// a missing file / a syntax error / an invalid spec each map to their own closed failure reason, and a
/// runaway definition is abandoned by the run's own deadline (<c>pipeline-timeout</c>).
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineDefinitionTests
{
    // ------------------------------------------------------------------
    // The prelude is really in the binary
    // ------------------------------------------------------------------

    [Fact]
    public void The_prelude_is_an_embedded_resource_of_the_worker_assembly()
    {
        Assert.Contains(
            PipelineDefinitionRunner.PreludeResourceName,
            PipelineDefinitionRunner.EmbeddedResourceNames());

        var prelude = PipelineDefinitionRunner.PreludeSource;
        Assert.Contains("function pipeline(", prelude, StringComparison.Ordinal);
        Assert.Contains("function step(", prelude, StringComparison.Ordinal);
        Assert.Contains("function log(", prelude, StringComparison.Ordinal);
        Assert.Contains("function is_windows(", prelude, StringComparison.Ordinal);
        Assert.Contains("function is_linux(", prelude, StringComparison.Ordinal);
        Assert.Contains("function is_macos(", prelude, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prelude_defines_the_helpers_and_nothing_that_can_touch_the_host()
    {
        var runtime = PipelineDefinitionRunner.CreateRuntime(new LuaLogSink());

        var result = runtime.Execute(
            "return tostring(type(pipeline)) .. ',' .. tostring(type(step)) .. ',' .. tostring(type(log)) "
            + ".. ',' .. tostring(type(is_windows)) .. ',' .. tostring(type(io)) ",
            "pipeline.lua",
            DateTimeOffset.UtcNow.AddSeconds(10),
            TestContext.Current.CancellationToken,
            new LuaExecutionOptions
            {
                PreludeSource = PipelineDefinitionRunner.PreludeSource,
                Globals = new Dictionary<string, string>
                {
                    [PipelineDefinitionRunner.PlatformGlobalName] = "linux",
                },
            });

        Assert.Equal("function,function,function,function,nil", result.Value);
    }

    // ------------------------------------------------------------------
    // The structured return channel (the seam F-014 added)
    // ------------------------------------------------------------------

    [Fact]
    public void An_execution_result_carries_the_table_the_script_built()
    {
        var runtime = new LuaRuntime(new LuaLogSink());

        var result = runtime.Execute(
            """
            return {
              name = "Build",
              steps = { { name = "Restore", run = { "dotnet", "restore" } }, { name = "Build", run = { "dotnet", "build" } } },
              continue_on_error = false,
              count = 2,
            }
            """,
            "pipeline.lua",
            DateTimeOffset.UtcNow.AddSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsCompleted);
        // The textual rendering still exists for log lines; the structure is what the definition
        // phase consumes.
        Assert.StartsWith("table", result.Value, StringComparison.Ordinal);
        Assert.Equal(LuaHostValueKind.Table, result.Result!.Value.Kind);

        var root = result.Result.Value.Table;
        Assert.False(root.IsSequence);
        var entries = root.Entries.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("Build", entries["name"].Text);
        Assert.Equal(2, entries["count"].Number);
        Assert.False(entries["continue_on_error"].Boolean);

        // A list becomes a sequence with integer keys 1..n, in order, and nested tables recurse.
        Assert.True(entries["steps"].Table.IsSequence);
        var steps = entries["steps"].Table.Items;
        Assert.Equal(2, steps.Count);
        var firstStep = steps[0].Table.Entries.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("Restore", firstStep["name"].Text);
        Assert.Equal(["dotnet", "restore"], firstStep["run"].Table.Items.Select(item => item.Text));
    }

    [Fact]
    public void An_execution_result_reads_a_named_global_when_the_script_returns_nothing()
    {
        var runtime = new LuaRuntime(new LuaLogSink());

        var result = runtime.Execute(
            "captured = { name = \"FromGlobal\" }",
            "pipeline.lua",
            DateTimeOffset.UtcNow.AddSeconds(10),
            TestContext.Current.CancellationToken,
            new LuaExecutionOptions { ResultGlobal = "captured" });

        Assert.Equal("FromGlobal", result.Result!.Value.Table.Entries[0].Value.Text);
    }

    [Fact]
    public void The_lua_seam_exposes_no_engine_type()
    {
        // Card F-011's rule, still true after F-014 widened the seam: swapping the engine must stay an
        // implementation change. Any public member whose type comes from MoonSharp would show up here.
        var seamTypes = new[]
        {
            typeof(ILuaRuntime),
            typeof(LuaExecutionResult),
            typeof(LuaExecutionOptions),
            typeof(LuaExecutionOutcome),
            typeof(LuaHostValue),
            typeof(LuaHostValueKind),
            typeof(LuaHostTable),
            typeof(LuaHostCall),
            typeof(LuaHostResult),
        };

        foreach (var seamType in seamTypes)
        {
            var surface = seamType
                .GetMembers(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(member => member switch
                {
                    System.Reflection.MethodInfo method => method.ReturnType,
                    System.Reflection.PropertyInfo property => property.PropertyType,
                    System.Reflection.FieldInfo field => field.FieldType,
                    System.Reflection.ConstructorInfo constructor => constructor.DeclaringType,
                    _ => null,
                })
                .Where(type => type is not null);

            Assert.DoesNotContain(
                surface,
                type => (type!.Namespace ?? string.Empty).StartsWith("MoonSharp", StringComparison.Ordinal));
        }
    }

    // ------------------------------------------------------------------
    // A definition cannot do I/O
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("run")]
    [InlineData("shell")]
    [InlineData("checkout")]
    [InlineData("artifact")]
    public void An_io_primitive_called_while_describing_says_it_is_available_inside_steps_only(string primitive)
    {
        var definition = $$"""
            {{primitive}}("x")
            pipeline { name = "Build", steps = { { name = "Only", run = { "dotnet", "--info" } } } }
            """;

        using var run = new LuaRuntimeTestDefinition(definition);
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("available inside steps only", failure.Message, StringComparison.Ordinal);
        // Positioned like any other script error, so the run log points at the call.
        Assert.Contains("pipeline.lua:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Env_and_secret_are_deferred_references_not_values_read_while_describing()
    {
        // F-015 replaces the definition-time refusal with a deferred binding: env() and secret() answer with
        // a reference the C# executor substitutes at the step boundary, because neither the run's
        // environment nor a decrypted secret exists while the pipeline is being described. The spec therefore
        // carries the reference, not a value - which is also why the definition phase never holds plaintext.
        var definition = """
            pipeline {
              name = "Build",
              steps = {
                {
                  name = "Only",
                  run = { "deploy", secret("DEPLOY_TOKEN") },
                  env = { CONFIG = env("CONFIG") },
                },
              },
            }
            """;

        using var run = new LuaRuntimeTestDefinition(definition);
        var result = run.Run();

        Assert.Equal("${secret:DEPLOY_TOKEN}", result.Spec.Steps[0].Argv[1]);
        Assert.Equal("${env:CONFIG}", result.Spec.Steps[0].Env!["CONFIG"]);
        // ...and the same reference survives the frozen JSON the Server stores (it is data, not code).
        Assert.True(PipelineSpecJson.TryRead(result.SpecJson, out var frozen, out var error), error);
        Assert.Equal("${secret:DEPLOY_TOKEN}", frozen!.Steps[0].Argv[1]);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("env")]
    public void A_reference_helper_refuses_an_argument_that_is_not_a_variable_name(string helper)
    {
        // A malformed argument is the script's mistake, reported at its call site (positioned like any other
        // Lua error) instead of silently becoming an empty string that fails somewhere unrelated.
        var definition = $$"""
            {{helper}}(42)
            pipeline { name = "Build", steps = { { name = "Only", run = { "dotnet", "--info" } } } }
            """;

        using var run = new LuaRuntimeTestDefinition(definition);
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains($"{helper}() takes one environment-variable-shaped name", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pipeline.lua:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_primitive_can_be_caught_by_the_scripts_own_pcall()
    {
        var definition = """
            local ok, err = pcall(function() return run("x") end)
            if ok then error("run() did not refuse") end
            if not string.find(tostring(err), "available inside steps only", 1, true) then
              error("unexpected refusal: " .. tostring(err))
            end
            pipeline { name = "Build", steps = { { name = "Only", run = { "dotnet", "--info" } } } }
            """;

        using var run = new LuaRuntimeTestDefinition(definition);
        var result = run.Run();

        Assert.Equal("Build", result.Spec.Name);
    }

    // ------------------------------------------------------------------
    // Missing file, syntax error, runaway
    // ------------------------------------------------------------------

    [Fact]
    public void A_missing_pipeline_file_names_its_path()
    {
        using var runner = new DefinitionRun(pipelinePath: "ci/pipeline.lua");

        var failure = Assert.Throws<PipelineDefinitionException>(() => runner.Run());

        Assert.Equal(PipelineFailureReasons.PipelineMissing, failure.FailureReason);
        Assert.Contains("ci/pipeline.lua", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_syntax_error_reports_the_position_and_nothing_runs()
    {
        using var runner = new DefinitionRun("local x = = 1");

        var failure = Assert.Throws<PipelineDefinitionException>(() => runner.Run());

        Assert.Equal(PipelineFailureReasons.PipelineSyntax, failure.FailureReason);
        // source:(line,col-col) - the engine's own decorated position (§6.2). The exact column is the
        // engine's own reading of "unexpected symbol near '='", so the shape is what is pinned.
        Assert.Matches(@"pipeline\.lua:\(1,\d+-\d+\)", failure.Message);
    }

    [Fact]
    public void A_runtime_error_reports_the_line_of_the_user_file_not_a_prelude_shifted_one()
    {
        // The prelude is loaded as its own chunk, so a script error on line 3 must say line 3 - not
        // "line 3 + prelude length". A syntax error alone would not prove that (it is reported from the
        // chunk's own parse, before any prelude ran); this one raises at runtime.
        using var runner = new DefinitionRun(string.Join('\n',
            "local steps = {",
            "  { name = \"Only\", run = { \"dotnet\", \"--info\" } },",
            "}",
            "error(\"boom\")"));

        var failure = Assert.Throws<PipelineDefinitionException>(() => runner.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("pipeline.lua:(4,", failure.Message, StringComparison.Ordinal);
        Assert.Contains("boom", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_runaway_definition_is_abandoned_at_the_runs_deadline()
    {
        // A deadline a fraction of a second away is the deterministic stand-in for "the run's wall
        // clock ran out": the F-011 driver abandons the coroutine at its next yield point, so this
        // cannot hang the test.
        using var runner = new DefinitionRun("while true do end")
        {
            Deadline = DateTimeOffset.UtcNow.AddMilliseconds(150),
        };

        var failure = Assert.Throws<PipelineDefinitionException>(() => runner.Run());

        Assert.Equal(PipelineFailureReasons.PipelineTimeout, failure.FailureReason);
    }

    [Fact]
    public void A_canceled_definition_is_an_operation_cancellation_not_a_failure()
    {
        using var runner = new DefinitionRun("while true do end");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        Assert.Throws<OperationCanceledException>(() => runner.Run(cancellation.Token));
    }

    [Fact]
    public void A_pipeline_path_that_leaves_the_repository_is_refused()
    {
        using var runner = new DefinitionRun(pipelinePath: "../outside.lua");

        var failure = Assert.Throws<PipelineDefinitionException>(() => runner.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("leaves the checked-out repository", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>One definition run in a throwaway repository directory (no git, no process).</summary>
    private sealed class DefinitionRun : IDisposable
    {
        private readonly string _root;
        private readonly RunWorkDirectory _work;

        public DefinitionRun(string? source = null, string pipelinePath = "pipeline.lua")
        {
            _root = Path.Combine(Path.GetTempPath(), $"zenkins-f014-def-{Guid.NewGuid():N}");
            var repository = Path.Combine(_root, "repository");
            Directory.CreateDirectory(repository);
            if (source is not null)
            {
                var target = Path.Combine(repository, pipelinePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, source);
            }

            _work = new RunWorkDirectory(
                Guid.NewGuid(), _root, repository, Path.Combine(_root, "artifacts"), Path.Combine(_root, "temp"));
            PipelinePath = pipelinePath;
        }

        public string PipelinePath { get; }

        public DateTimeOffset Deadline { get; set; } = DateTimeOffset.UtcNow.AddSeconds(20);

        public PipelineDefinitionResult Run(CancellationToken? cancellationToken = null) =>
            new PipelineDefinitionRunner(PipelineDefinitionRunner.CreateRuntime(new LuaLogSink()))
                .Run(_work, PipelinePath, Deadline, cancellationToken ?? TestContext.Current.CancellationToken);

        public void Dispose()
        {
            // Q-011: a failed delete used to be swallowed (IOException-only) and the root was left in
            // the system temp forever; TempRootCleanup retries the transient failures instead.
            TempRootCleanup.DeleteDirectory(_root);
        }
    }
}
