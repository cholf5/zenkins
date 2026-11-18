using Zenkins.Shared.Pipelines;

namespace Zenkins.Server.Runs;

/// <summary>
/// One stage section of a run page (card F-030): a <b>run</b> of adjacent steps whose frozen spec carries
/// the same stage name, with the aggregate a reader asks of it - worst state, wall-clock duration and the
/// step count.
/// <para>
/// <b>Built from the merged step views, never from the spec alone.</b> The steps are the ones
/// <see cref="RunService"/> already merged (declared step + the row the worker reported), so a stage of a
/// run that is only half done shows its pending steps and a stage whose steps were all skipped says so.
/// </para>
/// <para>
/// <b>The name is <see langword="null"/> for the unnamed run.</b> A spec frozen before schema v3 has no
/// stage on any step, and the page then renders a single flat list - not one section per step and not an
/// invented "Stage 1" header. A v3 spec can also mix staged and unstaged steps; the unstaged run renders
/// outside every section, exactly where it was declared.
/// </para>
/// </summary>
/// <param name="Name">The stage's frozen name, or <see langword="null"/> for steps that declare none.</param>
/// <param name="Steps">The steps of this section, in declaration order (never empty).</param>
/// <param name="Status">The aggregate state - <see cref="RunStages.AggregateStatus"/>'s worst-first answer.</param>
/// <param name="StartedAt">The earliest recorded step start, or <see langword="null"/> when none started.</param>
/// <param name="EndedAt">The latest recorded step end, or <see langword="null"/> while a step is still open.</param>
/// <param name="FirstFailedStepIndex">The 1-based index of this section's first failed step, if any.</param>
public sealed record RunStageView(
    string? Name,
    IReadOnlyList<RunStepView> Steps,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? FirstFailedStepIndex)
{
    /// <summary>Whether this is a named stage section (an unnamed run renders without a header).</summary>
    public bool IsNamed => Name is not null;

    /// <summary>How many steps the section holds.</summary>
    public int StepCount => Steps.Count;

    /// <summary>
    /// The stage's wall-clock duration - <b>the last step's end minus the first step's start</b> (card F-030),
    /// not the sum of the steps' own durations: the two agree only for a step list with no gaps, and a stage
    /// is a span of the run, gaps included.
    /// </summary>
    public TimeSpan? Duration =>
        StartedAt is { } start && EndedAt is { } end ? end - start : null;

    /// <summary>Whether any step of the section failed.</summary>
    public bool HasFailure => Status == PipelineStepStatus.Failed;

    /// <summary>
    /// The section's default fold state (card F-030): a failed stage is open and the page jumps to its first
    /// failed step; a running one is open so an in-flight run cannot hide what it is doing. Everything else -
    /// success, all-skipped, and steps that have not been reached - is collapsed.
    /// </summary>
    public bool IsExpandedByDefault =>
        Status is PipelineStepStatus.Failed or PipelineStepStatus.Running;
}

/// <summary>
/// Groups a run's merged step views into stage sections (card F-030).
/// <para>
/// <b>Runs, not set membership.</b> The grouping rule is "consecutive steps with the same stage name form one
/// stage". Two steps of stage <c>Build</c> separated by a <c>Test</c> step therefore produce two
/// <c>Build</c> sections instead of one merged section: what the run page shows follows the order the spec
/// declared, and merging across an intervening stage would reorder the reader's view of a run that really
/// did run in that order.
/// </para>
/// <para>
/// <b>An unstaged step is its own section with no name.</b> It is never appended to the previous stage -
/// attribution the author did not declare - and it never gets a header of its own; the page renders its
/// steps as direct children of the step list.
/// </para>
/// </summary>
public static class RunStages
{
    /// <summary>
    /// Groups <paramref name="steps"/> by adjacent equal stage name, preserving declaration order. An empty
    /// input produces no sections (the page shows its "no definition" copy instead).
    /// </summary>
    public static IReadOnlyList<RunStageView> Group(IReadOnlyList<RunStepView> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var sections = new List<RunStageView>();
        var index = 0;
        while (index < steps.Count)
        {
            var name = steps[index].Stage;
            var end = index + 1;
            while (end < steps.Count && string.Equals(steps[end].Stage, name, StringComparison.Ordinal))
            {
                end++;
            }

            var section = steps.Skip(index).Take(end - index).ToArray();
            sections.Add(Build(name, section));
            index = end;
        }

        return sections;
    }

    /// <summary>
    /// The worst state of <paramref name="steps"/>, worst first (card F-030's badge):
    /// <list type="number">
    /// <item><see cref="PipelineStepStatus.Failed"/> - a failure is not softened by the steps that passed;</item>
    /// <item><see cref="PipelineStepStatus.Running"/> - something in the section is still executing;</item>
    /// <item><see cref="PipelineStepStatus.Pending"/> - declared, not reached yet (the section is incomplete);</item>
    /// <item><see cref="PipelineStepStatus.Success"/> - at least one step ran and none failed;</item>
    /// <item><see cref="PipelineStepStatus.Skipped"/> - every step was skipped, so the section never ran.</item>
    /// </list>
    /// A single success next to a skip therefore reads <c>Success</c>: the section did its work, and "Skipped"
    /// would claim the whole skip it did not have. A section whose steps are all skipped reads <c>Skipped</c>
    /// instead of an invented success.
    /// </summary>
    public static string AggregateStatus(IReadOnlyList<RunStepView> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var hasSuccess = false;
        var hasSkipped = false;
        for (var i = 0; i < steps.Count; i++)
        {
            switch (steps[i].Status)
            {
                case PipelineStepStatus.Failed:
                    return PipelineStepStatus.Failed;
                case PipelineStepStatus.Running:
                    return PipelineStepStatus.Running;
                case PipelineStepStatus.Pending:
                    return PipelineStepStatus.Pending;
                case PipelineStepStatus.Success:
                    hasSuccess = true;
                    break;
                case PipelineStepStatus.Skipped:
                    hasSkipped = true;
                    break;
            }
        }

        if (hasSuccess)
        {
            return PipelineStepStatus.Success;
        }

        return hasSkipped ? PipelineStepStatus.Skipped : PipelineStepStatus.Pending;
    }

    private static RunStageView Build(string? name, RunStepView[] steps)
    {
        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;
        int? firstFailed = null;
        foreach (var step in steps)
        {
            // The stage's span is its first start to its last end. Min/max (not the first/last element in the
            // list) keeps the number honest if a report ever arrives out of order.
            if (step.StartedAt is { } start && (startedAt is null || start < startedAt))
            {
                startedAt = start;
            }

            if (step.EndedAt is { } end && (endedAt is null || end > endedAt))
            {
                endedAt = end;
            }

            if (firstFailed is null && step.Status == PipelineStepStatus.Failed)
            {
                firstFailed = step.Index;
            }
        }

        return new RunStageView(name, steps, AggregateStatus(steps), startedAt, endedAt, firstFailed);
    }
}
