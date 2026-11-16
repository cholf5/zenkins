namespace Zenkins.Shared.Projects;

/// <summary>
/// The two kinds of Project (card F-020). The kind decides what a run of the project executes and where
/// its definition comes from - it is data on the row, not a type hierarchy, so a run's snapshot (the
/// frozen spec) stays the single execution contract either way.
/// <para>
/// <b>Pipeline</b> is the original kind (PRD §3.2): the project names a Git repository, the Worker clones
/// it and the definition phase reads <c>pipeline.lua</c> from the checkout. <b>Freestyle</b> is the
/// Jenkins-style kind: no repository is required - the steps are configured in the Server UI, frozen into
/// a spec on the Project row, and that spec travels with every run. Both kinds share the frozen
/// <see cref="Zenkins.Shared.Pipelines.PipelineSpec"/> shape, the step executor and the run lifecycle;
/// only the clone/definition phase is skipped for Freestyle.
/// </para>
/// </summary>
public static class ProjectKinds
{
    /// <summary>A Git-repository project whose <c>pipeline.lua</c> declares the steps (the original kind).</summary>
    public const string Pipeline = "pipeline";

    /// <summary>A repository-free project whose steps are configured in the UI and frozen on the row (F-020).</summary>
    public const string Freestyle = "freestyle";

    /// <summary>Every kind the data layer accepts; anything else is refused by the check constraint.</summary>
    public static readonly string[] All = [Pipeline, Freestyle];

    /// <summary>Whether <paramref name="kind"/> is one of the known kinds.</summary>
    public static bool IsValid(string? kind) =>
        kind is not null && Array.IndexOf(All, kind) >= 0;
}
