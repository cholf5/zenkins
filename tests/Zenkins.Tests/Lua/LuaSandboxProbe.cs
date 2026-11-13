using MoonSharp.Interpreter;
using Xunit;
using Zenkins.Worker.Lua;

namespace Zenkins.Tests.Lua;

/// <summary>
/// Fixture helpers for card F-011: every assertion goes through the real <see cref="LuaRuntime"/>,
/// never through a hand-built <c>Script</c>, so the sandbox fixtures cannot drift from the
/// implementation (<c>docs/lua-runtime.md</c> §3.2).
/// <para>
/// The sandbox is probed by executing <c>return type(&lt;name&gt;)</c> as a script - the view a
/// pipeline actually gets. That is deliberate: nilling a global with <c>DynValue.Nil</c> leaves a
/// key that the C# <c>Globals</c> enumeration still yields (33 keys vs 29 visible to Lua, measured),
/// so a C#-side assertion can pass or fail for the wrong reason.
/// </para>
/// </summary>
internal static class LuaSandboxProbe
{
    /// <summary>The chunk name used by the contract's fixtures.</summary>
    internal const string ChunkName = "pipeline.lua";

    private static readonly TimeSpan FixtureBudget = TimeSpan.FromSeconds(30);

    /// <summary>The deny list of <c>docs/lua-runtime.md</c> §3.2 / fixture 11 (17 names).</summary>
    public static TheoryData<string> DeniedNames { get; } = new()
    {
        "io",
        "load",
        "loadfile",
        "loadstring",
        "dofile",
        "require",
        "debug",
        "dynamic",
        "json",
        "package",
        "_MOONSHARP",
        "os.execute",
        "os.exit",
        "os.getenv",
        "os.remove",
        "os.rename",
        "os.tmpname",
    };

    /// <summary>The functions a pipeline is allowed to call (§3.2, kept set).</summary>
    public static TheoryData<string> KeptFunctions { get; } = new()
    {
        "assert",
        "error",
        "pcall",
        "xpcall",
        "setmetatable",
        "getmetatable",
        "ipairs",
        "pairs",
        "next",
        "select",
        "type",
        "tostring",
        "tonumber",
        "unpack",
        "pack",
        "rawequal",
        "rawget",
        "rawlen",
        "rawset",
        "collectgarbage",
        "print",
        "os.clock",
        "os.date",
        "os.difftime",
        "os.time",
    };

    /// <summary>The tables a pipeline is allowed to use (§3.2, kept set).</summary>
    public static TheoryData<string> KeptTables { get; } = new()
    {
        "_G",
        "string",
        "table",
        "math",
        "bit32",
        "coroutine",
        "os",
    };

    /// <summary>Builds the production runtime; <paramref name="additionalModules"/> is the fixture seam.</summary>
    internal static LuaRuntime CreateRuntime(
        LuaLogSink? logSink = null,
        CoreModules additionalModules = CoreModules.None) =>
        new(logSink ?? new LuaLogSink(), new LuaRuntimeOptions { AdditionalModules = additionalModules });

    /// <summary>Runs <paramref name="source"/> as <c>pipeline.lua</c> with a fixture-sized budget.</summary>
    internal static LuaExecutionResult Run(
        LuaRuntime runtime,
        string source,
        TimeSpan? budget = null,
        string chunkName = ChunkName) =>
        runtime.Execute(
            source,
            chunkName,
            DateTimeOffset.UtcNow + (budget ?? FixtureBudget),
            CancellationToken.None);

    /// <summary><c>return type(&lt;name&gt;)</c> as the script sees it.</summary>
    internal static string? ScriptType(LuaRuntime runtime, string name) =>
        Run(runtime, "return type(" + name + ")").Value;

    /// <summary>
    /// Asserts one name from the deny list is unreachable. Shared by the theory and by any test that
    /// wants the whole list in one place.
    /// </summary>
    internal static void AssertDenied(LuaRuntime runtime, string name) =>
        Assert.Equal("nil", ScriptType(runtime, name));
}
