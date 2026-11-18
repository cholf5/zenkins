using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Shared.Workers;

namespace Zenkins.Server.Runs;

/// <summary>
/// Card F-033: the one place that asks "can any Worker do this?".
/// <para>
/// <b>Why this type exists.</b> Before it, three places each answered a version of that question with their
/// own code: the claim loop compared the run's comma list against a Worker's, the trigger collected every
/// Worker's tags and asked only "is this tag present anywhere" (Q-010's asymmetry - a presence check is not
/// a satisfiability check), and the definition-observation path asked nothing at all. Three answers to one
/// question is how "the trigger said somebody can build it" and "nobody ever claims it" happen at the same
/// time.
/// </para>
/// <para>
/// <b>Satisfiability is per Worker, not per union.</b> "Some known Worker satisfies <c>linux &amp;&amp; !windows</c>"
/// is not "the union of all tags contains linux and not windows" - a union always contains every tag anyone
/// has, so the negative atom would reject a machine that is exactly right. Every caller gets this same
/// predicate instead of a set operation on the union it happened to build.
/// </para>
/// </summary>
public static class WorkerTagCatalogue
{
    /// <summary>
    /// Every known Worker's tag list, split once here so no caller writes its own parser loop. Ordered by
    /// Worker id so a diagnostic reads the same twice.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyList<string>>> TagsByWorkerAsync(
        ZenkinsDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rows = await context.Workers
            .AsNoTracking()
            .OrderBy(worker => worker.Id)
            .Select(worker => worker.Tags)
            .ToListAsync(cancellationToken);

        return rows.Select(RunValidation.ParseTags).ToArray();
    }

    /// <summary>Whether at least one known Worker satisfies <paramref name="requirement"/>.</summary>
    public static async Task<bool> AnyWorkerSatisfiesAsync(
        ZenkinsDbContext context,
        WorkerRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (requirement.IsNone)
        {
            return true;
        }

        foreach (var tags in await TagsByWorkerAsync(context, cancellationToken))
        {
            if (requirement.SatisfiedBy(tags))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the requirement is waiting for a Worker, and which labels no Worker carries at all - one answer
    /// for a page, derived from <b>the same</b> predicate the claim loop uses.
    /// <para>
    /// <b>Atom-level on purpose.</b> A requirement like <c>(linux &amp;&amp; arm64) || (windows &amp;&amp; x64)</c> has no
    /// single "missing label": the honest sentence is "no known Worker has: linux, arm64", not "waiting for
    /// linux and arm64", which would claim a precision the boolean form does not have.
    /// </para>
    /// </summary>
    public static async Task<WorkerGap> DescribeGapAsync(
        ZenkinsDbContext context,
        WorkerRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var workers = await TagsByWorkerAsync(context, cancellationToken);
        if (workers.Any(tags => requirement.SatisfiedBy(tags)))
        {
            return new WorkerGap([], WaitingForWorker: false);
        }

        var missing = requirement.Atoms
            .Where(atom => !workers.Any(tags => tags.Contains(atom, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(atom => atom, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WorkerGap(missing, WaitingForWorker: true);
    }

    /// <summary>
    /// The labels no known Worker covers, for a message that says what to install - the first Worker's view
    /// of each clause is enough: the caller only reaches this after <see cref="AnyWorkerSatisfiesAsync"/> said
    /// no, so every clause is unsatisfied by every Worker.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UnsatisfiedAtomsAsync(
        ZenkinsDbContext context,
        WorkerRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var workers = await TagsByWorkerAsync(context, cancellationToken);
        if (workers.Count == 0)
        {
            return requirement.Atoms.ToArray();
        }

        var missing = new List<string>();
        foreach (var clause in requirement.Atoms)
        {
            if (!workers.Any(tags => tags.Contains(clause, StringComparer.OrdinalIgnoreCase)))
            {
                missing.Add(clause);
            }
        }

        return missing;
    }
}

/// <summary>
/// What a requirement's gap looks like to a page: whether any known Worker satisfies it, and - when none does -
/// the atoms no Worker carries (see <see cref="WorkerTagCatalogue.DescribeGapAsync"/>).
/// </summary>
public sealed record WorkerGap(IReadOnlyList<string> MissingAtoms, bool WaitingForWorker);
