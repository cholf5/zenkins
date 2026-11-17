namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The two words a definition-only validation can settle with (card F-022). <b>Valid</b> means the
/// script's definition phase produced a spec the Server accepted (the answer also carries the spec's
/// step list); <b>invalid</b> means the phase refused it (the answer carries the diagnostic, including
/// <c>pipeline.lua:(line,col)</c> for a Lua error). Nothing is executed either way.
/// </summary>
public static class DefinitionValidationOutcomes
{
    /// <summary>The definition phase produced a valid spec. The report carries it.</summary>
    public const string Valid = "valid";

    /// <summary>The definition phase refused the script. The report carries the diagnostic.</summary>
    public const string Invalid = "invalid";

    /// <summary>Every outcome a worker may report.</summary>
    public static readonly string[] All = [Valid, Invalid];

    /// <summary>Whether <paramref name="outcome"/> is one of the known outcomes.</summary>
    public static bool IsKnown(string? outcome) =>
        outcome is not null && Array.IndexOf(All, outcome) >= 0;
}

/// <summary>
/// One definition-only validation handed to a Worker (card F-022): the id to report against, the exact
/// script to describe, and until when the lease holds. It is <b>not a run</b>: no <c>BuildRun</c>, no
/// build slot, no work tree of a run.
/// </summary>
public sealed record DefinitionValidationClaim(
    Guid ValidationId,
    string Script,
    DateTimeOffset LeaseExpiresAt);

/// <summary>
/// What a Worker reports for a definition-only validation (card F-022): the outcome word, the frozen
/// spec on success, and the diagnostic on a refusal. The Server re-validates both, so a worker bug
/// cannot store a spec the run envelope would refuse.
/// </summary>
public static class DefinitionValidationRules
{
    /// <summary>The longest diagnostic a report may carry (the page renders it in one notice).</summary>
    public const int MaxDiagnosticLength = 2000;
}
