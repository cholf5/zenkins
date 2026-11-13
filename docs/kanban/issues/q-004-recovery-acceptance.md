---
id: Q-004
title: Recovery acceptance: server restart, worker crash, and the honest Lost path
type: test
priority: P1
size: L
status: done
created: 2026-11-13
updated: 2026-11-14
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

- [x] A `Queued` run survives a server restart: after restart it is still `Queued`, then executes
      exactly once when a worker connects (no duplicate run, no lost run).
- [x] Killing the worker's process mid-run leaves the run `Running` until the grace expires, then
      `Lost`; the failure reason names the offline worker rather than a step failure, and no retry
      happens.
- [x] The killed worker's late report (its process cannot produce one, so the test replays the same
      payload it would have sent) is refused with the existing conflict code and changes nothing.
- [x] A run whose lease expired is not claimable by another worker while the lease is live; after
      expiry the sweeper marks it `Lost` and a claim returns nothing.
- [x] A restarted worker claims new work and does not resume the abandoned run.
- [x] A `Lost` run never becomes `Success`, `Failed` or `Queued` afterwards (asserted over a settle
      window, not a single read).
- [x] The suite asserts observable state only (rows, HTTP, process ids) and cleans up every process and
      temporary directory, including on failure.
- [x] Runs in `Category=Recovery`, kept out of the fast suite like `Category=Milestone`, with the
      documentation updated for the third suite - including the fact that the fast filter now has to
      exclude two categories.

## Subtasks

- [x] Recovery harness reusing Q-002's process launcher with restart capability (server stop/start,
      worker kill/restart).
- [x] Four scenarios: queued-survives-restart, worker-killed-mid-run, late-report-refused,
      lease-expiry-not-reclaimed; plus the settle-window assertion.
- [x] `docs/development.md`: the third suite and how to run it.
- [x] Tests (`Category=Recovery`).

## Dependencies

- F-005, F-006, F-009, F-010 (the mechanisms), Q-002 (the real-process harness this extends)

## Test Cases

### TC-001: The queue outlives the server — PASS

Steps:
1. Trigger a run with no worker connected; stop the server; start it again on the same database; start a
   worker.

Expected:
- The run is still `Queued` after the restart, is claimed once, reaches a terminal state, and the
  organization has exactly one run.

Result (observed, 2026-11-14): PASS. The run row is read straight out of the SQLite file while the
server is down and is still `Queued` with no `WorkerId`; the restarted server (new port, same database
and key ring) still reports it `Queued`; the worker started afterwards claims it once and the run
reaches `Success` with three `Success` steps; the project holds exactly one run and the persisted log
holds exactly one `step 1/3 started` line; a settle window confirms no re-queue and no duplicate. The
Owner's session survives the restart because the Data Protection key ring does (the restarted server
answers the Owner's token-mint form without a new sign-in).

### TC-002: A dead worker produces Lost, not a retry — PASS

Steps:
1. Start a run; kill the worker process mid-step; wait past the grace period with the sweeper running.

Expected:
- The run is `Lost` with the offline reason; the step it was in remains `Failed` or `Canceled` as the
  card defines; no second execution starts; replaying the worker's last report is refused.

Result (observed, 2026-11-14): PASS, with one recorded semantics finding. The kill lands while the
in-flight step is `Running`; the run stays `Running` for a two-second observation window after the pid
is gone from the process table; then the real offline monitor (five-second threshold, one-second tick,
configuration only) flips the worker row `Offline` and marks the run `Lost` with reason
`worker-offline`, exit code null, the worker's id still on the run. No second execution starts: the
project keeps exactly one run, the step count never grows, and the marker file the later steps would
have written stays absent through a settle window. The late report is refused (TC-002's replay clause —
see the late-report test below for the exact verdict).

**Recorded finding (what the code actually does to the step):** the Lost transition touches the run row
only (`TryTerminateRunningBuildRunAsync`, a compare-and-set on `Status == Running`). The step the
worker was in keeps the state the worker last reported — `Running`, exit code null — and is never
re-stamped `Failed` or `Canceled`. The test pins this as the contract: the server does not fabricate a
step outcome for a run it did not see end. Neither of the card's two anticipated shapes occurs, and
none is needed — the run row carries the truth (`Lost` / `worker-offline`), and the honest-unknown
wording the PM ruled for F-009 covers the step the same way.

