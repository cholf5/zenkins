using System.Diagnostics;
using Xunit;

namespace Zenkins.Tests.Recovery;

/// <summary>Its own collection, so no other fact runs beside it (card Q-023).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LoadPressureCollection
{
    public const string Name = "load-pressure";
}

/// <summary>
/// Card Q-023's core evidence: the timing facts still give the same answer <b>while the machine is busy</b>.
/// Every one of them passes alone, so a fact that only runs on an idle machine proves nothing.
/// <para>
/// <b>Why this lives apart.</b> Saturating the CPU would sabotage every other test in the process, so this
/// class is in its own non-parallel collection, carries <c>Category=Load</c> (it must stay out of the fast
/// filter - it is a Windows-run instrument, not a per-commit gate), and the pressure threads have a hard
/// timeout and a <c>finally</c> that stops them: a leaked spinner is worse than a red.
/// </para>
/// </summary>
[Trait("Category", "Load")]
[Collection(LoadPressureCollection.Name)]
public sealed class LoadPressureTimingTests
{
    [Fact(Timeout = 90_000)]
    public async Task The_timing_answers_do_not_change_while_the_cpu_is_saturated()
    {
        // Calibrate the probe before the machine is busy: the same measurement is taken twice, so "the
        // machine really was saturated" is an observation rather than an assumption.
        var quiet = await JitterProbe.MaxStallAsync(TimeSpan.FromMilliseconds(600));

        using var pressure = new CpuPressure(Environment.ProcessorCount);
        pressure.Start();
        Assert.True(pressure.Started, $"the pressure threads did not start ({pressure.StartedThreads} of {pressure.Requested}).");
        Assert.True(
            pressure.WaitSpinning(TimeSpan.FromSeconds(10)),
            $"only {pressure.LiveThreads} of {pressure.Requested} spinner threads reached the loop.");

        try
        {
            // The evidence that the machine is busy: how much CPU this process actually burned over a short
            // window, measured against the wall time it had. Timer granularity cannot hide that (the 1 ms
            // stall probe was already ~10 ms on an idle machine), so the gate is CPU-time, and the probe's
            // two readings are carried in the message as context.
            var busy = await JitterProbe.MaxStallAsync(TimeSpan.FromMilliseconds(300));
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            await Task.Delay(300, TestContext.Current.CancellationToken);
            wall.Stop();
            var cpuBurned = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            var saturation = cpuBurned / wall.Elapsed;
            Assert.True(
                saturation > 0.5,
                $"the machine was not observably busy: this process burned {cpuBurned.TotalMilliseconds:F0} ms "
                + $"of CPU in {wall.Elapsed.TotalMilliseconds:F0} ms of wall time ({saturation:P0} of one core) "
                + $"with {pressure.Requested} spinners; the 1 ms timer stalled {busy.TotalMilliseconds:F0} ms "
                + $"against {quiet.TotalMilliseconds:F0} ms quiet.");

            // (1) A real invariant, held across the window: the sampling itself must be dense (many samples)
            //     and must span the budget, or "it held" is a statement about an instant.
            var invariant = 0;
            var samples = 0;
            var first = (DateTimeOffset?)null;
            var last = (DateTimeOffset?)null;
            var budget = TimeSpan.FromMilliseconds(600);
            var stopwatch = Stopwatch.StartNew();
            await TimingAssert.AssertHoldsAsync(
                _ =>
                {
                    Assert.Equal(0, Volatile.Read(ref invariant));
                    samples++;
                    first ??= DateTimeOffset.UtcNow;
                    last = DateTimeOffset.UtcNow;
                    return Task.CompletedTask;
                },
                budget,
                "the invariant holds while the machine is busy",
                TestContext.Current.CancellationToken);
            Assert.True(samples >= 3, $"the hold sampled only {samples} times in {budget.TotalMilliseconds:F0} ms.");
            Assert.True(
                last - first >= TimeSpan.FromTicks((long)(budget.Ticks * 0.8)),
                $"the hold's samples spanned {(last - first)?.TotalMilliseconds:F0} ms of a {budget.TotalMilliseconds:F0} ms budget.");

            // (2) A condition-driven wait that finishes inside the window - the shape the suite uses for
            //     "the run reaches Running", not a sleep.
            var flag = 0;
            var flip = Task.Run(async () =>
            {
                await Task.Delay(150, TestContext.Current.CancellationToken);
                Volatile.Write(ref flag, 1);
            }, TestContext.Current.CancellationToken);
            var waited = Stopwatch.StartNew();
            await TimingAssert.WaitUntilAsync(
                _ =>
                {
                    if (Volatile.Read(ref flag) == 0)
                    {
                        throw new InvalidOperationException("the flag is still 0");
                    }

                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(10),
                "the flag flips",
                TestContext.Current.CancellationToken);
            await flip;
            Assert.True(
                waited.Elapsed < TimeSpan.FromSeconds(5),
                $"the wait returned only after {waited.Elapsed.TotalSeconds:F1}s - it slept instead of polling.");

            // (3) A false invariant still fails INSIDE its budget: if a longer budget were a way to pass, this
            //     would go green.
            var failing = Stopwatch.StartNew();
            await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => TimingAssert.AssertHoldsAsync(
                _ => throw new InvalidOperationException("the run was claimed"),
                TimeSpan.FromMilliseconds(400),
                "a broken invariant is caught under load",
                TestContext.Current.CancellationToken));
            Assert.True(
                failing.Elapsed < TimeSpan.FromSeconds(5),
                $"the false hold took {failing.Elapsed.TotalSeconds:F1}s - it waited instead of failing.");

            // (3b) Every spinner must have done WORK while the window was open. A sleeping "spinner" is alive,
            //      started and counted, and still competes for nothing - this is the counter that sees it.
            var worked = Enumerable.Range(0, pressure.Requested)
                .Select(index => pressure.IterationsOf(index))
                .ToArray();
            Assert.All(worked, count => Assert.True(
                count > 1_000_000,
                $"a spinner did only {count} iterations - it is not competing. Counts: {string.Join(", ", worked)}."));

            // (3c) The timer probe, as a threshold rather than a vibe. Measured on this machine (macOS, arm64,
            //      8 spinners): a 1 ms sleep overshoots by ~10 ms quiet and ~16 ms saturated. Timer granularity
            //      differs per OS, so the rule is a shape, not a constant: the stall must either grow by at
            //      least 4 ms over the quiet reading, or already be past 20 ms on its own.
            var drifted = busy >= quiet + TimeSpan.FromMilliseconds(4) || busy >= TimeSpan.FromMilliseconds(20);
            Assert.True(
                drifted,
                $"the 1 ms timer stalled {busy.TotalMilliseconds:F0} ms under pressure against "
                + $"{quiet.TotalMilliseconds:F0} ms quiet - no contention is visible in the probe, and a "
                + "pressure fact that cannot see contention proves nothing.");

            // (5) The deadline decision, on the frozen clock: the answer is the fixture's, not the machine's.
            var clock = new Zenkins.Tests.Pipelines.ManualClock(DateTimeOffset.UtcNow);
            var remaining = clock.Now.AddSeconds(2) - clock.GetUtcNow();
            Assert.Equal(TimeSpan.FromSeconds(2), remaining);
            Assert.False(
                Zenkins.Worker.Pipelines.RunDeadlineComparison.OwnGoverns(TimeSpan.FromSeconds(3600), remaining),
                "a 3600s step limit cannot be the earlier one against two seconds left");
            Assert.True(
                Zenkins.Worker.Pipelines.RunDeadlineComparison.OwnGoverns(TimeSpan.FromMilliseconds(500), remaining),
                "a 0.5s step limit is the earlier one against two seconds left");
        }
        finally
        {
            pressure.Stop();
            Assert.True(
                await pressure.WaitStoppedAsync(TimeSpan.FromSeconds(10)),
                $"the pressure threads did not stop ({pressure.LiveThreads} still alive).");
        }
    }

