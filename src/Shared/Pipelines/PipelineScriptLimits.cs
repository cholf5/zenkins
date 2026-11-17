namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The one bound an inline Pipeline script shares between the Server and the Worker (card F-022). The
/// Server refuses a longer body when the project is saved, and the Worker re-checks a claimed script
/// before it writes anything, so neither side can hand the Lua compiler an unbounded string.
/// </summary>
public static class PipelineScriptLimits
{
    /// <summary>The most bytes one inline pipeline script may carry (64 KiB).</summary>
    public const int MaxScriptBytes = 64 * 1024;

    /// <summary>
    /// The synthetic file an inline script is materialised as. It is deliberately the PRD's own default
    /// name: the definition phase's diagnostic is <c>pipeline.lua:(line,col)</c>, exactly the one a
    /// repository pipeline produces, and the frozen spec's <c>PipelinePath</c> names the same file.
    /// </summary>
    public const string InlineFileName = "pipeline.lua";
}
