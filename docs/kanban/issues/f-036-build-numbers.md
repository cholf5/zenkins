---
id: F-036
title: Build numbers and permalinks — #N per project, /runs/last, /lastSuccessful
type: feature
priority: P2
size: S
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

Every run gets a per-project build number displayed as `#N` everywhere (UI, logs,
env, API, notifications) with stable permalinks, so a person or a script can name a
build without pasting a guid.

## Background

Runs are identified by guid and shown as the first 8 hex characters. That is
unreadable in chat ("the 4f2a1c9e build failed?"), impossible to type, and not
orderable by a human. Jenkins' `#N` plus `lastSuccessfulBuild` permalinks are the
convention people already use in dashboards and badges.

## Acceptance Criteria

- [x] `BuildRun.Number` is a per-project positive integer assigned atomically at
      trigger time; concurrent triggers of the same project never collide (unique
      index on (ProjectId, Number)).
      *Assigned in the staging routine under the project row lock (the F-028/F-029 lock,
      `MAX(Number) + 1`), with one savepoint-guarded retry if the index ever refuses a
      number. The number is also constrained to be positive (`CK_BuildRuns_Number`, with the
      column's leftover `DEFAULT 0` dropped - review M1), and the claim answers `null` rather than
      `0` for a row with no number. Pinned by
      `BuildNumberTests.Concurrent_triggers_get_distinct_numbers_one_to_n` (8 parallel triggers →
      exactly 1..8), by the unique index and the check constraint (`BuildNumberMigrationTests`), and
      on real PostgreSQL by `PostgresBuildNumberTests` (an uncommitted rival takes `#2`: the product
      collides, rolls back to its savepoint, re-reads `MAX`, and commits `#3` - red without the retry).*
- [x] Everything human-facing shows `#N`: run lists, the run page, the project's
      last-run column, the dashboard, the log download filename, notifications
      (F-039) and API responses (F-027).
      *Done on every surface that exists: run page title/heading and permalink line,
      project overview/pipelines recent runs, the project runs list, the worker detail page,
      the dashboard's recent runs and failures, the log download filename
      (`zenkins-run-<N>-<guid>-log.txt`), and `ApiRunResource.number`. Notifications (F-039)
      do not exist yet - they inherit the number when they land, because the number is on
      `RunView` and on the run resource.*
- [x] Steps' environment gets `ZENKINS_BUILD_NUMBER` (and `ZENKINS_BUILD_ID` keeps
      the guid), consistent with the existing `ZENKINS_*` metadata.
      *The claim payload carries `number`; the worker's run-environment layer writes
      `ZENKINS_BUILD_NUMBER` and `ZENKINS_BUILD_ID` (plus the untouched `ZENKINS_RUN_ID`)
      last, so a parameter of the same name cannot spoof either. Pinned by
      `RunEnvironmentTests` (3 facts); the end-to-end "a child process reads it" is the
      Milestone harness's existing env coverage, which now sees the variable too.*
