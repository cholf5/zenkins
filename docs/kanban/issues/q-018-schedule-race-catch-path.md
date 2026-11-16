---
id: Q-018
title: The schedule's lost-race catch path has no committed fact, and it drops the candidate it caught
type: test
priority: P3
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

The one path F-024's fix touched — a writer committing between the tick's ledger read and its save —
is covered by a fact that provokes it deterministically, and that path stops discarding the due time
it caught.

## Background

Both found by F-024's second review round (2026-11-16), after the fix that moved `DecideAsync` inside
the `ScheduleOccurrences` catch. Neither weakens an acceptance criterion; the reviewer called them
"correct behaviour, missing one fact and one sentence."

1. **The card says this path cannot be provoked from outside. It can.** F-024's Verification section
   claims a writer committing between the tick's ledger read and its save "cannot be produced
   deterministically from outside". The reviewer produced it: an open transaction holding the same
   `(ScheduleId, DueAt)` entry blocks the tick's save for ~2001 ms, then commits, the tick takes a
   23505 and the catch reports `Raced=1 / Failed=0 / runs=0` — where the pre-fix code reported
   `Failed=1`. So the M1 fix is genuinely untested in its own path: the committed fact
   (`A_missed_backlog_and_a_due_time_to_queue_survive_a_due_time_another_writer_recorded`) resolves
   through the earlier ledger check and never enters the catch (its assertions — `Queued=1`, four
   ledger rows — are mutually exclusive with a rollback).
2. **The catch swallows a due time silently.** `SettleAfterRaceAsync` advances the watermark to
   `cappedAt ?? now`, so the candidate that triggered the catch is neither queued nor recorded:
   measured `newestDueWas=12:01:00Z`, watermark after `12:01:30Z`, `queued=0 / rows=1 / runs=0`. No
   run, no ledger row, nothing on the page — while the card's Goal says every decision is recorded
   and visible. Safety is intact (nothing extra is queued); the window is narrow (a lock-bypassing
   writer must commit inside the tick's read→save gap).

## Acceptance Criteria

- [ ] A `Category=Postgres` fact provokes the catch deterministically: an external transaction holds
      the same `(ScheduleId, DueAt)`, the tick blocks, the holder commits, and the fact asserts
      `Failed=0`, `Raced >= 1`, `runs=0` — in the same collection as the other Postgres facts.
- [ ] F-024's "cannot be produced deterministically" sentence is corrected.
- [ ] The caught candidate is accounted for: either the tick re-evaluates after settling (the winner's
      ledger row is visible by then) or the settle stops at the last recorded due time instead of
      `now`, leaving the candidate for the next tick. Whichever is chosen, a fact shows the due time
      ends up either queued or recorded — not vanished.
- [ ] `docs/development.md` §2 counts re-recorded.

## Subtasks

- [ ] Promote the review's probe to a committed fact.
- [ ] Pick the candidate disposition and implement it.

## Dependencies

- none (F-024 is accepted)

## Notes and Decisions

- The reviewer's probe output, for reproduction: step 3 reports `tickBlocked=True waitedMs=2001
  failed=0 raced=1 queued=0 missed=0 rows=1 runs=0 watermark=…12:01:30Z newestDueWas=…12:01:00Z`, and
  step 4 shows the next tick healing (`queued=1 failed=0`).
