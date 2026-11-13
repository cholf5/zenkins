using System.Diagnostics;
using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// TC-003 (card F-011): a runaway script is abandoned at its budget, not left running. The wall clock
/// is the host's (<c>docs/lua-runtime.md</c> §5.3) and abandonment means "stop resuming, discard the
/// <c>Script</c>" (§5.1) - so the coroutine must never be resumed again and a later run must get a
/// fresh sandbox.
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaBudgetTests
{
    [Theory]
    [InlineData("while true do end")]
    [InlineData("while true do local t = {} end")]
    public void A_runaway_script_is_abandoned_at_its_budget(string body)
    {
        var runtime = LuaSandboxProbe.CreateRuntime();
        var budget = TimeSpan.FromMilliseconds(200);

        var stopwatch = Stopwatch.StartNew();
        var result = LuaSandboxProbe.Run(runtime, body, budget);
        stopwatch.Stop();

        Assert.Equal(LuaExecutionOutcome.TimedOut, result.Outcome);
        Assert.Null(result.Value);
        // Budget plus a small margin: the initial Resume can run at most InstructionBudget VM
        // instructions before the first budget yield, so the overrun is well under 100 ms.
        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, budget.TotalMilliseconds + 100);

        // A fresh execution on the same runtime works: the abandoned Script was discarded, not reused.
        Assert.Equal("42", LuaSandboxProbe.Run(runtime, "return 42").Value);
    }

    [Fact]
    public void An_abandoned_run_is_never_resumed_again()
    {
        var calls = 0;
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction(
            "tick",
            _ =>
            {
                Interlocked.Increment(ref calls);
                return LuaHostResult.Suspend(() => LuaHostValue.FromString("ok"));
            });

        var result = LuaSandboxProbe.Run(runtime, "while true do tick() end", TimeSpan.FromMilliseconds(150));

        Assert.Equal(LuaExecutionOutcome.TimedOut, result.Outcome);
        var atAbandon = Volatile.Read(ref calls);
        Assert.True(atAbandon > 0, "The script never reached the host primitive, so the test proves nothing.");

        Thread.Sleep(150);
        Assert.Equal(atAbandon, Volatile.Read(ref calls));
    }

    [Fact]
    public void A_script_that_finishes_inside_its_budget_is_completed()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        var result = LuaSandboxProbe.Run(
            runtime,
            "local n = 0 for i = 1, 200000 do n = n + 1 end return tostring(n)",
            TimeSpan.FromSeconds(30));

        // 200 000 VM instructions is ~20 instruction-budget yields: a ForceSuspended resume must be
        // answered with no arguments, and the value must still arrive.
        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("200000", result.Value);
    }
}
