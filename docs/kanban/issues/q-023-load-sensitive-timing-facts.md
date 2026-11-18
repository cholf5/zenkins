---
id: Q-023
title: The timing facts fail under load and pass alone, so a red full run does not mean a regression
type: test
priority: P2
size: M
status: todo
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
- **`AssertHolds` with a fixed observation window**: 15 call sites in 10 files (`Recovery/*`,
  `Concurrency/*` and others). Under CPU saturation a three-to-five second window turns from "wait for the
  condition" into "the condition never came", and each such wait also lengthens every other test's wall
  time.

Neither family shares a lock with the other; they compete for CPU and for timers.

## Acceptance Criteria

- [ ] A fact proves the timing family holds **while the machine is busy** - the only evidence that
      matters, because every one of these tests already passes when it runs alone.
- [ ] A wait that gives up says so: the failure names the condition, how long it waited and the budget it
      had, instead of only "collection did not contain any matching items".
- [ ] The deadline family either stops comparing against the wall clock or documents why it must and is
      made load-tolerant; a skipped wait is never silently converted into a pass.
- [ ] A fact fails fast when it should still fail fast - making waits longer must not be able to turn a
      real failure into a pass, and a mutation that breaks the behaviour under test must still redden it.
- [ ] The suite reports its own wall-clock variance so a slow run is distinguishable from a regression
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
