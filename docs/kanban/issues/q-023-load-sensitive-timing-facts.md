---
id: Q-023
title: The timing facts fail under load and pass alone, so a red full run does not mean a regression
type: test
priority: P2
size: M
status: done
created: 2026-11-18
updated: 2026-11-18
blocked: none
---

## Goal

A full-suite red tells the truth again: the facts that compare against the wall clock either stop doing
it, or the suite proves they hold while the machine is busy.

## Background

Three full runs of the same tree, same filter, on the Windows runner:

| Run | Failures | Red tests' own time | Wall clock |
|---|---|---|---|
| `a181134` | 3 | ~30.4 s | 611 s |
| `abd15c4` | 4 | ~36.6 s | 769 s |
| `45b748e` | 2 | ~4.8 s | 672 s |

The wall clock moves by ±15% while the failing tests' own time does not, so the variance is contention
between waits, not slower work. Historical values for the same filter: 602 / 615 / 611 / 769 / 672 s.

Two shapes produce it:

- **`PipelineRunnerTests.The_step_limit_is_clamped_by_the_runs_remaining_deadline_and_the_log_says_which_fired`**
  was red in two of three full runs (`Assert.Single: collection did not contain any matching items`,
  `PipelineRunnerTests.cs:825`, 3.9–4.7 s) and green in four isolated reruns. It compares a step's own
  limit against the run's remaining deadline using real elapsed time, so a busy machine moves the answer.
  `RunWorkerCancellationTests`' `timeout` vs `git-timeout` case is the same family and has been seen once.