- [x] Permalinks resolve: `/projects/{id}/runs/{number:int}` renders the run;
      `/projects/{id}/runs/last` and `/projects/{id}/lastSuccessful` redirect to the
      right run, and answer 404 honestly when there is none.
      *The number route is a second `@page` on the run detail page (same rendering, same
      actions); the two aliases are minimal-API redirects to `/projects/{id}/runs/{N}` and a
      bare 404 when there is nothing to point at (a failed-only project has no "last
      successful"). Pinned by `BuildNumberTests.The_three_permalinks_resolve_and_answer_404_honestly`.*
- [x] The guid route keeps working (bookmarks, the log hub's ticket flow), so no
      existing link breaks.
      *`/runs/{guid}` is unchanged and every action on the page still targets the id;
      pinned by `BuildNumberTests.The_guid_route_keeps_working_beside_the_number_route`.*
- [x] Existing runs are backfilled deterministically by creation order, and the
      backfill is one migration that cannot produce duplicates.
      *`20261116075749_AddBuildNumbersAndRebuild`: the number is a correlated
      `COUNT(*) + 1` ordered by `(CreatedAt, Id)` per project - a bijection inside each
      project, so no duplicates - applied before the unique index is created. Pinned by
      `BuildNumberMigrationTests`, which migrates a database that already holds runs
      (inserted out of order, two sharing an instant), asserts 1..n per project including the
      id tie-break, and asserts the index refuses a duplicate row.*
- [ ] In multibranch mode (F-023) numbering is per branch job, not shared across the
      container.
      **Not verifiable yet: F-023 does not exist.** The counter is per `ProjectId` - the
      only "job" unit the product has today - so when F-023 makes each branch job its own
      job row, the counter follows that row by construction. Left unchecked rather than
      claimed on a card that is not there.

## Subtasks

- [x] Entity + migration: `Number`, unique index, backfill statement ordered by
      `CreatedAt` then `Id`.
- [x] Assignment: inside the trigger transaction, take the project row lock (same
      pattern as the claim) and increment; retry once on a unique-violation race.
- [x] Display sweep: every page and message that currently prints the short guid.
- [x] Permalink routes plus their 404 behaviour and Q-006 inventory rows.
- [x] `RunView`/API: expose `Number` (and keep `Id`).
- [x] PRD §3.5 and the run-list UI doc: record the number contract.
      *PRD §3.5 gained "Build Number 与固定链接"; there is no separate run-list UI document in
      this repository, so §3.5 is where the run list's own contract lives (it already owns
      the run information list). `docs/api-v1.md` documents `number` on the run resource.*

## Dependencies

- none (F-023 and F-039 consume the number; both reference it)

## Test Cases

### TC-001: Concurrent triggers get distinct, increasing numbers

Steps:
1. Fire N triggers for one project in parallel (test host, no worker).

Expected:
- N distinct numbers, exactly 1..N as a set; no duplicate, no gap-causing retry loop
  left behind.

### TC-002: Permalinks resolve and refuse honestly

Steps:
1. Project with two runs, the second successful: GET `/runs/1`, `/runs/last`,
   `/lastSuccessful`; then repeat on a project with no runs.

Expected:
- The three routes render/redirect to `#1`, the newest, and the newest successful
  run respectively; the empty project answers 404 on all three.

### TC-003: The number is in the environment and the log filename

Steps:
1. Trigger and complete a run; inspect the step's `ZENKINS_BUILD_NUMBER` and the log
   download's filename.

Expected:
- The env value equals the displayed `#N`; the filename contains it.

## Notes and Decisions

- A per-project counter row was rejected in favour of the row lock: the project row
  is already locked on hot paths, and one counter table would need its own tenant
  and cleanup story.

## Implementation notes (2026-11-16)

- **The number is assigned where the gate already locks.** `RunService.StageQueuedRunAsync` - the one
  staging routine every trigger source uses (form, API, webhook) - takes the project row lock, applies
  F-028/F-029, then assigns `MAX(Number) + 1` and inserts. Nothing needed a second counter table or a
  second insert path; a rebuild (F-037) goes through the same call and is numbered like any other run.
- **The retry is a savepoint, not a second statement.** On PostgreSQL a failed statement poisons the
  transaction, so "retry once" has to roll back to a savepoint before re-reading the maximum. The
  retry only ever fires for a writer outside this routine; the lock is what makes the sequence correct.
- **Two columns, one migration, provider-neutral SQL.** `Number` (int, unique with `ProjectId`) and
  `RebuiltFromRunId` (nullable, no FK - F-037's). The backfill is a correlated count, not a window
  function, so PostgreSQL and SQLite answer it identically; the unique index is created *after* the
  backfill, so the statement cannot collide with itself. The column keeps the migration's default of 0;
  an out-of-band insert that relied on it would hit the unique index rather than share `#0`.
- **The number route is the run page, not a redirect.** `RunDetail` carries a second `@page`
  (`/projects/{ProjectId:guid}/runs/{Number:int}`) and resolves by (project, number); the guid route
  resolves by id. One component, two addresses, and every form on the page still targets the id.
- **The log filename carries both**: `zenkins-run-<N>-<guid>-log.txt`. The number is what a person
  reads; the guid keeps the file traceable to the row it came from (and keeps the old contract that the
  filename contains the run id).
- **Seeded test runs now carry numbers too.** The unique index is a real constraint, so `RunTestHost`,
  `DashboardTestHost` and the hand-seeded rows in the tenancy fixtures assign `MAX + 1` (or `1` where the
  fixture owns the whole project) exactly as the product does.

### Verification (2026-11-16)

- `dotnet build Zenkins.slnx -c Release`: 0 warnings, 0 errors (`artifacts/logs/f-036-f-037-build.log`).
- The repository gate, `scripts/verify.sh`'s commands and filters, one suite at a time:
  fast **1165**, Milestone **12**, Recovery **4**, PublishSmoke **2**, PublishAot **2**, GitSshd **3**,
  Postgres **2**; all seven **1190**. The Postgres suite ran against a live throwaway PostgreSQL 16.15
  (`initdb` in a temp directory) named by `ZENKINS_TEST_POSTGRES`; without that variable both facts skip
  with the recorded reason (verified in the same run). Logs: `artifacts/logs/f-036-f-037-fast.log` and
  `artifacts/logs/f-036-f-037-gate.log` (the six harnesses, one section each).
- `dotnet ef migrations has-pending-model-changes` with the **PostgreSQL** provider: **no pending
  changes**. (`scripts/verify.sh` does not run it; it is the documented migration contract.)
- This card's own tests: `BuildNumberTests` (6), `BuildNumberMigrationTests` (1),
  `PostgresBuildNumberTests` (1, `Category=Postgres`, run here against a throwaway PostgreSQL 16.15),
  `RebuildPinTests` (2, F-037's worker half), `RunEnvironmentTests` (3) and the three Q-006 probes -
  all green.

### Not covered, and why

- **Notifications (F-039)**: the card is not implemented, so "notifications show #N" is inherited work,
  satisfied by the number being on `RunView`/the run resource. AC2 is checked for every surface that
  exists today.
- **Multibranch (F-023)**: AC7 is left unchecked; the counter is per job (`ProjectId`), which is the only
  job unit the product has.
- **Nothing on this card is left at "reviewed only".** The unique-violation retry used to be; review M2
  pointed out that it is unreachable on SQLite (every write serializes), so the one provider where it can
  run had no test. `PostgresBuildNumberTests` now pins it: a rival connection's uncommitted `#2` is
  invisible to the product's READ COMMITTED `MAX` read, the product's INSERT blocks on the unique index
  (the test waits for that lock wait in `pg_stat_activity` rather than sleeping), the rival then commits,
  and the product must answer success with `#3`. **Mutation check**: with the retry deleted (local edit,
  reverted immediately) the fact goes red on PostgreSQL; restored, it is green. The throwaway server was
  stopped and deleted afterwards.

## Review round 1 (2026-11-16) - the fixes

Three findings, all closed here; the review also judged the two pre-existing test edits (the pages that
used to link by guid) a legitimate display change rather than a loosened assertion, which is what they
are: the new assertions name one explicit number per row, so a wrong number is red.

- **M1 - the leftover `DEFAULT 0` was a real defect, not a comment.** The first out-of-band insert of a
  project would silently have got `#0` (a renderable `/projects/{id}/runs/0`, `Run #0`,
  `ZENKINS_BUILD_NUMBER=0`); only the second would have collided, which is what the card had claimed.
  Fixed three ways, and each is pinned: migration `20261116085457_AddBuildNumberPositiveCheck` drops the
  default (`ALTER COLUMN … DROP DEFAULT` on PostgreSQL, the table rebuild EF performs for SQLite) **and**
  adds `CK_BuildRuns_Number` (`"Number" > 0`), both in the model and in the schema;
  `BuildNumberMigrationTests` now asserts that an insert without `Number` fails as `NOT NULL` and one
  with `0` fails the check; and the claim answers `int?` - `Number is > 0 ? Number : null` - so the
  worker's "no number, no `ZENKINS_BUILD_NUMBER`" defence is reachable, with `RunEnvironmentTests`
  exercising both spellings of "no number" (`null` and `0`).
- **M2 - the retry is verified where it is reachable.** See the last bullet above; it is a committed
  `Category=Postgres` fact in the same xunit collection as F-029's, with a mutation record.
- **M3/L1/L2/L5 - the card's evidence statements now match the tests.** This card's display claim gained
  a real page assertion (`The_dashboard_and_the_project_pages_name_a_run_by_its_number`: the dashboard's
  recent runs and the project overview print `Run #1` and link to the permalink), and the permalink line
  shows `#N` as its label with the permalink as its `href` - the card's motivation was a name a person can
  read out loud, so the whole path is no longer the visible text.
- **One follow-up recorded rather than done:** `RunService` reads the run's own facts through
  `RunReruns.Refusal`, but its two service-side checks (a resolvable Git credential, today's parameter
  definitions) are outside that function because they read project rows the run does not snapshot. The
  drift is therefore possible in exactly one direction - a rendered button whose click is refused with a
  full sentence - and it is documented at the function. Snapshotting the credential assignment and the
  repository URL on `BuildRun` (F-037's note) is what would close it.

### 2026-11-16 — independent review: PASS

Two rounds in an isolated copy of the uncommitted batch, the second with a real `initdb` PostgreSQL
16.15 cluster and a mutation. Six of seven criteria satisfied; AC7 (multibranch numbering) is parked
because F-023 does not exist — the counter is keyed on `ProjectId`, which is today's only job unit,
so F-023 inherits it by construction.

- Allocation is atomic where it matters: the number is `MAX+1` inside `StageQueuedRunAsync`'s
  project row lock, with a savepoint retry on a unique violation. Eight concurrent triggers yield
  exactly `{1..8}`, three consecutive runs, no flakes.
- The retry branch was the first round's finding: it is only reachable on PostgreSQL, because
  SQLite serializes the whole write, and it had no test. It now does — a deterministic fact that
  inserts an uncommitted `#2` from a second connection, waits on `pg_stat_activity` until the
  product's insert is really blocked (`staging.IsCompleted == false` asserted at that moment),
  commits the rival, and checks the product retries to `#3` with the row intact. Disabling the retry
  turns it red with a real `23505`; the reviewer reproduced both directions.
- The migration backfills by `(CreatedAt, Id)` and the reviewer confirmed the test builds the dirty
  case first — out-of-order rows and two rows sharing a `CreatedAt` — then asserts `1/2/3` with ties
  broken by `Id`, a second project restarting at `1`, and the unique index refusing a duplicate.
- The `AddColumn` default was the second finding: `DEFAULT 0` survived the backfill, so a project's
  first out-of-band insert would have taken `#0` silently — rendering `Run #0` and writing
  `ZENKINS_BUILD_NUMBER=0` into a step's environment. Fixed with a `DROP DEFAULT` plus
  `CHECK (Number > 0)`, verified in both providers' generated SQL (PostgreSQL alters, SQLite
  rebuilds the table), and the claim now sends `int?` so the worker's "no number" path is reachable
  instead of dead.

### 2026-11-16 — what the display change cost elsewhere

Two existing assertions changed shape: `WorkersUiDetailTests` and `ProjectEnableDisableTests` used to
locate a run link by short guid and now assert `Run #2` plus an exact number permalink. The reviewer
checked them line by line and called them a legitimate display-contract change — one concrete number
per line is a stronger assertion than a truncated guid, and every other assertion on those pages
survived. The Dashboard command ceiling was not touched and did not need to move.
