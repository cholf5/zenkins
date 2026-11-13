using MoonSharp.Interpreter;
using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// TC-001 (card F-011): the sandbox deny list of <c>docs/lua-runtime.md</c> §3.2, asserted through
/// the real runtime as <c>return type(&lt;name&gt;)</c> - one assertion per name, never a loop.
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaSandboxTests
{
    [Theory]
    [MemberData(nameof(LuaSandboxProbe.DeniedNames), MemberType = typeof(LuaSandboxProbe))]
    public void A_denied_name_is_nil_as_a_script_sees_it(string name)
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        LuaSandboxProbe.AssertDenied(runtime, name);
    }

    [Theory]
    [MemberData(nameof(LuaSandboxProbe.KeptFunctions), MemberType = typeof(LuaSandboxProbe))]
    public void A_kept_function_is_a_script_visible_function(string name)
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        Assert.Equal("function", LuaSandboxProbe.ScriptType(runtime, name));
    }

    [Theory]
    [MemberData(nameof(LuaSandboxProbe.KeptTables), MemberType = typeof(LuaSandboxProbe))]
    public void A_kept_library_is_a_script_visible_table(string name)
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        Assert.Equal("table", LuaSandboxProbe.ScriptType(runtime, name));
    }

    [Fact]
    public void Version_is_a_string_and_the_engine_banner_is_gone()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        Assert.Equal("string", LuaSandboxProbe.ScriptType(runtime, "_VERSION"));
        Assert.Equal("nil", LuaSandboxProbe.ScriptType(runtime, "_MOONSHARP"));
    }

    [Fact]
    public void The_runtime_registers_no_clr_type()
    {
        // The engine ships four built-in user-data types of its own (EventFacade, AnonWrapper, ...).
        // What the contract forbids is *adding* one: no UserData.RegisterType/RegisterAssembly and no
        // proxy types, so the set is exactly the same after the sandbox ran a script (§3.2).
        var before = UserData.GetRegisteredTypes(true).ToArray();
        Assert.DoesNotContain(before, type => type.Assembly == typeof(LuaRuntime).Assembly);

        var runtime = LuaSandboxProbe.CreateRuntime();
        Assert.Equal("table", LuaSandboxProbe.ScriptType(runtime, "_G"));
        Assert.Equal("hello", LuaSandboxProbe.Run(runtime, "return ('he' .. 'llo')").Value);

        var after = UserData.GetRegisteredTypes(true).ToArray();
        Assert.Equal(before.Length, after.Length);
        Assert.DoesNotContain(after, type => type.Assembly == typeof(LuaRuntime).Assembly);
    }
}