- **The three fixed-window helpers**: **22 call sites in 7 files** (Recovery's 4 classes and
  Concurrency's 3) - `AssertHoldsAsync` (10), `AssertRunHoldsStatusAsync` (4) and
  `AssertRunSettlesInAsync` (8). Fixing only `AssertHolds` would leave the other two families flaking. Under CPU saturation a three-to-five second window turns from "wait for the
  condition" into "the condition never came", and each such wait also lengthens every other test's wall
  time.

Neither family shares a lock with the other; they compete for CPU and for timers.

## Acceptance Criteria

- [ ] All three fixed-window helpers (22 call sites) answer their question through `TimingAssert`, not
      through their own fixed loop.
- [x] A fact proves the timing family holds **while the machine is busy** - the only evidence that
      matters, because every one of these tests already passes when it runs alone.
- [x] A wait that gives up says so: the failure names the condition, how long it waited and the budget it
      had, instead of only "collection did not contain any matching items".
- [x] The deadline family either stops comparing against the wall clock or documents why it must and is
      made load-tolerant; a skipped wait is never silently converted into a pass.
- [x] A fact fails fast when it should still fail fast - making waits longer must not be able to turn a
      real failure into a pass, and a mutation that breaks the behaviour under test must still redden it.
- [x] The suite reports its own wall-clock variance so a slow run is distinguishable from a regression
      without re-reading three logs by hand.

## Subtasks

- [ ] Inventory the two families (`AssertHolds` call sites; real-clock comparisons in `Worker/` and
      `Pipelines/`) and record which ones can change an outcome and which only lengthen a run.
- [ ] Give `AssertHolds` a budget model: condition-driven by default, a stated ceiling, and a failure
      message that carries what it waited for.
- [ ] Decide the clock story for the deadline family - a controllable provider where the code allows it,
      a measured tolerance where it does not.
- [ ] Add the pressure fact and the still-fails-fast fact.

## Dependencies

- none. `PipelineRunnerTests` came from F-032 and `RunWorkerCancellationTests` from F-009; both are
  accepted.

## Notes and Decisions

- Rejected: raising the timeouts. That converts a red into a slow flake and hides the contention instead
  of measuring it - the tests would take longer and still disagree with themselves under load.
- This is not F-033's defect. It was filed while closing F-033, whose full run contained one such red that
  four isolated reruns could not reproduce.

## Which clock a fact uses (so a green is readable)

- **Real wall clock**: every fact that observes the outside world (worker processes, recovery, concurrency
  holds, the pipeline executor). `TimingAssert` samples those with a budget; the budget is reported on failure
  and is never a knob to raise.
- **Frozen clock** (`Zenkins.Tests.Pipelines.ManualClock`): exactly the two clamp cases in
  `PipelineRunnerTests.The_step_limit_is_clamped_by_the_runs_remaining_deadline_and_the_log_says_which_fired`.
  Their answer is a property of the fixture ("two seconds left"), not of how quickly the process was scheduled.
- **Production** is unchanged: `PipelineRunner`'s clock parameter defaults to `TimeProvider.System`.

## Facts added by this card

- `TimingAssertTests` (4): a hold fails on the **first** sample that does not hold (a hold→break→hold sequence
  reddens it), a hold that never breaks returns, a wait that never holds fails naming what it waited for, how
  long and the budget, and a wait returns as soon as the condition holds.
- `LoadPressureTimingTests` (1, `Category=Load`, its own non-parallel collection): under `ProcessorCount`
  spinner threads a true property still holds, a false one still fails **within** its budget (a
  timeout-shaped "pass" would show up here), and the clamp decision is still "the run's deadline" against two
  seconds. The pressure threads have a hard timeout and are stopped in `finally`, and the fact asserts they
  stopped - a leaked spinner is worse than a red. It is **out of the fast filter** (`Category!=Load`, added to
  `docs/development.md` and `scripts/verify.sh`); the gate's own filter string must add it too.

## Verification (replaces the earlier "four rounds" line)

1. **Three full runs** of the same tree and filter, each recording (a) wall clock, (b) the failing tests' own
   time, (c) the suite's self-reported duration.
2. **A fourth full run under CPU pressure** (`ProcessorCount` busy loops): 0 red.
3. **Targeted reruns are the verdict**: each family's `--filter` **10 times, 5 of them under pressure** - a
   full run does not reach a flake, this does.
4. **Pass line**: 4/4 full runs green **and** 20/20 targeted green **and** wall-clock range within ±10% (or the
   difference attributed to contention and reported as such).
## How a failing window describes itself

The description of a hold is the **call site's own lambda expression**, normalized to one readable
sentence (whitespace collapsed, `async () =>` and braces stripped, truncated past 140 characters). That is
more accurate than a hand-written sentence - it names the exact assertions - and it cannot drift away from
the code it describes. Where the lambda is too long to read, a call site passes its own `description:`
(the two longest windows in `RecoveryLeaseTests` do), so the override is the exception rather than ten
edits. `TimingAssertTests.A_call_sites_lambda_reads_as_one_sentence_in_the_failure` pins both halves.
## What the load fact runs inside its window, and what it deliberately does not

1. **Inside the window** the fact exercises `RunDeadlineComparison.OwnGoverns` - the real product decision
   that picks which limit is earlier - plus one synthetic invariant (held across the window, with the
   sample-span guard) and one condition-driven wait that must finish inside it.
2. **Not covered**: the executor (`PipelineRunner`) running a real step under contention. Driving it from
   this fact would mean lifting `PipelineRunHarness` out of `PipelineRunnerTests`' private nesting, which
   reaches into card F-032's freshly stabilised tests - and this card's subject is the *semantics of hold and
   wait under load*, not whether the executor works.
3. **When to revisit**: the moment either the hold/wait implementation or the executor's timing changes, lift
   the harness to a shared `internal` helper and run a real step inside the window. Until then the evidence
   is the two layers that do exist: contest is *measured* (CPU time against wall time, per-spinner iteration
   counts) and both semantics carry their own mutation.

## Close-out verification (2026-11-18)

The card's own subject made this the hardest one to accept, because every property it promises is one that
looks fine when nothing else is running.

- **The real culprit was a race, not a slow comparison.** `PipelineRunner` decided which limit governs by
  comparing against `DateTimeOffset.UtcNow`, and - the part that actually produced the flake - armed its
  two-second cancellation in the constructor, before a saturated machine had started the step. Freezing
  only the comparison left the race intact: with a 2.5 s startup delay injected, the old arming still
  failed (`Expected "Failed" / Actual "Skipped"`, the run cancelled before the step began) while arming
  after the step reports `Running` passes. `TimeProvider` is injected with `System` as the default, so
  production behaviour is unchanged; one test class uses a frozen clock, and the card says which.
- **`AssertHolds` kept its meaning.** It still fails at the first sample that does not hold - a
  hold/break/hold sequence reddens - and the span guard (`samples >= 2`, first-to-last >= 80% of the
  budget, and a non-zero budget) moved into `TimingAssert` itself rather than living in a load test, so
  "the window was actually sampled across" is a property of the helper.
- **The three same-shaped helpers all go through it** (`AssertHoldsAsync`, `AssertRunHoldsStatusAsync`,
  `AssertRunSettlesInAsync`), failures carry their lambda text by `[CallerArgumentExpression]` with the
  two longest windows overriding it, and `WaitUntilAsync` finally has production callers - the three
  "wait for a worker to go Offline" sites - which is the distinction the helper was meant to express:
  "will arrive" is not "must hold".
- **The load fact measures contention instead of assuming it.** Spinners handshake before the window
  opens, each reports its own iteration count (a sleeping spinner fails), and the gate is CPU time burned
  by this process rather than timer jitter - measured at 10 ms quiet versus 16 ms saturated, too narrow to
  discriminate. Three mutations redden it: spinner sleeping, a zero budget, and a single spinner.
- **What it does not cover is written down.** The window runs a real decision function
  (`RunDeadlineComparison`) plus the helper's own hold and wait, not the executor; closing that gap means
  promoting `PipelineRunHarness` out of its private nesting, which was deliberately not paid for here, and
  the card says when it should be.
- **`scripts/suite-duration.sh` answers AC-5.** One `--output detailed` log now yields the summary, the
  slowest areas with their share of summed test time, the failed tests and the slowest single tests, and a
  log without per-test timings is refused rather than summarised as zeros.
- **The Windows stability protocol was deferred by instruction**, not skipped silently: three full runs, a
  fourth under external CPU pressure, and twenty targeted replays were planned while full runs were still
  per-card. The pipeline stopped running the suite per card partway through this card, so the numbers
  belong to the single batch run that closes the series; until that run exists this card's claim is the
  narrower one - the facts above hold on this machine, and the flake's mechanism is fixed rather than
  timed around.
