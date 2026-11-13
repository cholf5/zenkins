---
id: D-002
title: Dashboard: what is running, what is queued, what broke recently
type: feature
priority: P1
size: M
status: done
created: 2026-11-13
updated: 2026-11-14
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
  **Measured and documented ceiling:** the read model issues exactly 4 commands per render (worker
  status aggregate, slim run scan, recent-20 detail with project names, project count); a full page
  render measured 13 commands (3 tenant/session resolutions + the read model), so the page ceiling is
  documented at 16. Both are asserted with a `DbCommandInterceptor` against a fixture with 201 runs.
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

- [x] Counts match the rows: queued, running, and workers online/offline/busy equal the corresponding
      table counts for the active organization at the same instant (asserted against direct SQL).
- [x] Recent runs show project, branch, status, trigger type, duration (or elapsed while running) and
      failure reason where present, ordered newest first, capped at 20.
- [x] A run that is `Running` shows live elapsed time rather than a blank or a zero.
- [x] Recent failures are the `Failed`/`Lost` runs in the window with their reason; `Canceled` is not
      counted as a failure but is visible in the recent list.
- [x] Worker summary distinguishes offline from busy, and the offline count agrees with the value the
      offline sweeper would produce (no independent heartbeat arithmetic).
- [x] Empty states are specific: no projects, no runs yet, no workers connected each say what to do
      next rather than one generic blank.
- [x] Switching organization changes every figure on the page; another organization's runs and
      workers never appear (both directions).
- [x] The page issues a bounded number of database commands regardless of how many runs exist
      (asserted with an interceptor and a fixture with more runs than the cap).
- [x] Owner and Member both see the dashboard; no write affordance appears on it.

## Subtasks

- [x] `src/Server/Dashboard/DashboardQuery.cs` (new): bounded read model (counts, recent runs, worker
      summary, failure window).
- [x] `src/Server/Components/Pages/Home.razor`: replace the empty shell with the real dashboard,
      keeping the existing layout, heading and `NoOrganizationNotice` behaviour.
- [x] Shared bits: status badge and duration formatting used by both the dashboard and the runs list,
      so the same run does not read differently on two pages.
- [x] Tests (`Category=Dashboard`): counts vs SQL, ordering and cap, live elapsed for a running run,
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

