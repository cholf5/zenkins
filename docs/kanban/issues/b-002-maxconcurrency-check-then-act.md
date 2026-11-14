---
id: B-002
title: maxConcurrency slot check is check-then-act — a token-holding client can exceed the cap (Q-005 hardening)
type: bug
priority: P2
size: M
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

The `maxConcurrency` a worker reports is enforced by the data layer, not by the client's good
behaviour: even a non-compliant client holding a valid worker token cannot hold more running runs
than its reported cap, no matter how it races the claim endpoint.

## Background

Q-005 recorded the seam verbatim (development log, concurred by the independent reviewer): the
server's `maxConcurrency` slot check is check-then-act, so a non-compliant client holding a valid
token could exceed the cap while real worker processes are structurally bounded (claim loops × at
most one run each). F-006's claim itself is a single-statement compare-and-set with a lease; the cap
check sits outside that atomic step, so two racing claim responses can both pass the check.

The consequence today is over-admission beyond the reported cap — not double execution (Q-005's
executed-once property held), not data corruption. That is why the user's ledger defers this past
the v1.0 tag (v1.0.1), with the known issue recorded in the CHANGELOG by P-001 rather than silently
shipped.

The likely shape of the fix: move the slot reservation into the same atomic step as the claim (the
cap is enforced where the compare-and-set is, or a slot row is reserved in the same transaction).
If the claim query changes, Q-005's rule applies: the PostgreSQL contract gate runs and is recorded.

## Acceptance Criteria

- [ ] The slot check and the claim are one race-free step; a hostile-but-authenticated claimer
      cannot exceed its reported `maxConcurrency` even under concurrent claims.
- [ ] A permanent regression test proves the race is closed: concurrent claims against one worker
      pinned to `maxConcurrency=1` never admit more than one running run.
- [ ] Real-worker behaviour is unchanged: Q-005's concurrency scenarios stay green.
- [ ] If the claim query changed, the PostgreSQL contract gate ran and its result is recorded here.
- [ ] The CHANGELOG known-issue entry (added by P-001) is updated or removed when this lands.

## Subtasks

- [ ] Locate the check-then-act window and pin it with a failing test before fixing.
- [ ] Make the cap atomic (inside the claim's compare-and-set / transaction).
- [ ] Regression test + Q-005 concurrency regression; PostgreSQL contract gate if the claim query
      moved.

## Dependencies

- none technically; scheduled **after the v1.0 tag** (P-001) per the user's ledger decision

## Test Cases

### TC-001: The race the seam allowed

Steps:
1. Register a worker with `maxConcurrency=1`; fire concurrent claim requests from multiple
   authenticated clients inside one window; count running runs attributed to that worker.

Expected:
- Exactly one run is admitted past the cap; the losers receive no work; the test asserts this from
  rows, not from absence of errors.

### TC-002: Compliant clients unchanged

Steps:
1. Re-run the Q-005 concurrency filter (`FullyQualifiedName~Zenkins.Tests.Concurrency`).

Expected:
- All green; no behaviour change for real workers.

## Development Log

### 2026-11-14

- Card opened from Q-005's recorded seam (dev + reviewer concurrence) and the user's ledger decision:
  hardening deferred past v1.0, CHANGELOG entry in the meantime, this card schedules the fix for
  v1.0.1. Next step: pin the window with a failing test.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for the seam Q-005 recorded (P2, deferred with rationale). | Open | Closed here when the acceptance criteria pass. |

## Verification

- Related files: `src/Server/Runs/RunService.cs` (claim query), `src/Server/Workers/*` (cap
  validation at registration), `tests/Zenkins.Tests/Concurrency/*` (harness to extend)
- How to run/verify: the new regression test; `FullyQualifiedName~Zenkins.Tests.Concurrency`;
  PostgreSQL contract gate if the claim query changed.
- Results: not run yet (card creation).
