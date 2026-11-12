---
id: Q-001
title: Tenant isolation test harness (two-org fixture)
type: test
priority: P1
size: S
status: done
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

A reusable test fixture that stands up two Organizations and proves — automatically, on every run — that no query, endpoint, or file path can cross between them.

## Background

PRD §10 scenario five is an acceptance gate for v1.0, but the isolation mechanism itself ships in v0.1 (`I-002`). Tests written at the same time as the mechanism are the only realistic way it stays true through Phase 2–5. Build the fixture now; every later card extends the same list of probes.

Fixture shape (implemented in `tests/Zenkins.Tests/Tenancy/TwoOrgsFixture.cs`):

```text
TwoOrgsFixture                (one temp SQLite file per instance, migrated with the real migration set)
  OrgA a0000000-…-000000000001 "Alpha"  (owner: alice) (member: bob) + Project A a0000000-…-011 "alpha-app"
  OrgB b0000000-…-000000000002 "Beta"   (owner: carol)              + Project B b0000000-…-012 "beta-app"
  - ids exposed as static readonly Guid / const string fields so tests read as prose
  - UseOrganizationA() / UseOrganizationB() / UseOrganization(id) re-point the tenant mid-test
  - Seed() is the single routine that creates all of it, inside an ISystemDataScope
```

## Acceptance Criteria

- [x] `TwoOrgsFixture` creates both tenants from a single seed routine, usable by any test class.
  - `IClassFixture<TwoOrgsFixture>` for read-mostly probe classes; a `new TwoOrgsFixture()` per test where a test mutates rows. One temp SQLite file per instance, deleted on dispose (pools cleared first).
- [x] Probe: unfiltered query returns only the active tenant's rows, for A then for B.
- [x] Probe: write carrying the other tenant's id is rejected (and nothing persists, in the active *and* the foreign tenant).
- [~] Probe: a request authenticated as A's user, with B's ids substituted into route/query/form, fails authorization — never returns B's data.
  **Data-layer half done** here (route/query/form id swaps return 404, never B's row). The **authorization half** (a caller must not be able to *choose* tenant B) is deferred: the test host trusts `X-Test-Organization`, which F-001/F-002 replace with membership-derived tenant resolution. F-003 must add the authenticated probe.
  - No auth pipeline exists yet (F-001/F-003), so a **test-only** middleware selects the tenant from the `X-Test-Organization` header. The request itself is real HTTP over the app's pipeline (`WebApplicationFactory<Program>`, TestServer) and the id-swap is real (B's id in the route, the query string and the form body). The probe returns 404 with no B data; the positive control returns A's own row. The auth-dependent half (a user may only activate an organization they belong to) lands with F-003 — see Handoffs. Not skipped.
- [x] Probing helper functions are named so a new entity only needs one line to join the harness.
  (`TenantIsolationProbes.AssertActiveTenantSeesOnlyOwnRowsAsync<TEntity>(fixture)`, `…AssertCrossTenantWriteRejectedAsync<TEntity>(fixture, factory)`, `…AssertFindAfterTenantSwitchReturnsNullAsync<TEntity>(fixture, idOfA, idOfB)`; plus one registry line in `CoveredEntityTypes`, enforced by `Every_tenant_owned_entity_is_covered_by_the_harness`.)
- [x] Harness runs as part of the default `dotnet test`.

## Subtasks

- [x] Add `xunit` v3 + `Microsoft.AspNetCore.Mvc.Testing` (or `WebApplicationFactory`) where needed.
  - xunit v3 was already present; added `Microsoft.AspNetCore.Mvc.Testing` **10.0.13** centrally pinned in `Directory.Packages.props` (versionless reference in `tests/Zenkins.Tests/Zenkins.Tests.csproj`).
- [x] Build the seed + fixture.
- [x] Implement the three probes as reusable helpers.
- [x] Add the id-swap probe over a real HTTP request once an endpoint exists (may be marked skipped until `F-003`).
  - Implemented now, not skipped: the endpoint is mapped **inside the test factory** (`/__probe/projects/{id}`), so the card lands without waiting for F-003.

## Dependencies

- I-002

## Test Cases

### TC-001: Harness is not vacuous

Steps:
1. Temporarily remove the global query filter on `Project` (`ZenkinsDbContext.OnModelCreating`).
2. Run the isolation tests.

Expected:
- Tests fail. (A green harness that cannot go red proves nothing.)

Result: **PASS (red)** — with the `Project` filter removed, `dotnet test --filter Category=TenantIsolation`
reports **18 failed / 14 succeeded / 0 skipped** out of 32. Restoring the filter gives **32 passed / 0 failed / 0 skipped**.
Assertion lines are pasted under Verification.

### TC-002: Harness passes on the real implementation

Steps:
1. `dotnet test --filter Category=TenantIsolation`

Expected:
- All probes green.

Result: **PASS** — 32 passed / 0 failed / 0 skipped (13 I-002 isolation tests + 8 data-layer probes + 11 HTTP id-swap probe cases).

