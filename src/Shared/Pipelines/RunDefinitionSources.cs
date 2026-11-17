namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The three shapes a run's definition can take on the Worker (card F-022). This is the <b>claim's own
/// word</b>, not something the Worker infers from "is there a frozen spec?": an inline run carries a
/// script and may also carry a spec (a rebuilt or already-reported run), and a Freestyle run carries a
/// spec, so presence alone cannot tell the three apart. The Server writes the word from the run's own
/// snapshot columns; the Worker switches on it and never reads the project row.
/// </summary>
public static class RunDefinitionSources
{
    /// <summary>The definition is the checkout's <c>pipeline.lua</c> (clone, then definition phase).</summary>
    public const string Repository = "repository";

    /// <summary>The definition is the run's frozen spec; no clone and no definition phase (F-020).</summary>
    public const string Freestyle = "freestyle";

    /// <summary>The definition is the script snapshotted on the run; no clone, then definition phase (F-022).</summary>
    public const string Inline = "inline";

    /// <summary>
    /// The word for one run, from its own snapshot columns - the only writer is the Server's trigger
    /// path, and the claim carries the result. A script always wins (an inline run is inline even if it
    /// also has a spec); a frozen spec with no branch is Freestyle; everything else is a repository run.
    /// </summary>
    public static string For(string? pipelineScriptText, string? pipelineSpecJson, string? gitBranch) =>
        pipelineScriptText is not null
            ? Inline
            : pipelineSpecJson is not null && string.IsNullOrEmpty(gitBranch)
                ? Freestyle
                : Repository;
}
