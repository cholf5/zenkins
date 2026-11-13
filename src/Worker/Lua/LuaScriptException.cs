using System.Text.RegularExpressions;

namespace Zenkins.Worker.Lua;

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

    private LuaScriptException(string message, string sourceName, int line, int column)
        : base(message)
    {
        SourceName = sourceName;
        Line = line;
        Column = column;
    }

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
    internal static LuaScriptException FromDecoratedMessage(string? decoratedMessage, string fallbackSourceName)
    {
        var line = FirstLine(decoratedMessage);
        if (line.Length == 0)
        {
            return new LuaScriptException($"{fallbackSourceName}: script failed", fallbackSourceName, 0, 0);
        }

        var match = PositionPattern.Match(line);
        if (!match.Success)
        {
            return new LuaScriptException(line, fallbackSourceName, 0, 0);
        }

        return new LuaScriptException(
            line,
            match.Groups["source"].Value,
            int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

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
