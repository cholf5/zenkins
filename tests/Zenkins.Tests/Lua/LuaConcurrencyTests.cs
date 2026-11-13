using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// Thread affinity (card F-011, <c>docs/lua-runtime.md</c> §5.1): one <c>Script</c> per execution,
/// one owning thread, never shared. Two runs on two threads must not see each other's sandbox state -
/// the engine itself does not protect against concurrent reuse (fixture 19: cross-thread access is
/// silently accepted and concurrent reuse corrupts the interpreter).
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaConcurrencyTests
{
    private const string ComputeScript =
        "local n = 0 for i = 1, 300000 do n = n + i % 13 end return tostring(n)";

    private const string ComputeResult = "1800006";

    [Fact]
    public async Task Two_runtimes_on_two_threads_run_concurrently()
    {
        var first = Task.Run(() => RunOnItsOwnThread(ComputeScript));
        var second = Task.Run(() => RunOnItsOwnThread(ComputeScript));

        var runs = await Task.WhenAll(first, second);

        Assert.All(runs, run => Assert.Equal(LuaExecutionOutcome.Completed, run.Result.Outcome));
        Assert.All(runs, run => Assert.Equal(ComputeResult, run.Result.Value));
        Assert.All(runs, run => Assert.True(run.ThreadId > 0));
    }

    [Fact]
    public async Task Two_runtimes_can_be_suspended_at_the_same_time()
    {
        // A barrier in the answer callback only passes when both runs are suspended at once, which is
        // the state the driver's single-thread discipline has to survive across runtimes.
        using var bothSuspended = new Barrier(2);

        var first = Task.Run(() => RunSuspending(bothSuspended));
        var second = Task.Run(() => RunSuspending(bothSuspended));

        var runs = await Task.WhenAll(first, second);

        Assert.All(runs, run => Assert.Equal(LuaExecutionOutcome.Completed, run.Result.Outcome));
        Assert.All(runs, run => Assert.Equal("done:ok", run.Result.Value));

        static (LuaExecutionResult Result, int ThreadId) RunSuspending(Barrier barrier)
        {
            var runtime = LuaSandboxProbe.CreateRuntime();
            runtime.RegisterHostFunction(
                "wait",
                _ => LuaHostResult.Suspend(() =>
                {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    return "ok";
                }));

            var result = LuaSandboxProbe.Run(runtime, "local a = wait() return 'done:' .. a");
            return (result, Environment.CurrentManagedThreadId);
        }
    }

    private static (LuaExecutionResult Result, int ThreadId) RunOnItsOwnThread(string source)
    {
        var runtime = LuaSandboxProbe.CreateRuntime();
        var result = LuaSandboxProbe.Run(runtime, source);
        return (result, Environment.CurrentManagedThreadId);
    }
}