## Development Log

### 2026-11-12

Card created. Not started.

### 2026-11-12 — implemented

**Fixture (`Tenancy/TwoOrgsFixture.cs`).** One public type, one `Seed()` routine, one temp SQLite file
per instance (`%TEMP%/zenkins-q001-<guid:N>.db`, migrated with the product's real migration set, never
`App_Data`). Seed: OrgA "Alpha" with `alice` (Owner) and `bob` (Member) and project `alpha-app`; OrgB
"Beta" with `carol` (Owner) and project `beta-app`. Ids are fixed literals (`OrgAId`, `OrgBId`,
`ProjectAId`, `ProjectBId`, three membership ids) so tests and URLs read as prose; names/users are
`const string`s. `UseOrganizationA()/UseOrganizationB()/UseOrganization(id)` re-point the tenant on the
fixture's scoped `TenantContext` mid-test; `ClearOrganizationSelection()` stands for an anonymous
request. `CreateContext()` returns a migrated context on the fixture file; `CreateContext(ITenantContext)`
returns one bound to a caller's tenant context (the F-002 decorator shape);
`ReadAcrossTenantsAsync<TEntity>` is the only cross-tenant read and goes through
`SystemQueryAsync` inside a real `ISystemDataScope`, so the harness's expectations never trust the
filtered view it is testing. Pool clearing before deletion keeps Windows from locking the file.

**Probes (`Tenancy/TenantIsolationProbes.cs`).** Generic over `ITenantOwned`, so a new entity joins
with one line:
- `AssertActiveTenantSeesOnlyOwnRowsAsync<TEntity>(fixture)` — derives the expected count per
  organization from the system read, then asserts, for A and then B, that a **`Where`-less** query
  returns exactly that many rows and every row carries the active `OrganizationId`. Deriving the
  expectation from reality is deliberate: a hard-coded "1" would keep passing if the seed changed,
  and a zero-row entity fails loudly (an empty probe proves nothing).
- `AssertCrossTenantWriteRejectedAsync<TEntity>(fixture, createRow)` — both directions (A→B, B→A):
  insert naming the foreign organization throws `TenantIsolationException`, and the row count is
  unchanged in the active tenant's filtered view *and* in the unfiltered system read of both tenants.
- `AssertFindAfterTenantSwitchReturnsNullAsync<TEntity>(fixture, idOfA, idOfB)` — I-002's tracker
  guarantee: a row tracked under A is not returned by `Find`/`FindAsync` after switching to B, in
  either direction, on the same context instance.
- `CoveredEntityTypes` + the `Every_tenant_owned_entity_is_covered_by_the_harness` reflection guard:
  a later card that adds an `ITenantOwned` entity without a **registry line** fails instead of silently widening
  the isolation surface.

**Existing I-002 tests refactored onto the fixture.** `TenantIsolationTests` keeps all 13 tests and
assertions, but its local `SeedTwoOrganizations`, temp-file path, tenant context, system scope and
configuration are gone; it now constructs a fixture per test (these tests mutate rows, so a class
fixture would make them order-dependent) and uses `UseOrganizationA/B`. One assertion necessarily
changed: `Duplicate_membership_is_rejected_by_the_unique_index_sqlite` used to assert exactly one
surviving membership, and the fixture now seeds `bob` as well, so it asserts exactly one surviving
**alice** row with `Role = Owner` (duplicate rejected, first row untouched) — same intent, adjusted to
the card's fixture shape. Everything else is assertion-for-assertion identical.

**HTTP id-swap probe (`Tenancy/TenantProbeApplicationFactory.cs`, `Tenancy/TenantIsolationHttpProbeTests.cs`).**
`WebApplicationFactory<Program>` starts the real app; `ConfigureTestServices` replaces the
`ZenkinsDataOptions` singleton with the fixture's temp SQLite connection string (so the app never
touches `App_Data`) and registers an `IStartupFilter` that adds the test-only probe middleware in front
of the app's pipeline. The middleware (a) selects the tenant from the `X-Test-Organization` header,
standing in for F-001's auth middleware, and (b) answers `/__probe/projects/{id}` for the route, query
(`?id=`) and form (`POST`, `id=`) shapes by looking the project up through a request-scoped
`IDbContextFactory<ZenkinsDbContext>` — i.e. the same filtered context a real endpoint uses. A foreign
id is therefore indistinguishable from a nonexistent one (404, empty body); A's own id returns A's row
(200, JSON). A malformed id is a 400 rather than a silent "list everything".

**Why a middleware and not `MapGet`.** The app is already built (and `app.Run()` executed) when a
`WebApplicationFactory` can hook in, so there is no `IEndpointRouteBuilder` to map onto without adding
a production hook — the card allows exactly one production line. The `IStartupFilter` probe still
exercises the whole host: request features, the request-scoped DI container, the real `ZenkinsDbContext`
with its global query filters, JSON serialization and status codes. It is deliberately namespaced
`/__probe/*` and reachable only from the test factory.

