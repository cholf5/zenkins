using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// The runtime's two knobs and their defaults (card F-011, Subtasks: "document the runtime knobs next
/// to the existing worker configuration pins"). Unlike <c>WorkerConfigurationTests</c>, neither knob
/// comes from the environment: the production run budget is the run's own deadline
/// (<c>BuildRun.DeadlineAt</c>, F-009) and the instruction budget is a driver property, so they are
/// pinned here as code constants rather than as parse rules.
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaRuntimeKnobsTests
{
    [Fact]
    public void The_instruction_budget_defaults_to_ten_thousand_vm_instructions()
    {
        // docs/lua-runtime.md §5.1: hand control back every 10 000 instructions; the measured count
        // of host round-trips inside a 300 ms budget follows from this value (§9 #14).
        Assert.Equal(10_000, LuaRuntimeOptions.DefaultInstructionBudget);
        Assert.Equal(10_000, new LuaRuntimeOptions().InstructionBudget);
    }

    [Fact]
    public void The_fallback_run_budget_is_five_minutes()
    {
        // Only reachable when a caller passes DateTimeOffset.MaxValue; production always passes the
        // run's deadline, so this ceiling exists so a forgotten deadline cannot hang a worker.
        Assert.Equal(TimeSpan.FromMinutes(5), LuaRuntimeOptions.DefaultRunBudget);
        Assert.Equal(TimeSpan.FromMinutes(5), new LuaRuntimeOptions().RunBudget);
    }

    [Fact]
    public void A_deadline_in_the_past_abandons_before_the_script_runs()
    {
        var sink = new LuaLogSink();
        var runtime = new LuaRuntime(sink);

        var result = runtime.Execute(
            "print('must not run')",
            LuaSandboxProbe.ChunkName,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            CancellationToken.None);

        Assert.Equal(LuaExecutionOutcome.TimedOut, result.Outcome);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void The_fallback_budget_covers_a_call_with_no_deadline()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        var result = runtime.Execute(
            "return 7",
            LuaSandboxProbe.ChunkName,
            DateTimeOffset.MaxValue,
            CancellationToken.None);

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("7", result.Value);
    }

    [Fact]
    public void A_non_positive_instruction_budget_is_refused_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LuaRuntime(new LuaLogSink(), new LuaRuntimeOptions { InstructionBudget = 0 }));
    }
}
