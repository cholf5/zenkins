---
id: F-024
title: Scheduled triggers — cron schedules per project, with an honest overlap and miss policy
type: feature
priority: P1
size: M
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A project can declare one or more cron schedules; the Server queues a run at each
due time with the build parameters' defaults, and every decision the schedule
makes (queued, skipped because a run is in flight, missed while the Server was
down) is recorded and visible.

## Background

The only triggers today are manual (`POST /projects/{id}/runs/start`) and the
signed webhook (`POST /webhooks/{id}`); F-027 adds the API. PRD §7.2 lists 定时任务
as a v1.0 non-goal and the roadmap parked it — this card reverses that decision,
so it also owns the PRD change.

There is harness to reuse: the Server already runs background services
(`LeaseSweeper`, `ArtifactSweeper`, `WorkerOfflineMonitor`) and already has a
row-lock pattern for exclusive work (`ZenkinsDbContext.LockWorkerForClaimAsync`),
plus a webhook dedupe mechanism (`WebhookDelivery.DedupeKey`) as the model for
"one due time, one run".

## Acceptance Criteria

- [x] A project can carry schedules (cron expression, IANA time zone, enabled);
      both kinds (Pipeline, Freestyle) and an inline project support them.
      *`ProjectSchedules` (cron ≤ 120 chars, IANA `TimeZone`, `Enabled`, `QueueAnyway`, watermark,
      attribution). The card lives on the settings tab for **both** kinds - a Freestyle project has no
      repository file to declare a schedule in - and the run it queues carries the project's frozen
      step spec. Pinned by `ScheduleTests.A_freestyle_project_can_carry_a_schedule_too` and the
      Pipeline facts beside it.*
- [x] A due schedule queues exactly one run, `TriggerType = Schedule`, no
      triggering user, parameters resolved to their defaults.
      *`RunService.QueueScheduledRunAsync` builds the run and stages it through the same
      `StageQueuedRunAsync` every other trigger uses (same project row lock, same F-028/F-029 gate,
      same build number). Pinned by
      `ScheduleTests.A_due_schedule_queues_one_run_with_the_defaults_and_never_twice` (a `COUNT=42`
      default arrives on the run; the second tick queues nothing) and
      `A_scheduled_run_is_an_ordinary_run_for_the_worker_contract` (claim → accept → complete).*
