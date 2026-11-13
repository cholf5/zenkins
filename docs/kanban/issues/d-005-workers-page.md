---
id: D-005
title: Workers page: per-worker detail, what each one is running, and safe controls
type: feature
priority: P2
size: M
status: done
created: 2026-11-13
updated: 2026-11-14
blocked: none
---

## Goal

The workers page answers "which machines do I have, are they healthy, what are they running right
now, and how do I stop trusting one" — today it lists name, OS/architecture, tags, status and last
heartbeat, with no link from a worker to its work and no way to act on it.

## Background

Roadmap v0.9 ("Workers page: OS, arch, tags, status, current runs, concurrency, last heartbeat").
`Workers.razor` (173 lines) already renders the roster and F-004 already mints and revokes
registration tokens; `Worker` carries `Status`, `LastHeartbeatAt`, `MaxConcurrency`, `Tags`, `Os`,
`Architecture`, `Version`, `CpuCount`. `BuildRun.WorkerId` is the join that is missing from the page.

Decisions:

- **Health is F-010's judgement, shown, not recomputed.** The page displays `Status` as the sweeper sets
  it; a page that computed "offline" from heartbeat age would eventually disagree with the run
  lifecycle and then someone would trust the wrong one.
- **"Current runs" is the honest question, so it is a query**: `Running` runs where `WorkerId` matches,
  capped, with links. The count next to the roster comes from the same query, not from a denormalised
  column.
- **Controls are non-destructive by design.** Revoking a registration token (exists) and disabling a
  worker (new) are the actions; deleting a worker that has run history would orphan the runs that name
  it, so deletion is refused with an explanation. Disable means "stop claiming new work": the worker's
  next claim gets the existing not-assigned answer, in-flight runs continue, and the state is
  reversible.
- **Disabling is a server fact, not a UI flag.** It is written to the worker row and enforced in the
  claim query, so a worker cannot bypass it by ignoring the page.
- **Member read-only**, Owner may act: the same rule F-004 already applies to tokens.

## Acceptance Criteria

- [x] The roster shows OS, architecture, version, CPU count, max concurrency, tags, status, last
      heartbeat, and current run count per worker.
- [x] A worker row links to its detail; the detail lists its current and recent runs with status and
      links to each run.
- [x] The displayed offline state equals what the offline sweeper would conclude at that moment
      (asserted by ageing a heartbeat and sweeping, then comparing).
- [x] Disabling a worker prevents further claims (asserted through the real claim endpoint) while a run
      already running continues to completion; re-enabling restores claiming.
- [x] Revoking a registration token prevents a new registration with it immediately, and does not
      affect an already-registered worker's heartbeats.
- [x] Deleting a worker with run history is refused with a message naming why; a worker with no runs
      can be deleted.
- [x] Every action is Owner-only on the server; a Member's forged request is 403 and changes nothing,
      and the UI does not offer what the server would refuse.
- [x] Another organization's worker id is 404 on the detail page and on every action.

## Subtasks

- [x] `Worker` gains a disabled flag (`IsDisabled`) enforced in the claim query, with a migration
      (`20261113203304_AddWorkerDisabled`, generated with the PostgreSQL provider).
- [x] Roster and detail read model: worker plus its current runs, bounded
      (`RunService.GetCurrentRunCountsAsync` / `GetWorkerRunsAsync`, capped 20 current / 10 recent).
- [x] Disable/enable/revoke/delete actions with the permission rules above (revoke is F-004's
      existing Owner-only endpoint, surfaced from the roster as before).
- [x] Tests (`Category=WorkersUi` plus the claim-path test): roster fields, offline agreement with the
      sweeper, disable/enable through the real claim endpoint, token revoke, delete refusal,
      permissions, cross-tenant 404. The claim-path test (TC-002) lives in `WorkersUiControlsTests`
      and runs in the fast suite, so it exercises the real claim endpoint exactly where the rest of
      this card's tests live.

## Dependencies

- F-004 (workers, tokens), F-006 (claim/lease), F-010 (offline sweeper), D-002 (shared status badge)

## Test Cases

### TC-001: The page and the lifecycle agree — PASS

Steps:
1. Register a worker and let it heartbeat; stop the heartbeat; run the offline sweeper.

Expected:
- After the sweeper marks it offline, the page shows offline; before that, online. The page's answer
  never differs from the row the sweeper wrote.

