---
id: B-001
title: Dashboard org-switch flake — the seeded Running run is swept Lost before the page reads it (D-002-B2)
type: bug
priority: P1
size: S
status: todo
created: 2026-11-14
updated: 2026-11-14
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

- [ ] The org-switch test passes a recorded streak of repeated runs under load (the card records how
      many consecutive runs and in what configuration) with no `Lost`-related assertion failure.
- [ ] The fix lives in the test host's configuration or seeding, not in the sweeper; the offline
      sweep and the `Lost` path stay exercised where they belong (Recovery suite still green).
- [ ] The org-switch assertions themselves are unchanged — the test still proves that switching
      organization changes every figure on the page, both directions.
- [ ] D-002's bug table (D-002-B2) and this card reference each other, and the deferral note is
      retired.

## Subtasks

- [ ] Pin the failure shape first: loop the test under load before fixing, so the fix addresses the
      observed mechanism and not a guess.
- [ ] Fix the test host's sweep/offline configuration or the seeding order per the decision above.
- [ ] Loop-verify: repeated filtered runs green; fast suite green.

## Dependencies

- none (blocks P-001: a known flake would make CI red on arrival)

## Test Cases

### TC-001: The streak

Steps:
1. Loop `dotnet test -c Release --filter "FullyQualifiedName~DashboardOrganizationSwitch"` (and at
   least one full fast-suite run) N times with parallel machine load, as before the fix.

Expected:
- All N runs green; the failure mode seen in F-018's review rounds does not recur.

### TC-002: The mechanism still exists where it belongs

Steps:
1. Run `Category=Recovery` and `Category=Dashboard` after the change.

Expected:
- Recovery still proves sweep → `Lost` with its own compressed configuration; every other Dashboard
  test is unchanged and green.

## Development Log

### 2026-11-14

- Card opened from the user's post-Q-007 ledger: the flake was captured with a concrete hypothesis
  during F-018's review runs (quoted in Background) and the user prioritized fixing it before CI
  exists (P-001 depends on it). Next step: reproduce, then fix the test host configuration.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for D-002-B2 (P2): the fast-suite flake captured during F-018 review. | Open | Closed here when the acceptance criteria pass. |

## Verification

- Related files: `tests/Zenkins.Tests/Dashboard/DashboardPageTests.cs`
  (`DashboardOrganizationSwitchPageTests`), `tests/Zenkins.Tests/Dashboard/DashboardTestHost.cs`,
  `docs/kanban/issues/d-002-dashboard.md` (D-002-B2 row)
- How to run/verify: `dotnet test -c Release --filter "FullyQualifiedName~DashboardOrganizationSwitch"`;
  the full fast suite; `Category=Recovery` for the sweep regression.
- Results: not run yet (card creation).
