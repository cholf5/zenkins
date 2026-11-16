---
id: B-003
title: Dashboard ceiling tests share one fixture and both seed 200 runs — the count assertion sees 401
type: bug
priority: P2
size: S
status: done
created: 2026-11-15
updated: 2026-11-16
blocked: none
---

## Goal

`DashboardQueryCeilingTests` passes again: each of its two tests observes a
database state that matches its own seeding only, instead of the second test
counting the first test's 200 leftover runs.

## Background

Found on 2026-11-15 while verifying D-007's full-suite run (three consecutive
runs, each failing exactly one test). Reproduced on a stashed clean tree at
d8b7b96 with `--filter Category=Dashboard` (1/13 failed), so the failure is
pre-existing on main and unrelated to D-007.

Mechanism: `DashboardQueryTests.cs` — both
`The_read_model_command_count_is_fixed_regardless_of_history_length`
(:318) and `The_page_command_count_stays_under_the_ceiling_and_shows_exactly_twenty_runs`
(:353) run against the same class fixture (`IClassFixture<DashboardPageFixture>`,
one `TwoOrgsFixture` per class), and each seeds 200 `Success` runs into the
same `TwoOrgsFixture.OrgAId` / `ProjectAId` with no cleanup. Whichever test
runs second sees 200 extra runs; the read-model test's assertion at :337
(exact total `201` across Success+Queued) then reports **Expected 201,
Actual 401**. The failure surfaced consistently because the page test sorts
before the read-model test.

Symptom in full-suite output: `Assert.Equal() Failure: Values differ —
Expected: 201, Actual: 401` at `Dashboard/DashboardQueryTests.cs:337`.

## Acceptance Criteria

- [x] The Dashboard category (`--filter Category=Dashboard`) passes 13/13.
- [x] The fix restores per-test isolation (per-test fixture, cleanup between
      tests, or seed scoping) rather than relaxing the 201 assertion.
- [x] The full suite no longer fails on this test.

## Subtasks

- [x] Pick the isolation shape (the repo's other class fixtures seed through
      `TwoOrgsFixture` too — check how they avoid the same collision).
- [x] Implement and verify 13/13 plus a full-suite run.

## Resolution

`DashboardQueryCeilingTests` dropped `IClassFixture<DashboardPageFixture>` for a per-instance
`private readonly DashboardPageFixture _host = new()` plus `IDisposable`; xunit v3 builds a fresh
test-class instance per fact, so each fact gets its own temporary SQLite database and host and the
200 seeded runs no longer accumulate across the two facts. The `201` assertion is untouched.

The card's second concern — how the other Dashboard classes avoid this — has a narrow answer:
they hold a single fact each, so a class fixture cannot collide there. Only this class had two
facts seeding 200 runs apiece, so only this class needed per-test isolation.

## Verification

- `dotnet test --filter Category=Dashboard` → 13 passed / 0 failed / 0 skipped (implementer and
  reviewer, independently).
- `dotnet test` (full suite) → 1129 passed / 0 failed / 0 skipped, three consecutive runs.
- Reviewer confirmed the diff carries no change inside the assertion block: the only hunks are the
  class declaration, the fixture field, `Dispose`, and the mechanism comment.

## Dependencies

- none

## Test Cases

### TC-001: Dashboard category green

Steps:
1. `dotnet test --filter Category=Dashboard`.

Expected:
- 13/13 passed.

Result: PASS — 13/13 (2026-11-16).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the record of the failure. | Open | — |
