---
id: F-024
title: Scheduled triggers — cron schedules per project, with an honest overlap and miss policy
type: feature
priority: P1
size: M
status: todo
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

- [ ] A project can carry schedules (cron expression, IANA time zone, enabled);
      both kinds (Pipeline, Freestyle) and an inline project support them.
- [ ] A due schedule queues exactly one run, `TriggerType = Schedule`, no
      triggering user, parameters resolved to their defaults.
- [ ] Overlap policy is explicit and recorded: by default a due time while any run
      of the project is queued or running is **skipped** (`skipped-overlap`), not
      stacked; a per-schedule "queue anyway" switch queues it instead (bounded by
      F-029's queue cap).
- [ ] No backfill: a Server that was down at a due time does not queue on startup.
      Each missed due time is recorded as `missed` with the time it was due, so the
      page can say "3 missed while the Server was down" instead of lying.
- [ ] A disabled project (F-028) or a disabled schedule queues nothing, and the
      reason is recorded the same way.
- [ ] Two schedulers racing the same due time cannot queue twice (row lock plus a
      unique (schedule, due-at) record).
- [ ] Invalid cron or an unknown time zone is refused at save with a field error;
      the next three due times are previewed before saving.
- [ ] PRD §3.12 (new) documents schedules; §7.2 drops 定时任务 from the MVP
      non-goals with the date and this card's id.

## Subtasks

- [ ] `ProjectSchedule` entity: project, cron (5-field), time zone, enabled,
      queue-anyway, last evaluated at, and an audit of the last N outcomes.
- [ ] `ScheduleEvaluator`: next-due computation for a 5-field cron in a IANA zone,
      DST-aware (a 02:30 that does not exist must be decided, not crashed on —
      pick "skip that day and record it").
- [ ] `ScheduleSweeper` background service: due rows under a row lock, one run per
      (schedule, due-at), outcome recorded; tick interval configurable for tests.
- [ ] Trigger reuse: the queue path is `RunService.CreateRunAsync` (parameters
      defaults, tag validation, kind guards), never a parallel insert.
- [ ] UI: a Schedules card on the project settings tab (list, add, enable/disable,
      delete, next-due preview, last outcomes), Owner-or-Member per existing rules.
- [ ] PRD update as described in the acceptance criteria.

## Dependencies

- F-021 (parameter defaults for headless triggers) — in review
- F-029 (queue cap, if the "queue anyway" switch lands after it, document the
  interim behaviour)

## Test Cases

### TC-001: A due schedule queues one run with defaults

Steps:
1. Seed a project with a parameter whose default is `42` and a schedule due now
   (test uses a sub-minute cron or an injected clock).
2. Let the sweeper tick once, then tick again.

Expected:
- Exactly one run exists, `TriggerType = Schedule`, parameter value `42`.
- The second tick queues nothing (same due time already handled).

### TC-002: An in-flight run makes the due time skipped, and it says so

Steps:
1. Leave one run of the project queued (no worker).
2. Let a due time arrive.

Expected:
- No second run is queued; the schedule records `skipped-overlap` with the
  in-flight run's number.
- The project page shows the skip as the last outcome.

### TC-003: A restart does not backfill

Steps:
1. Record a due time that passed while "the Server was down" (seed `last
   evaluated` before it).
2. Start the sweeper and let it tick.

Expected:
- No run is queued for the passed due time; it is recorded as `missed`.
- The next future due time is scheduled normally.

## Notes and Decisions

- Schedules live in a table, not in the Lua `triggers{}` block: a Freestyle or
  inline project has no repository file to declare them in, and the clock is the
  Server's. A `triggers` cron block in `pipeline.lua` may later materialize into
  rows at freeze time; that is explicitly not in this card.
- Rejected: Jenkins' `H` hash. It spreads load across a large job count; here the
  queue is cheap and deterministic times are easier to explain. Revisit if a
  deployment reports a thundering herd.
