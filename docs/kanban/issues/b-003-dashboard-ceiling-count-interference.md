---
id: B-003
title: Dashboard ceiling tests share one fixture and both seed 200 runs — the count assertion sees 401
type: bug
priority: P2
size: S
status: todo
created: 2026-11-15
updated: 2026-11-15
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

- [ ] The Dashboard category (`--filter Category=Dashboard`) passes 13/13.
- [ ] The fix restores per-test isolation (per-test fixture, cleanup between
      tests, or seed scoping) rather than relaxing the 201 assertion.
- [ ] The full suite no longer fails on this test.

## Subtasks

- [ ] Pick the isolation shape (the repo's other class fixtures seed through
      `TwoOrgsFixture` too — check how they avoid the same collision).
- [ ] Implement and verify 13/13 plus a full-suite run.

## Dependencies

- none

## Test Cases

### TC-001: Dashboard category green

Steps:
1. `dotnet test --filter Category=Dashboard`.

Expected:
- 13/13 passed.

Result: not yet run (card opened by D-007's session; no fix attempted there).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the record of the failure. | Open | — |