Result: PASS (`DashboardCountsTests.The_numbers_are_the_rows`). Seeded A with a second project plus
`Queued`/`Running`/`Success`/`Failed`/`Lost`/`Canceled` runs and an online worker; every tile was
compared with direct SQL (`DashboardSql` raw COUNT queries over `BuildRuns`/`Workers`/`Projects`)
before asserting the exact numbers (queued 1, running 1, busy 1, online 1, offline 1, failures 2 with
the `Lost` run's `worker-offline` reason present and the `Canceled` run only in the recent list).

### TC-002: A running run keeps moving

Steps:
1. Open the dashboard with a run in `Running` whose `StartedAt` is two minutes old; wait a few seconds.

Expected:
- Elapsed time is shown and increases; the row does not read as finished or blank.

Result: PASS (`DashboardElapsedPageTests.A_running_run_shows_elapsed_time_that_increases_between_renders`,
plus the unit-level `DashboardDisplayFormatTests.Duration_is_prose_and_elapsed_stays_live`). The page
rendered `2m 05s (running)` for a two-minute-old run, and a second render two seconds later showed a
strictly larger elapsed. In an interactive circuit a `PeriodicTimer` re-renders the already-read
summary once per second (no extra database commands) so the column keeps moving on screen.

### TC-003: The cost does not grow with history

Steps:
1. Seed 200 runs, open the dashboard with a command interceptor counting executions.

Expected:
- The command count stays at or below the documented ceiling and does not scale with the 200 rows;
  the page shows exactly 20 recent runs.

Result: PASS (`DashboardQueryCeilingTests`, `DbCommandInterceptor` counting `CommandCreated` on the
contexts). With 201 runs the read model issued exactly 4 commands (the documented ceiling, also
asserted as the same fixed 4 for an organization with 1 run), and the full page render issued 13
commands against the documented ceiling of 16; the HTML contained exactly 20 recent-run rows.

## Development Log

### 2026-11-13

- Planned from roadmap v0.9 after the v0.3 milestone was accepted. The page reads rows that already
  exist; no schema change is expected, and no charting is in scope.

### 2026-11-14

- Implemented `DashboardQuery` as one scoped read model in at most four commands: (1) a `GroupBy(1)`
  worker status aggregate, (2) a slim run scan (`Id`, `Status`, `WorkerId`, `StartedAt`, `EndedAt`,
  `CreatedAt`), (3) a detail query for exactly the 20 recent ids joined to the project name, (4) the
  project count for the "no projects yet" empty state. Queued/running/busy counts, the 7-day failure
  count and the recency cap are all derived from the scan in memory.
- **Key decision — recency is ordered in memory, on purpose.** The SQLite provider refuses
  `DateTimeOffset` in ORDER BY and comparison (verified: `NotSupportedException` / not translatable on
  EF Core 10.0.15 — the same constraint the claim endpoint and lease sweeper already document), and
  the model must stay provider-neutral. So a bounded command count comes from one slim scan; only the
  scan's row count follows history, which is why its projection is minimal. Conditional aggregates and
  `COUNT(DISTINCT)` inside one `GroupBy(1)` query were verified to translate cleanly on SQLite.
- Busy is defined as the distinct worker ids behind the organization's `Running` runs (a subset of
  Online in steady state, since F-010 marks a lost worker's runs `Lost` when it flips them Offline).
  Offline/online are read straight from `Worker.Status`; no heartbeat arithmetic exists on the page.
- Shared display bits landed as `RunDisplay` (badge variant map + duration/elapsed formatting) and a
  `RunStatusBadge` component; the dashboard, the runs list and the run detail page now render one run
  through the same bits. The runs list gained the same duration-or-elapsed column the dashboard shows.
- Live elapsed: the page computes elapsed from the server clock at render time; inside an interactive
  circuit a `PeriodicTimer` re-renders every second while `RunningCount > 0` (pure re-render of the
  already-read summary — zero extra database commands; the timer is cancelled and awaited in
  `DisposeAsync`).
- Empty states are three distinct facts: no projects (`BbEmpty` + "New project" button), projects but
  no runs, and no workers connected; `ProjectCount` is read precisely to separate the first two.
- Test-seeding surprise: `ZenkinsDbContext.SaveChangesAsync` is the single owner of audit stamps and
  overwrites `CreatedAt` on every insert (F-003's documented rule), so tests that need a specific
  queue time pin it after the insert with a primary-key `UPDATE` (`DashboardSeed.SetRunCreatedAtAsync`)
  — no product code was bent for tests.
- The query-ceiling tests count commands with a `DbCommandInterceptor` attached to the context options
  in the test host (page level) and to a hand-built `ZenkinsDbContextFactory` (read-model level).
  EF Core 10's interceptor surface changed shape (`CommandCreated(CommandEndEventData, DbCommand)`,
  no async variant) — noted here because it surprised the first draft.
- Also exercised along the way: the offline monitor sweeps once at host startup (before its first
  timer tick), which the liveness test accounts for by seeding its stale worker after sign-in.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| D-002-B1 | Low | The runs list (`ProjectRuns.razor`) mapped run statuses with a local `RunBadgeVariant` that had no `Canceled`/`Lost` cases, while the run page mapped `Lost` to Destructive — the same run could render Outline on one page and Destructive on another (exactly the drift this card forbids). | Fixed | Both pages (and the dashboard) now render `RunStatusBadge`, whose variant map has an explicit case for every `BuildRunStatus` and is pinned by `DashboardDisplayFormatTests`. |
| D-002-B2 | P2 | Fast-suite flake observed once during review (896 total / 1 failed on a single run; 0 failed on six other runs; all Dashboard tests green in every run; the failing test's identity was not captured). | Deferred | Track on the next cards' full-suite runs and hunt it if it reappears; no reproducible signature exists to fix today. |
| D-002-B3 | P2 | The interactive 1 s elapsed-timer loop is verified by code reading only — the page tests exercise static SSR, where the timer never starts. | Deferred | A bUnit-style circuit test would close this; none exists in the repo yet, so introducing one is a separate concern. |
| D-002-B4 | P2 | The read-model recency scan reads O(history) rows (slim projection, in-memory cap) because SQLite cannot order `DateTimeOffset` in SQL. | Deferred | Documented trade-off of the bounded-command design; the lever, if history grows, is a SQL-side pre-filter — which needs a schema change or a provider-specific query. |
| D-002-B5 | P2 | The elapsed re-render clock only starts when a run was already running at first render and never stops while the page is open (no run-finished check, no idle pause). | Deferred | Consistent with the snapshot-page behaviour and harmless (a pure re-render of already-read data, no queries); noted for a future interactive-refresh card. |

## Verification

- Related files: `src/Server/Dashboard/DashboardQuery.cs`,
  `src/Server/Dashboard/DashboardServiceCollectionExtensions.cs`, `src/Server/Program.cs` (registers
  `AddZenkinsDashboard`), `src/Server/Components/Pages/Home.razor`,
  `src/Server/Components/Shared/RunDisplay.cs`, `src/Server/Components/Shared/RunStatusBadge.razor`,
  `src/Server/Components/Pages/Runs/ProjectRuns.razor` and `RunDetail.razor` (switched to the shared
  bits; the runs list also gained the duration column), `src/Server/Components/_Imports.razor`,
  `tests/Zenkins.Tests/Dashboard/DashboardTestHost.cs`, `DashboardQueryTests.cs`, `DashboardPageTests.cs`.
- How to run/verify: `dotnet build -c Release`;
  `dotnet test -c Release --filter "Category=Dashboard"`; full suite
  `dotnet test -c Release --filter "Category!=Milestone"`; milestone gate
  `dotnet test -c Release --filter "Category=Milestone"`. PostgreSQL contract gate not required (no
  model change — verified: no migration added, `has-pending-model-changes` unaffected by a new service).
- Results:
  - `dotnet build -c Release` (full solution, `Zenkins.slnx`): 0 warnings, 0 errors.
  - `dotnet test -c Release --filter "Category=Dashboard"`: 13 passed, 0 failed, 0 skipped.
  - `dotnet test -c Release --filter "Category!=Milestone"`: 896 passed, 0 failed, 0 skipped.
  - `dotnet test -c Release --filter "Category=Milestone"`: 6 passed, 0 failed, 0 skipped (33 s, real
    worker processes; nothing in the run path changed, as expected).
