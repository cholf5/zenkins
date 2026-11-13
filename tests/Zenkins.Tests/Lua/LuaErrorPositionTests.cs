using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// TC-004 part 2 (card F-011): script failures surface <c>source:(line,col-col)</c> in one line, with
/// no CLR stack trace, and the log lines emitted before the failure survive
/// (<c>docs/lua-runtime.md</c> §6.2, §7).
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaErrorPositionTests
{
    [Fact]
    public void A_runtime_error_reports_the_source_position_in_one_line()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        var exception = Assert.Throws<LuaScriptException>(
            () => LuaSandboxProbe.Run(runtime, "local x = 1\n\nboom()"));

        Assert.StartsWith("pipeline.lua:(3,", exception.Message, StringComparison.Ordinal);
        Assert.Contains("attempt to call a nil value", exception.Message, StringComparison.Ordinal);
        Assert.Equal(LuaSandboxProbe.ChunkName, exception.SourceName);
        Assert.Equal(3, exception.Line);
        Assert.True(exception.Column > 0, $"Expected a column but got {exception.Column}.");
        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MoonSharp", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Zenkins", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_syntax_error_reports_the_source_position_in_one_line()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        var exception = Assert.Throws<LuaScriptException>(
            () => LuaSandboxProbe.Run(runtime, "local x = = 1"));

        Assert.StartsWith("pipeline.lua:(1,10-11): unexpected symbol near '='", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, exception.Line);
        Assert.Equal(10, exception.Column);
        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chunk_name_that_contains_a_colon_keeps_its_whole_position_prefix()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        var exception = Assert.Throws<LuaScriptException>(
            () => LuaSandboxProbe.Run(runtime, "boom()", chunkName: "C:/work/run-1/pipeline.lua"));

        Assert.Equal("C:/work/run-1/pipeline.lua", exception.SourceName);
        Assert.StartsWith("C:/work/run-1/pipeline.lua:(1,", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, exception.Line);
    }

    [Fact]
    public void Log_lines_emitted_before_a_failure_are_kept()
    {
        var sink = new LuaLogSink();
        var runtime = new LuaRuntime(sink);

        Assert.Throws<LuaScriptException>(
            () => LuaSandboxProbe.Run(runtime, "print('one')\nprint('two')\nboom()"));

        Assert.Equal(new[] { "one", "two" }, sink.Lines);
    }
}