### TC-003: A live lease is respected, an expired one is not re-queued — PASS

Steps:
1. Claim a run with worker A, keep its lease live, and have worker B attempt a claim; then let the
   lease expire.

Expected:
- B gets nothing while the lease is live; after expiry the sweeper marks the run `Lost` and B still
  gets nothing.

Result (observed, 2026-11-14): PASS, with the card's "lease" read against the product's own
semantics. F-006's claim lease exists only between a claim and its accepted report (the accept clears
it), and its sweeper deliberately *requeues* a claimed-but-never-accepted run — nothing ever executed,
so a requeue is not a retry (`RunService.SweepExpiredLeasesAsync`). The hold whose expiry produces
`Lost` is the worker's liveness (F-010): worker A claims **and accepts** the run, stays alive (fresh
heartbeats), and that hold is what expires when its process is killed. While A holds the run, worker B
(a real second process) never executes anything and a probe worker's direct `POST /api/workers/claim`
returns 204 every time; after the kill, the real monitor marks the run `Lost` (`worker-offline`) and
both B and the probe still get nothing through a settle window — the run is never re-queued. The
control at the end proves B was able to claim all along: once new work exists, B takes it and finishes
it (`Success`, B's row id on the run), and the probe's claim is empty again.

### Late report refused — PASS (the replay TC-002 names, verified on its own)

The killed worker's token is read from the token file it persisted under its own data directory (its
own observable artifact), and the terminal report its completion path would have sent —
`POST /api/workers/runs/{id}/completed` with `{"exitCode":1,"failureReason":"step-failed"}`, bearer
that token — is replayed after the run is `Lost`. The server answers **409 with
`{"error":"invalid-transition"}`** (the existing conflict code: `Lost` is terminal, no outgoing
edges), a re-delivered replay is refused the same way, and nothing changes: same status, same
`worker-offline` reason, exit code still null, the same `EndedAt`, the same step signatures, and the
settle window still finds only `Lost`.

### Restarted worker claims new work — PASS (inside the crash test)

A new worker process starts with the same name and the same data directory, so it loads the persisted
token (its output shows `using the persisted worker token` — F-005's real reconnect path, not a
re-registration), reuses the server's worker row, and its heartbeats flip the `Offline` row back
`Online`. It claims a new run on a second project and finishes it `Success` with its row's id on the
run; the abandoned run keeps its one `Running` step, its `Lost` status and its null exit code
throughout.

## Development Log

### 2026-11-13

- Planned after v0.3: the mechanisms are individually tested but have never been composed, and the
  roadmap lists recovery as a v1.0 acceptance item. Deliberately separate from Q-003, which is unit-level
  debt: this one needs real processes.

### 2026-11-14

- Implemented. Harness: `tests/Zenkins.Tests/Recovery/RecoveryHost.cs` subclasses Q-002's
  `MilestoneHost` (unsealed with a minimal, behaviour-preserving refactor: the start sequence moved into
  a protected `StartCoreAsync`, the process-log/lookup/kill plumbing and binary discovery widened to
  `protected internal`) and adds exactly the two abilities recovery needs — `StopServer()` /
  `StartServerAgainAsync()` (same database, same key ring, same options, new port) and multi-worker
  start/kill/restart (`StartWorkerAsync` mints a fresh registration token per start and gives each
  worker its own data directory and work root, so a restart with the same name loads the persisted
  token). Every killed process stays tracked, so dispose kills the whole tree and deletes the root
  even after a mid-test kill. Tests: `RecoveryRestartTests` (TC-001), `RecoveryWorkerCrashTests`
  (TC-002 + the restarted worker), `RecoveryLateReportTests` (the replay),
  `RecoveryLeaseTests` (TC-003 with the settle windows and the B-claim control),
  `RecoveryPipelines` reuses Q-002's ArgvEcho fixtures for real child processes.
