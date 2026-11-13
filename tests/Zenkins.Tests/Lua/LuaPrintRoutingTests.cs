using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// <c>print</c> is re-bound to the run-log sink (card F-011, <c>docs/lua-runtime.md</c> §3.1/§7): the
/// engine's default writes straight to <see cref="Console"/>, where a pipeline's output is invisible
/// in the UI and unredactable.
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaPrintRoutingTests
{
    private static readonly object ConsoleGate = new();

    [Fact]
    public void Print_reaches_the_log_sink()
    {
        var sink = new LuaLogSink();
        var runtime = new LuaRuntime(sink);

        var result = LuaSandboxProbe.Run(runtime, "print('hello', 42, nil, true) return 'ok'");

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        // Lua's print separates its arguments with a tab; 42 is rendered as Lua renders it.
        Assert.Equal(new[] { "hello\t42\tnil\ttrue" }, sink.Lines);
    }

    [Fact]
    public void Forwarded_lines_arrive_in_order()
    {
        var forwarded = new List<string>();
        var runtime = new LuaRuntime(new LuaLogSink(forwarded.Add));

        LuaSandboxProbe.Run(runtime, "print('first') print('second')");

        Assert.Equal(new[] { "first", "second" }, forwarded);
    }

    [Fact]
    public void Retention_is_bounded_while_forwarding_is_not()
    {
        var forwarded = new List<string>();
        var sink = new LuaLogSink(forwarded.Add, retainedLines: 3);
        var runtime = new LuaRuntime(sink);

        LuaSandboxProbe.Run(runtime, "for i = 1, 6 do print('line-' .. i) end");

        // A chatty script cannot grow worker memory line by line: only the tail is kept, but the log
        // pipeline still saw every line.
        Assert.Equal(new[] { "line-4", "line-5", "line-6" }, sink.Lines);
        Assert.Equal(6, forwarded.Count);
    }

    [Fact]
    public void Print_does_not_reach_the_worker_console()
    {
        const string Marker = "f011-console-marker-9f3c1d";

        // Console.SetOut is process-global; serialise the swap so this test cannot race itself. The
        // assertion is only about the marker, so unrelated test output in the writer is harmless.
        lock (ConsoleGate)
        {
            var original = Console.Out;
            using var console = new StringWriter();
            try
            {
                Console.SetOut(console);

                var sink = new LuaLogSink();
                var runtime = new LuaRuntime(sink);
                var result = LuaSandboxProbe.Run(runtime, "print('" + Marker + "') return 'ok'");

                Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
                Assert.Equal(new[] { Marker }, sink.Lines);
                Assert.DoesNotContain(Marker, console.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}
