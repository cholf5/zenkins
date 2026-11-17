namespace Zenkins.Shared.Projects;

/// <summary>
/// Where a <see cref="ProjectKinds.Pipeline"/> project's definition comes from (card F-022). The
/// <b>kind</b> decides what a run executes; the <b>source</b> decides where the definition text lives.
/// A source is deliberately <b>not</b> a third kind: the kind is immutable and every guard (webhook,
/// credential, artifact paths) keys off "this project has a repository", so adding a kind would
/// multiply each guard instead of adding one predicate.
/// <para>
/// <b>Repository</b> is the original source: the project names a Git repository, the Worker clones it
/// and the definition phase reads <c>pipeline.lua</c> from the checkout. <b>Inline</b> keeps the script
/// on the Server (typed in the UI): the Worker skips the clone, writes the snapshotted text to the
/// run's <c>temp/pipeline.lua</c> and runs the same definition phase against it - so the
/// <c>pipeline.lua:(line,col)</c> diagnostic and the frozen spec stay identical.
/// </para>
/// <para>
/// Only a Pipeline project carries a meaningful source. A Freestyle project's steps are configured in
/// the UI and frozen as a spec; its column keeps the historical default and is never read.
/// </para>
/// </summary>
public static class ProjectSources
{
    /// <summary>The original source: a Git repository holds <c>pipeline.lua</c> (PRD §3.2).</summary>
    public const string Repository = "repository";

    /// <summary>The script lives on the Server; the run snapshots it and the Worker runs the definition phase.</summary>
    public const string Inline = "inline";

    /// <summary>Every source the data layer accepts; anything else is refused by the check constraint.</summary>
    public static readonly string[] All = [Repository, Inline];

    /// <summary>Whether <paramref name="source"/> is one of the known sources.</summary>
    public static bool IsValid(string? source) =>
        source is not null && Array.IndexOf(All, source) >= 0;

    /// <summary>
    /// Whether a project of <paramref name="kind"/> with <paramref name="source"/> has a repository to
    /// clone, push to or materialise a credential for. <b>One predicate, every guard:</b> the webhook
    /// surface, the Git-credential surface and the Worker's clone path all ask this question, so an
    /// inline Pipeline and a Freestyle project answer it the same way without either guard learning a
    /// second condition.
    /// </summary>
    /// <param name="kind">A <see cref="ProjectKinds"/> value.</param>
    /// <param name="source">A <see cref="ProjectSources"/> value; null is treated as the historical default.</param>
    public static bool HasRepository(string? kind, string? source) =>
        string.Equals(kind, ProjectKinds.Pipeline, StringComparison.Ordinal)
        && !string.Equals(source, Inline, StringComparison.Ordinal);
}
