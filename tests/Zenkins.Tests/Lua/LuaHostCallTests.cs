using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// The host-call suspension protocol (card F-011, <c>docs/lua-runtime.md</c> §5.1/§5.2, fixtures
/// 15-16/18): a primitive yields, the driver resumes it with the answer, and that answer crosses Lua
/// frames. A suspension that did not come from a primitive is a script violation and must abandon the
/// run rather than dequeue an empty request queue.
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaHostCallTests
{
    [Fact]
    public void A_host_call_suspends_and_receives_its_answer()
    {
        var sink = new LuaLogSink();
        var runtime = new LuaRuntime(sink);
        var calls = new List<string>();
        runtime.RegisterHostFunction(
            "run_process",
            call =>
            {
                calls.Add(call.FirstArgument ?? string.Empty);
                return LuaHostResult.Suspend(() => "exit=0");
            });

        var result = LuaSandboxProbe.Run(
            runtime,
            "local a = run_process('make build')\n"
            + "print('build -> ' .. tostring(a))\n"
            + "local b = run_process('make test')\n"
            + "print('test -> ' .. tostring(b))\n"
            + "return 'pipeline-ok'");

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("pipeline-ok", result.Value);
        Assert.Equal(new[] { "make build", "make test" }, calls);
        Assert.Equal(new[] { "build -> exit=0", "test -> exit=0" }, sink.Lines);
    }

    [Fact]
    public void A_synchronous_primitive_answers_without_a_suspension()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction("env", call => LuaHostResult.Value("value-of-" + call.FirstArgument));
        runtime.RegisterHostFunction("nothing", _ => LuaHostResult.Value(null));

        var result = LuaSandboxProbe.Run(
            runtime,
            "return tostring(env('NAME')) .. '|' .. tostring(nothing())");

        Assert.Equal("value-of-NAME|nil", result.Value);
    }

    [Theory]
    [InlineData(
        "local function wrap() return run_process('x') end return 'wrapped:' .. tostring(wrap())",
        "wrapped:VAL")]
    [InlineData(
        "local ok, v = pcall(function() return run_process('x') end) "
        + "return 'pcall:' .. tostring(ok) .. ',' .. tostring(v)",
        "pcall:true,VAL")]
    [InlineData(
        "local ok, v = xpcall(function() return run_process('x') end, function(e) return 'E' end) "
        + "return 'xpcall:' .. tostring(ok) .. ',' .. tostring(v)",
        "xpcall:true,VAL")]
    [InlineData(
        "local out = '' for i = 1, 2 do out = out .. run_process('i') .. ';' end return 'for:' .. out",
        "for:VAL;VAL;")]
    [InlineData(
        "local t = setmetatable({}, {__index = function() return run_process('x') end}) "
        + "return 'meta:' .. tostring(t.any)",
        "meta:VAL")]
    public void A_resumed_value_crosses_lua_frames(string source, string expected)
    {
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction("run_process", _ => LuaHostResult.Suspend(() => "VAL"));

        var result = LuaSandboxProbe.Run(runtime, source);

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void A_script_that_yields_on_its_own_is_a_script_violation()
    {
        // Measured shape (§5.1): state=Suspended, res=user-payload, pending=0. The naive driver would
        // call Dequeue() on an empty queue and throw InvalidOperationException: Queue empty. — which
        // is not a script error handler, so this must be an outcome instead of an exception.
        var runtime = LuaSandboxProbe.CreateRuntime();

        var result = LuaSandboxProbe.Run(runtime, "coroutine.yield('user-payload') return 'after'");

        Assert.Equal(LuaExecutionOutcome.ScriptViolation, result.Outcome);
        Assert.Null(result.Value);

        // The runtime is still usable afterwards (fresh Script per execution).
        Assert.Equal("after-all", LuaSandboxProbe.Run(runtime, "return 'after-all'").Value);
    }

    [Fact]
    public void A_script_that_yields_while_no_host_call_is_pending_never_dequeues()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction("run_process", _ => LuaHostResult.Suspend(() => "VAL"));

        // The wrapper yields before calling the primitive: the suspension carries no request.
        var result = LuaSandboxProbe.Run(
            runtime,
            "local function wrap() coroutine.yield() return run_process('x') end return wrap()");

        Assert.Equal(LuaExecutionOutcome.ScriptViolation, result.Outcome);
    }
}
