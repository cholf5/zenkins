---
id: F-010
title: Offline detection → Lost runs
type: feature
priority: P1
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

A Worker that stops heartbeating is marked Offline by the Server and receives no new assignments;
runs still `Running` on it are marked `Lost` (result unknown) — never silently retried, never
assumed failed.

## Background

PRD §3.4.5: "Worker 离线后，Server 将其标记为 Offline，不再分配新任务。正在执行的任务标记为 Lost
或 Unknown，不能简单地假定任务已失败或自动重试。" Roadmap candidate 8. `Lost` is a statement that
the result is unknown — the run must not be requeued, and a returning worker must not resurrect it.

Decisions:

- Staleness threshold: server configuration (`Workers:OfflineAfterSeconds`, default `60`, i.e. a few
  missed heartbeats at the default 15 s interval). A background monitor evaluates it.
- On `Online → Offline` transition: every `Running` run of that worker becomes `Lost` with
  `EndedAt` and `FailureReason = worker-offline`; the worker's claimed-but-unaccepted lease is just
  requeued by F-006's sweeper (never accepted ⇒ never started ⇒ safe to requeue; that is not a
  retry of an executed run).
- Heartbeat resuming (or re-registration) flips the worker back Online; its `Lost` runs stay `Lost`.

## Acceptance Criteria

- [ ] A worker missing heartbeats past the threshold is marked `Offline` by the monitor; before the
      threshold it stays `Online` (no flapping at one missed beat).
- [ ] `Running` runs on that worker become `Lost` with end time and reason `worker-offline`; the
      transition is recorded and visible on the run page.
- [ ] `Lost` runs are never requeued, re-claimed or retried; a returning worker's heartbeat does not
      change them.
- [ ] Offline workers are excluded from claims (claim returns nothing for them) and the scheduler
      never targets them.
- [ ] Lease-expiry requeue (F-006) still applies to claimed-but-unaccepted runs of an offline
      worker, and only to those.
- [ ] Threshold is configurable and honoured in tests (short threshold, controllable timing).

## Subtasks

- [ ] Heartbeat staleness monitor (background service, config) with Online/Offline transitions.
- [ ] Offline transition handler: Running → Lost (reason, end time).
- [ ] Claim/scheduler exclusion of Offline workers (wire with F-006).
- [ ] Run page + workers page show `Lost`/`Offline` states with reasons.
- [ ] Tests (`Category=RunLifecycle`): threshold timing, Lost transition exactly once, no-retry
      invariant, claim exclusion, lease requeue only for unaccepted runs, recovery to Online.

## Dependencies

- F-004 (heartbeat)
- F-006 (runs, claim, lease sweeper)

## Test Cases

### TC-001: Staleness marks Offline, then Lost

Steps:
1. Register a worker, start a run (accepted/Running), stop heartbeats past the threshold.

Expected:
- Worker → `Offline`; run → `Lost` with reason `worker-offline`; exactly one Lost transition even if
  the monitor ticks repeatedly.

### TC-002: One missed heartbeat is not Offline

Steps:
1. Delay one heartbeat beyond the interval but below the threshold, then resume.

Expected:
- Worker stays `Online`; its running run is untouched.

### TC-003: No retry and no resurrection

Steps:
1. After a Lost transition, resume heartbeats; attempt to claim; inspect the Lost run.

Expected:
- Worker back Online; claim yields other queued runs or 204 — never the Lost run; the Lost run stays
  `Lost` with unchanged fields.

### TC-004: Lease of an offline worker

Steps:
1. Worker claims (not yet accepted), then goes silent past the threshold.

Expected:
- The claimed-unaccepted run returns to `Queued` via the lease sweeper and is claimable by another
  worker; it never becomes `Lost`.

## Development Log

### 2026-11-13

- (pending implementation)

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: (pending)
- How to run/verify: `dotnet build -c Release` && `dotnet test -c Release --filter "Category=RunLifecycle"`
- Results: (pending)
