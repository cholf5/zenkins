namespace Zenkins.Shared.Pipelines;

/// <summary>
/// What a definition-only task describes (cards F-022 and F-023): an inline script the Pipelines tab
/// asked about, or a branch of a repository whose tree a discovery pass wants to read.
/// <para>
/// <b>One task family, two subjects.</b> Both are "look at a definition without creating a run": no
/// <c>BuildRun</c>, no build slot, no history. Reusing the family keeps one claim/report lease, one
/// bounded wait and one Worker loop instead of a second, parallel primitive.
/// </para>
/// <para>
/// <b>Absent means <see cref="InlineScript"/>.</b> A Worker built before F-023 receives a claim whose
/// server never sent a kind, and must read it as the F-022 script validation it always was - which is
/// why the wire field is optional on both sides.
/// </para>
/// </summary>
public static class DefinitionValidationKinds
{
    /// <summary>Describe a Lua body the server stores (F-022's Validate action).</summary>
    public const string InlineScript = "inline-script";

    /// <summary>
    /// Read one repository branch's tree at one commit and answer whether the project's pipeline file
    /// is there (F-023's branch observation). Never queues by itself.
    /// </summary>
    public const string RepositoryBranch = "repository-branch";

    /// <summary>Every kind a claim can carry.</summary>
    public static readonly string[] All = [InlineScript, RepositoryBranch];

    /// <summary>Whether <paramref name="kind"/> is one of the known kinds; null/blank is the F-022 default.</summary>
    public static bool IsKnown(string? kind) =>
        kind is null || kind.Length == 0 || Array.IndexOf(All, kind) >= 0;

    /// <summary>The kind as a Worker must read it: a known value, or <see cref="InlineScript"/> for absent.</summary>
    public static string Normalize(string? kind) =>
        string.Equals(kind, RepositoryBranch, StringComparison.Ordinal) ? RepositoryBranch : InlineScript;
}

/// <summary>
/// The words a definition-only task can settle with. <b>Valid</b>/<b>Invalid</b> belong to an inline
/// script (F-022). <b>HasPipeline</b>/<b>NoPipeline</b> belong to a repository branch (F-023) - the
/// worker looked at the tree and says whether the project's pipeline file is there.
/// <para>
/// <b>"No pipeline" is an observation, never an inference.</b> Only a worker that checked out the
/// commit may report it; a failed clone is <b>not</b> one of these words, so a network error can never
/// be stored as "this branch has no pipeline".
/// </para>
/// </summary>
public static class DefinitionValidationOutcomes
{
    /// <summary>The definition phase produced a valid spec. The report carries it.</summary>
    public const string Valid = "valid";

    /// <summary>The definition phase refused the script. The report carries the diagnostic.</summary>
    public const string Invalid = "invalid";

    /// <summary>The branch's checkout at the observed commit contains the project's pipeline file.</summary>
    public const string HasPipeline = "has-pipeline";

    /// <summary>The branch's checkout at the observed commit does not contain the pipeline file.</summary>
    public const string NoPipeline = "no-pipeline";

    /// <summary>
    /// The worker could not read the tree at all (the clone or the checkout failed). <b>Not a
    /// verdict</b>: the branch keeps its previous state and the observation is retried, because a
    /// network failure must never be stored as "this branch has no pipeline".
    /// </summary>
    public const string Unreadable = "unreadable";

    /// <summary>Every outcome a worker may report.</summary>
    public static readonly string[] All = [Valid, Invalid, HasPipeline, NoPipeline, Unreadable];

    /// <summary>Whether <paramref name="outcome"/> is one of the known outcomes.</summary>
    public static bool IsKnown(string? outcome) =>
        outcome is not null && Array.IndexOf(All, outcome) >= 0;

    /// <summary>The outcomes an inline-script task may report.</summary>
    public static bool IsInlineOutcome(string? outcome) =>
        string.Equals(outcome, Valid, StringComparison.Ordinal)
        || string.Equals(outcome, Invalid, StringComparison.Ordinal);

    /// <summary>
    /// The outcomes that are a <b>verdict</b> about a branch - the only ones that may write a state.
    /// <see cref="Unreadable"/> is deliberately not here.
    /// </summary>
    public static bool IsRepositoryOutcome(string? outcome) =>
        string.Equals(outcome, HasPipeline, StringComparison.Ordinal)
        || string.Equals(outcome, NoPipeline, StringComparison.Ordinal);

    /// <summary>Every outcome a repository-branch task may report (a verdict or <see cref="Unreadable"/>).</summary>
    public static bool IsRepositoryReportable(string? outcome) =>
        IsRepositoryOutcome(outcome) || string.Equals(outcome, Unreadable, StringComparison.Ordinal);
}

/// <summary>
/// One definition-only task handed to a Worker (cards F-022 and F-023): the id to report against, the
/// lease, and the subject.
/// <para>
/// For <see cref="DefinitionValidationKinds.InlineScript"/> only <see cref="Script"/> is set; for
/// <see cref="DefinitionValidationKinds.RepositoryBranch"/> the repository snapshot is set and
/// <see cref="Script"/> is null. The credential never rides here - the Worker fetches it under this
/// task's lease, exactly as the run and probe paths do.
/// </para>
/// </summary>
public sealed record DefinitionValidationClaim(
    Guid ValidationId,
    string? Script,
    DateTimeOffset LeaseExpiresAt,
    string Kind = DefinitionValidationKinds.InlineScript,
    string? RepositoryUrl = null,
    string? Branch = null,
    string? Commit = null,
    string? PipelinePath = null);

/// <summary>
/// The bounds a definition-only report is held to (cards F-022 and F-023).
/// </summary>
public static class DefinitionValidationRules
{
    /// <summary>The longest diagnostic a report may carry (the page renders it in one notice).</summary>
    public const int MaxDiagnosticLength = 2000;

    /// <summary>The longest branch name an observation may target (the run column's bound).</summary>
    public const int MaxBranchLength = 200;
}
