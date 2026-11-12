---
id: Q-001
title: Tenant isolation test harness (two-org fixture)
type: test
priority: P1
size: S
status: todo
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

A reusable test fixture that stands up two Organizations and proves — automatically, on every run — that no query, endpoint, or file path can cross between them.

## Background

PRD §10 scenario five is an acceptance gate for v1.0, but the isolation mechanism itself ships in v0.1 (`I-002`). Tests written at the same time as the mechanism are the only realistic way it stays true through Phase 2–5. Build the fixture now; every later card extends the same list of probes.

Fixture shape:

```text
TwoOrgsFixture
  OrgA (owner: alice, member: bob) + Project A
  OrgB (owner: carol)              + Project B
  - Ids exposed as constants so tests read as prose
  - TenantContext can be re-pointed between A and B mid-test
```

## Acceptance Criteria

- [ ] `TwoOrgsFixture` creates both tenants from a single seed routine, usable by any test class.
- [ ] Probe: unfiltered query returns only the active tenant's rows, for A then for B.
- [ ] Probe: write carrying the other tenant's id is rejected.
- [ ] Probe: a request authenticated as A's user, with B's ids substituted into route/query/form, fails authorization — never returns B's data.
- [ ] Probing helper functions are named so a new entity only needs one line to join the harness.
- [ ] Harness runs as part of the default `dotnet test`.

## Subtasks

- [ ] Add `xunit` v3 + `Microsoft.AspNetCore.Mvc.Testing` (or `WebApplicationFactory`) where needed.
- [ ] Build the seed + fixture.
- [ ] Implement the three probes as reusable helpers.
- [ ] Add the id-swap probe over a real HTTP request once an endpoint exists (may be marked skipped until `F-003`).

## Dependencies

- I-002

## Test Cases

### TC-001: Harness is not vacuous

Steps:
1. Temporarily remove the global query filter.
2. Run the isolation tests.

Expected:
- Tests fail. (A green harness that cannot go red proves nothing.)

### TC-002: Harness passes on the real implementation

Steps:
1. `dotnet test --filter Category=TenantIsolation`

Expected:
- All probes green.

## Development Log

### 2026-11-12

- Card created. Not started.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/Zenkins.Tests/Tenancy/**`
- How to run/verify: `dotnet test --filter Category=TenantIsolation`
- Results: not run yet
