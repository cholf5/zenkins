using MoonSharp.Interpreter;
using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// TC-002 (card F-011): the registry hazard of <c>docs/lua-runtime.md</c> §3.1 is handled where it can
/// occur. Nil-ing the global is not enough - <c>package.loaded</c> keeps the module table alive - so
/// the runtime nils <c>package</c> too. The first test records the hazard itself (fixture 12a) so the
/// gate cannot pass by accident; the others assert the production shape (fixture 12b).
/// </summary>
[Trait("Category", "Lua")]
public sealed class LuaRegistryEscapeTests
{
    // The contract composition, duplicated here only for the "what the hazard looks like" fixture:
    // that test uses a raw engine precisely because it must not go through LuaRuntime's hardening.
    private const CoreModules ContractModules =
        CoreModules.Basic | CoreModules.ErrorHandling | CoreModules.Metatables
        | CoreModules.Coroutine | CoreModules.String | CoreModules.Table
        | CoreModules.TableIterators | CoreModules.Math | CoreModules.Bit32
        | CoreModules.OS_Time | CoreModules.GlobalConsts;

    [Fact]
    public void The_registry_hazard_is_real_without_the_package_hardening()
    {
        // Fixture 12a, recorded - not the assertion. A raw engine that composes Dynamic keeps the
        // module table in package.loaded, so nil-ing the global is theatre: this returns 2.
        var script = new Script(ContractModules | CoreModules.Dynamic);
        script.Globals["dynamic"] = DynValue.Nil;

        var escaped = script.DoString(
            "dynamic = nil return package.loaded.dynamic.eval('1+1')",
            null,
            LuaSandboxProbe.ChunkName);

        Assert.Equal("2", escaped.ToPrintString());
    }

    [Fact]
    public void A_runtime_that_composes_dynamic_on_purpose_is_still_hardened()
    {
        // The gate: same hazardous composition, this time built by the production runtime, so the
        // post-construction hardening (§3.1) is applied exactly as production applies it.
        var runtime = LuaSandboxProbe.CreateRuntime(additionalModules: CoreModules.Dynamic);

        Assert.Equal("nil", LuaSandboxProbe.ScriptType(runtime, "package"));

        var result = LuaSandboxProbe.Run(
            runtime,
            "local ok, e = pcall(function() return package.loaded.dynamic end) "
            + "return tostring(ok) .. ' | ' .. tostring(e)");

        Assert.True(result.IsCompleted, $"Expected a completed script but got {result.Outcome}.");
        Assert.StartsWith("false | ", result.Value, StringComparison.Ordinal);
        Assert.Contains("attempt to index a nil value", result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_production_composition_exposes_no_registry_at_all()
    {
        var runtime = LuaSandboxProbe.CreateRuntime();

        Assert.Equal("nil", LuaSandboxProbe.ScriptType(runtime, "package"));
        Assert.Equal("nil", LuaSandboxProbe.ScriptType(runtime, "dynamic"));
        Assert.Equal("nil", LuaSandboxProbe.ScriptType(runtime, "json"));

        var probe = LuaSandboxProbe.Run(
            runtime,
            "return tostring(pcall(function() return package.loaded end))");

        Assert.Equal("false", probe.Value);
    }
}
