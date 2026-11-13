---
id: Q-004
title: Recovery acceptance: server restart, worker crash, and the honest Lost path
type: test
priority: P1
size: L
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

Prove with real processes what the product promises when something dies: queued work survives a server
restart, a run whose worker disappears becomes `Lost` rather than silently retried or claimed twice, and
a worker that comes back does not resurrect a run that has been given up on.

## Background

Roadmap v1.0 ("Worker disconnect recovery, Server restart recovery") and PRD §10's acceptance scenarios.
The mechanisms exist and are separately tested: `LeaseSweeper` (F-010) marks a `Running` run `Lost` after
the grace period, F-006's claim is a single-statement compare-and-set with a lease, F-005 reconnects a
heartbeat, F-009 propagates cancellation. What has never been exercised is the composition — the same
gap Q-002 closed for the happy path, and the same reason: F-008's review once found a rejection
discriminator that never matched, visible only end to end.

Decisions:

- **These are acceptance tests first and product gaps second.** If a scenario reveals a real defect
  (a run claimed twice, a `Lost` run later reported `Success`, a queued run silently dropped on
  restart), it is fixed here, with the failing scenario kept as the regression.
- **The intended semantics are pinned before the test is written**, because "recovery" can mean
  several things and the wrong one is worse than none:
  - a `Queued` run survives a server restart and is executed once afterwards;
  - a `Running` run whose worker stops heartbeating becomes `Lost` after the grace period, and its
    worker's later report is refused rather than accepted;
  - a run whose lease expired is not claimable by a second worker while the first still holds it, and
    after expiry the run is `Lost` (never re-queued, because re-running someone's build silently is a
    lie about what happened);
  - a worker restarted mid-run does not resume that run: it claims new work, and the abandoned run is
    `Lost`.
- **Time is compressed, not faked.** The grace period and lease are configuration; the tests set them
  small and drive the real sweeper, rather than calling internal methods to force a state change.
- **Real processes again**: a real server process and a real worker executable, killed and restarted as
  a process, with assertions on rows, HTTP responses and process identity.

## Acceptance Criteria

- [ ] A `Queued` run survives a server restart: after restart it is still `Queued`, then executes
      exactly once when a worker connects (no duplicate run, no lost run).
- [ ] Killing the worker's process mid-run leaves the run `Running` until the grace expires, then
      `Lost`; the failure reason names the offline worker rather than a step failure, and no retry
      happens.
- [ ] The killed worker's late report (its process cannot produce one, so the test replays the same
      payload it would have sent) is refused with the existing conflict code and changes nothing.
- [ ] A run whose lease expired is not claimable by another worker while the lease is live; after
      expiry the sweeper marks it `Lost` and a claim returns nothing.
- [ ] A restarted worker claims new work and does not resume the abandoned run.
- [ ] A `Lost` run never becomes `Success`, `Failed` or `Queued` afterwards (asserted over a settle
      window, not a single read).
- [ ] The suite asserts observable state only (rows, HTTP, process ids) and cleans up every process and
      temporary directory, including on failure.
- [ ] Runs in `Category=Recovery`, kept out of the fast suite like `Category=Milestone`, with the
      documentation updated for the third suite - including the fact that the fast filter now has to
      exclude two categories.

## Subtasks

- [ ] Recovery harness reusing Q-002's process launcher with restart capability (server stop/start,
      worker kill/restart).
- [ ] Four scenarios: queued-survives-restart, worker-killed-mid-run, late-report-refused,
      lease-expiry-not-reclaimed; plus the settle-window assertion.
- [ ] `docs/development.md`: the third suite and how to run it.
- [ ] Tests (`Category=Recovery`).

## Dependencies

- F-005, F-006, F-009, F-010 (the mechanisms), Q-002 (the real-process harness this extends)

## Test Cases

### TC-001: The queue outlives the server

Steps:
1. Trigger a run with no worker connected; stop the server; start it again on the same database; start a
   worker.

Expected:
- The run is still `Queued` after the restart, is claimed once, reaches a terminal state, and the
  organization has exactly one run.

### TC-002: A dead worker produces Lost, not a retry

Steps:
1. Start a run; kill the worker process mid-step; wait past the grace period with the sweeper running.

Expected:
- The run is `Lost` with the offline reason; the step it was in remains `Failed` or `Canceled` as the
  card defines; no second execution starts; replaying the worker's last report is refused.

### TC-003: A live lease is respected, an expired one is not re-queued

Steps:
1. Claim a run with worker A, keep its lease live, and have worker B attempt a claim; then let the
   lease expire.

Expected:
- B gets nothing while the lease is live; after expiry the sweeper marks the run `Lost` and B still
  gets nothing.

## Development Log

### 2026-11-13

- Planned after v0.3: the mechanisms are individually tested but have never been composed, and the
  roadmap lists recovery as a v1.0 acceptance item. Deliberately separate from Q-003, which is unit-level
  debt: this one needs real processes.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/Zenkins.Tests/Recovery/*`, `src/Server/Runs/LeaseSweeper.cs`,
  `src/Server/Runs/RunService.cs`, `docs/development.md`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Recovery"`;
  the fast suite must now exclude two categories, so this card also fixes the documented fast filter
  (confirm which syntax the runner accepts for the combination - the current docs say
  `Category!=Milestone` - and update `docs/development.md` accordingly); full suite.
- Results: not run (card not started).