- Time compression: `Runs:SweepIntervalSeconds=1`, `Workers:OfflineAfterSeconds=5`,
  `Workers:MonitorIntervalSeconds=1` — the real `LeaseSweeper` and `WorkerOfflineMonitor` drive every
  transition; no internal method is ever called to force a state. Five seconds (production: sixty)
  leaves the "still Running" observation window (2 s) more than two seconds of margin against the
  earliest possible flip (last heartbeat up to 1 s stale + 5 s threshold), so a stalled machine cannot
  turn the assertion into a flake.
- Fast-filter syntax confirmed empirically, not assumed: `dotnet test -c Release --filter
  "Category!=Milestone&Category!=Recovery"` runs (931 tests, 0 failed, 0 skipped), while the previously
  documented `Category!=Milestone` alone now answers 935 — it pulls the four recovery tests in.
  `docs/development.md` rewritten for three suites with the observed counts and durations.
- Two test-harness bugs found and fixed during development (kept here for honesty; neither is a product
  defect): the crash test initially shared one marker file between the abandoned run and the control
  run (the "resumed the abandoned run" assertion fired on the control run's own steps — the control run
  now uses a marker-free pipeline on a second project); and the restarted server left the Owner client
  pointing at the dead port — the harness now rebuilds the Owner client over the same cookie jar, which
  is also what makes "the session survives the restart" an assertion instead of a coincidence.
- No product-code changes: every scenario behaved as the mechanisms' own tests and docs promise. The
  one semantics finding (the in-flight step of a Lost run stays exactly as the worker last reported it,
  see TC-002) is recorded as the pinned contract, not a bug — the server never fabricates a step
  outcome, consistent with the F-009/F-010 honest-unknown ruling.
- Verification, all observed on the reference machine (macOS 26.5 arm64, SDK 10.0.403):
  `dotnet build -c Release` → 0 warnings / 0 errors; `dotnet test -c Release --filter
  "Category=Recovery"` → 4 passed, seven green runs in total across the day, three consecutive with
  the final timing (31.4 s, 28.7 s, 28.2 s) and one more after the last cosmetic touch-up (29.7 s);
  fast suite with the new filter → 931 / 0 / 0 (1 m 43 s);
  Milestone → 6 / 0 / 0 (33 s, unchanged after the harness refactor); full suite, no filter →
  941 / 0 / 0 (2 m 04 s).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | No product defect found. Every scenario behaved as pinned: the queue survives the restart, a killed worker produces `Lost`/`worker-offline` (never a retry), the late report is refused `invalid-transition`, expired holds are never re-queued, and a restarted worker claims new work. | Closed | The one recorded semantics finding (a Lost run's in-flight step keeps the worker's last-reported `Running` state) is pinned in `RecoveryWorkerCrashTests` as the contract, consistent with the honest-unknown ruling. |

## Verification

- Related files: `tests/Zenkins.Tests/Recovery/RecoveryHost.cs`, `RecoveryPipelines.cs`,
  `RecoveryRestartTests.cs`, `RecoveryWorkerCrashTests.cs`, `RecoveryLateReportTests.cs`,
  `RecoveryLeaseTests.cs`; `tests/Zenkins.Tests/Milestone/MilestoneHost.cs` (behaviour-preserving
  restart/multi-worker refactor); `src/Server/Runs/LeaseSweeper.cs`, `src/Server/Runs/RunService.cs`,
  `src/Server/Workers/WorkerOfflineMonitor.cs` (mechanisms under test, unchanged);
  `docs/development.md`.
- How to run/verify: `dotnet build -c Release` (0 warnings / 0 errors); `dotnet test -c Release
  --filter "Category=Recovery"`; fast suite with the corrected filter `dotnet test -c Release --filter
  "Category!=Milestone&Category!=Recovery"` (the runner accepts the `&`-joined double exclusion —
  verified, and the single exclusion is now wrong, see `docs/development.md` §2);
  `dotnet test -c Release --filter "Category=Milestone"`; full suite.
- Results (observed 2026-11-14): Recovery 4 passed / 0 failed / 0 skipped, ~26–31 s, three consecutive
  green runs with the final timing; fast suite 931 / 0 / 0 (1 m 43 s); Milestone 6 / 0 / 0 (33 s);
  full suite 941 / 0 / 0 (2 m 04 s); the old filter `Category!=Milestone` answers 935 with Recovery
  included, which is what the corrected documentation rules out.
