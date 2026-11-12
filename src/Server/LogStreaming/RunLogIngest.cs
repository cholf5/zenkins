using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.LogStreaming;

namespace Zenkins.Server.LogStreaming;

/// <summary>Server → browser contract of the run-log hub (typed client). History chunks carry the
/// lines persisted up to the join; live batches carry everything flushed afterwards.</summary>
public interface IRunLogClient
{
    /// <summary>A chunk of the run's persisted history (ordered by Seq), sent right after joining.</summary>
    Task HistoryLines(IReadOnlyList<LogLineMessage> lines);

    /// <summary>Lines flushed to the database for the joined run, in Seq order.</summary>
    Task ReceiveLines(IReadOnlyList<LogLineMessage> lines);
}

/// <summary>The group name for one run's log subscribers. Groups are per run id (run ids are
/// globally unique); organization membership is validated at join time, not by group naming.</summary>
public static class RunLogGroups
{
    public static string ForRun(Guid runId) => $"run:{runId.ToString("N")}";
}

/// <summary>Counters proving the ingest's bounded-batch behaviour (the memory-boundedness test's
/// observability seam). All values are process-wide since startup.</summary>
public sealed record RunLogIngestStatistics(long TotalAppended, long FlushCount, long MaxBatchSize);

/// <summary>
/// The single writer of <see cref="BuildLog"/> rows (card F-008). Lines arrive from the worker
/// upstream hub in bursts; they are appended to the database in bounded batches - when
/// <see cref="RunLogOptions.FlushLineCount"/> lines are pending, or after
/// <see cref="RunLogOptions.FlushIntervalMilliseconds"/>, whichever comes first - and each flushed
/// batch is broadcast to the run's browser group immediately afterwards.
/// <para>
/// <b>Memory stays bounded:</b> each run holds at most one pending list capped at the flush size
/// plus the chunk currently being written. A burst is split into flush-sized chunks before it ever
/// reaches a buffer, so a 10k-line call costs 100 bounded batches, never one 10k-row insert.
/// Nothing is retained after the run completes (<see cref="CompleteRunAsync"/> flushes the tail
/// and evicts the buffer).
/// </para>
/// <para>
/// <b>Duplicates are absorbed by the database</b>, not by bookkeeping: <c>(RunId, Seq)</c> is the
/// table's primary key. A happy-path batch inserts in one SaveChanges; if the reconnecting worker
/// re-delivered overlapping rows, the violation falls back to a per-batch "which Seqs does the
/// database already have" read and inserts only the missing lines - so every line lands exactly
/// once, in Seq order, no matter how often it was sent.
/// </para>
/// <para>
/// <b>System scope discipline:</b> appends are cross-tenant by nature (the worker upstream hub
/// serves every organization), so inserts run inside <see cref="ISystemDataScope"/> with the
/// OrganizationId stamped from the run row the hub already validated - the same pattern as the
/// lease sweeper.
/// </para>
/// </summary>
public sealed class RunLogIngest(
    IServiceScopeFactory scopeFactory,
    IHubContext<RunLogHub, IRunLogClient> hubContext,
    RunLogOptions options,
    ILogger<RunLogIngest> logger)
{
    private readonly ConcurrentDictionary<Guid, PendingRun> _runs = new();

    /// <summary>
    /// The runs whose ingest state was evicted by <see cref="CompleteRunAsync"/> (review fix F3).
    /// An append that arrives after the eviction - the worker's drain still retrying while the
    /// completed report went ahead through the <c>HasEverConnected</c> escape hatch - must not
    /// recreate per-run state that nothing will ever evict again (the old PendingRun leak); it is
    /// flushed inline instead. The flag goes up before the state leaves the dictionary, so an
    /// append racing the eviction either sees the flag (late path) or has its lines inside the
    /// state the eviction takes below - never in between. One entry per completed run, capped
    /// because the flag is a leak guard, not a correctness authority: the orphan-safety of late
    /// lines comes from the object-referenced flush timer, so a flag entry that fell out of the
    /// cap only recreates state that lingers until the process restarts, without losing lines.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, bool> _evicted = new();

    /// <summary>Serializes the rare inline flushes of late (post-eviction) appends against each
    /// other. They deliberately share no per-run state, so no per-run gate can be disposed out
    /// from under them (review fix F3).</summary>
    private readonly SemaphoreSlim _lateFlushGate = new(1, 1);

    /// <summary>The cap for <see cref="_evicted"/> (one entry per completed run).</summary>
    private const int EvictedRunFlagCap = 100_000;

    private long _totalAppended;
    private long _flushCount;
    private long _maxBatchSize;

    /// <summary>How many runs currently hold ingest state. The leak guard's test seam: a
    /// completed (evicted) run must leave this at its pre-run level, late appends included.</summary>
    public int PendingRunCount => _runs.Count;

    /// <summary>Snapshot of the bounded-batch counters (test seam and ops observability).</summary>
    public RunLogIngestStatistics Statistics => new(
        Interlocked.Read(ref _totalAppended),
        Interlocked.Read(ref _flushCount),
        Interlocked.Read(ref _maxBatchSize));

    /// <summary>
    /// Queues validated lines for one run and flushes whenever a full batch has accumulated.
    /// The burst is handed over in flush-sized chunks, so the caller's array is consumed
    /// incrementally and no run's pending state exceeds the flush size.
    /// </summary>
    public async Task AppendAsync(
        Guid runId,
        Guid organizationId,
        IReadOnlyList<LogLineMessage> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return;
        }

        var offset = 0;
        while (offset < lines.Count)
        {
            // Post-eviction append (review fix F3): the run finished while the worker was still
            // retrying. Flush the remaining lines inline - no pending state is created, so a
            // completed run leaves neither lines behind nor memory behind, and nothing is
            // silently dropped.
            if (_evicted.ContainsKey(runId))
            {
                List<LogLineMessage> lateLines = [.. lines.Skip(offset)];
                offset = lines.Count;
                await FlushLateAsync(runId, organizationId, lateLines, cancellationToken);
                continue;
            }

            var run = _runs.GetOrAdd(runId, _ => new PendingRun { RunId = runId });
            List<LogLineMessage>? handoff;
            lock (run.Lock)
            {
                run.OrganizationId = organizationId;
                var take = Math.Min(options.FlushLineCount, lines.Count - offset);
                for (var i = 0; i < take; i++)
                {
                    run.Pending.Add(lines[offset + i]);
                }

                offset += take;
                if (run.Pending.Count >= options.FlushLineCount)
                {
                    handoff = SwapPending(run);
                    EnsureFlushTimer(run);
                }
                else
                {
                    // Partial batch: the timer flushes it within the interval unless more lines arrive.
                    handoff = null;
                    EnsureFlushTimer(run);
                }
            }

            if (handoff is not null)
            {
                await FlushAsync(runId, run, handoff, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Flushes whatever is pending and evicts the run's buffer. Called when the run reaches a
    /// terminal state, so a completed run never leaves lines behind nor keeps server memory.
    /// Idempotent: an already-evicted (or never-buffered) run id is a no-op.
    /// </summary>
    public async Task CompleteRunAsync(Guid runId)
    {
        // The flag goes up BEFORE the state leaves the dictionary (review fix F3): an append
        // racing this eviction must either see the flag (late inline flush) or have its lines
        // inside the state taken below - it can never recreate state that nothing evicts again.
        if (_evicted.Count >= EvictedRunFlagCap)
        {
            _evicted.Clear();
            logger.LogWarning(
                "The evicted-run flag set exceeded {Cap} entries and was cleared; a late append to a "
                + "run completed before this point may leave (empty) per-run ingest state behind.",
                EvictedRunFlagCap);
        }

        _evicted[runId] = true;

        if (!_runs.TryRemove(runId, out var run))
        {
            return;
        }

        List<LogLineMessage> handoff;
        Timer? timer;
        lock (run.Lock)
        {
            timer = run.Timer;
            run.Timer = null;
            handoff = SwapPending(run);
        }

        timer?.Dispose();
        if (handoff is { Count: > 0 })
        {
            await FlushAsync(runId, run, handoff, CancellationToken.None);
        }

        // No FlushGate/Timer disposal beyond the one-shot timer taken above (review fix F3): a
        // mid-append eviction races chunks that were added to this very object and flush paths
        // that reference it directly - disposing shared primitives here turned those races into
        // ObjectDisposedExceptions or silent loss. The object becomes garbage once nothing
        // references it; a SemaphoreSlim that never created its WaitHandle needs no Dispose.
    }

    private void EnsureFlushTimer(PendingRun run)
    {
        // Called under the run lock. A one-shot timer: restarted whenever lines arrive again, so
        // an idle run costs nothing (the timer disposes itself after flushing an empty buffer).
        if (run.Timer is not null)
        {
            return;
        }

        run.Timer = new Timer(
            _ => _ = TimerFlushAsync(run),
            null,
            TimeSpan.FromMilliseconds(options.FlushIntervalMilliseconds),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The flush timer's body. It deliberately receives the <see cref="PendingRun"/> object, not
    /// a run-id lookup (review fix F3): an append can hand chunks to a run object that a concurrent
    /// <see cref="CompleteRunAsync"/> has just evicted, and those lines must still reach the
    /// database through the object's own timer - the old dictionary lookup silently dropped them.
    /// </summary>
    private async Task TimerFlushAsync(PendingRun run)
    {
        List<LogLineMessage>? handoff;
        Timer? timer;
        lock (run.Lock)
        {
            timer = run.Timer;
            run.Timer = null;
            handoff = SwapPending(run);
        }

        timer?.Dispose();
        if (handoff is not { Count: > 0 })
        {
            return;
        }

        try
        {
            await FlushAsync(run.RunId, run, handoff, CancellationToken.None);
        }
        catch (Exception failure)
        {
            // A failed timer flush must not kill the process; the next batch retries the append
            // (lines were only removed from the buffer after the database accepted them).
            logger.LogError(
                failure,
                "Timer flush of run {RunId} log lines failed ({ExceptionType}: {ExceptionMessage}).",
                run.RunId,
                failure.GetType().Name,
                failure.Message);
            lock (run.Lock)
            {
                for (var i = handoff.Count - 1; i >= 0; i--)
                {
                    run.Pending.Insert(0, handoff[i]);
                }
                EnsureFlushTimer(run);
            }
        }
    }

    /// <summary>Takes the whole pending list and leaves a fresh one. Caller holds the lock.</summary>
    private static List<LogLineMessage> SwapPending(PendingRun run)
    {
        var pending = run.Pending;
        run.Pending = [];
        return pending;
    }

    private async Task FlushAsync(
        Guid runId,
        PendingRun run,
        List<LogLineMessage> batch,
        CancellationToken cancellationToken)
    {
        // One writer per run: keeps the database write order equal to the Seq order and keeps two
        // flushes (timer + append-triggered) from interleaving their reads-and-inserts.
        await run.FlushGate.WaitAsync(cancellationToken);
        try
        {
            await FlushCoreAsync(runId, run.OrganizationId, batch, cancellationToken);
        }
        finally
        {
            run.FlushGate.Release();
        }
    }

    /// <summary>
    /// The inline flush of a post-eviction (late) append (review fix F3). The lines go straight
    /// to the database and the browser group - no per-run state is created or kept, so the
    /// completed run's memory profile stays at zero and nothing is silently dropped. The global
    /// gate serializes late flushes against each other; against the eviction's own final flush
    /// they may interleave harmlessly (disjoint Seqs, the key absorbs any overlap, the browser
    /// merges by Seq).
    /// </summary>
    private async Task FlushLateAsync(
        Guid runId,
        Guid organizationId,
        List<LogLineMessage> lines,
        CancellationToken cancellationToken)
    {
        await _lateFlushGate.WaitAsync(cancellationToken);
        try
        {
            logger.LogInformation(
                "Run {RunId} already finished; flushing {Count} late log line(s) that arrived after "
                + "the ingest evicted its state.",
                runId,
                lines.Count);
            await FlushCoreAsync(runId, organizationId, lines, cancellationToken);
        }
        finally
        {
            _lateFlushGate.Release();
        }
    }

    private async Task FlushCoreAsync(
        Guid runId,
        Guid organizationId,
        List<LogLineMessage> batch,
        CancellationToken cancellationToken)
    {
        // Within-batch duplicates (a misbehaving sender) must not poison the insert.
        var lines = batch.DistinctBy(line => line.Seq).ToArray();

        // The ingest is a singleton: every flush opens its own DI scope (the LeaseSweeper
        // pattern) - one context and one system scope per flush, never a shared one.
        await using var scope = scopeFactory.CreateAsyncScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>();
        var systemDataScope = scope.ServiceProvider.GetRequiredService<ISystemDataScope>();
        using var systemScope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await InsertWithDedupeAsync(context, runId, organizationId, lines, cancellationToken);

        var appended = Interlocked.Add(ref _totalAppended, lines.Length);
        var flushCount = Interlocked.Increment(ref _flushCount);
        var maxBatch = Interlocked.Read(ref _maxBatchSize);
        while (lines.Length > maxBatch && Interlocked.CompareExchange(ref _maxBatchSize, lines.Length, maxBatch) != maxBatch)
        {
            maxBatch = Interlocked.Read(ref _maxBatchSize);
        }

        logger.LogDebug(
            "Flushed {Count} log lines for run {RunId} (flush #{Flush}, total {Total}).",
            lines.Length,
            runId,
            flushCount,
            appended);

        // Persisted first, then pushed: a browser only ever sees durable lines, and the
        // history read of a later join cannot miss anything that was already broadcast.
        try
        {
            await hubContext.Clients
                .Group(RunLogGroups.ForRun(runId))
                .ReceiveLines(lines)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception failure)
        {
            // A broadcast failure must not undo the append; live viewers can still recover the
            // chunk from the history read (re-join) or the download endpoint.
            logger.LogWarning(
                failure,
                "Broadcasting {Count} log lines of run {RunId} to the browser group failed.",
                lines.Length,
                runId);
        }
    }

    /// <summary>
    /// Inserts one flush-sized batch. Happy path: a single SaveChanges. If the database rejects a
    /// row because its (RunId, Seq) already exists - a reconnecting worker re-delivering overlap,
    /// exactly the case the primary key exists for - the batch is re-planned against the rows the
    /// database actually holds and only the missing lines are inserted.
    /// </summary>
    private static async Task InsertWithDedupeAsync(
        ZenkinsDbContext context,
        Guid runId,
        Guid organizationId,
        IReadOnlyList<LogLineMessage> lines,
        CancellationToken cancellationToken)
    {
        try
        {
            AddLines(context, runId, organizationId, lines);
            await context.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (DbUpdateException failure) when (DatabaseErrors.IsUniqueViolation(failure, "BuildLogs"))
        {
            context.ChangeTracker.Clear();
        }

        var seqs = lines.Select(line => line.Seq).ToArray();
        var present = (await context.BuildLogs
            .AsNoTracking()
            .Where(log => log.RunId == runId && seqs.Contains(log.Seq))
            .Select(log => log.Seq)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var missing = lines.Where(line => !present.Contains(line.Seq)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        AddLines(context, runId, organizationId, missing);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static void AddLines(
        ZenkinsDbContext context,
        Guid runId,
        Guid organizationId,
        IReadOnlyList<LogLineMessage> lines)
    {
        foreach (var line in lines)
        {
            context.BuildLogs.Add(new BuildLog
            {
                OrganizationId = organizationId,
                RunId = runId,
                Seq = line.Seq,
                Stream = line.Stream,
                Message = line.Message,
                Timestamp = line.Timestamp,
            });
        }
    }

    /// <summary>Per-run ingest state. The pending list never exceeds the flush size.</summary>
    private sealed class PendingRun
    {
        public readonly object Lock = new();
        public readonly SemaphoreSlim FlushGate = new(1, 1);
        public List<LogLineMessage> Pending = [];
        public Guid OrganizationId;
        public Timer? Timer;
        public Guid RunId; // the timer callback needs it after eviction races
    }
}