**Production change.** One line at the end of `src/Server/Program.cs`: `public partial class Program { }`
(WebApplicationFactory needs a public entry-point type). Nothing else in `src/Server` changed.

**Counts.** Total suite: 15 → **34** tests (32 of them `Category=TenantIsolation`); 0 skipped.

Handoffs:

- **F-002** replaces the test-only tenant selection with the organization switcher. The probe's
  `X-Test-Organization` header must disappear with it.
- **F-003** replaces `/__probe/projects/{id}` with the real authenticated project endpoint. The
  auth-dependent half of the HTTP probe — a signed-in user must only be able to activate an
  organization they are a member of, and an id swap must therefore fail authorization rather than
  merely "not be found" — lands with F-003 (it needs the real principal). The data-layer half is
  covered now and must stay green when the endpoint is swapped: reuse
  `TenantIsolationHttpProbeTests` by pointing the request shapes at the real route.
- Any card adding an `ITenantOwned` entity: add it to `TenantIsolationProbes.CoveredEntityTypes`
  (the reflection guard fails until you do) plus one probe line per helper.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/Zenkins.Tests/Tenancy/**` (`TwoOrgsFixture`, `TenantIsolationProbes`,
  `TenantIsolationProbeTests`, `TenantProbeApplicationFactory`, `TenantIsolationHttpProbeTests`,
  refactored `TenantIsolationTests`), `tests/Zenkins.Tests/Zenkins.Tests.csproj`,
  `Directory.Packages.props`, `src/Server/Program.cs` (one line).
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test`
  3. `dotnet test --filter Category=TenantIsolation`
  4. TC-001: comment out the `Project` `HasQueryFilter` line in `ZenkinsDbContext.OnModelCreating`,
     run 3, restore the line, run 1–3 again.
- Results (full output in `/tmp/orca-logs/Q-001/`):
  - `dotnet build -c Release` -> **PASS**, 0 warnings / 0 errors -> `build-final.log`
  - `dotnet test` -> **PASS**, **34 passed / 0 failed / 0 skipped** (was 15 before this card) -> `test-all-final.log`
  - `dotnet test --filter Category=TenantIsolation` -> **PASS**, 32 passed / 0 failed / 0 skipped -> `test-tenant-isolation-final.log`
  - TC-001 red proof, `Project` query filter removed -> `dotnet test --filter Category=TenantIsolation`
    **FAIL**, 18 failed / 14 succeeded / 0 skipped -> `red-proof-no-project-filter.log`. Assertion lines:
    - `TenantIsolationProbeTests.Projects_are_invisible_across_tenants`:
      `Assert.Equal() Failure: Values differ` / `Expected: 1` / `Actual:   2`
    - `TenantIsolationProbeTests.Find_after_a_tenant_switch_returns_null_for_projects`:
      `Assert.Null() Failure: Value is not null` / `Expected: null` /
      `Actual:   Project { … DefaultBranch = "develop", … Id = b0000000-0000-4000-8000-000000000012, Name = "beta-app", … }`
    - `TenantIsolationHttpProbeTests.Request_as_organization_a_with_organization_bs_project_id_returns_not_found(shape: "route")`
      (and the `query` / `form` cases, plus the B→A direction):
      `Assert.Equal() Failure: Values differ` / `Expected: NotFound` / `Actual:   OK`
  - TC-001 restored (line restored, `git diff src/Server/Data/ZenkinsDbContext.cs` empty) -> the three
    runs above are the restored/final evidence: `build-final.log` 0/0, `test-all-final.log` 34/0/0,
    `test-tenant-isolation-final.log` 32/0/0.
  - No test in the suite is `[Skip]` / `Skip =`; total skipped is 0 in every run above.

## Known gaps (from the review, deliberately not fixed here)

- **The HTTP probe is a terminal middleware, not an endpoint.** It short-circuits before endpoint
  routing, `UseAntiforgery`, the status-page gate and any future auth middleware. What it proves is the
  host + request-scoped DI + `IDbContextFactory` + query-filter + JSON/status path — not middleware
  ordering. F-003 must add the authenticated probe against a real endpoint.
- **The query/write probes are per-entity by design.** Update/delete guards stay in
  `TenantIsolationTests`; a new entity joining the harness gets insert-only coverage until it adds its
  own update/delete probe.
- **Registry membership is not coverage.** `CoveredEntityTypes` is enforced by a reflection guard, but
  adding a type to the registry without calling a probe is still green. Driving the probes from the
  registry via `[Theory][MemberData]` would make one imply the other.
- **Cheap test strengthening left undone:** negative HTTP cases should assert
  `Content-Length == 0` (the 404 body is empty by construction, so `DoesNotContain` cannot fail
  independently); the positive control should assert the returned `id`; `AssertFindAfterTenantSwitch...`
  should be constrained `where TEntity : ITenantOwned`; the read probe should require rows in both orgs
  instead of silently narrowing to one direction; the duplicate-membership test should assert all three
  seeded memberships survive the failed insert.
