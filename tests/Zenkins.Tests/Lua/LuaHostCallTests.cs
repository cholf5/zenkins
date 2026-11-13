using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// The host-call suspension protocol (card F-011, <c>docs/lua-runtime.md</c> §5.1/§5.2, fixtures
/// 15-16/18): a primitive yields, the driver resumes it with the answer, and that answer crosses Lua
/// frames with its Lua type intact. A suspension that did not come from a primitive is a script
/// violation and must abandon the run rather than dequeue an empty request queue.
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
                calls.Add(call.FirstArgument is { } first ? first.Text : string.Empty);
                return LuaHostResult.Suspend(() => LuaHostValue.FromString("exit=0"));
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
        runtime.RegisterHostFunction(
            "env",
            call => LuaHostResult.Value(
                LuaHostValue.FromString("value-of-" + (call.FirstArgument is { } first ? first.Text : string.Empty))));
        runtime.RegisterHostFunction("nothing", _ => LuaHostResult.Value(LuaHostValue.Nil));

        var result = LuaSandboxProbe.Run(
            runtime,
            "return tostring(env('NAME')) .. '|' .. tostring(nothing())");

        Assert.Equal("value-of-NAME|nil", result.Value);
    }

    [Fact]
    public void A_suspended_answer_keeps_its_lua_type()
    {
        // The trap this test exists for: a boolean false coerced to text becomes the *string*
        // "false", which is truthy in Lua, so `if flag then` would take the wrong branch; an exit
        // code coerced to text never compares equal to 0. Each answer here must keep its type.
        var answers = new Queue<LuaHostValue>([
            LuaHostValue.FromBoolean(false),
            LuaHostValue.FromNumber(0),
            LuaHostValue.FromString("text"),
            LuaHostValue.Nil,
        ]);
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction("answer", _ => LuaHostResult.Suspend(answers.Dequeue));

        var result = LuaSandboxProbe.Run(
            runtime,
            "local flag = answer('bool')\n"
            + "local exit = answer('number')\n"
            + "local word = answer('string')\n"
            + "local none = answer('nil')\n"
            + "local branch = 'then'\n"
            + "if flag then branch = 'then' else branch = 'else' end\n"
            + "return 'flag=' .. branch\n"
            + "  .. ';flagType=' .. type(flag)\n"
            + "  .. ';exit0=' .. tostring(exit == 0)\n"
            + "  .. ';exitType=' .. type(exit)\n"
            + "  .. ';word=' .. word\n"
            + "  .. ';wordType=' .. type(word)\n"
            + "  .. ';nilType=' .. type(none)");

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(
            "flag=else;flagType=boolean;exit0=true;exitType=number;word=text;wordType=string;nilType=nil",
            result.Value);
    }

    [Fact]
    public void An_immediate_answer_keeps_its_lua_type()
    {
        // The same trap on the non-suspending path: a predicate that returns false must be falsy, and
        // an exit code must be a number that compares equal to 0.
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction(
            "is_windows",
            _ => LuaHostResult.Value(LuaHostValue.FromBoolean(false)));
        runtime.RegisterHostFunction(
            "exit_code",
            _ => LuaHostResult.Value(LuaHostValue.FromNumber(0)));

        var result = LuaSandboxProbe.Run(
            runtime,
            "if is_windows() then return 'windows' end\n"
            + "local code = exit_code()\n"
            + "if code == 0 then return 'posix;exit0=' .. tostring(code == 0) end\n"
            + "return 'unexpected'");

        Assert.Equal("posix;exit0=true", result.Value);
    }

    [Fact]
    public void A_host_call_argument_keeps_its_lua_type()
    {
        var seen = new List<LuaHostValue>();
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction(
            "inspect",
            call =>
            {
                seen.AddRange(call.Arguments);
                return LuaHostResult.Value(LuaHostValue.FromString("ok"));
            });

        var result = LuaSandboxProbe.Run(
            runtime,
            "return inspect('text', 42, 1.5, true, false, nil, {})");

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(
            new[]
            {
                LuaHostValueKind.String,
                LuaHostValueKind.Number,
                LuaHostValueKind.Number,
                LuaHostValueKind.Boolean,
                LuaHostValueKind.Boolean,
                LuaHostValueKind.Nil,
                // A table is not part of the SDK surface: it arrives as nil, never as a string.
                LuaHostValueKind.Nil,
            },
            seen.Select(value => value.Kind));

        Assert.Equal("text", seen[0].Text);
        Assert.Equal(42d, seen[1].Number);
        Assert.Equal(1.5d, seen[2].Number);
        Assert.True(seen[3].Boolean);
        Assert.False(seen[4].Boolean);
        Assert.True(seen[5].IsNil);
        Assert.True(seen[6].IsNil);
        Assert.Equal("ok", result.Value);
    }

    [Fact]
    public void A_kind_mismatch_fails_loudly_instead_of_returning_a_default()
    {
        // A primitive that guesses the kind must not read a silent 0 / null / false / empty table.
        var boolean = LuaHostValue.FromBoolean(false);

        Assert.Throws<InvalidOperationException>(() => boolean.Number);
        Assert.Throws<InvalidOperationException>(() => boolean.Text);
        Assert.Throws<InvalidOperationException>(() => LuaHostValue.FromNumber(0).Boolean);
        Assert.Throws<InvalidOperationException>(() => LuaHostValue.Nil.Text);

        var table = LuaHostValue.FromTable(LuaHostTable.Sequence(LuaHostValue.FromNumber(1)));
        Assert.Equal(LuaHostValueKind.Table, table.Kind);
        Assert.Throws<InvalidOperationException>(() => table.Text);
        Assert.Throws<InvalidOperationException>(() => table.Number);
        Assert.Throws<InvalidOperationException>(() => table.Boolean);
        Assert.Throws<InvalidOperationException>(() => LuaHostValue.FromString("x").Table);
        Assert.Throws<InvalidOperationException>(() => LuaHostValue.Nil.Table);
    }

    [Fact]
    public void A_sequence_answer_is_a_lua_table_with_integer_keys()
    {
        // §5.2's "resume table -> table:table,v" shape, as a sequence: integer keys 1..n, so #v is the
        // length and ipairs walks it in order.
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction(
            "argv",
            _ => LuaHostResult.Suspend(() => LuaHostValue.FromTable(
                LuaHostTable.Sequence(
                    LuaHostValue.FromString("build"),
                    LuaHostValue.FromString("--release"),
                    LuaHostValue.FromNumber(2)))));
        runtime.RegisterHostFunction(
            "empty",
            _ => LuaHostResult.Suspend(() => LuaHostValue.FromTable(LuaHostTable.Sequence())));

        var result = LuaSandboxProbe.Run(
            runtime,
            "local v = argv()\n"
            + "local visited = 0\n"
            + "local joined = ''\n"
            + "for index, item in ipairs(v) do\n"
            + "  visited = visited + 1\n"
            + "  joined = joined .. index .. ':' .. tostring(item) .. ';'\n"
            + "end\n"
            + "return 'type=' .. type(v) .. ';len=' .. #v .. ';first=' .. v[1]\n"
            + "  .. ';third=' .. tostring(v[3]) .. ';zero=' .. tostring(v[0])\n"
            + "  .. ';ipairs=' .. visited .. ';joined=' .. joined\n"
            + "  .. 'emptyLen=' .. #empty()");

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(
            "type=table;len=3;first=build;third=2;zero=nil;ipairs=3;joined=1:build;2:--release;3:2;emptyLen=0",
            result.Value);
    }

    [Fact]
    public void A_mapping_answer_is_a_lua_table_with_string_keys()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction(
            "process_result",
            _ => LuaHostResult.Suspend(() => LuaHostValue.FromTable(
                LuaHostTable.Map(
                    ("branch", LuaHostValue.FromString("main")),
                    ("exit", LuaHostValue.FromNumber(0)),
                    ("label", LuaHostValue.FromString("build"))))));

        var result = LuaSandboxProbe.Run(
            runtime,
            "local v = process_result()\n"
            + "local keys = {}\n"
            + "for key, value in pairs(v) do keys[#keys + 1] = key end\n"
            + "table.sort(keys)\n"
            + "return 'type=' .. type(v) .. ';branch=' .. v.branch\n"
            + "  .. ';index=' .. v['branch'] .. ';exit0=' .. tostring(v.exit == 0)\n"
            + "  .. ';missing=' .. tostring(v.missing)\n"
            + "  .. ';keys=' .. #keys .. ';sorted=' .. table.concat(keys, ',')");

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(
            "type=table;branch=main;index=main;exit0=true;missing=nil;keys=3;sorted=branch,exit,label",
            result.Value);
    }

    [Fact]
    public void A_nested_answer_table_is_readable_one_level_down()
    {
        // Delivered through the immediate (non-suspending) path: the same ToDynValue switch builds it.
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction(
            "pipeline",
            _ => LuaHostResult.Value(LuaHostValue.FromTable(
                LuaHostTable.Map(
                    ("name", LuaHostValue.FromString("build")),
                    ("steps", LuaHostValue.FromTable(
                        LuaHostTable.Sequence(
                            LuaHostValue.FromTable(LuaHostTable.Map(
                                ("run", LuaHostValue.FromString("dotnet build")),
                                ("continue_on_error", LuaHostValue.FromBoolean(false)))),
                            LuaHostValue.FromTable(LuaHostTable.Map(
                                ("run", LuaHostValue.FromString("dotnet test")))))))))));

        var result = LuaSandboxProbe.Run(
            runtime,
            "local v = pipeline()\n"
            + "local first = v.steps[1]\n"
            + "return v.name .. ';steps=' .. #v.steps .. ';step1=' .. first.run\n"
            + "  .. ';coe=' .. tostring(first.continue_on_error)\n"
            + "  .. ';step2=' .. v.steps[2].run .. ';stepType=' .. type(v.steps)");

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(
            "build;steps=2;step1=dotnet build;coe=false;step2=dotnet test;stepType=table",
            result.Value);
    }

    [Fact]
    public void A_table_answer_crosses_a_wrapper_and_pcall_as_a_table()
    {
        // §5.2 measured shape: a resumed table crosses frames and the script sees a table, not its text
        // or a nil.
        var runtime = LuaSandboxProbe.CreateRuntime();
        runtime.RegisterHostFunction(
            "payload",
            _ => LuaHostResult.Suspend(() => LuaHostValue.FromTable(
                LuaHostTable.Map(
                    ("kind", LuaHostValue.FromString("table")),
                    ("n", LuaHostValue.FromNumber(7))))));

        var result = LuaSandboxProbe.Run(
            runtime,
            "local function wrap() return payload() end\n"
            + "local direct = wrap()\n"
            + "local ok, caught = pcall(function() return payload() end)\n"
            + "return 'directType=' .. type(direct) .. ';directN=' .. direct.n\n"
            + "  .. ';pcallOk=' .. tostring(ok) .. ';pcallType=' .. type(caught)\n"
            + "  .. ';pcallN=' .. caught.n .. ';pcallKind=' .. caught.kind");

        Assert.Equal(LuaExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(
            "directType=table;directN=7;pcallOk=true;pcallType=table;pcallN=7;pcallKind=table",
            result.Value);
    }

    [Fact]
    public void A_table_value_rejects_an_invalid_shape()
    {
        // A duplicate or empty key would silently overwrite/vanish in Lua, so the description refuses
        // it; reading the wrong face of a table throws instead of returning an empty default.
        Assert.Throws<ArgumentException>(() => LuaHostTable.Map(
            ("a", LuaHostValue.Nil),
            ("a", LuaHostValue.Nil)));
        Assert.ThrowsAny<ArgumentException>(() => LuaHostTable.Map((string.Empty, LuaHostValue.Nil)));

        var map = LuaHostTable.Map(("a", LuaHostValue.Nil));
        Assert.False(map.IsSequence);
        Assert.Single(map.Entries);
        Assert.Throws<InvalidOperationException>(() => map.Items);

        var sequence = LuaHostTable.Sequence(LuaHostValue.FromNumber(1));
        Assert.True(sequence.IsSequence);
        Assert.Single(sequence.Items);
        Assert.Throws<InvalidOperationException>(() => sequence.Entries);
        Assert.Throws<InvalidOperationException>(() => sequence.Items[0].Text);
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
        runtime.RegisterHostFunction(
            "run_process",
            _ => LuaHostResult.Suspend(() => LuaHostValue.FromString("VAL")));

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
        runtime.RegisterHostFunction(
            "run_process",
            _ => LuaHostResult.Suspend(() => LuaHostValue.FromString("VAL")));

        // The wrapper yields before calling the primitive: the suspension carries no request.
        var result = LuaSandboxProbe.Run(
            runtime,
            "local function wrap() coroutine.yield() return run_process('x') end return wrap()");

        Assert.Equal(LuaExecutionOutcome.ScriptViolation, result.Outcome);
    }
}
