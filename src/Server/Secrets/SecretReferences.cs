using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Server.Secrets;

/// <summary>
/// What the recorded definitions say about one secret name (card D-006).
/// <para>
/// <b>Three states, because "zero" is not the same answer as "we did not record it".</b> A secret that
/// nothing references is safe to delete - but only when capture actually ran for every definition that
/// could have referenced it. A spec frozen before capture existed may reference anything, so it reports
/// <see cref="Unknown"/> instead of pretending to be empty (TC-003).
/// </para>
/// </summary>
public enum SecretReferenceState
{
    /// <summary>No recorded definition references the name, and every one of them recorded its references.</summary>
    None = 0,

    /// <summary>At least one recorded definition references the name.</summary>
    Known = 1,

    /// <summary>
    /// Nothing references the name, but at least one recorded definition predates the capture mechanism
    /// (or could not be read), so "nothing" cannot be claimed.
    /// </summary>
    Unknown = 2,
}

/// <summary>
/// One run whose recorded definition references a secret name: a <b>name and a link</b>, never a value
/// (F-012's invariant).
/// </summary>
/// <param name="RunId">The run to link to.</param>
/// <param name="ProjectName">The run's project, for a label a person recognises.</param>
/// <param name="PipelineName">The pipeline name from the frozen spec.</param>
/// <param name="RunCreatedAt">When the run was created (the list is newest first).</param>
public sealed record SecretReference(
    Guid RunId,
    string ProjectName,
    string PipelineName,
    DateTimeOffset RunCreatedAt);

/// <summary>
/// The reference answer for one secret name: its state, the referencing runs (bounded - see
/// <see cref="MaxRecorded"/>, with <see cref="ReferenceCount"/> the true total) and how many recorded
/// definitions could not be inspected because they predate capture.
/// </summary>
public sealed record SecretUsage(
    SecretReferenceState State,
    IReadOnlyList<SecretReference> References,
    int ReferenceCount,
    int UncapturedRuns)
{
    /// <summary>
    /// How many referencing runs one answer carries. The page lists a handful and summarises the rest;
    /// the count stays exact, so nothing is silently dropped.
    /// </summary>
    public const int MaxRecorded = 20;

    /// <summary>Every definition recorded its references and none of them names this secret.</summary>
    public static SecretUsage None { get; } = new(SecretReferenceState.None, [], 0, 0);

    /// <summary>Cannot be answered: at least one definition predates capture.</summary>
    public static SecretUsage Unknown(int uncapturedRuns) =>
        new(SecretReferenceState.Unknown, [], 0, uncapturedRuns);

    /// <summary>At least one definition references the name.</summary>
    public static SecretUsage Known(
        IReadOnlyList<SecretReference> references,
        int referenceCount,
        int uncapturedRuns) =>
        new(SecretReferenceState.Known, references, referenceCount, uncapturedRuns);
}

/// <summary>
/// Reads which stored definitions reference which secret names (card D-006).
/// <para>
/// <b>The page never reads a repository and never parses code.</b> It asks this class, which walks the
/// active tenant's runs that carry a frozen spec and reads the <see cref="PipelineSpec.SecretReferences"/>
/// the definition phase recorded. The tenant filter of <see cref="ZenkinsDbContext"/> is the only boundary
/// (no organization parameter exists here), so a foreign run is not merely hidden - it is unreadable, and
/// the only caller (<see cref="SecretService.GetSecretsAsync"/>) runs its Owner check first.
/// </para>
/// <para>
/// <b>Cost is proportional to this tenant's runs-with-a-spec, and that is deliberate.</b> The answer "nothing
/// references this name" is only worth acting on (delete) when it is a complete statement, so the walk is not
/// truncated: a time window would turn an old referencing run into an invisible one, which is exactly the
/// mistake that makes a delete unsafe. Runs are streamed one at a time and per name at most
/// <see cref="SecretUsage.MaxRecorded"/> references are kept while the total keeps counting, so memory stays
/// bounded; the price is one pass over the tenant's spec rows per page load. If that ever becomes too
/// expensive, the fix is to materialise the (run, secret name) pairs at definition-report time - not a
/// LIMIT here. Nothing else holds a secret value: a spec carries names only.
/// </para>
/// </summary>
internal static class SecretReferenceIndex
{
    /// <summary>
    /// The usage of each of <paramref name="secretNames"/> in the active tenant. A name no run mentions
    /// comes back as <see cref="SecretUsage.None"/> or <see cref="SecretUsage.Unknown"/>, never null.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, SecretUsage>> ReadAsync(
        ZenkinsDbContext context,
        IReadOnlyList<string> secretNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(secretNames);

        var usage = new Dictionary<string, SecretUsage>(secretNames.Count, StringComparer.Ordinal);
        if (secretNames.Count == 0)
        {
            return usage;
        }

        var wanted = new HashSet<string>(secretNames, StringComparer.Ordinal);
        var references = new Dictionary<string, List<SecretReference>>(wanted.Count, StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(wanted.Count, StringComparer.Ordinal);
        foreach (var name in wanted)
        {
            references[name] = [];
            counts[name] = 0;
        }

        var uncapturedRuns = 0;

        // No ORDER BY here: SQLite cannot order by DateTimeOffset, and the ordering only matters per name.
        // Projecting the four columns keeps each row small; the walk is one pass over runs with a spec.
        var rows = context.BuildRuns
            .AsNoTracking()
            .Where(run => run.PipelineSpecJson != null)
            .Select(run => new
            {
                run.Id,
                run.Project.Name,
                run.PipelineSpecJson,
                run.CreatedAt,
            });

        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            // A spec that cannot be read is as unknown as a pre-capture one: it may name anything, so it must
            // not let the page claim "nothing references this".
            if (!PipelineSpecJson.TryRead(row.PipelineSpecJson, out var spec, out _)
                || spec is null
                || spec.SecretReferences is null)
            {
                uncapturedRuns++;
                continue;
            }

            var referenced = new HashSet<string>(spec.SecretReferences, StringComparer.Ordinal);
            referenced.IntersectWith(wanted);
            foreach (var name in referenced)
            {
                counts[name]++;
                var list = references[name];
                if (list.Count < SecretUsage.MaxRecorded)
                {
                    list.Add(new SecretReference(row.Id, row.Name, spec.Name, row.CreatedAt));
                }
            }
        }

        foreach (var name in wanted)
        {
            var count = counts[name];
            var list = references[name];
            list.Sort(static (left, right) => right.RunCreatedAt.CompareTo(left.RunCreatedAt));

            usage[name] = count > 0
                ? SecretUsage.Known(list, count, uncapturedRuns)
                : uncapturedRuns > 0
                    ? SecretUsage.Unknown(uncapturedRuns)
                    : SecretUsage.None;
        }

        return usage;
    }
}
