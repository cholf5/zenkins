---
id: D-005
title: Workers page: per-worker detail, what each one is running, and safe controls
type: feature
priority: P2
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
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

- [ ] The roster shows OS, architecture, version, CPU count, max concurrency, tags, status, last
      heartbeat, and current run count per worker.
- [ ] A worker row links to its detail; the detail lists its current and recent runs with status and
      links to each run.
- [ ] The displayed offline state equals what the offline sweeper would conclude at that moment
      (asserted by ageing a heartbeat and sweeping, then comparing).
- [ ] Disabling a worker prevents further claims (asserted through the real claim endpoint) while a run
      already running continues to completion; re-enabling restores claiming.
- [ ] Revoking a registration token prevents a new registration with it immediately, and does not
      affect an already-registered worker's heartbeats.
- [ ] Deleting a worker with run history is refused with a message naming why; a worker with no runs
      can be deleted.
- [ ] Every action is Owner-only on the server; a Member's forged request is 403 and changes nothing,
      and the UI does not offer what the server would refuse.
- [ ] Another organization's worker id is 404 on the detail page and on every action.

## Subtasks

- [ ] `Worker` gains a disabled flag (or equivalent) enforced in the claim query, with a migration.
- [ ] Roster and detail read model: worker plus its current runs, bounded.
- [ ] Disable/enable/revoke/delete actions with the permission rules above.
- [ ] Tests (`Category=WorkersUi` plus the claim-path test): roster fields, offline agreement with the
      sweeper, disable/enable through the real claim endpoint, token revoke, delete refusal,
      permissions, cross-tenant 404.

## Dependencies

- F-004 (workers, tokens), F-006 (claim/lease), F-010 (offline sweeper), D-002 (shared status badge)

## Test Cases

### TC-001: The page and the lifecycle agree

Steps:
1. Register a worker and let it heartbeat; stop the heartbeat; run the offline sweeper.

Expected:
- After the sweeper marks it offline, the page shows offline; before that, online. The page's answer
  never differs from the row the sweeper wrote.

### TC-002: Disable means stop claiming, not stop running

Steps:
1. Start a long run on a worker; disable the worker; queue a second run; re-enable; wait.

Expected:
- The first run finishes normally; the second is not claimed while disabled; after re-enabling it is
  claimed and executed exactly once.

### TC-003: A worker with history is not deletable

Steps:
1. Attempt to delete a worker that has a completed run; then one that never ran.

Expected:
- The first is refused with a message that names the runs; the second is deleted. No run loses its
  worker reference in either case.

## Development Log

### 2026-11-13

- Planned from roadmap v0.9. The disable flag is the only schema change in this batch's UI cards, and
  it exists because "stop using this machine" is otherwise a database edit.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Components/Pages/Workers.razor`, `src/Server/Workers/*`,
  `src/Server/Data/Entities/Worker.cs`, `src/Server/Data/Migrations/*`, `src/Server/Runs/RunService.cs`
  (claim query), `tests/Zenkins.Tests/WorkersUi/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=WorkersUi"`;
  full suite; PostgreSQL contract gate must be clean if the model changes.
- Results: not run (card not started).
