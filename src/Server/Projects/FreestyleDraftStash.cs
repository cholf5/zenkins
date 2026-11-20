namespace Zenkins.Server.Projects;

/// <summary>
/// One refused steps submission, kept so the page can render it back (card B-015). The input is the
/// four lists' JSON exactly as the browser posted it - not a rebuilt spec, which is the point: the
/// submission was refused, so there is no valid spec to rebuild.
/// </summary>
public sealed record FreestyleDraftStashEntry(
    FreestyleStepsInput Input,

    /// <summary>The builder's own refusal sentence (<see cref="FreestyleSteps.BuildSpecJson"/>'s error),
    /// the same one a worker's definition error shows. The page renders it under the stable code.</summary>
    string? Reason,

    DateTimeOffset StashedAt);

/// <summary>
/// The one-shot in-memory stash of a refused steps submission (card B-015). Saving is PRG: the POST's
/// 302 is the only thing that survives it, so without this stash a refused draft is gone and the page
/// renders the last <em>saved</em> spec instead - which, with the collapsed-row editor, looks exactly
/// like the page having eaten the reader's work.
/// <para>
/// <b>Keyed to (user, project).</b> The stash answers one reader's own refused POST; another member of
/// the same organization gets the saved spec, never someone else's draft. A foreign organization's id
/// is indistinguishable from a nonexistent project to the page, so the tenant boundary is unchanged.
/// </para>
/// <para>
/// <b>Read once.</b> <see cref="Take"/> consumes: the PRG GET renders the draft, and a refresh of that
/// same URL shows the saved spec again. A save that lands retires whatever the stash still holds, so a
/// fixed draft cannot resurrect on a later visit that still carries the old error code.
/// </para>
/// <para>
/// <b>Bounded.</b> The lists are the browser's text verbatim and a hand-crafted body can be large, so
/// an entry beyond <see cref="MaxEntryBytes"/> is not stashed (the page then falls back to the stable
/// code alone, losing nothing saved), the stash keeps at most <see cref="MaxEntries"/> entries, and an
/// entry expires. Nothing here is durable: a Server restart drops the stash and the page renders the
/// saved spec - the same answer it gave before B-015 existed.
/// </para>
/// </summary>
public sealed class FreestyleDraftStash
{
    /// <summary>The most refused submissions kept. The stash answers one PRG redirect, so a small cap
    /// holds: a real reader is back on the page within seconds of their POST.</summary>
    public const int MaxEntries = 128;

    /// <summary>The most characters one entry carries (the four lists plus the reason). Generous for
    /// any editor a person actually fills in, small enough that 128 of them are not a memory leak.</summary>
    public const int MaxEntryBytes = 256 * 1024;

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<(string UserId, Guid ProjectId), FreestyleDraftStashEntry> _entries = [];

    /// <summary>
    /// Stashes a refused submission, replacing any earlier one for the same user and project. A
    /// submission beyond <see cref="MaxEntryBytes"/> is dropped silently - the refusal still redirects
    /// with its stable code, exactly as it did before the stash existed.
    /// </summary>
    public void Refuse(string userId, Guid projectId, FreestyleStepsInput input, string? reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var size = (input.StepsJson?.Length ?? 0)
            + (input.AlwaysStepsJson?.Length ?? 0)
            + (input.SuccessStepsJson?.Length ?? 0)
            + (input.FailureStepsJson?.Length ?? 0)
            + (reason?.Length ?? 0);
        if (size > MaxEntryBytes)
        {
            return;
        }

        lock (_gate)
        {
            PruneExpired(now);
            _entries[(userId, projectId)] = new FreestyleDraftStashEntry(input, reason, now);
            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries.MinBy(pair => pair.Value.StashedAt);
                _entries.Remove(oldest.Key);
            }
        }
    }

    /// <summary>
    /// The user's refused submission for this project, if any - and it is consumed by the read, so the
    /// next GET (a refresh, a later visit that still carries the error code) answers the saved spec.
    /// </summary>
    public FreestyleDraftStashEntry? Take(string userId, Guid projectId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (_gate)
        {
            PruneExpired(now);
            return _entries.Remove((userId, projectId), out var entry) ? entry : null;
        }
    }

    /// <summary>Retires the user's stash for this project - a save that landed makes any draft stale.</summary>
    public void Clear(string userId, Guid projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        lock (_gate)
        {
            _entries.Remove((userId, projectId));
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        var expired = _entries
            .Where(pair => now - pair.Value.StashedAt >= MaxAge)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired)
        {
            _entries.Remove(key);
        }
    }
}