Result (`WorkersUiRosterTests.The_displayed_offline_state_equals_the_offline_sweepers_judgement`):
- Before the sweep the row reads `Online` and the detail/roster pages show no Offline badge; after
  ageing the heartbeat 5 s (threshold 2 s) and one tick through the real
  `WorkerOfflineMonitor.SweepOnceAsync` seam (1 worker offlined), the row reads `Offline` and the
  detail page and the roster both show `Offline`. The page is never compared against a recomputation —
  only against the row the sweeper wrote.

### TC-002: Disable means stop claiming, not stop running — PASS

Steps:
1. Start a long run on a worker; disable the worker; queue a second run; re-enable; wait.

Expected:
- The first run finishes normally; the second is not claimed while disabled; after re-enabling it is
  claimed and executed exactly once.

Result (`WorkersUiControlsTests.Disabling_stops_claims_while_the_in_flight_run_finishes_and_enable_restores`):
- Run 1 claimed and accepted (Running) with max concurrency 2 (so a free slot exists); disable via the
  real Owner form (PRG to `/workers/{id}?notice=worker-disabled`, row `IsDisabled=true`, detail page
  shows the Disabled badge and offers Enable); the second run's claim through the real
  `/api/workers/claim` endpoint answers 204 while disabled (slot was free — the refusal is the flag,
  not concurrency); run 1 completes normally through the real completed endpoint (Success); a second
  claim still answers 204; after enable (PRG `?notice=worker-enabled`, row `IsDisabled=false`) the same
  run 2 is claimed (200), accepted and completed exactly once (single terminal `Success` row).

### TC-003: A worker with history is not deletable — PASS

Steps:
1. Attempt to delete a worker that has a completed run; then one that never ran.

Expected:
- The first is refused with a message that names the runs; the second is deleted. No run loses its
  worker reference in either case.

Result (`WorkersUiControlsTests.A_worker_with_run_history_is_not_deletable_and_one_without_runs_is`):
- The history worker's delete answers a PRG redirect to
  `/workers/{id}?error=worker-has-runs&runs=1` and the page that redirect lands on reads "This worker
  executed 1 run, so it cannot be deleted - its runs keep their worker reference."; the worker row
  remains. The worker without runs deletes with `?notice=worker-deleted` and its row is gone from the
  roster. After both operations the completed run still carries its `WorkerId`.

## Development Log

### 2026-11-13

- Planned from roadmap v0.9. The disable flag is the only schema change in this batch's UI cards, and
  it exists because "stop using this machine" is otherwise a database edit.

### 2026-11-14

- Implemented. `Worker.IsDisabled` (non-nullable bool, default `false`) added; migration
  `20261113203304_AddWorkerDisabled` generated with the PostgreSQL provider per the contract
  (`uuid`/`timestamp with time zone`/`character varying(...)` snapshot strings intact);
  `has-pending-model-changes` clean under the same provider env; the migration applied to a fresh
  SQLite database (`dotnet ef database update`, default provider) and the column landed as
  `boolean NOT NULL DEFAULT 0`.
- Claim enforcement lives in `RunService.ClaimAsync`: a disabled worker's claim returns the same
  `RunClaimResult.NoRun()` (204) an unmatched worker gets, so ignoring the page cannot bypass it;
  `AcceptAsync`/`CompleteAsync` are untouched, which is what lets the in-flight run finish.
- Current-runs is one definition with two readers in `RunService`: `GetCurrentRunCountsAsync` (the
  roster's per-worker count, one grouped read over the tenant's Running runs) and
  `GetWorkerRunsAsync` (the detail page's current list, capped at 20, plus recent non-Running runs,
  capped at 10, newest first, ordered in memory like every other run list). No denormalised column.
- Controls: `WorkerService.SetDisabledAsync` / `DeleteWorkerAsync` (Owner-only via the F-004
  `IsOwnerAsync` gate, tenant-filtered lookups so a foreign id is 404) behind form POSTs
  `/workers/{id}/disable`, `/workers/{id}/enable`, `/workers/{id}/delete` with antiforgery and PRG.
  The delete count-and-remove runs inside one transaction so a run landing between check and commit
  cannot orphan silently. Token revoke remains F-004's existing Owner-only endpoint.
- UI: the roster gained the current-run count, the Disabled badge and the per-row detail link; the new
  `/workers/{id}` detail page (F-003 404 discipline, D-002's `RunStatusBadge` + `RunDisplay` — no
  second status map) shows the machine fields, current and recent runs, and — Owner only — the
  disable/enable and delete controls.
