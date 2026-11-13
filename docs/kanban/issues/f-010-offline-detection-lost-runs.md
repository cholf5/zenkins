---
id: F-010
title: Offline detection → Lost runs
type: feature
priority: P1
size: M
status: done
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

- [x] A worker missing heartbeats past the threshold is marked `Offline` by the monitor; before the
      threshold it stays `Online` (no flapping at one missed beat).
- [x] `Running` runs on that worker become `Lost` with end time and reason `worker-offline`; the
      transition is recorded and visible on the run page.
- [x] `Lost` runs are never requeued, re-claimed or retried; a returning worker's heartbeat does not
      change them.
- [x] Offline workers are excluded from claims (claim returns nothing for them) and the scheduler
      never targets them.
- [x] Lease-expiry requeue (F-006) still applies to claimed-but-unaccepted runs of an offline
      worker, and only to those.
- [x] Threshold is configurable and honoured in tests (short threshold, controllable timing).

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

**Configuration.** `WorkerMonitorOptions` (`src/Server/Workers/WorkerMonitorOptions.cs`) reads
`Workers:OfflineAfterSeconds` (default 60 — a few missed heartbeats at the default 15 s interval,
the card's decision) and `Workers:MonitorIntervalSeconds` (default 15, the house sweep cadence).
Both are registered as **lazy singletons** (`sp => Options.FromConfiguration(sp.GetRequiredService<IConfiguration>())`)
— the same fix `RunOptions` needed on F-009: the eager read of the WebApplicationBuilder's
Configuration freezes the values before the host builder's extra configuration sources land (the
test hosts add theirs through `ConfigureAppConfiguration`), and `ZenkinsDataOptions` has been
DI-replaced in tests for exactly this reason since F-001. Lazy resolution reads the merged
configuration at first resolution; in production that is the same instant as before. Tests use a
short threshold plus a long interval (controllable timing): the monitor's background tick must be
silenceable, because the tests drive the sweep seam directly and its exactly-once transitions are
asserted on — a racing background tick would win some of them.

**The monitor.** `WorkerOfflineMonitor` (BackgroundService, registered by `AddZenkinsWorkers`) ticks
every `MonitorIntervalSeconds` and calls `RunService.SweepOfflineWorkersAsync(offlineAfter)`. The
sweep spans tenants (system scope, `IgnoreQueryFilters` — the lease-sweeper discipline), reads the
Online workers and evaluates staleness **in memory** (SQLite cannot compare DateTimeOffset values
in SQL — F-006's B1, respected everywhere): `LastHeartbeatAt <= now − threshold` flips the worker
through `TryMarkWorkerOfflineAsync` (`WHERE Status == Online AND LastHeartbeatAt == observed` —
the compare-and-set carries the snapshot's heartbeat value as its staleness evidence, so a fresh
heartbeat landing between the snapshot read and the flip refuses the row instead of faking an
Offline; a refused flip skips that worker's runs and the next tick re-evaluates it from a fresh
snapshot), and **only the sweep that wins a worker's flip** looks at that worker's runs. A worker
with no heartbeat timestamp is never flipped (unknown data must not produce an Offline;
registration always sets the first heartbeat, so the null case is corruption, not staleness).

**Exactly-once Lost, mechanically.** The worker-status CAS is the gate: two concurrent monitor
ticks both read the stale Online worker, but only one UPDATE matches (`Status == Online`); the
loser skips the runs. The runs themselves are terminated by `TryTerminateRunningBuildRunAsync`
(`WHERE Status == Running`, system scope) — `Lost` + `EndedAt` + `FailureReason = worker-offline`,
never an exit code — and `RunLogIngest.CompleteRunAsync` evicts each run's ingest state (the same
terminal discipline the completed/canceled paths have). A late sequential tick finds the worker
already Offline and the runs already terminal: nothing is re-marked, overwritten or resurrected.
Double protection is deliberate: the run-side CAS is what the exactly-once red proof strips (the
worker gate alone would hide the regression in the single-worker case).

**Claim/scheduler exclusion — verified wiring, no new code.** F-006's claim already requires
`worker.Status == WorkerStatus.Online` before touching the queue (`RunService.ClaimAsync` answers
the 204-shaped `NoRun()` for anything else), and no scheduler path pushes work. The tests now pin
the wire end to end: an Offline worker's claim yields 204 while its queued run stays queued, and
a real heartbeat recovers the worker to `Online` (F-004's heartbeat path — `LastHeartbeatAt`
refresh + status flip) after which the same run is handed out.

