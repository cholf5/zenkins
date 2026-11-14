---
id: B-001
title: Dashboard org-switch flake — the seeded Running run is swept Lost before the page reads it (D-002-B2)
type: bug
priority: P1
size: S
status: done
created: 2026-11-14
updated: 2026-11-15
blocked: none
---

## Goal

`DashboardOrganizationSwitchPageTests` stops being load-dependent: the organization-switch test
passes deterministically regardless of what else the machine is doing, so it never poisons a
`Category!=Milestone` run or the CI this repo is about to gain (P-001).

## Background

D-002-B2 was deferred as a flake without a reproducible signature. F-018's review runs captured the
identity and a concrete hypothesis: the test's host sets `OfflineAfterSeconds=1` while the offline
monitor runs on a one-hour interval — and the monitor also sweeps **once at host startup, before its
first timer tick** (recorded in D-002's development log). A seeded `Running` run whose worker's
heartbeat is older than one second by the time the page reads it gets its worker flipped Offline and
the run swept `Lost`, so the org-switch assertions compare against numbers that changed underneath
them. The product rule is correct (honest sweeping, F-010); the test's compressed configuration is
incoherent: a 1-second offline threshold is shorter than the seed-to-read window it must survive.

Direction (decision to make on the card, test-side only): make the host's sweep configuration
coherent with the seeding — an offline threshold that comfortably exceeds the seed-to-read window
while keeping the sweep real, or re-stamp the worker's heartbeat inside the window right before the
read (the Q-004 way: compress time, never fake the mechanism). No product change is expected; if one
turns out to be needed, this card must say why before it is made.

## Acceptance Criteria

- [x] The org-switch test passes a recorded streak of repeated runs under load (the card records how
      many consecutive runs and in what configuration) with no `Lost`-related assertion failure.
- [x] The fix lives in the test host's configuration or seeding, not in the sweeper; the offline
      sweep and the `Lost` path stay exercised where they belong (Recovery suite still green).
- [x] The org-switch assertions themselves are unchanged — the test still proves that switching
      organization changes every figure on the page, both directions.
- [x] D-002's bug table (D-002-B2) and this card reference each other, and the deferral note is
      retired.

## Subtasks

- [x] Pin the failure shape first: loop the test under load before fixing, so the fix addresses the
      observed mechanism and not a guess.
- [x] Fix the test host's sweep/offline configuration or the seeding order per the decision above.
- [x] Loop-verify: repeated filtered runs green; fast suite green.

## Dependencies

- none (blocks P-001: a known flake would make CI red on arrival)

## Test Cases

### TC-001: The streak

Steps:
1. Loop `dotnet test -c Release --filter "FullyQualifiedName~DashboardOrganizationSwitch"` (and at
   least one full fast-suite run) N times with parallel machine load, as before the fix.

Expected:
- All N runs green; the failure mode seen in F-018's review rounds does not recur.

Result: PASS (observed 2026-11-14). Load: 10 background `yes` processes saturating all 8 cores
(observed load averages 41–159, 0% idle). Configuration: one `dotnet build -c Release`, then
`dotnet test -c Release --no-build` with the filter above. **Post-fix streak: 12 consecutive filtered
runs green**, plus 3 consecutive green `Category=Dashboard` runs and 1 full fast-suite run
(1048 tests) under the same load. Pre-fix, the same harness could not reproduce the flake — recorded
honestly per the pin-first subtask: 15 consecutive filtered runs, 10 `Category=Dashboard` runs and 1
full fast-suite run all green under load (4m 43s under load vs ~1m 43s unloaded, so the load was
effective); the fix therefore addresses the recorded mechanism (verified by reading the code, see
Development Log), not a local repro.

### TC-002: The mechanism still exists where it belongs

Steps:
1. Run `Category=Recovery` and `Category=Dashboard` after the change.

Expected:
- Recovery still proves sweep → `Lost` with its own compressed configuration; every other Dashboard
  test is unchanged and green.

Result: PASS (observed 2026-11-14, after the change). `dotnet test -c Release --no-build --filter
"Category=Recovery"` → 4 passed / 0 failed / 0 skipped (28.0 s) — Recovery's own compressed
configuration (5 s threshold, 1 s ticks, real processes) is untouched and still drives sweep →
`Lost`. `Category=Dashboard` → 13 passed / 0 failed / 0 skipped (6.7 s), including
`DashboardWorkerLivenessTests`, which still drives the real `WorkerOfflineMonitor.SweepOnceAsync`
(now against a ten-minute threshold, with its seeded heartbeat half an hour stale). Full fast suite
(`Category!=Milestone&Category!=Recovery`) → 1048 passed / 0 failed / 0 skipped (2m 47s).

## Development Log

### 2026-11-14

- Card opened from the user's post-Q-007 ledger: the flake was captured with a concrete hypothesis
  during F-018's review runs (quoted in Background) and the user prioritized fixing it before CI
  exists (P-001 depends on it). Next step: reproduce, then fix the test host configuration.
- **Pinned first, honestly: the flake does not reproduce on this machine.** Before any change, under
  a 10-process CPU hog saturating all 8 cores (load averages up to ~54, 0% idle): 15 consecutive
  filtered `DashboardOrganizationSwitch` runs green, 10 consecutive `Category=Dashboard` runs green,
  and one full fast-suite run green (4m 43s under load vs ~1m 43s unloaded — the same 896-test shape
  F-018's review captured the failure in). The recorded mechanism was therefore verified by reading
  the code, and it holds: `WorkerOfflineMonitor.ExecuteAsync` sweeps once at startup before its
  first (parked, hourly) timer tick, and `RunService.SweepOfflineWorkersAsync` flips every `Online`
  worker whose heartbeat is older than `Workers:OfflineAfterSeconds` (null heartbeats are skipped).
  The org-switch test seeds `switch-agent` Online with its heartbeat stamped at seed time, **before**
  sign-in boots the host — so whenever the seed-to-boot window exceeds the 1 s threshold, the boot
  sweep flips the worker Offline and sweeps its `Running` run `Lost`, and the alpha figures move
  underneath the assertions (running 1→0, busy 1→0, online 1→0, offline 1→2, failures 1→2). The
  fixture's own rows are immune at any threshold (OrgA's worker is already Offline, OrgB's is Online
  with no heartbeat), so `switch-agent` is the only flippable row — exactly the recorded shape.
  `DashboardElapsedPageTests` shares the same latent shape (its seeded run feeds the "still running"
  cell).
- **Decision: option (a) — make the host's sweep configuration coherent.** `DashboardPageFixture`
  sets `Workers:OfflineAfterSeconds` 1 → 600: the threshold must comfortably exceed the seconds-wide
  seed-to-read window, and ten minutes leaves it orders of magnitude of margin, so no plausible
  machine load bridges it. The sweep stays real — the monitor still runs and sweeps at startup, and
  the liveness test still drives `SweepOnceAsync`; only its seeded staleness moved (-30 s → -30 min,
  seeding only) so it sits far past the new threshold. Option (b) — re-stamping the heartbeat before
  the read — was rejected because the sweep that races the seed fires during host boot inside
  sign-in: a re-stamp landing after that boot sweep cannot undo an already-applied Offline/`Lost`
  flip, so it would still be timing-dependent. The coherent threshold instead removes the race for
  every page test at once, assertions untouched. No product code change was needed (the product rule
  is correct and honest; F-010/Recovery keep exercising it). Recovery untouched (its 5 s threshold
  and 1 s ticks drive real processes and remain the sweep → `Lost` proof).
- Verification, all observed on the reference machine (macOS arm64, SDK 10.0.403): post-fix streak
  **12/12** filtered runs green under the same saturation, plus 3/3 `Category=Dashboard` runs and
  1/1 fast suite under load (1048/0/0, load average peaked at ~159 during the run);
  `Category=Recovery` 4/0/0 (28.0 s); `Category=Dashboard` 13/0/0 (6.7 s); fast suite unloaded
  1048/0/0 (2m 47s). The diff is confined to `DashboardTestHost.cs` (threshold + comment) and the
  liveness test's seeded staleness + comment — no assertion changed anywhere, no product file
  touched.

### 2026-11-15

- **Independent review: PASS.** Adversarial checks all hold: (1) the liveness test's
  assertions (`WorkersOfflined==1`, `RunsLost==1`, offline 2 cross-checked against raw SQL, the run
  `Lost`/`WorkerOffline`) are unchanged and would still fail if the sweeper broke — with the 600 s
  threshold a −30 s heartbeat would be skipped (`RunService.SweepOfflineWorkersAsync` continues on
  any beat inside the threshold), so the −30 min seeding is *necessary*, not cosmetic, and the test
  proves the same real-sweeper behavior it proved before; (2) no Dashboard test relied on the 1 s
  threshold — the only Dashboard sweeper test drives `SweepOnceAsync` directly, `DashboardQueryTests`
  seeds `Lost` rows directly, and the compressed-threshold proofs live in Recovery (5 s) and
  RunLifecycle (2 s), untouched; (3) the org-switch test section is byte-identical to `HEAD`
  (verified by diffing the class section, not just the hunks); (4) mechanism re-verified in product
  code: `WorkerOfflineMonitor.ExecuteAsync` sweeps once before its first `PeriodicTimer` tick
  (`MonitorIntervalSeconds=3600` in this fixture), null heartbeats are skipped, and the fixture's own
  rows (alpha-agent Offline, beta-agent Online with a null heartbeat) leave `switch-agent` as the
  only flippable row. Reviewer's own runs (Release, macOS arm64, unloaded machine — a complement to
  the developer's under-load streak, `--no-build` after one clean Release build with 0 warnings):
  `FullyQualifiedName~DashboardOrganizationSwitch` × 3 consecutive green (2/2 tests each — the
  filter matches the page test and the read-model `DashboardOrganizationSwitchTests`; 5.7 s / 4.6 s /
  4.4 s), `Category=Dashboard` 13/0/0 (7.2 s), `Category=Recovery` 4/0/0 (31.8 s), fast suite
  `Category!=Milestone&Category!=Recovery` 1048/0/0 (4m 02s). No defects found; card moved to done
  and the board row updated in the same operation.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for D-002-B2 (P2): the fast-suite flake captured during F-018 review. | Closed | Fixed here (Development Log 2026-11-14): the dashboard test host's offline threshold is now coherent with its seeding (600 s against a seconds-wide seed-to-read window); D-002-B2's row points back at this card. |

## Verification

- Related files: `tests/Zenkins.Tests/Dashboard/DashboardTestHost.cs`
  (`DashboardPageFixture`: `Workers:OfflineAfterSeconds` 1 → 600),
  `tests/Zenkins.Tests/Dashboard/DashboardPageTests.cs` (`DashboardWorkerLivenessTests`: seeded
  staleness -30 s → -30 min, seeding only; no assertion changed),
  `docs/kanban/issues/d-002-dashboard.md` (D-002-B2 row retired to Closed).
- How to run/verify: `dotnet test -c Release --filter "FullyQualifiedName~DashboardOrganizationSwitch"`;
  the full fast suite `dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery"`;
  `Category=Recovery` for the sweep regression; `Category=Dashboard`.
- Results (observed 2026-11-14):
  - Pre-fix pin attempt under 8-core CPU saturation (10 `yes` hogs, load averages up to ~54):
    15/15 filtered runs green, 10/10 `Category=Dashboard` green, 1/1 fast suite green (4m 43s under
    load) — the flake did not reproduce; recorded honestly, fix addresses the code-verified mechanism.
  - Post-fix streak: **12/12 filtered runs green** under the same saturation (Release, one build then
    `--no-build`), plus 3/3 `Category=Dashboard` green under load and 1/1 fast suite under load
    (1048/0/0, load average peaked at ~159).
  - `Category=Recovery`: 4 passed / 0 failed / 0 skipped (28.0 s). `Category=Dashboard`: 13 passed /
    0 failed / 0 skipped (6.7 s). Fast suite unloaded: 1048 passed / 0 failed / 0 skipped (2m 47s).
  - `dotnet build -c Release`: 0 warnings, 0 errors.
