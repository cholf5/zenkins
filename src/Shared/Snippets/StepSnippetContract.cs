using Zenkins.Shared.Pipelines;

namespace Zenkins.Shared.Snippets;

/// <summary>
/// The closed set of snippet kinds (card F-035). A snippet is <b>text</b>, never a run-time dependency: the
/// kind only says which editor it is offered in and which bound its body must respect.
/// <para>
/// <b>`script-step` feeds a Freestyle step's script.</b> Inserting one appends a fully formed step row
/// (name = the snippet's name, script = its body) to the steps editor; the normal save then freezes it into
/// the project's spec.
/// </para>
/// <para>
/// <b>`lua-fragment` feeds a pipeline's Lua text</b> - the inline script editor, or the user's own editor for
/// a repository pipeline. The product copies the text; it never rewrites a repository file.
/// </para>
/// </summary>
public static class StepSnippetKinds
{
    /// <summary>A Freestyle step body: inserted as a step row's script.</summary>
    public const string ScriptStep = "script-step";

    /// <summary>A piece of `pipeline.lua` text: inserted into a pipeline script, never executed on its own.</summary>
    public const string LuaFragment = "lua-fragment";

    /// <summary>Every kind a snippet may be, in the order the form offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [ScriptStep, LuaFragment];

    /// <summary>Whether <paramref name="kind"/> is one the product knows.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary>The kind as a person reads it (a label, never a stored value).</summary>
    public static string LabelFor(string kind) => kind switch
    {
        ScriptStep => "Script step (Freestyle)",
        LuaFragment => "Lua fragment (pipeline)",
        _ => kind,
    };

    /// <summary>
    /// The bound a snippet body shares with the text it feeds (card F-035): a script step is bounded like a
    /// Freestyle script (<see cref="PipelineSpecSchema.MaxScriptChars"/>), a Lua fragment like an inline
    /// pipeline script (<see cref="PipelineScriptLimits.MaxScriptBytes"/>). One number per kind, so a snippet
    /// cannot hold text the target would refuse.
    /// </summary>
    public static int MaxBodyChars(string kind) => kind == LuaFragment
        ? PipelineScriptLimits.MaxScriptBytes
        : PipelineSpecSchema.MaxScriptChars;
}

/// <summary>The bounds of a snippet's own fields (card F-035).</summary>
public static class StepSnippetLimits
{
    /// <summary>An upper bound on a snippet name (a list label, not a path).</summary>
    public const int MaxNameLength = 100;

    /// <summary>An upper bound on the optional description.</summary>
    public const int MaxDescriptionLength = 500;
}