**Lease interplay.** The offline sweep deliberately queries only `Status == Running` runs. A
claimed-but-unaccepted lease of a stale worker is left exactly as F-006's sweeper expects it:
still `Queued`, lease intact, unreachable for the Lost marking — the lease sweeper (single expiry
authority) requeues it once the lease window passed, and another worker claims it. A requeue of a
run that never executed is not a retry; marking it Lost would be a lie. Pinned end to end
(claim → silent past threshold → sweep → run Queued with lease → lease sweep → unqueued →
claimed by another worker).

**F-007's shutdown-kill-skip-completed gap closes here.** F-007's known gap: a worker shut down
mid-run kills the whole tree and skips the completed report, so the run stays `Running` — and
since the worker process is gone, its heartbeats stop too. This card's monitor closes the loop
mechanically: heartbeats stop → threshold passes → worker `Offline` → run `Lost(worker-offline)`
with an end time. No new worker-side code was needed (the final best-effort heartbeat of a
graceful shutdown only delays the transition by one threshold; the killed-process case has
nothing left to send). The same loop also covers F-007's persistently-failing completed report on
a worker that later dies. The only residual case — an **Online** worker that can execute but can
never report (transport cut one way) — is F-009's PM-ruling territory: the run's deadline passes,
the grace period expires, and the deadline sweeper marks it
`Lost(run-deadline-exceeded-result-unknown)`; both Lost reasons share this card's mechanism,
`TryTerminateRunningBuildRunAsync`, and the run-page display.

**Tests (`Category=RunLifecycle`, shared with F-009).** Threshold timing (backdated heartbeats, no
real-time waits): past threshold → Offline + Running run → `Lost(worker-offline)` with end time,
two concurrent monitor sweeps produce exactly one Offline and one Lost in total, and a late
sequential tick changes nothing (EndedAt/reason stable); inside the threshold (one and two missed
beats) → Online, run Running; the termination-CAS pin (second call refuses a run that left
Running); no resurrection (heartbeat → Online, claim → 204, Lost row field-identical); claim
exclusion + recovery; the offline worker's lease requeued by the lease sweeper and never Lost;
configuration pins (defaults, out-of-range fallbacks, controllable interval).

**Red proof (restored immediately afterwards, re-verified green).** The `Status == Running`
condition stripped from `TryTerminateRunningBuildRunAsync`'s WHERE →
`The_termination_compare_and_set_refuses_a_run_that_already_left_running` red (**Expected: 0,
Actual: 1** — the second termination re-terminated the already-Lost run, the exactly-once
violation the card forbids: a duplicated/overwritten transition would move `EndedAt`) → restored
→ green.

**Known gaps.**
- Detection latency is bounded by the monitor tick (default 15 s): a worker that dies right after
  a sweep waits up to one interval plus the threshold. Honest, configurable, and irrelevant to
  correctness (the CAS makes any tick count exactly once).
- The monitor evaluates staleness of all workers on every tick (in-memory comparison); at
  thousands of workers this becomes the query to index or batch — not a v0.2 scale.
- `Workers.razor` shows the persisted status (Online/Offline) it has rendered since F-004; the
  transition instant itself is not historized (the run rows carry the evidence).
- A worker that has already been flipped Offline can still land a late `accepted` report (accept
  is a worker-initiated report; an unexpired lease is honoured regardless of the worker's
  status), which leaves that one run `Running` on an Offline worker until its deadline + grace
  expires and the deadline sweeper marks it `Lost(run-deadline-exceeded-result-unknown)`. The
  terminal state is honest, but the display lingers for the whole window. Registered here rather
  than changing F-006's accept semantics: after the flip the worker's heartbeats have stopped, so
  the monitor's next tick takes every other run it still owns; the already-accepted one is an
  honest window with the deadline sweeper as the backstop.

### 2026-11-13 — review fixes

**Major: the fake-Offline window between the snapshot and the CAS.** Review found
`SweepOfflineWorkersAsync` reading a snapshot of Online workers and then flipping each stale one
through `TryMarkWorkerOfflineAsync`, whose WHERE carried only `Status == Online` — no staleness
evidence. A fresh heartbeat landing after the snapshot but before the compare-and-set still got
the worker flipped Offline on evidence that was no longer true, and the sweep that won the flip
marked its Running runs `Lost` — an unretryable, wrong terminal state for runs a live worker was
executing.

