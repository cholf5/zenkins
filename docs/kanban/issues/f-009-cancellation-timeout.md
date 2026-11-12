---
id: F-009
title: Cancellation propagation and per-run timeout
type: feature
priority: P1
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

A user can cancel a Queued or Running run; the Server propagates the cancellation to the executing
Worker, which kills the process tree and reports the run as `Canceled`. A run exceeding its timeout
is killed and marked `Failed` with a timeout reason. Neither is ever silently retried.

## Background

PRD §3.4.5 (Worker 必须能接收取消请求), §3.5 (`Canceled` state), roadmap candidate 7. Queued
cancellation is a pure server-side transition (the claim must never hand out a Canceled run).
Running cancellation needs a server→worker signal; the Worker connects outbound, so the signal rides
the Worker's existing upstream connection (SignalR from F-008), with a fallback `cancelRequested`
flag returned by the heartbeat/claim path so a dropped SignalR connection cannot strand a cancel.
The Server cannot kill a remote process: the deadline marks the run "cancel requested (timeout)",
the Worker observes and kills; if the Worker is unreachable the offline path (F-010) marks the run
`Lost` — that is honest, not a fake failure.

Decisions:

- Timeout default: server configuration (`Runs:DefaultTimeoutMinutes`, default 60). Per-run
  override field (minutes, blank = default) on the trigger form.
- Terminal reasons recorded in `FailureReason` (`canceled-by-user <user>`, `timeout`).
- Cancel of a terminal run is a no-op with a friendly notice (not an error page). Double-cancel is
  idempotent.
- No automatic retry anywhere: the scheduler only ever picks `Queued`.

## Acceptance Criteria

- [ ] Cancel action on the run page (Owner or Member of the org, antiforgery-protected): Queued →
      `Canceled` immediately and atomically — a concurrent claim can never obtain it.
- [ ] Running: the Worker receives the cancellation (SignalR, heartbeat/claim fallback), kills the
      process tree (F-007 kill), reports; run becomes `Canceled` with end time and reason; partial
      log is preserved.
- [ ] Per-run timeout: a run exceeding its deadline gets the cancel-requested(timeout) signal and
      the Worker kills it; final state `Failed` with reason `timeout`.
- [ ] A Worker that never observes the signal (offline) leaves the run to F-010's `Lost` path — the
      deadline must not fabricate `Failed` for an unreachable worker.
- [ ] Cancel on a terminal run and double-cancel are no-ops; unauthorized cancel (foreign org /
      worker token) is 403/401 and changes nothing.
- [ ] No code path requeues or retries a Canceled/Failed/Lost run.

## Subtasks

- [ ] Cancel service + form endpoint + run page button (queued and running states).
- [ ] Server→worker cancel signal on the F-008 hub + heartbeat/claim `cancelRequested` fallback.
- [ ] Worker-side cancel handling wired to F-007 kill-tree; report `canceled`.
- [ ] Timeout sweeper (deadline from per-run override or default) + reason recording.
- [ ] Tests (`Category=RunLifecycle`): queued cancel vs concurrent claim race, running cancel kills
      child, timeout kill, unreachable-worker deadline does not fake state, no-retry invariant,
      authorization.

## Dependencies

- F-006
- F-007 (kill-tree)
- F-008 (transport for the cancel signal)

## Test Cases

### TC-001: Queued cancel beats the claim

Steps:
1. Queue a run; cancel it while a claim storm is in flight.

Expected:
- Run ends `Canceled`; no claim ever returns it (race pinned by test).

### TC-002: Running cancel kills the process

Steps:
1. Trigger a long-running child (sleep); cancel from the UI as a Member.

Expected:
- Process tree killed, run `Canceled` with reason `canceled-by-user …`, partial lines preserved.

### TC-003: Timeout

Steps:
1. Trigger with a short per-run timeout override (config allows sub-minute values for tests); the
   child sleeps longer than the deadline.

Expected:
- Run `Failed`, reason `timeout`, process killed, log preserved.

### TC-004: Unreachable worker + expired deadline

Steps:
1. Accepted run whose worker stops responding; deadline passes.

Expected:
- Run is not marked `Failed` by the timeout path while the worker is merely reconnecting within the
  offline threshold; once F-010's threshold passes, it becomes `Lost`. (Coordinate the boundary
  with F-010; assert no fake `Failed`.)

### TC-005: No-ops and guards

Steps:
1. Cancel a finished run twice; POST cancel from Organization B for A's run.

Expected:
- Friendly no-op notice, state unchanged; B gets 403/404 and A's run is untouched.

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
