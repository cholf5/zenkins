---
id: Q-018
title: The schedule's lost-race catch path has no committed fact, and it drops the candidate it caught
type: test
priority: P3
size: S
status: done
created: 2026-11-16
updated: 2026-11-17
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

- [x] A `Category=Postgres` fact provokes the catch deterministically: an external transaction holds
      the same `(ScheduleId, DueAt)`, the tick blocks, the holder commits, and the fact asserts
      `Failed=0`, `Raced >= 1`, `runs=0` — in the same collection as the other Postgres facts.
- [x] F-024's "cannot be produced deterministically" sentence is corrected.
- [x] The caught candidate is accounted for: either the tick re-evaluates after settling (the winner's
      ledger row is visible by then) or the settle stops at the last recorded due time instead of
      `now`, leaving the candidate for the next tick. Whichever is chosen, a fact shows the due time
      ends up either queued or recorded — not vanished.
- [x] `docs/development.md` §2 counts re-recorded.

**AC-1's `runs=0` is superseded, deliberately.** Two things review round 1 established about that
clause. (a) The due time the rival holds decides *where* the refusal surfaces: hold the **candidate's**
own due time and the conflict comes from the tick's final `SaveChanges`, which sits inside the guard
both before and after review M1 — a green fact that proves nothing about the fix. The M1 shape is a
**missed backlog plus a newer due time to queue**, with the rival holding one of the *missed* due
times, because the run's staging save flushes the missed rows and the refusal then surfaces from
inside `DecideAsync`. (b) In that shape a disposition that accounts for the candidate (AC-3) *queues*
it, so `runs=0` and AC-3 cannot both hold in one scenario. The fact therefore keeps AC-1's
`Failed=0` / `Raced >= 1` and states the `runs=0` intent where it is still true: the rival's due time
gets no run of its own (its ledger row is left as the rival wrote it, `RunId` null) and nothing is
queued twice. The `{Failed=0, Raced>=1, runs=0}` set on its own would have proved nothing — the
pre-Q-018 tree satisfies it — so the fact's load-bearing assertions are the AC-3 ones.

## Subtasks

- [x] Promote the review's probe to a committed fact.
- [x] Pick the candidate disposition and implement it.

## Dependencies

- none (F-024 is accepted)

## Notes and Decisions

### The disposition: re-evaluate once, and never settle past an undecided due time

`ScheduleService.EvaluateOneAsync` is now a bounded loop over `EvaluateAttemptAsync` (one attempt =
the previous whole body: row lock → window → ledger → staging → commit). A unique-violation refusal
rolls the attempt back and reports `LostRace`; the second attempt re-reads the ledger — **the winner
has committed by then**, which is what makes its rows visible — so the due time that started the
collision is counted as `Raced` if it was the winner's own row, or gets its decision (queued) if it was
not. The retry is bounded at one: a second refusal settles (`SettleAfterRaceAsync`) and returns
`Raced`, never `Failed`.

Why this disposition rather than "leave the candidate to the next tick": the due time is decided
inside the tick that found it, so the outcome, the run and the ledger row land together in one
transaction — nothing depends on the next sweep's timing, and the operator sees a run, not a hole
that heals a minute later. The measured loss had no run and no row at all, which is the state the
Goal forbids.

`SettleAfterRaceAsync` no longer takes a "stop at" instant: it stops at the **newest due time already
in the ledger at or before this tick's instant**. That is the safety net for the (rare) second
refusal, and it cannot skip work: a writer can only refuse this tick on a due time it has committed
itself, so the ledger holds at least one row, and the next tick's window starts where the recorded
decision ended — a candidate *newer* than that row stays inside the next window. Advancing to `now`
is exactly what moved the watermark past a due time no one had decided.

### The fact