**Fix.** `TryMarkWorkerOfflineAsync` gained an observed-value parameter (the snapshot's
`LastHeartbeatAt`); the WHERE now demands `Status == Online && LastHeartbeatAt == observed`, and
the sweep passes the value it snapshotted. Zero rows ⇒ the tick skips that worker's runs entirely;
the next tick re-evaluates it from a fresh snapshot. No schema change (a parameter, not a
column), so the PostgreSQL contract gate is untouched.

**Residual window, restated honestly.** The snapshot-to-CAS window still exists — that is
unavoidable while the staleness comparison is made in memory — but after the fix a beat landing
inside it no longer produces a wrong Offline or a wrong Lost: the tick merely skips the worker
(one tick of the same detection latency the known gap above already describes) and the next tick
re-evaluates. The failure mode changed from "wrong Lost, unretryable" to "skipped this tick".

**Tests (`Category=RunLifecycle`, +2).** The stale-snapshot CAS pin: a run accepted and Running,
heartbeat backdated past the threshold, a real heartbeat lands, then the flip is called with the
stale observation — zero rows, worker still Online, run still Running; the same call with the
current observation still flips exactly once (guards against a vacuously-passing WHERE). The
sweep-seam contrast: two workers, both backdated past the threshold, only one sends a real
heartbeat before the sweep — the beating one stays Online with its run Running, the silent one
flips Offline with its run `Lost(worker-offline)` exactly as before.

**Red proof (restored immediately afterwards, re-verified green).** The
`LastHeartbeatAt == observed` condition stripped from the CAS's WHERE →
`A_fresh_heartbeat_after_the_stale_snapshot_refuses_the_offline_flip` red (**Expected: 0,
Actual: 1** — the flip still won with a stale observation, i.e. the fake-Offline window the fix
closes) → restored → green.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Server/Workers/{WorkerMonitorOptions (new),WorkerOfflineMonitor (new),
    WorkerServiceCollectionExtensions (registers monitor + lazy options)}.cs`
  - `src/Server/Runs/RunService.cs` (`SweepOfflineWorkersAsync` + the shared
    `TryTerminateRunningBuildRunAsync`/`RunFailureReasons.WorkerOffline` with F-009)
  - `src/Server/Data/ZenkinsDbContext.cs` (`TryMarkWorkerOfflineAsync`,
    `TryTerminateRunningBuildRunAsync` — the guarded CAS pair)
  - `src/Server/Program.cs` (AddZenkinsWorkers registration)
  - `src/Server/Components/Pages/Runs/RunDetail.razor` (Lost badge + failure reason display)
  - `tests/Zenkins.Tests/RunLifecycle/{RunOfflineLostTests,RunLifecycleConfigurationTests,
    RunLifecycleHost}.cs (new, shared host with F-009)`
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test -c Release --filter "Category=RunLifecycle"`
  3. `dotnet test -c Release` (the whole suite must stay green)
  4. PostgreSQL contract gate:
     `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
- Results (2026-11-13, re-verified after the review fixes below):
  - Build: **PASS** — `dotnet build -c Release`, 0 warnings / 0 errors (`TreatWarningsAsErrors` active).
  - Tests: **PASS** — **348/348** across the suite (322 pre-card baseline + 26
    `Category=RunLifecycle`: the original 24 plus this card's two review tests).
  - `Category=RunLifecycle`: **PASS** — 26/26.
  - PostgreSQL contract gate: **PASS** — "No changes have been made to the model since the last
    migration." (the review fix added a CAS parameter, not a column).
  - Red proof: **PASS** — two recorded: the exactly-once guard stripped →
    `The_termination_compare_and_set_refuses_a_run_that_already_left_running` red (Expected: 0,
    Actual: 1) → restored → green; and the review fix's observed-heartbeat evidence stripped →
    `A_fresh_heartbeat_after_the_stale_snapshot_refuses_the_offline_flip` red (Expected: 0,
    Actual: 1) → restored → green (recorded in the review fixes above).
  - TC-001…TC-004 all covered as listed above (staleness → Offline → Lost exactly once; one
    missed beat is not Offline; no retry/no resurrection with a returning worker; the offline
    worker's lease requeued and claimable by another worker, never Lost).