- Two test-authoring mistakes were made and fixed while proving this card (D-005-B1/B2 in Bugs); no
  product defect surfaced. Recorded outputs: build 0 warnings / 0 errors; `Category=WorkersUi` 9/9;
  fast suite 940/940 (0 failed, 0 skipped); Recovery 4/4; Milestone 6/6.
- Review follow-ups (PASS with P2 observations) applied: the delete comment now states the real
  guarantee (sub-millisecond window, one transaction, SetNull as last line of defence) instead of an
  absolute claim; `GetCurrentRunCountsAsync` no longer selects the unused `run.Id`; the roster's
  unreachable `worker-has-runs` error mapping (delete refusal always PRGs to the detail page) was
  removed; D-005-B3/B4 recorded as deferred. Re-verified: build 0 warnings / 0 errors;
  `Category=WorkersUi` 9/9.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| D-005-B1 | trivial | First test draft asserted the roster's "8 CPUs" rendering against the detail page, whose Machine card shows a "CPU count" field instead — the assertion failed on a correct page. | Fixed | Test now asserts the page's own labels/ids (`CPU count`, `worker-cpus`). |
| D-005-B2 | trivial | The delete-refusal test read the detail page without the `?error=worker-has-runs&runs=1` query the server's PRG redirect carries, so the refusal message was absent from the fetched HTML. | Fixed | The test follows the asserted redirect Location (as the browser would) and then finds the message. |
| D-005-B3 | P2 | `RunService.GetWorkerRunsAsync` reads all of the worker's non-Running runs into memory before capping the recent list at 10. | Deferred | Same established pattern as `GetProjectRunsAsync` (in-memory ordering because SQLite cannot ORDER BY a DateTimeOffset column); a SQL-side cap is a lever for a later pass. |
| D-005-B4 | P2 | The delete-refusal message counts every run still referencing the worker, so a queued-but-never-executed run makes "executed N run(s)" overcount in that corner case. | Deferred | The refusal itself is correct either way — the worker reference is real and must survive; message-wording precision can ride a later pass. |

## Verification

- Related files: `src/Server/Components/Pages/Workers.razor`,
  `src/Server/Components/Pages/WorkerDetail.razor`, `src/Server/Workers/WorkerEndpoints.cs`,
  `src/Server/Workers/WorkerService.cs`, `src/Server/Runs/RunService.cs` (claim query + current-runs
  reads), `src/Server/Data/Entities/Worker.cs`,
  `src/Server/Data/Migrations/20261113203304_AddWorkerDisabled.cs` (+ Designer + snapshot),
  `tests/Zenkins.Tests/WorkersUi/*` (host + roster/detail/controls tests)
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=WorkersUi"`;
  fast suite `dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery"`;
  `Category=Recovery` and `Category=Milestone`; PostgreSQL contract gate (generate/check with
  `Database__Provider=Postgres`).
- Results (all run 2026-11-14, actual outputs):
  - `dotnet build -c Release` → `Build succeeded.` / `0 Warning(s)` / `0 Error(s)`.
  - Migration gate:
    `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
    → `No changes have been made to the model since the last migration.` The generated
    `20261113203304_AddWorkerDisabled` adds `Workers.IsDisabled` as `type: "boolean", nullable: false,
    defaultValue: false`; the snapshot keeps the PostgreSQL type strings (`uuid`,
    `timestamp with time zone`, `character varying(...)`).
  - Fresh SQLite database migrates cleanly through the new migration: `ConnectionStrings__Default="Data Source=<tmp>/zenkins-fresh.db" dotnet ef database update --project src/Server` applies all
    migrations including `20261113203304_AddWorkerDisabled`; a second update reports
    `No migrations were applied. The database is already up to date.`; `PRAGMA table_info(Workers)`
    shows `IsDisabled | boolean | notnull | default 0`.
  - `dotnet test -c Release --filter "Category=WorkersUi"` → total 9, failed 0, skipped 0.
  - Fast suite `dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery"` →
    total 940, failed 0, skipped 0 (1m 29s).
  - `dotnet test -c Release --filter "Category=Recovery"` → total 4, failed 0, skipped 0.
  - `dotnet test -c Release --filter "Category=Milestone"` → total 6, failed 0, skipped 0.
