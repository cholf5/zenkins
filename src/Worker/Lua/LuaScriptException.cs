using System.Text.RegularExpressions;

namespace Zenkins.Worker.Lua;

/// <summary>Which phase of one execution raised (card F-014).</summary>
public enum LuaScriptErrorKind
{
    /// <summary>The chunk did not compile - <c>pipeline.lua:(1,10-11): unexpected symbol near '='</c>.</summary>
    Syntax = 0,

    /// <summary>The chunk compiled and raised at runtime (including a refused I/O primitive).</summary>
    Runtime = 1,

    /// <summary>The host refused the value the script built (mixed-key or too deeply nested table).</summary>
    Host = 2,
}

/// <summary>
/// A script that did not compile or raised at runtime (card F-011, <c>docs/lua-runtime.md</c> §6.2).
/// <see cref="Exception.Message"/> is exactly one line - <c>pipeline.lua:(3,4-6): attempt to call a
/// nil value</c> - because it is rendered from the engine's decorated message and never from a CLR
/// stack trace; the run log shows it verbatim. When the engine gives no position the message is the
/// engine's first line and <see cref="Line"/>/<see cref="Column"/> are zero.
/// <para>
/// This is the one exception type <see cref="ILuaRuntime.Execute"/> throws for script failures;
/// anything else escaping it is a host bug, not a script error.
/// </para>
/// </summary>
public sealed class LuaScriptException : Exception
{
    // MoonSharp renders "source:(line,col-endcol): message". The source part is greedy: a chunk name
    // may itself contain a colon (a Windows path, a URL), so the position is matched from the right.
    private static readonly Regex PositionPattern = new(
        @"^(?<source>.+):\((?<line>\d+),(?<column>\d+)-(?<end>\d+)\):\s?(?<message>.*)$",
        RegexOptions.CultureInvariant);

    private LuaScriptException(
        string message,
        string sourceName,
        int line,
        int column,
        LuaScriptErrorKind kind = LuaScriptErrorKind.Runtime)
        : base(message)
    {
        SourceName = sourceName;
        Line = line;
        Column = column;
        Kind = kind;
    }

    /// <summary>Which phase failed (card F-014: a compile failure is <c>pipeline-syntax</c>, a raising
    /// script is <c>pipeline-invalid</c>).</summary>
    public LuaScriptErrorKind Kind { get; }

    /// <summary>The chunk name the script ran as (<c>pipeline.lua</c>).</summary>
    public string SourceName { get; }

    /// <summary>The 1-based line the engine blamed; 0 when the message carried no position.</summary>
    public int Line { get; }

    /// <summary>The 1-based column the engine blamed; 0 when the message carried no position.</summary>
    public int Column { get; }

    /// <summary>
    /// Builds the exception from the engine's decorated message (<c>ScriptRuntimeException</c> /
    /// <c>SyntaxErrorException</c> both produce one). Only the first line is kept: a multi-frame
    /// engine message must not smuggle a traceback into the run log (§6.2).
    /// </summary>
    internal static LuaScriptException FromDecoratedMessage(
        string? decoratedMessage,
        string fallbackSourceName,
        LuaScriptErrorKind kind = LuaScriptErrorKind.Runtime)
    {
        var line = FirstLine(decoratedMessage);
        if (line.Length == 0)
        {
            return new LuaScriptException($"{fallbackSourceName}: script failed", fallbackSourceName, 0, 0, kind);
        }

        var match = PositionPattern.Match(line);
        if (!match.Success)
        {
            return new LuaScriptException(line, fallbackSourceName, 0, 0, kind);
        }

        return new LuaScriptException(
            line,
            match.Groups["source"].Value,
            int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture),
            kind);
    }

    /// <summary>
    /// A host-detected violation of the seam's structural rules (card F-014): a result table that
    /// mixes array and map keys, or one that nests deeper than the host is willing to walk. It is
    /// reported as a script failure because it is the script that built the unbeatable value, and the
    /// message is engine-free (no position - there is no raising frame).
    /// </summary>
    internal static LuaScriptException FromHost(string message, string sourceName = "pipeline.lua") =>
        new(message, sourceName, 0, 0, LuaScriptErrorKind.Host);

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var breakAt = text.IndexOfAny(['\r', '\n']);
        return (breakAt < 0 ? text : text[..breakAt]).Trim();
    }
}
