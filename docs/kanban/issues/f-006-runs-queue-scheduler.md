---
id: F-006
title: Build runs, queue and scheduler (atomic claim/lease)
type: feature
priority: P1
size: L
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

An Owner or Member can trigger a run for a Project (a native-process command given as argv, no
shell), the run sits in a Queued queue, and the Server hands queued runs to eligible idle Workers
through an atomic claim so one run is never executed twice; Workers report accepted/finished with an
exit code and the run's state, timing and worker are persisted and visible.

## Background

PRD §3.5 (run states and metadata), §8 constraints 1 (Server never executes builds) and 6 (no shell
concatenation). Phase 2 has no `pipeline.lua` yet (v0.3): a v0.2 run executes a manually specified
argv command inside the worker's per-run work directory (F-007). `BuildRun.Pipeline*` fields stay
nullable for Phase 3; Git fields stay out until Phase 4.

Decisions:

- Run states: `Queued → Running → Success | Failed | Canceled | Lost` (PRD §3.5). Transitions are
  validated in one service; illegal transitions are `409`.
- Trigger form (Project detail, Owner or Member): command as **one argument per line** (trimmed,
  no shell quoting interpretation), optional required tags (comma-separated). Empty argv or >100
  args / >32 KiB per arg is a field error.
- Claim/lease: `POST /api/workers/claim` (worker token) atomically leases the next eligible Queued
  run (tag ⊆ worker tags, worker Online, worker's Running count < its max concurrency) for the
  worker's Organization and sets `LeaseExpiresAt`. The worker must report `accepted` within the
  lease window or the run returns to `Queued`. After `accepted` there is no lease — cancellation
  (F-009) and offline detection (F-010) own the rest.
- Reporting: `POST /api/workers/runs/{id}/accepted` and `…/completed {exitCode}` (worker token, own
  run only). `completed` with exit code 0 → `Success`, non-zero → `Failed`.
- A background scheduler assigns nothing by itself if workers pull; its job is lease expiry
  (requeue runs whose lease expired without an `accepted`) — the pull model is the PRD's
  "Worker 主动拉取" and avoids push fan-out.
- Minimal UI: per-project runs list (`/projects/{id}/runs`) and run detail (`/runs/{id}`) showing
  status, worker, command, trigger user, start/end/duration, exit code.

## Acceptance Criteria

- [ ] `BuildRun` entity (tenant-owned) + PostgreSQL-contract migration; states and transitions
      enforced in one service, illegal transitions rejected.
- [ ] Trigger form on Project detail creates a Queued run with validated argv and optional tags;
      anonymous/unauthenticated → sign-in redirect; cross-tenant project id → 404.
- [ ] Concurrent claims never return the same run: parallel `claim` calls (SQLite and PostgreSQL
      semantics) each yield distinct runs or 204; a run is claimed exactly once. The atomicity does
      not depend on application-level locks alone in PostgreSQL (`FOR UPDATE SKIP LOCKED` or
      equivalent), and SQLite relies on a transactional compare-and-set.
- [ ] Lease expiry requeues a claimed-but-never-accepted run (never one already Running).
- [ ] Scheduler eligibility: Online worker, free concurrency slot, required tags satisfied; an
      Offline worker or tag mismatch is never handed a run.
- [ ] `accepted` sets Running + worker + start time; `completed` sets terminal state + end time +
      duration + exit code; foreign run ids and foreign worker tokens are 401/404 with no data.
- [ ] Runs list and detail pages show the recorded data and respect tenant scoping.

## Subtasks

- [ ] `BuildRun` entity + migration + transition-rule service.
- [ ] Trigger form endpoint + validation (argv rules) + pages (runs list, run detail).
- [ ] Claim endpoint with atomic claim/lease (tests for double-claim on both providers' semantics).
- [ ] Reporting endpoints + lease-expiry background service.
- [ ] Tests (`Category=Runs`): happy path, double-claim concurrency, lease expiry, eligibility
      (tags/concurrency/offline), transition rules, cross-tenant denials, argv validation.

## Dependencies

- F-004 (worker identity)
- F-005 (a real consumer for the end-to-end test)

## Test Cases

### TC-001: Trigger → claim → complete happy path

Steps:
1. Member triggers `dotnet --version` run on a project with required tags matching a registered
   worker; worker claims, reports accepted then completed with exit 0.

Expected:
- Queued → Running → Success; start/end/duration and exit code persisted; run detail shows them.

### TC-002: Double-claim concurrency

Steps:
1. Queue N≥2 runs. Fire concurrent claim requests (single worker or several).

Expected:
- Every run returned at most once across all claims; no run handed to two workers (pinned by a
  concurrency test).

### TC-003: Eligibility

Steps:
1. Queue a run requiring tag `windows`; only a tagless worker exists.
2. Mark that worker Offline and queue another run.

Expected:
- Neither run is ever claimed; both stay Queued.

### TC-004: Lease expiry

Steps:
1. Claim a run and never report accepted; advance past the lease window (configurable short window
   in tests).

Expected:
- Run returns to Queued and is claimable again; a run already accepted/Running is never requeued by
  the lease sweeper.

### TC-005: Transition and tenant guards

Steps:
1. `completed` before `accepted` (409). Complete with exit 3 (Failed).
2. Claim with Organization B's worker token against A's queue.

Expected:
- 409 for the illegal transition; Failed recorded with exit 3; B's token gets 401/404 and never
  sees A's runs.

## Development Log

### 2026-11-13

- (pending implementation)

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: (pending)
- How to run/verify: `dotnet build -c Release` && `dotnet test -c Release --filter "Category=Runs"`
- Results: (pending)
