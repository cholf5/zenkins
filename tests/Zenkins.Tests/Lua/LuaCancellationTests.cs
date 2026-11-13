using System.Diagnostics;
using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// TC-004 part 1 (card F-011): cancellation reuses the run's token (<c>RunCancelSignal</c> in
/// production) and is observed at every yield point, so a script honours it within one instruction
/// budget even if it never calls an SDK function (<c>docs/lua-runtime.md</c> §5.3). The abandon
/// semantics are the timeout's: never resume again, discard the <c>Script</c>.
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaCancellationTests
{
    [Fact]
    public void Cancellation_stops_a_script_that_only_computes()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        var result = runtime.Execute(
            "while true do end",
            LuaSandboxProbe.ChunkName,
            DateTimeOffset.UtcNow.AddSeconds(30),
            cancellation.Token);
        stopwatch.Stop();

        Assert.Equal(LuaExecutionOutcome.Canceled, result.Outcome);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 1_000);
        Assert.Equal("still-works", LuaSandboxProbe.Run(runtime, "return 'still-works'").Value);
    }

    [Fact]
    public void Cancellation_stops_a_script_that_loops_over_a_host_stub()
    {
        var calls = 0;
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction(
            "run_process",
            _ =>
            {
                Interlocked.Increment(ref calls);
                return LuaHostResult.Suspend(() => "exit=0");
            });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = runtime.Execute(
            "while true do run_process('make test') end",
            LuaSandboxProbe.ChunkName,
            DateTimeOffset.UtcNow.AddSeconds(30),
            cancellation.Token);

        Assert.Equal(LuaExecutionOutcome.Canceled, result.Outcome);
        var atCancel = Volatile.Read(ref calls);
        Assert.True(atCancel > 0, "The stub was never called, so cancellation was not observed at a yield point.");

        Thread.Sleep(150);
        Assert.Equal(atCancel, Volatile.Read(ref calls));
    }

    [Fact]
    public void An_already_canceled_token_runs_nothing()
    {
        var sink = new LuaLogSink();
        var runtime = new LuaRuntime(sink);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = runtime.Execute(
            "print('should not run')",
            LuaSandboxProbe.ChunkName,
            DateTimeOffset.UtcNow.AddSeconds(30),
            cancellation.Token);

        Assert.Equal(LuaExecutionOutcome.Canceled, result.Outcome);
        Assert.Equal(0, sink.Count);
    }
}
