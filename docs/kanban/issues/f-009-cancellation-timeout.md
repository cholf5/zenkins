---
id: F-009
title: Cancellation propagation and per-run timeout
type: feature
priority: P1
size: M
status: done
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

- [x] Cancel action on the run page (Owner or Member of the org, antiforgery-protected): Queued →
      `Canceled` immediately and atomically — a concurrent claim can never obtain it.
- [x] Running: the Worker receives the cancellation (SignalR, heartbeat/claim fallback), kills the
      process tree (F-007 kill), reports; run becomes `Canceled` with end time and reason; partial
      log is preserved.
- [x] Per-run timeout: a run exceeding its deadline gets the cancel-requested(timeout) signal and
      the Worker kills it; final state `Failed` with reason `timeout`.
- [x] A Worker that never observes the signal (offline) leaves the run to F-010's `Lost` path — the
      deadline must not fabricate `Failed` for an unreachable worker.
- [x] Cancel on a terminal run and double-cancel are no-ops; unauthorized cancel (foreign org /
      worker token) is 403/401 and changes nothing.
- [x] No code path requeues or retries a Canceled/Failed/Lost run.

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

**Data model (PostgreSQL-contract migration `AddRunCancellationAndTimeout`).** Four nullable
`BuildRuns` columns: `TimeoutSeconds` (the per-run override in seconds — the form collects minutes,
the row stores seconds so the sub-minute test values the default accepts survive storage),
`DeadlineAt` (the execution deadline, **frozen at accept time** as `StartedAt + effective timeout`,
so a later configuration change never moves an accepted run's contract), and the cancel-request
duo `CancelRequestedAt` / `CancelReason` (max length 200, like `FailureReason` it becomes). Queued
runs have no deadline; the sweeper never touches a run before its `DeadlineAt`.

**Six guarded compare-and-set methods on `ZenkinsDbContext`** (the claim-CAS discipline, extended):
`TryCancelQueuedBuildRunAsync` (tenant scope; `WHERE Status == Queued` — mutually exclusive with
`TryLeaseBuildRunAsync`'s WHERE under both providers' write locking, so under a claim storm
exactly one of the two wins the row; the lease columns are cleared with the terminal state),
`TryRequestBuildRunCancelAsync` (tenant scope, user path) and its cross-tenant variant (system
scope, the deadline sweeper), both `WHERE Status == Running AND CancelRequestedAt IS NULL` — first
request wins, so a double cancel or a user cancel racing the sweeper records one reason and never
overwrites one; `TryTerminateRunningBuildRunAsync` (system scope, `WHERE Status == Running`) for
the two server-initiated Lost transitions; `TryReportCanceledBuildRunAsync` (tenant scope,
`WHERE WorkerId == caller AND Status == Running`) so a canceled report racing a Lost transition
loses the row and answers the honest 409 instead of overwriting; `TryMarkWorkerOfflineAsync`
(F-010's exactly-once gate).

**Cancel signal path.** Primary: the worker's existing upstream SignalR connection —
`WorkerLogHub.OnConnectedAsync` joins every authenticated connection to a per-worker group
(`WorkerLogGroups.ForWorker`, re-joined automatically on reconnect), and both cancel sites
(`RunService.CancelAsync`, the deadline sweeper) push `cancelRun(runId, reason)` into that group.
Fallback: the heartbeat response carries `cancelRequestedRuns: [{runId, reason}]` (every Running
run of that worker with a pending request), so a worker whose SignalR connection is down observes
the cancel at most one heartbeat period late. Both channels feed one worker-side in-process bus
(`RunCancelSignal`); each run loop subscribes and reacts only to its own run id. Reason strings
live in `Zenkins.Shared.RunCancellation` (`RunCancelReasons`: `timeout`, `canceled-by-user <user>`,
and the wire records), so Worker and Server compile against one contract. A broadcast failure is
logged, never thrown into the cancel path — the heartbeat fallback recovers it.

**Worker side.** `WorkerRunLoop` gates each run on a linked kill token (shutdown ∨ this run's
cancel request); the kill reuses F-007's process-tree kill path verbatim (`ProcessRunner` kills on
cancellation and drains the pipes), then the log buffer drains (partial lines upstream before the
report, the F-008 cleanup-before-report discipline) and the worker reports through the new
`POST /api/workers/runs/{id}/canceled {outcome}`. The outcome selects the server mapping
(`canceled` → `Canceled` with the recorded cancel reason; `timeout` → `Failed` with reason
`timeout`) — one kill, one endpoint, the server owns the wording. `WorkerRunner`'s heartbeat loop
raises the response's cancel entries onto the bus. Reports keep the definitive-4xx semantics
(a 409 means the run is already terminal — e.g. Lost won the race — and is given up, never retried).

**Queued vs running cancel, and the race window.** `RunService.CancelAsync` (membership = any
member, like the trigger): Queued → the CAS above (immediate terminal; a leaseholder whose accept
lands after the cancel gets the 409 and gives up before executing — the documented race-window
semantic); Running → cancel-requested + signal; terminal → the friendly no-op
(`?notice=already-finished`, PRG, nothing written). Double-cancel is the same no-op; foreign-org
run → 404 (tenant filter, no existence oracle); memberless user → 403; anonymous/worker token →
the sign-in challenge before the endpoint runs. `POST /runs/{id}/cancel` is form-bound
(antiforgery) like every other form endpoint; the run page shows the button exactly while the run
is Queued/Running and renders the three PRG notices.

**Timeout.** `Runs:DefaultTimeoutMinutes` (default 60; fractional minutes accepted so tests run
the real deadline path sub-minute) plus the optional per-run override on the trigger form
(minutes, blank = default, 1..10 080, field error `timeout-invalid`). `AcceptAsync` freezes
`DeadlineAt`. The deadline sweeper (`RunService.SweepDeadlinesAsync`, ticked by the LeaseSweeper's
loop and driveable directly by tests): overdue Running runs get the timeout cancel request (same
signal path as a user cancel); the expiry comparison is in memory (SQLite cannot compare
DateTimeOffset in SQL — the lease sweep's discovery) and each write is a CAS, so before the
 deadline nothing changes and a run that leaves Running during the sweep is never touched.

**PM 裁决落实：online-but-unreportable 缺口收口（F-007 遗留）。** After the deadline, a run still
`Running` after the grace period — deadline + 2 × `Workers:OfflineAfterSeconds` — with no worker
terminal report is marked **`Lost` with reason `run-deadline-exceeded-result-unknown`** by the
sweeper's CAS: honest wording for an unknown result, never a fabricated `Failed`, never requeued,
never retried. A worker that is merely reconnecting inside the offline threshold keeps its run
untouched (only the sweeper's grace applies); a dead worker's run is taken by F-010's
offline path first (`worker-offline`) — both are `Lost`, the reason says which truth was
observable. This shares the mechanism (`TryTerminateRunningBuildRunAsync`), the terminal
discipline (`runLogIngest.CompleteRunAsync` evicts the ingest state — the F-008 Canceled-gap
closure) and the page display with F-010's Lost.

**Tests (`Category=RunLifecycle`, shared with F-010, 24 tests incl. 2 config pins).**
Queued cancel before a real concurrent claim storm (3 workers × 8 claims, all 204, run Canceled
with reason/lease cleared); the true cancel-vs-claim race through one barrier pinning the
invariants (never two claimants; Canceled ⇒ no accepted edge, Running ⇒ recorded cancel request —
both legal interleavings of the race window); the CAS-discipline pin (the queued-cancel CAS
refuses a Running run); heartbeat payload carries/never carries the pending cancels (own worker
only, gone once terminal); the `cancelRun` signal delivered over the real hub group (long-polling
connection, bounded await); terminal no-op + double cancel idempotence; foreign-org 404 /
memberless 403 / anonymous challenge, run untouched; canceled-report guards (no pending request →
409 `cancel-not-requested`, foreign token → 404, other worker → 409 `run-not-assigned-to-worker`,
unknown outcome → 400, report racing Lost → 409 and the Lost row survives); timeout override
validation and storage; two process-killing integrations through the real `WorkerRunner`
(real registration, real child process `ArgvEcho stream`): the user-cancel one over the connected
SignalR channel (real form cancel **as a Member** with the reason naming the member, partial log
preserved on the server, temp cleaned), and the timeout one with a deliberately unreachable
upstream (`connectUpstream: false`) so the heartbeat fallback delivers the kill end to end
(terminal mapping, reason and temp cleanup asserted; no log lines — without an upstream none can
reach the server); the timeout path with `Runs:DefaultTimeoutMinutes=0.05` (sub-minute test
value) — deadline ≈ StartedAt+3 s, sweeper marks the timeout request, worker kills via the
heartbeat fallback, run `Failed` + `timeout`; the grace-period sweep (request within grace, honest
`Lost(run-deadline-exceeded-result-unknown)` after it, worker Online throughout, never `Failed`,
terminal closed); the user cancel reason never overwritten by the timeout sweep; the outcome
mapping (timeout → Failed, canceled → Canceled) on fresh runs; and the configuration pins
(`Workers:OfflineAfterSeconds`/`MonitorIntervalSeconds`, `Runs:DefaultTimeoutMinutes` incl.
sub-minute parsing).

**Red proofs (both restored immediately afterwards, re-verified green).**
1. The `Status == Queued` condition stripped from `TryCancelQueuedBuildRunAsync`'s WHERE →
   `The_queued_cancel_compare_and_set_only_touches_a_queued_run` red (**Expected: 0, Actual: 1** —
   the unconditional UPDATE flipped a Running run to Canceled without any cancel request, exactly
   the race damage the guard exists to prevent) → restored → green.
2. The `Status == Running` condition stripped from `TryTerminateRunningBuildRunAsync`'s WHERE →
   `The_termination_compare_and_set_refuses_a_run_that_already_left_running` red (**Expected: 0,
   Actual: 1** — the second termination re-terminated the Lost run and would have moved
   `EndedAt`, i.e. a duplicated/overwritten Lost transition) → restored → green.

**Known gaps.**
- The deadline starts at accept, not at trigger: queue time does not consume the timeout budget
  (a run stuck queued is the lease sweeper's problem). Deliberate — the timeout is an execution
  deadline; revisit only if PRD wording changes.
- The SignalR `cancelRun` push is fire-and-forget with no delivery receipt; the heartbeat fallback
  is the recovery path, and a worker that misses both (offline) is exactly F-010's Lost territory.
- A user-cancel reason that names the actor is capped at 200 characters (email inside); the
  trigger user's email is bounded by Identity anyway.
- `Runs:DefaultTimeoutMinutes` falls back to the default on unparsable values (the same silent
  fallback every seconds-knob in `RunOptions` uses) — pinned in the configuration tests.

### 2026-11-13 — review fixes

**The fallback E2E the record claimed did not exist; it does now.** Review found the Development
Log and Verification asserting a process-level E2E where "a deliberately unreachable upstream"
carries the kill through the heartbeat fallback — while in fact both process-killing E2Es built
their worker with `InProcessWorker.StartAsync(host, name)` (default `connectUpstream: true`) and
no `connectUpstream: false` call site existed anywhere in the repository. The helper's parameter
and doc described exactly this scenario, but no test used it: the fallback was pinned only at the
payload level (heartbeat carries/never carries `cancelRequestedRuns`) and at the hub-group level,
never executed end to end. That false statement is removed above; this section records the real
shape.

**The reworked test (`RunWorkerCancellationTests`, the timeout one).** Its worker now starts with
`connectUpstream: false` — `WorkerConfiguration.ServerUrl` points at an unreachable port, so the
SignalR upstream (log stream and `cancelRun` push) is down for the whole run while the API client
(registration, heartbeat, claim, reports) still reaches the TestServer. The test genuinely
executes the fallback chain: the deadline sweeper records the timeout cancel request → the
heartbeat response carries `cancelRequestedRuns` → `WorkerRunner`'s heartbeat loop raises each
entry onto `RunCancelSignal` → the run loop's kill token fires → the F-007 tree kill.

**Assertion scope, honestly.** Asserted: the deadline is frozen at `StartedAt` + 3 s; the real
sweeper seam records the timeout request (`CancelReason = timeout`); the run reaches `Failed`
with reason `timeout` — not `Canceled` (the server-owned outcome mapping) — with `EndedAt` set
and `ExitCode` null; the worker-side `temp/` exists while the child executes and is gone once the
terminal state is visible (the kill plus the cleanup-before-report discipline, evidenced without
the log). Deliberately **not** asserted: partial log lines. With the upstream down, no line can
reach the server; partial-log preservation is asserted in the connected user-cancel E2E, and the
SignalR group delivery itself is pinned in `RunCancellationTests`. Reaching `Failed` inside the
test window is itself kill evidence: the canceled report is only sent after the process wait
throws on the kill token, which only happens when the tree was killed.

**Red proof (restored immediately afterwards, re-verified green).** The heartbeat loop's
`cancelRequestedRuns → RunCancelSignal.Raise` suppressed → the fallback E2E red
(`System.TimeoutException: Run … never reached Failed within 00:01:00` — with the upstream down
and the raise gone, no channel can deliver the kill, so the run lives until the test's status
timeout) → restored → green. This pins that the test exercises the heartbeat fallback chain and
not some other delivery path.

**Counts.** F-009's own test count is unchanged (the timeout E2E was reworked in place, not
added); `Category=RunLifecycle` is now 26 with F-010's two review tests, suite 348 — see the
re-verified Results below.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Shared/RunCancellation/RunCancelContract.cs` (wire contract: reasons, outcomes,
    `WorkerCancelRequest`, new)
  - `src/Server/Data/Entities/BuildRun.cs` (`TimeoutSeconds`, `DeadlineAt`, `CancelRequestedAt`,
    `CancelReason`),
    `src/Server/Data/Migrations/{20261113000705_AddRunCancellationAndTimeout.cs,.Designer.cs}` +
    snapshot, `src/Server/Data/ZenkinsDbContext.cs` (model config + the six guarded CAS methods)
  - `src/Server/Runs/{RunService (CancelAsync/ReportCanceledAsync/SweepDeadlinesAsync/
    SweepOfflineWorkersAsync, deadline at accept, RunView+),RunEndpoints (cancel + canceled
    endpoints, timeout form field),RunOptions (DefaultTimeout),RunValidation
    (ValidateTimeoutMinutes),BuildRunStateMachine (error codes),LeaseSweeper (deadline tick),
    RunsServiceCollectionExtensions}.cs`
  - `src/Server/Workers/{WorkerService (heartbeat cancelRequestedRuns),WorkerEndpoints
    (heartbeat response),WorkerMonitorOptions,WorkerOfflineMonitor,WorkerServiceCollectionExtensions}.cs`
  - `src/Server/LogStreaming/{WorkerLogHub (per-worker group join),WorkerTokenAuthentication}.cs`
  - `src/Server/Program.cs` (AddZenkinsRuns/AddZenkinsWorkers registration)
  - `src/Server/Components/Pages/Runs/RunDetail.razor` (cancel button, PRG notices, timeout/
    deadline display, Lost/Canceled badges),
    `src/Server/Components/Pages/Projects/ProjectDetail.razor` (timeout override field)
  - `src/Worker/{RunCancelSignal (new),WorkerRunLoop (per-run cancel gate, canceled report),
    WorkerRunner (bus wiring, heartbeat forwarding, connection configurator),LogStreamClient
    (cancelRun handler),WorkerApiClient (heartbeat payload, ReportCanceledAsync)}.cs`
  - `tests/Zenkins.Tests/RunLifecycle/{RunLifecycleHost,InProcessWorker,RunCancellationTests,
    RunDeadlineSweepTests,RunWorkerCancellationTests,RunOfflineLostTests,
    RunLifecycleConfigurationTests}.cs (new)`,
    `tests/Zenkins.Tests/Runs/RunTestHost.cs` (timeout form field + host passthroughs)
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test -c Release --filter "Category=RunLifecycle"`
  3. `dotnet test -c Release` (the whole suite must stay green)
  4. PostgreSQL contract gate:
     `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
  5. Fresh-database migration gate (SQLite):
     `Database__Provider=Sqlite ConnectionStrings__Default="Data Source=/tmp/zenkins-f009-migrate.db" dotnet ef database update --project src/Server`, inspect `BuildRuns`, delete the file.
- Results (2026-11-13, re-verified after the review fixes below):
  - Build: **PASS** — `dotnet build -c Release`, 0 warnings / 0 errors (`TreatWarningsAsErrors` active).
  - Tests: **PASS** — **348/348** across the suite (322 pre-card baseline + 26 `Category=RunLifecycle`:
    the original 24 plus F-010's two review tests).
  - `Category=RunLifecycle`: **PASS** — 26/26.
  - PostgreSQL contract gate: **PASS** — "No changes have been made to the model since the last
    migration." (unchanged by the review fixes: the offline CAS gained a parameter, not a column).
  - Fresh-database migration gate: **PASS** — all migrations apply on an empty SQLite file;
    `BuildRuns` carries the four new columns with the Postgres store types (`timestamp with time
    zone`, `character varying(200)`, `integer`); temp file deleted.
  - Red proofs: **PASS** — three recorded (queued-cancel CAS guard; termination exactly-once
    guard; the review fixes' heartbeat-fallback-chain suppression, recorded in the review section),
    each red on removal and green after restore.
  - TC-001…TC-005 all covered as listed above (cancel/claim race; running cancel kills the real
    child with partial log; timeout kill → Failed(timeout); unreachable worker → grace → honest
    Lost, never Failed; terminal no-ops, double-cancel idempotence, foreign-org 404 / memberless
    403 / anonymous challenge).
