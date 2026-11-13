---
id: D-002
title: Dashboard: what is running, what is queued, what broke recently
type: feature
priority: P1
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

One screen answers the operator's three questions on arrival: is anything running, is anything stuck
in the queue, and what failed recently. Today `/` is a 59-line page that renders a single "No builds
yet" empty state and nothing else.

## Background

PRD §5.3 (visibility) and roadmap v0.9 ("Dashboard: recent runs, running, queued, worker status,
recent failures"). Everything this page shows already exists as rows: `BuildRun` carries status,
trigger, branch, timings and failure reason; `Worker` carries status and last heartbeat; F-010's
sweeper already decides which workers are offline.

Decisions:

- **One read model, bounded queries.** A single `DashboardQuery` service returns everything the page
  needs for the active organization; the page must not issue a query per run row. The test asserts a
  query ceiling (counting commands via an interceptor), because "it renders" says nothing about cost.
- **"Recent" is bounded and defined**: the last 20 runs across the organization's projects by
  `StartedAt ?? CreatedAt`, and failures are the terminal `Failed`/`Lost` runs among those, plus a
  7-day count. No pagination on the dashboard — the runs list is the place to browse.
- **Counts, not charts.** Queued/running counts, workers online/offline/busy, and a failure count for
  the window. Trend analysis is a parking-lot item; inventing charts here would be scope without a
  consumer.
- **Liveness derivation reuses F-010, not a second rule.** A worker is offline when the sweeper says
  so; the page reads `Status` rather than comparing `LastHeartbeatAt` to its own clock, so the
  dashboard and the run lifecycle can never disagree.
- **Tenant and switcher honesty**: everything is scoped by the existing global filter, and switching
  organization changes every number on the page (asserted by comparing two organizations' dashboards).

## Acceptance Criteria

- [ ] Counts match the rows: queued, running, and workers online/offline/busy equal the corresponding
      table counts for the active organization at the same instant (asserted against direct SQL).
- [ ] Recent runs show project, branch, status, trigger type, duration (or elapsed while running) and
      failure reason where present, ordered newest first, capped at 20.
- [ ] A run that is `Running` shows live elapsed time rather than a blank or a zero.
- [ ] Recent failures are the `Failed`/`Lost` runs in the window with their reason; `Canceled` is not
      counted as a failure but is visible in the recent list.
- [ ] Worker summary distinguishes offline from busy, and the offline count agrees with the value the
      offline sweeper would produce (no independent heartbeat arithmetic).
- [ ] Empty states are specific: no projects, no runs yet, no workers connected each say what to do
      next rather than one generic blank.
- [ ] Switching organization changes every figure on the page; another organization's runs and
      workers never appear (both directions).
- [ ] The page issues a bounded number of database commands regardless of how many runs exist
      (asserted with an interceptor and a fixture with more runs than the cap).
- [ ] Owner and Member both see the dashboard; no write affordance appears on it.

## Subtasks

- [ ] `src/Server/Dashboard/DashboardQuery.cs` (new): bounded read model (counts, recent runs, worker
      summary, failure window).
- [ ] `src/Server/Components/Pages/Home.razor`: replace the empty shell with the real dashboard,
      keeping the existing layout, heading and `NoOrganizationNotice` behaviour.
- [ ] Shared bits: status badge and duration formatting used by both the dashboard and the runs list,
      so the same run does not read differently on two pages.
- [ ] Tests (`Category=Dashboard`): counts vs SQL, ordering and cap, live elapsed for a running run,
      failure window, worker summary vs sweeper, empty states, organization switch, query ceiling.

## Dependencies

- none (F-006..F-010 produce the rows this page reads)

## Test Cases

### TC-001: The numbers are the rows

Steps:
1. Seed an organization with two projects, runs in `Queued`, `Running`, `Success`, `Failed` and
   `Canceled`, and two workers (one online, one swept offline).
2. Open `/`.

Expected:
- Queued and running counts equal the row counts; the failure count and list contain the `Failed` run
  and not the `Canceled` one; the worker summary shows one online and one offline.

### TC-002: A running run keeps moving

Steps:
1. Open the dashboard with a run in `Running` whose `StartedAt` is two minutes old; wait a few seconds.

Expected:
- Elapsed time is shown and increases; the row does not read as finished or blank.

### TC-003: The cost does not grow with history

Steps:
1. Seed 200 runs, open the dashboard with a command interceptor counting executions.

Expected:
- The command count stays at or below the documented ceiling and does not scale with the 200 rows;
  the page shows exactly 20 recent runs.

## Development Log

### 2026-11-13

- Planned from roadmap v0.9 after the v0.3 milestone was accepted. The page reads rows that already
  exist; no schema change is expected, and no charting is in scope.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Dashboard/*`, `src/Server/Components/Pages/Home.razor`,
  `src/Server/Components/Shared/*` (status badge and duration formatting),
  `tests/Zenkins.Tests/Dashboard/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Dashboard"`;
  full suite; PostgreSQL contract gate not required unless the model changes.
- Results: not run (card not started).