    /// <summary>
    /// Busy threads with a hard stop. Each spins on a token; <see cref="Stop"/> cancels, and
    /// <see cref="WaitStoppedAsync"/> is what the fact asserts, so a spinner that refuses to end fails the
    /// test instead of outliving it.
    /// </summary>
    private sealed class CpuPressure(int requested) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Thread> _threads = [];
        private readonly CountdownEvent _spinning = new(requested);
        private readonly long[] _iterations = new long[requested];

        public int Requested { get; } = requested;

        /// <summary>
        /// Work done by spinner <paramref name="index"/>. A thread that is alive but sleeping - the shape that
        /// used to make this fact vacuous - advances this zero, so the fact can tell "started" from "working".
        /// </summary>
        public long IterationsOf(int index) => Interlocked.Read(ref _iterations[index]);

        public int StartedThreads => _threads.Count;

        public bool Started => _threads.Count == Requested;

        public bool IsBusy => LiveThreads == Requested;

        /// <summary>
        /// Blocks until every spinner has signalled that it is actually executing - "the threads exist" is not
        /// "the CPU is busy", and a pressure fact that only starts threads proves nothing.
        /// </summary>
        public bool WaitSpinning(TimeSpan timeout) => _spinning.Wait(timeout);

        public int LiveThreads => _threads.Count(thread => thread.IsAlive);

        public void Start()
        {
            for (var i = 0; i < Requested; i++)
            {
                var slot = i;
                var thread = new Thread(() =>
                {
                    var sink = 0L;
                    _spinning.Signal();
                    while (!_stop.IsCancellationRequested)
                    {
                        sink++;
                        if ((sink & 0xFFF) == 0)
                        {
                            Interlocked.Add(ref _iterations[slot], 0x1000);
                        }
                    }

                    Interlocked.Add(ref _iterations[slot], sink & 0xFFF);
                    GC.KeepAlive(sink);
                })
                {
                    IsBackground = true,
                    Name = $"q-023-pressure-{i}",
                };

                thread.Start();
                _threads.Add(thread);
            }
        }

        public void Stop()
        {
            _stop.Cancel();
            foreach (var thread in _threads)
            {
                thread.Join(TimeSpan.FromSeconds(5));
            }
        }

        public async Task<bool> WaitStoppedAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (LiveThreads == 0)
                {
                    return true;
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            return LiveThreads == 0;
        }

        public void Dispose()
        {
            Stop();
            _stop.Dispose();
        }
    }
}

/// <summary>
/// Card Q-023: how badly this machine misses a 1 ms timer - the evidence that a "load" fact really ran under
/// load. Measured quiet first, then under pressure; the difference is what the fact asserts.
/// </summary>
internal static class JitterProbe
{
    public static async Task<TimeSpan> MaxStallAsync(TimeSpan window)
    {
        var slowest = TimeSpan.Zero;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < window)
        {
            var before = Stopwatch.GetTimestamp();
            await Task.Delay(1, TestContext.Current.CancellationToken);
            var stall = Stopwatch.GetElapsedTime(before);
            if (stall > slowest)
            {
                slowest = stall;
            }
        }

        return slowest;
    }
}