`tests/Zenkins.Tests/Schedules/PostgresScheduleRaceTests.cs:69`
`A_missed_backlog_and_the_due_time_to_queue_survive_a_due_time_another_writer_recorded`, trait
`Category=Postgres`, `[Collection("Postgres")]` — the same collection as the other PostgreSQL facts
(they all drop `public`). One schedule, watermark four minutes back, four due times; a rival
connection inserts the **11:58** one (`Missed`) inside an open transaction; the tick's ledger read
cannot see it (READ COMMITTED), so its INSERT queues behind the rival on
`IX_ScheduleOccurrences_ScheduleId_DueAt`. The fact waits for that wait to appear in
`pg_stat_activity` (bounded poll, `wait_event_type='Lock'` **and** `query LIKE '%ScheduleOccurrences%'`
so a foreign lock wait cannot stand in for it), asserts the tick is still running, and only then
commits the rival. No sleep decides anything: the commit is triggered by the observed lock wait.

The refusal is raised from inside `DecideAsync` — the staging save flushes the missed rows with the
run — and the assertions are: `Failed=0`, `Raced=1`, `Queued=1`, `Missed=2`, one run
(`TriggerType=Schedule`, `Queued`), four ledger rows, the candidate's row `Queued` with that run's
id and number, the rival's row untouched and `RunId` null, and the watermark back at this tick's
instant with `LastMissedCount=3` (the whole window, the rival's miss included).

### Provisional instance and how it was run

`initdb` into a throwaway directory (its locale must be set, or the postmaster refuses to start on
macOS), `pg_ctl start` on a random free loopback port, `createdb zenkins_test`, export
`ZENKINS_TEST_POSTGRES`, run, then `pg_ctl stop` and delete the directory. The database name must
contain `test` — the suite's own guard against pointing it at a real database, whose `public` schema
it drops. No connection string, host or port is written into any tracked file.

### Mutations (each restored; `ScheduleService.cs` back at its committed hash afterwards)

| Mutation | Result |
|---|---|
| AC-3 reverted: no retry, settle advanced to `now` (the pre-Q-018 tree) | red at `Assert.Equal(1, result.Queued)`, `Expected 1 / Actual 0`; `Raced=1, Failed=0` still held, so the failure is the accounting, not the catch |
| Review M1 reverted: `DecideAsync` moved out of the `try` | red at `Assert.Equal(0, result.Failed)`, `Failed=1` (`0 raced`), and no settle — the exception reached the sweep's per-schedule catch |
| (none of the other facts moved) | Postgres 4/4, fast 1284 |

## Verification (2026-11-17)

- `dotnet build -c Release` — succeeded, 0 warnings.
- Fast suite (the sextuple exclusion filter of `docs/development.md` §2) — **1284 passed, 0 failed,
  0 skipped**, unchanged: the new fact is `Category=Postgres`.
- `--filter "Category=Postgres"` against a disposable PostgreSQL 16 instance — **4 passed, 0 failed,
  0 skipped** (3 existing + this card's).
- Post-Q-018 totals: fast 1284, Postgres 4, all seven 1311; `docs/development.md` §2 (the suite
  paragraph's (e), the table row, and the counted baseline note) re-recorded.
- Logs: `artifacts/q-018/` (`fast.log`, `postgres-run1.log`, `mutation-A.log`, `mutation-B.log`).

## Settled: AC-1's `runs=0`

**Settled 2026-11-17, accepted by independent review.** AC-1's literal `runs=0` and AC-3's disposition
cannot both hold in one scenario: in the only shape that covers the M1 fix, accounting for the candidate
means queueing it. The criterion is therefore read as *no run originates from the losing decision* —
the rival's due time yields no run, and its row is asserted untouched with `RunId` null — while the
candidate's run comes from the re-evaluation. Review adopted this reading independently, for three
reasons: the literal value is itself a false green (reverting the disposition satisfies
`{Failed=0, Raced>=1, runs=0}` too); the two criteria cannot coexist in the M1-catching shape; and the
losing-side claim is *asserted*, not dropped, and mutation M2 proves that half can still be falsified on
its own. Nothing here is an abandoned criterion: AC-1's provocation, `Failed=0` and `Raced>=1` all
remain, and the three-account half of the proof moved to AC-3, where it is separately falsifiable.

The other disposition (leave the candidate for the next tick) was not taken: it makes AC-1's wording
literally true again, at the cost of the window healing a sweep later and the ledger row arriving from a
different tick than the run.

## Verification

Independent review PASSed all four criteria on its own PostgreSQL instance (16.15, `zenkins_test`, random
loopback port, stopped and deleted afterwards) and re-applied its mutations in the shared worktree,
restoring every file and checking the shas.

- **The fact has the shape that can actually catch the fix, proven by a stack rather than by a comment.**
  Under mutation M1 (`DecideAsync` moved out of the catch) the exception walks
  `SaveStagedRunAsync:1166 -> StageQueuedRunAsync:1013 -> QueueScheduledRunAsync:1596 -> DecideAsync:891`.
  Only a rival holding a **missed** due time -- so the run-staging save flushes the backlog and the 23505
  surfaces inside `DecideAsync` -- produces that stack. A rival holding the candidate's own due time would
  collide at the final save instead, where the code is inside the `try` both before and after the fix:
  green under the mutation, and hollow.
- **The three assertions AC-1 originally named are not evidence on their own.** HEAD already satisfies
  `{Failed=0, Raced>=1, runs=0}`, and `Raced` is `Math.Max(1, raced)`, so it is >= 1 the moment the catch
  is entered. The fact therefore carries the AC-3 accounting as well, and the reviewer's M2 shows that
  half fails independently: reverting the disposition reds `Queued` (1 vs 0) while `Failed` and `Raced`
  still pass.
- **Determinism measured, not assumed.** The single fact ran **8/8 green**: six quiet runs at
  5.82-6.51 s, then two under six `yes` processes at load average 7.05 (8.31 s and 9.41 s). There is no
  sleep: the release point is driven by observing `wait_event_type='Lock'` with a `ScheduleOccurrences`
  query in `pg_stat_activity`, gated by `Assert.False(tick.IsCompleted)`; the failure path rolls the
  rival back and awaits the tick so a red run cannot leave a blocked backend behind.
- **AC-3's disposition is proven by its ledger, not by a run count.** Re-evaluate once, in the same tick;
  the settle (now only a fallback for a second consecutive refusal) stops at the newest due time already
  in the ledger at or before now. The fact asserts the candidate's row `Queued` carrying that run's id
  **and** number, four due times and four ledger rows, `Missed=2`, watermark `Now`, `LastMissedCount=3`.
- **AC-2's replacement sentence carries all five parts** (`f-024:249-266`): it says the old claim was
  wrong, how the race is built, that the wait is observed rather than timed, names the fact with its
  suite and collection, keeps the honest bounds (SQLite's single writer cannot do it; outside the fast
  filter; skips without `ZENKINS_TEST_POSTGRES`), and rewrites the outcome to `Failed=0 / Raced=1 /
  Missed=2 / Queued=1` so the old "no run" reading is gone.
- **Counts.** Fast **1284** (unchanged: the new fact is Postgres-only), Postgres **3 -> 4**, all seven
  **1311** = 1284 + 12 + 4 + 2 + 2 + 3 + 4. `docs/development.md` agrees in the per-card note, the §2
  clause, the Fast row, the Postgres row and the all-seven line. No existing fact was loosened:
  `tests/Zenkins.Tests/Schedules` shows the new file and nothing else.
- **Observed and not covered.** Mutation M3 (removing the settle's forward-only guard) survives -- that
  guard was inherited from HEAD and is only reachable after two consecutive refusals, which needs three
  writers inside one window; AC-1 does not ask for a fact there. Recorded as a follow-up rather than
  boarded here.
- **Settled at close-out.** The card's `runs=0` question is now a decision, with the reading, the reason
  the literal value is a false green, and the disposition not taken, so it cannot be read later as an
  abandoned criterion.
- Logs: `artifacts/q-018/` (both sides) - `mutation-A.log`, `mutation-B.log`, `postgres-run1.log`,
  `review-verdict.md`, `review-mutation-*.log`, `review-postgres.log`.