- [x] Overlap policy is explicit and recorded: by default a due time while any run
      of the project is queued or running is **skipped** (`skipped-overlap`), not
      stacked; a per-schedule "queue anyway" switch queues it instead (bounded by
      F-029's queue cap).
      *Pinned by `A_due_time_while_a_run_is_in_flight_is_skipped_and_names_the_run` (the skip carries
      the in-flight `#N` and the page shows it), `Queue_anyway_queues_the_second_run_instead_of_skipping`
      and `Queue_anyway_is_still_bounded_by_the_projects_queue_cap` (`skipped-queue-full`, nothing
      queued).*
- [x] No backfill: a Server that was down at a due time does not queue on startup.
      Each missed due time is recorded as `missed` with the time it was due, so the
      page can say "3 missed while the Server was down" instead of lying.
      *One evaluation queues at most the newest ordinary due time, and only when it is within
      `Schedules:MaxDueLatenessSeconds` **and** the previous evaluation was too; otherwise every due
      time in the window is `missed`. The record is bounded (`MaxRecordedMisses` rows, the remainder
      folded into the oldest kept row's `CollapsedDueTimes`; `MissWindowHours` for the enumeration) but
      the count is exact, and `ProjectSchedule.LastMissedCount` keeps it after the ledger's retention
      prunes rows. Pinned by
      `A_server_that_was_down_records_every_missed_due_time_and_queues_nothing` (32 due times, 0 runs,
      20 rows + 12 folded, page says 32) and
      `A_long_outage_lists_a_bounded_backlog_and_never_states_a_wrong_total`.*
- [x] A disabled project (F-028) or a disabled schedule queues nothing, and the
      reason is recorded the same way.
      *Both write `skipped-disabled` - the code `ProjectTriggerRefusals.SkippedDisabled` declared and
      nothing wrote before this card (F-028's AC2, schedule half). A disabled schedule is still
      evaluated, so every due time it passes is recorded rather than silently vanishing. Pinned by
      `A_disabled_project_records_skipped_disabled_and_queues_nothing` and
      `A_disabled_schedule_is_evaluated_but_records_a_skip`.*
- [x] Two schedulers racing the same due time cannot queue twice (row lock plus a
      unique (schedule, due-at) record).
      *Two mechanisms, and they are not interchangeable (review L2):*
      1. ***Two real schedulers* (two Server instances, or two ticks of one) never reach the insert.
      `LockScheduleForEvaluationAsync` serializes them on the schedule row; the loser re-reads the
      watermark the winner committed, finds nothing due and returns empty - `Raced` stays `0`.**
      *Evidence: the review's PostgreSQL probe - four concurrent ticks, three rounds, exactly one run
      and one occurrence row per round; plus a positive control (an external transaction holding the
      row lock blocks a tick for 2003 ms before it proceeds) proving the no-op write really is taken
      before the decision.*
      2. ***`Raced` is the bypass-the-lock case only*** - a writer that does not take the schedule's
      row lock (an external process, a future trigger source). The tick detects the already-recorded
      due time *by reading the ledger* and treats it as answered: no run, no second row, and the
      watermark moves past it (review M1/M2 - an exception-only guard reported `Failed` and then
      retried the same due time forever). The unique index is the belt for a writer that commits
      between that read and the save; that path rolls the whole transaction back and settles the
      watermark the same way. *Pinned by
      `A_due_time_another_writer_already_recorded_queues_nothing`,
      `A_lost_due_time_does_not_stall_the_schedule_and_the_next_one_queues` and
      `A_missed_backlog_and_a_due_time_to_queue_survive_a_due_time_another_writer_recorded` (the
      shape review M1 found: missed rows **and** a run to queue in one tick, with one due time already
      recorded).*
- [x] Invalid cron or an unknown time zone is refused at save with a field error;
      the next three due times are previewed before saving.
      *`cron-required`, `cron-invalid`, `cron-never-fires`, `timezone-required`, `timezone-unknown`,
      `schedule-limit`, `schedule-duplicate`, `field-invalid` - each rendered by the settings tab from
      the PRG's `?error=`. The create form's "Preview next runs" submits the same values and stores
      nothing. Pinned by
      `ScheduleTests.The_card_saves_a_valid_schedule_and_refuses_an_invalid_one_with_a_field_code`,
      `The_card_previews_the_next_three_due_times_before_anything_is_saved`, and the
      `ScheduleCronTests.Malformed_expressions_are_refused_with_a_field_code` theory (15 shapes).*
- [x] PRD §3.12 (new) documents schedules; §7.2 drops 定时任务 from the MVP
      non-goals with the date and this card's id.
      *§3.12 was already the organization API (F-027), so the schedules section landed as **§3.13**
      (and §3.5 gained the overlap/miss policy the card asked to have written down). §7.2's bullet is
      gone, with a dated change record naming this card, and §7.1's build-management list now names
      scheduled triggers.*

## Subtasks

- [x] `ProjectSchedule` entity: project, cron (5-field), time zone, enabled,
      queue-anyway, last evaluated at, and an audit of the last N outcomes.
      *The audit is `ScheduleOccurrence` - one row per (schedule, due time) with the outcome, the run
      it queued (or the number of the run that was in flight), the folded miss count and a detail code;
      the newest `Schedules:RetainedOutcomes` are kept.*
- [x] `ScheduleEvaluator`: next-due computation for a 5-field cron in a IANA zone,
      DST-aware (a 02:30 that does not exist must be decided, not crashed on —
      pick "skip that day and record it").
      *`ScheduleCron` (Vixie semantics: named ranges/steps/lists, 0 and 7 both Sunday, the day-of-month
      OR day-of-week rule, no seconds/macros/`H`) plus `ScheduleEvaluator` (day scan in the zone's own
      calendar, bounded lookahead). The gap is reported as `skipped-nonexistent` at the transition
      instant and never queued; a repeated hour fires once, at the earlier instant.*
- [x] `ScheduleSweeper` background service: due rows under a row lock, one run per
      (schedule, due-at), outcome recorded; tick interval configurable for tests.
      *`Schedules:TickSeconds`; `SweepOnceAsync` is the tests' seam (no sleeps in the fast suite).*
- [x] Trigger reuse: the queue path is `RunService.CreateRunAsync` (parameters
      defaults, tag validation, kind guards), never a parallel insert.
      *`RunService.QueueScheduledRunAsync` → `StageQueuedRunAsync` (the one insert path, shared with the
      form, the API, the webhook and F-037's re-runs).*
- [x] UI: a Schedules card on the project settings tab (list, add, enable/disable,
      delete, next-due preview, last outcomes), Owner-or-Member per existing rules.
      *Plus the "queue anyway" switch and the preview button; the next three due times are shown in UTC
      and in the schedule's own zone, and the last ten outcomes are listed with their sentences.*
- [x] PRD update as described in the acceptance criteria.

## Dependencies

- F-021 (parameter defaults for headless triggers) — landed; the scheduled run resolves the
  definitions' defaults exactly as a webhook delivery does.
- F-029 (queue cap) — landed; "queue anyway" is bounded by it and records `skipped-queue-full`.

## Test Cases

### TC-001: A due schedule queues one run with defaults

Steps:
1. Seed a project with a parameter whose default is `42` and a schedule due now
   (test uses a sub-minute cron or an injected clock).
2. Let the sweeper tick once, then tick again.

Expected:
- Exactly one run exists, `TriggerType = Schedule`, parameter value `42`.
- The second tick queues nothing (same due time already handled).

*Covered by `ScheduleTests.A_due_schedule_queues_one_run_with_the_defaults_and_never_twice`, which
uses a `COUNT=42` default and an injected clock (the schedule service's `TimeProvider`) so "due now" is
an exact instant rather than a sleep.*

### TC-002: An in-flight run makes the due time skipped, and it says so

Steps:
1. Leave one run of the project queued (no worker).
2. Let a due time arrive.

Expected:
- No second run is queued; the schedule records `skipped-overlap` with the
  in-flight run's number.
- The project page shows the skip as the last outcome.

*Covered by `ScheduleTests.A_due_time_while_a_run_is_in_flight_is_skipped_and_names_the_run`; the page
assertion reads the settings tab's Schedules card, which is where the schedule's outcomes live.*

### TC-003: A restart does not backfill

Steps:
1. Record a due time that passed while "the Server was down" (seed `last
   evaluated` before it).
2. Start the sweeper and let it tick.

Expected:
- No run is queued for the passed due time; it is recorded as `missed`.
- The next future due time is scheduled normally.

*Covered by `A_server_that_was_down_records_every_missed_due_time_and_queues_nothing` (a 32-minute
watermark gap) and `A_long_outage_lists_a_bounded_backlog_and_never_states_a_wrong_total` (three
days).*

## Notes and Decisions

- Schedules live in a table, not in the Lua `triggers{}` block: a Freestyle or
  inline project has no repository file to declare them in, and the clock is the
  Server's. A `triggers` cron block in `pipeline.lua` may later materialize into
  rows at freeze time; that is explicitly not in this card.
- Rejected: Jenkins' `H` hash. It spreads load across a large job count; here the
  queue is cheap and deterministic times are easier to explain. Revisit if a
  deployment reports a thundering herd.
- **The card left four things open; this is what they became** (all four are in PRD §3.5 and §3.13):
  1. *How late may a due time still queue?* `Schedules:MaxDueLatenessSeconds`, default 300 s, **and**
     the previous evaluation must itself be that fresh. Two conditions, because a lateness bound alone
     cannot tell "the tick was 20 seconds late" from "the Server was down for 20 minutes" with a
     per-minute cron.
  2. *How large may the miss record grow?* Bounded twice: `MaxRecordedMisses` (20) rows per evaluation
     with the remainder folded into the oldest kept row (`CollapsedDueTimes`, so the count stays
     exact), and `MissWindowHours` (24 h) for the enumeration (older misses become one "earlier misses
     are not listed" note). `ProjectSchedule.LastMissedCount` keeps the last evaluation's number so
     the ledger's retention cannot shrink what the page states.
  3. *What happens at a DST boundary?* A local time the zone skips is `skipped-nonexistent` (recorded
     at the transition instant, never queued, never moved to another clock time); a repeated local
     time fires once at the earlier instant. Both are unit-tested in both directions and the gap is
     also driven through a real tick.
  4. *What does a due time look like in the record?* One row per (schedule, due time) with a closed
     outcome vocabulary: `queued`, `skipped-overlap`, `skipped-disabled`, `skipped-queue-full`,
     `skipped-refused` (+ a detail code, today only `steps-missing`), `missed`, `skipped-nonexistent`.
- **`skipped-disabled` is written by this card** (F-028's AC2, schedule half): the constant existed
  with no writer. F-028's AC2 stays unticked for the poll half only.
- **New: the duplicate-schedule index.** `(ProjectId, Cron, TimeZone)` is unique, so the same
  expression twice on one project (which would queue two runs for every due time) is a field error
  rather than a silent double. Also new: `MaxSchedulesPerProject` = 20.
- **New: the sweeper's clock is injectable** (`TimeProvider`, the F-017 pattern). Production reads
  the system clock; the tests state an instant so "a due time arrived" is exact. The row's
  `CreatedAt`/`UpdatedAt` remain the context's audit stamps (real clock) by design.

## Verification (2026-11-16)

`scripts/verify.sh` green in 503 s (build, fast suite, six harnesses) - re-run after review round 1,
2026-11-16. Measured counts, this revision: fast suite **1220** (0 failed, 0 skipped), Milestone 12,
Recovery 4, PublishSmoke 2, PublishAot 2, GitSshd 3, Postgres 3 (skipped without
`ZENKINS_TEST_POSTGRES`, as designed) - **all seven 1246** (baseline before this card: 1172 / 1198,
so this card adds 48). Full output:
[`artifacts/logs/f-024-gate.log`](../../../artifacts/logs/f-024-gate.log) (per-suite runs beside it).

What the 48 are: 24 cron/zone arithmetic facts (9 facts + one 15-row theory), 18 schedule facts
(`tests/Zenkins.Tests/Schedules/ScheduleTests.cs`), 6 tenancy probes for the two new tenant-owned
entities.

The two-scheduler race on real PostgreSQL was verified by the review, not here (this machine has no
PostgreSQL instance: the suite's three facts skip as designed): four concurrent ticks, three rounds,
one run and one occurrence row per round, plus a lock-block positive control. Its DST probe used
concrete dates in a second zone (America/New_York: 2026-03-08 yields exactly one `Nonexistent` entry
and is not moved to another clock time; 2026-11-01 fires the earlier of the two 02:30s).

Not covered, and why: a real *process* restart in the `Recovery` harness for a schedule (the
no-backfill rule is proven by the watermark facts, not by killing a Server mid-outage). The
unique-index *exception* path is covered, and this paragraph used to say it could not be (corrected
by card Q-018): a writer committing between the tick's ledger read and its save **is** produced
deterministically, by a second connection that inserts the same `(ScheduleId, DueAt)` and does not
commit. The tick's INSERT then blocks on `IX_ScheduleOccurrences_ScheduleId_DueAt`, and the fact
*observes* that wait in `pg_stat_activity` before it commits the holder, so the resulting 23505 is
triggered by an event rather than by a timed window. It is
`PostgresScheduleRaceTests.A_missed_backlog_and_the_due_time_to_queue_survive_a_due_time_another_writer_recorded`,
a `Category=Postgres` fact in `PostgresCollection` with the other PostgreSQL facts, and its shape is
the one review M1 widened the guard for: a missed backlog plus a newer due time to queue, with the
rival holding one of the *missed* due times, so the refusal surfaces from inside `DecideAsync`. Honest
bounds: SQLite's single writer cannot hold that row for a second transaction, so this needs a real
PostgreSQL, sits outside the fast filter and skips with the recorded reason without
`ZENKINS_TEST_POSTGRES`. Its outcome clause is no longer "no run": the refusal rolls the candidate
back with the rest of the attempt, so the tick re-evaluates once after it (card Q-018), records the
remaining backlog and queues that due time itself - `Failed = 0`, `Raced = 1`, `Missed = 2`,
`Queued = 1`, the rival's row left exactly as the rival wrote it and no run for it.

## Review round 1 (2026-11-16) - the two Mediums and the two Lows

Verdict was PASS on the eight ACs; these four came out of the same read and are fixed here.

- **M1 - a tick with a missed backlog *and* a run to queue reported `Failed` instead of `Raced`.** The
  run's own staging save flushes the pending occurrence rows with it, so the unique-index refusal could
  surface from inside `DecideAsync`, outside the tick's guard. *Fixed:* `DecideAsync` now runs inside
  the same `try`, so both saves share one catch. **New fact** for the shape that was missing:
  `A_missed_backlog_and_a_due_time_to_queue_survive_a_due_time_another_writer_recorded` (four due
  times in the window, one already recorded, three misses to write, one run to queue -> `Failed = 0`,
  `Raced = 1`, `Missed = 2`, `Queued = 1`).
- **M2 - the `Raced` branch did not advance the watermark, so the schedule retried the same due time
  forever.** *Fixed twice over:* the tick now reads the ledger first and treats an already-recorded due
  time as answered (so the common case never reaches an exception), and the exception path calls
  `SettleAfterRaceAsync`, which advances the watermark in a fresh context/transaction (forward only,
  never backwards). **New fact:**
  `A_lost_due_time_does_not_stall_the_schedule_and_the_next_one_queues` (the lost tick leaves no run
  and a moved watermark; the next minute queues exactly one run).
- **L1 - the page's miss count could read as the whole outage.** `LastMissedCount` is a 24-hour-window
  number, and the "earlier misses are not listed" sentence lived on the oldest *retained* row, which
  the card never shows (it lists the newest ten). *Fixed:* `ProjectSchedule.LastMissedWindowTruncated`
  records that the window was capped, and the count sentence itself now says "Only the newest 24 hours
  are counted; earlier missed due times are not listed" when it was. Asserted in
  `A_long_outage_lists_a_bounded_backlog_and_never_states_a_wrong_total` and in PRD §3.5.
- **L2 - AC6's evidence sentence conflated the two mechanisms.** Split above: the row lock is what
  makes two schedulers safe (and `Raced` stays `0` for them); `Raced` describes a writer that bypassed
  the lock.

### 2026-11-16 — independent review: PASS (second round)

Two rounds in an isolated copy of the uncommitted batch, the second on a temporary PostgreSQL 16.15
cluster. All eight criteria satisfied.

The criterion the implementer had left uncovered — two schedulers racing one due time — turned out to
be the interesting one, in a way neither side expected. On a real server, the two-scheduler case
never reaches the insert at all: the row lock serializes them, the loser re-reads the advanced
watermark and returns without queuing, and `Raced` stays `0`. The reviewer proved the lock is what
does it, positively: an open transaction holding the schedule row blocks a tick for 2003 ms before it
proceeds. The SQLite probe that "proved" this criterion had only ever inserted a row by hand and then
run one tick, so it could show the unique index exists and that a conflicting tick rolls back whole,
but nothing about serialization.

Two medium findings came out of that, both in the fallback path the product's own code cannot reach:
the unique-violation translation lived outside the `try` that was supposed to catch it, so a tick
carrying both missed rows and a candidate reported `Failed` instead of `Raced`; and the `Raced` branch
did not advance the watermark, so a lock-bypassing writer left the schedule in a permanent
`Failed`-per-tick loop that never queued again. Both are fixed — the decision moved inside the catch,
and a tick now reads the ledger first (making it idempotent) while a genuine race settles the
watermark forward in its own transaction. The reviewer re-ran both shapes on the server: the catch
path blocks 2001 ms, reports `Raced=1 / Failed=0 / runs=0`, advances the watermark, and the next tick
queues normally.

Also verified on the real server: a 32-minute outage produces zero runs with twenty ledger rows
folding twelve, `LastMissedCount=32`, and a three-day outage reports `1440` with the window-truncation
marker; DST in both directions on concrete dates (`America/New_York` 2026-03-08 yields exactly one
`Nonexistent` at the transition instant and no shifted 03:30; 2026-11-01 fires once at the earlier
instant); and `BuildRuns.Add` still appears only inside `StageQueuedRunAsync`, so a scheduled run is
an ordinary run.

Two lows are Q-018: the catch path itself has no committed fact (the card wrongly claims it cannot be
provoked, and the reviewer provoked it), and the catch discards the due time it caught — no run, no
ledger row, nothing on the page.

`SkippedDisabled` now has a writer, so F-028's AC2 is complete on the schedule surface and still
correctly unticked on the poll one.
