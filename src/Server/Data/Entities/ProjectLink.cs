using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One "run the downstream after the upstream succeeds" edge between two projects (card F-026).
/// <para>
/// <b>The graph is per organization and must stay acyclic.</b> A link is refused at save time when it would
/// close a cycle (the refusal names the path). That search ignores branch policies: A's <c>main</c> triggering B's
/// <c>main</c>, which triggers A's <c>main</c> again, is the graph cycle and is refused like any other. The runtime
/// hop counter bounds how deep a legal chain may go; it does not make a cycle acceptable.
/// </para>
/// <para>
/// <b>Direction is stored, not inferred.</b> <see cref="UpstreamProjectId"/> is the project whose run finishing
/// causes the trigger; <see cref="DownstreamProjectId"/> is the project that gets the run. The pair is unique, so
/// one edge cannot be declared twice - a second row would mean two runs per upstream completion, which is a
/// different rule nobody asked for.
/// </para>
/// </summary>
public sealed class ProjectLink : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The project whose run's terminal state is the trigger. Cascade-deleted with it.</summary>
    public Guid UpstreamProjectId { get; set; }

    public Project UpstreamProject { get; set; } = null!;

    /// <summary>The project that is queued. Cascade-deleted with it.</summary>
    public Guid DownstreamProjectId { get; set; }

    public Project DownstreamProject { get; set; } = null!;

    /// <summary>
    /// Whether only a <b>successful</b> upstream run triggers (card F-026's "only on success" flag). When false a
    /// failed upstream still triggers - and a failed upstream with this set records <c>skipped-upstream-failed</c>
    /// on the upstream run's chain, so "nothing happened" is never the answer a reader gets.
    /// </summary>
    public bool OnlyOnSuccess { get; set; } = true;

    /// <summary>
    /// Which branch the downstream builds, one of <see cref="ProjectLinkBranchPolicies"/>. A branch the downstream
    /// cannot build is recorded as a skip, never as a failure: the chain did what it was told and the branch simply
    /// does not exist there.
    /// </summary>
    public required string BranchPolicy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The branch policies a link may declare (card F-026).</summary>
public static class ProjectLinkBranchPolicies
{
    /// <summary>The downstream builds the branch its upstream built (meaningful with F-023's multibranch runs).</summary>
    public const string SameBranch = "sameBranch";

    /// <summary>The downstream builds its own default branch, whatever the upstream built.</summary>
    public const string DefaultBranch = "defaultBranch";

    /// <summary>Every policy, in the order the form offers them.</summary>
    public static readonly string[] All = [SameBranch, DefaultBranch];

    /// <summary>Whether <paramref name="policy"/> is one of the known policies.</summary>
    public static bool IsValid(string? policy) =>
        policy is not null && Array.IndexOf(All, policy) >= 0;

    /// <summary>The policy an absent value means: follow the upstream's branch.</summary>
    public const string Default = SameBranch;
}
