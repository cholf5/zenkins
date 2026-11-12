---
id: I-002
title: EF Core data layer with enforced tenant isolation
type: feature
priority: P1
size: M
status: done
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

A single `ZenkinsDbContext` that can never return another tenant's rows, on SQLite for development and PostgreSQL for production, with the v0.1 entity set migrated and seeded.

## Background

PRD §3.1.3 and §8.8 make isolation a data-access-layer guarantee, not a UI convention. The mechanism must work without every query author remembering to add `Where(x => x.OrganizationId == ...)`.

Entities for this card (v0.1 scope only):

| Entity | Notes |
|---|---|
| `User` | Identity user; not tenant-scoped |
| `Organization` | the tenant |
| `OrganizationMember` | (OrganizationId, UserId, Role: Owner\|Member) |
| `Project` | tenant-scoped; repo URL, default branch, pipeline path, credential ref |

`Project.Credentials` / `Secret` / `Worker` / `BuildRun` arrive in later cards — do not stub them.

Isolation design:

- `ITenantContext` (scoped) exposing the active `OrganizationId`, resolved from the authenticated user's *selected* org — never from a route/query parameter alone.
- Global query filter on every `ITenantOwned` entity, injected from `ITenantContext`.
- `SaveChanges` override: stamp and reject writes whose `OrganizationId` does not match the active tenant.
- Explicit escape hatch for system-level operations (scheduler, migrations) that is marked `#pragma`-free and requires a distinct service type, so it cannot be reached by accident.

## Acceptance Criteria

- [x] `dotnet ef migrations add Initial` produces a migration; `dotnet ef database update` applies it to SQLite.
- [x] The migration set is provider-agnostic: one `Initial` migration (generated against Npgsql) applies to SQLite and produces the expected PostgreSQL schema in the idempotent script (`uuid`, `timestamp with time zone`, `character varying`).
- [~] The same migration applies to a **live** PostgreSQL instance (provider selected by connection string / config).
  **Deferred to first deployment** (not open work: the migration set and its Npgsql script are verified).
  - Not verified: no PostgreSQL server, client or Docker exists in this environment (PRD §8.9 rules out Docker as an infrastructure dependency). Proof once one exists: `Database__Provider=Postgres ConnectionStrings__Default="Host=...;Database=...;Username=...;Password=..." dotnet ef database update --project src/Server`.
  - **Reviewer decision:** this AC is deferred to first deployment; merge of I-002 is accepted without a follow-up card, on the strength of the idempotent-script schema parity.
- [x] Every tenant-scoped entity has a global query filter; a query with no `Where` clause returns only the active tenant's rows.
- [x] `SaveChanges` throws when a tenant-scoped entity carries a foreign `OrganizationId`.
- [x] `OrganizationMember` enforces a unique `(OrganizationId, UserId)` index.
- [x] The context is registered scoped (per HTTP request / per operation, never per process) and resolves the tenant through the scoped `ITenantContext`. Interactive Blazor circuits are long-lived, so UI code must not inject the context; it uses the scoped `IDbContextFactory<ZenkinsDbContext>` and creates one context per operation.

## Subtasks

- [x] Add `Microsoft.EntityFrameworkCore`, `.Sqlite`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`.
  - Pinned centrally at EF Core **10.0.15**; 10.0.2 was rejected because it drags in `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which has a high-severity advisory and fails the build via `NU1903` under `TreatWarningsAsErrors`.
- [x] Define `Organization`, `OrganizationMember`, `Project` (+ enums) per PRD §6.
  - `User` is intentionally **not** modeled (see Development Log); `OrganizationMember.UserId` is a plain string column without a navigation/foreign key.
- [x] Implement `ITenantOwned`, `ITenantContext`, filter injection, and the `SaveChanges` guard.
- [x] Provider selection in `Program.cs` from configuration.
- [ ] Create the initial migration; verify against SQLite and PostgreSQL.
  - Migration created and applied to SQLite; PostgreSQL verified by schema parity of the idempotent Npgsql script only. Regenerated after the `CK_OrganizationMembers_Role` check constraint was added.
- [x] Wire a design-time `IDbContextFactory` for `dotnet ef`.
  - Implemented as `ZenkinsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ZenkinsDbContext>` (the hook `dotnet ef` actually uses). It loads `appsettings.json`, `appsettings.{ASPNETCORE_ENVIRONMENT ?? "Production"}.json`, user secrets and environment variables, and resolves its content root from `ZENKINS_CONTENT_ROOT` or the directory holding the server `.csproj`.

## Dependencies

- I-001

## Test Cases

### TC-001: Filter returns only active tenant rows

Steps:
1. Seed Organizations A and B, one Project each.
2. Set active tenant to A; `db.Projects.ToList()`.

Expected:
- Exactly one project, belonging to A.

Result: **PASS** — `TenantIsolationTests.Query_filter_returns_only_the_active_tenant_and_follows_a_tenant_switch`
(also re-points the same context to B mid-test and gets exactly B's project).

### TC-002: Cross-tenant write rejected

Steps:
1. Active tenant A; add a `Project` with `OrganizationId = B`.
2. `SaveChanges`.

Expected:
- Throws; nothing persisted.

Result: **PASS** — `TenantIsolationTests.Cross_tenant_insert_throws_and_persists_nothing`; a fresh
context under tenant A sees exactly one project (the seeded `alpha-app`), so the foreign row was never
persisted. A companion test also rejects a forged `Attach` update of another tenant's row
(`Cross_tenant_update_throws_even_when_the_caller_fakes_the_original_value`).

### TC-003: Duplicate membership rejected

Steps:
1. Insert two `OrganizationMember` rows for the same (org, user).

Expected:
- Unique index violation on the second insert.

Result: **PASS** — `TenantIsolationTests.Duplicate_membership_is_rejected_by_the_unique_index_sqlite`
(the `(OrganizationId, UserId)` unique-index violation; only the first membership survives).

### TC-004: Migration parity

Steps:
1. `dotnet ef database update` against SQLite and against a PostgreSQL instance.

Expected:
- Both succeed; schema matches for the v0.1 entity set.

Result: **SQLite PASS**; **PostgreSQL NOT RUN** (no live instance). Schema parity shown by the
idempotent Npgsql script; see Verification for the command that proves it once a PostgreSQL exists.

## Development Log

### 2026-11-12

Card created.

Implementation notes:

- **Query filters are anchored on the context, not on the tenant service.** `OnModelCreating` runs once
  per model, so `HasQueryFilter(e => e.OrganizationId == tenantContext.OrganizationId)` would freeze the
  tenant of whichever request built the model first. The filters are therefore written against
  `ZenkinsDbContext.CurrentOrganizationId`, a property that reads the scoped `ITenantContext` on every
  evaluation; EF parameterizes it per query. `CurrentOrganizationId` falls back to `Guid.Empty` when no
  tenant is selected, so an unset tenant matches no rows instead of all rows. Verified by a test that
  re-points the tenant on the *same* context instance, between A and B, and gets one project each time.
- **`SaveChanges` guards tenant writes and owns the audit timestamps.** `Added` tenant-owned rows take `OrganizationId` from the
  active tenant; a caller-supplied non-empty foreign value throws `TenantIsolationException` before any
  SQL is sent, and no tenant at all also throws. `Modified`/`Deleted` check the tracked original value,
  reject an `OrganizationId` mutation, and then re-read the persisted row via `GetDatabaseValues()`:
  tracker values alone are forgeable with `Attach`, so an id-swapped `Attach` of another tenant's row is
  rejected by the database-side check (covered by a dedicated test). The guard is one synchronous code
  path shared by `SaveChanges`/`SaveChangesAsync`; only updates/deletes do one lookup per row, and v0.1
  has no async write path yet.
- **Escape hatch is a service, not a flag.** `ISystemDataScope` is a distinct scoped service; entering a
  scope is the only way to bypass the write guard, and cross-tenant reads must go through
  `ZenkinsDbContext.SystemQueryAsync<T>()`, which refuses to run without an active scope. `IgnoreQueryFilters()`
  at the call site is not enough because it bypasses only one query (not `Find`, includes, or writes), and
  it is invisible in a review: nothing in the constructor or diff shows the escalation. It is deliberately
  not reachable through `ITenantContext`. `TenantContext` itself is mutable so F-001's auth pipeline (and
  these tests) can set the selected organization; it is never read from a route/query parameter here.
- **Provider selection is configuration, not `#if`.** `Database:Provider` (`Sqlite` default, `Postgres`) +
  `ConnectionStrings:Default`; the runtime SQLite default is `<runtime content root>/App_Data/zenkins.db`
  (`src/Server/App_Data` under `dotnet run`, git-ignored, directory created on demand), while the
  design-time factory resolves the content root from `ZENKINS_CONTENT_ROOT` or the server project
  directory - never `AppContext.BaseDirectory`, which would put the design-time database under `bin/`. `ZenkinsDbContext` is registered scoped via `AddDbContext`, the tenant
  context and system scope are scoped too, and a scoped-registration test proves two request scopes get
  different context instances. `ZenkinsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ZenkinsDbContext>`
  reads `appsettings.json` + environment variables so `dotnet ef` works without the web host and gates can
  pass `Database__Provider=Postgres ConnectionStrings__Default=...` inline.
- **Migrations are generated against PostgreSQL, not SQLite.** A SQLite-generated migration emits
  `TEXT` for `Guid`/`DateTimeOffset`, which Npgsql cannot bind, so the single migration set was generated
  with `Database__Provider=Postgres`. SQLite is dynamically typed and accepts `uuid`/`timestamp with time
  zone`/`character varying` type names; all tests run against a temp SQLite file migrated with this set.
  The price is that EF sees "pending model changes" under SQLite (same model, different store type names),
  so `PendingModelChangesWarning` is ignored **only** for the SQLite provider; under PostgreSQL the check
  stays active and `dotnet ef migrations has-pending-model-changes` passes. Convention for later cards:
  add migrations with `Database__Provider=Postgres`.
- **`User` is deliberately not modeled.** F-001 brings ASP.NET Identity and the real principal.
  `OrganizationMember.UserId` is a `string` (max 450) with no navigation and no foreign key; F-001 must add
  the FK to the Identity users table and the Identity stores on this context. Roles are
  `OrganizationRole.Owner|Member` stored as strings, not Identity roles (they are per-organization).
- Tests use a temp SQLite file per test (never the repo's `App_Data`) and exercise the real
  `ZenkinsDbContext` filters; `tests/Zenkins.Tests` now references `Zenkins.Server` for that.
- Not done / not claimed: applying the migration to a live PostgreSQL instance. `pg_isready`, `psql` and
  `docker` are absent and port 5432 is closed; no PostgreSQL was installed and Docker was not used
  (PRD §8.9). Proof of real sync later: `Database__Provider=Postgres
  ConnectionStrings__Default="Host=...;Database=...;Username=...;Password=..." dotnet ef database update
  --project src/Server`.

### 2026-11-12 - review round 1 (FAIL -> fixes)

Six blocking findings from review, all fixed:

1. **`Organization` was outside isolation.** It stays readable unfiltered (the switcher needs it before a
   tenant exists), but `SaveChanges` now guards it explicitly: `Modified`/`Deleted` require
   `organization.Id == activeOrganizationId` unless a system scope is active, so tenant A can no longer
   delete organization B and cascade away its projects and members. The guard is by identity, no DB
   round-trip. Probe: cross-tenant delete and rename are rejected and the row survives with its project.
2. **`SystemQuery<T>()` guarded construction, not execution.** Removed. `SystemQueryAsync<T>(predicate, ct)`
   checks `ISystemDataScope.IsActive` and materialises inside the call, so an `IQueryable` cannot be
   captured inside the scope and enumerated after it ends. Probe: it throws with no scope and returns both
   tenants' rows inside one.
3. **Tenant switch leaked tracked rows.** `TenantContext.OrganizationId` now raises an internal change
   event; `ZenkinsDbContext` subscribes in its constructor (and unsubscribes in `Dispose`/`DisposeAsync`)
   and calls `ChangeTracker.Clear()`. **Superseded in round 2:** the event/subscription/downcast was
   replaced by the cast-free `ITenantContext.TenantRevision` counter. Probe: load A's project, switch to B, `FindAsync(A's id)` is null and
   `Projects.ToList()` is B's. Red proof: with the clear disabled this test fails.
4. **The "one context per request" claim was false for Blazor circuits.** The context is still registered
   scoped via `AddDbContext`, and `AddDbContextFactory<ZenkinsDbContext, ZenkinsDbContextFactory>(...,
   ServiceLifetime.Scoped)` now also registers a scoped `IDbContextFactory<ZenkinsDbContext>`. Rule (in
   code and card): components and page handlers create a context per operation through the factory; they
   never inject `ZenkinsDbContext`. The AC wording was corrected to "per HTTP request / per operation,
   never per process". Probe: a factory context created on A, then a new one after switching to B, each
   see their own tenant.
5. **`UpdatedAt` never changed.** `SaveChanges` now stamps `CreatedAt` on insert and `UpdatedAt` on
   insert/modify (UTC) for every entity that has those properties. F-003 (project management) consumes
   these values and must not stamp them again.
6. **Design-time could target a different database than runtime.** The design-time factory now loads
   `appsettings.json`, `appsettings.{ASPNETCORE_ENVIRONMENT ?? "Production"}.json`, user secrets and
   environment variables, and resolves its content root from `ZENKINS_CONTENT_ROOT` or the directory
   holding the server `.csproj` instead of `AppContext.BaseDirectory`, so the design-time default SQLite
   file is `src/Server/App_Data/zenkins.db`. Recorded in the card.

Cheap notes also done: `ExecuteUpdate`/`ExecuteDelete` bypass warning in the context class comment;
`ISystemDataScope` docs now say "create a child DI scope" (not root provider, which would race);
`TenantContext.OrganizationId` setter is `internal` plus `InternalsVisibleTo("Zenkins.Tests")`;
`CK_OrganizationMembers_Role` check constraint so a bad `Role` value cannot break member listing;
TC-003 now asserts the SQLite unique-index violation and its `(OrganizationId, UserId)` columns instead of
any `DbUpdateException`; `Cross_tenant_insert_...` verifies "nothing persisted" through tenant A's own
filtered `Projects` view. Migration regenerated for the check constraint (still generated against Npgsql).

Handoffs and known gaps (no follow-up card):

- **F-001**: `Program.cs` does not call `Migrate()` yet, so F-001 owns the startup migration hook. The
  bootstrap insert must run under `ISystemDataScope` with an explicit `OrganizationId` (the first owner
  membership is created before any tenant is selected) and must add the User foreign key to
  `OrganizationMember`.
- **F-002**: the organization switcher enumerates memberships before a tenant is selected, when the
  filtered `OrganizationMembers` set is empty; that read needs the system scope (or a dedicated guarded
  method) by design.
- **F-003**: consumes `CreatedAt`/`UpdatedAt` as stamped by the data layer; do not duplicate the stamping.

### 2026-11-12 - review round 2 (FAIL -> fixes)

- **B1: tracker-clear protection no longer depends on the concrete tenant type.** `ITenantContext` now
  exposes a monotonic `TenantRevision`; `TenantContext` increments it on every real change. `ZenkinsDbContext`
  samples it in the constructor, drops the change tracker whenever it moves (`Set<T>()` and the
  non-generic `Find`/`FindAsync` overrides sample before `Find`/`Include` can short-circuit, and
  `SaveChanges` checks first too), and the event/subscription/downcast is gone. `ZenkinsDbContextFactory` no longer has a dispose-time unsubscribe
  obligation, though callers still own disposal. Probe:
  `Find_after_a_tenant_switch_does_not_leak_through_a_decorated_tenant_context` wraps the real context in a
  forwarding `ITenantContext` decorator (standing in for F-002) and asserts `FindAsync` returns null after
  a switch. Red proof: with the revision check disabled it fails.
- **N2: a tenant can actually be selected.** `TenantContext` is registered scoped as itself, and
  `ITenantContext` is mapped onto that same instance, so F-001 can inject the concrete type to switch and
  everyone else reads the interface. Test asserts both resolve to the same scoped instance and that a
  factory context follows a switch made through the concrete type. The `Assert.IsType<TenantContext>`
  expectation is expected to change in F-002, when a decorator is registered instead of the concrete type
  (the switch test already exercises the decorated shape).
- **N3:** `base.OnModelCreating(modelBuilder)` is now called first, so F-001 re-basing on Identity cannot
  silently drop the Identity configuration.
- **N5:** TC-003 test renamed `Duplicate_membership_is_rejected_by_the_unique_index_sqlite`, because SQLite
  reports the violated index columns rather than its name; the assertion now checks the
  `(OrganizationId, UserId)` unique-index violation instead of any `DbUpdateException`.
- **N7:** comment on the `AddDbContext` + `AddDbContextFactory` pair: they `TryAdd`-share
  `DbContextOptions<ZenkinsDbContext>`, so the two options actions must stay identical or the last
  registration silently wins for both.
- **N8:** class doc now states that `Organization` **inserts** are deliberately unguarded (bootstrap creates
  the first organization before any tenant exists); only modify/delete are constrained.
- **N9:** `ZenkinsDbContextFactory.CreateDbContext` documents that the caller owns and must dispose the
  context.
- **N4 (operational note):** after regenerating a migration in place, delete `src/Server/App_Data/` (or run
  `dotnet ef database drop`) before the next `Migrate()`/`database update`; otherwise it fails with
  "table Organizations already exists".
- **N6:** the two (now three) red-proof assertion lines are pasted into Verification so the strongest
  evidence survives the machine.

### 2026-11-12 - review round 3 (B2 + notes)

- **B2: `DbContext.Find` reaches the tracker without the sampler.** EF Core 10 implements the generic
  `Find<T>`/`FindAsync<T>` on top of `Set<T>()` (so they were already covered), but the non-generic
  `Find(Type, ...)`/`FindAsync(Type, ...)` resolve through the entity finder and bypass `Set<T>()`.
  Both non-generic overloads now call `DropTrackerIfTenantChanged()` first. The decorated-context probe
  asserts the non-generic `Find`/`FindAsync` **before** any `Set<T>()` access, otherwise the tracker would
  already be cleared by the generic path. Red proof: removing the sampler from `Find(Type, ...)` makes
  `Assert.Null(context.Find(typeof(Project), projectOfA.Id))` return the previous tenant's project.
- **Dirty tracker fails loudly.** `DropTrackerIfTenantChanged` throws
  `TenantIsolationException("Cannot switch the active organization with unsaved changes.")` when the
  revision moved while `ChangeTracker.HasChanges()` is true. Tracked-but-unmodified entries still clear
  silently, so the switch probes stay green. Pinned by
  `Switching_tenant_with_unsaved_changes_throws_instead_of_discarding_them`.
- **Same-organization re-select does not bump the revision.** Pinned in TC-001:
  assigning the same id twice leaves `TenantRevision` unchanged, so a no-op re-select cannot clear the
  tracker or trip the dirty-tracker guard.

### 2026-11-12 - lead review round 4 (doc accuracy + generic-Find probe)

- **The round-3 reviewer note that the generic overloads delegate to the non-generic `Find(Type, ...)`
  was empirically wrong.** Removing the sampler from `Find(Type, ...)` and re-running the probe left
  `Find<Project>` returning `null`, i.e. in EF Core 10 the generic overload really does resolve through
  `Set<T>()` and was already covered. The card and the code comment keep that claim; the comment notes it
  is an implementation detail, so the non-generic overloads carry their own guard instead of relying on it.
- **New probe `Generic_find_is_the_first_tracker_access_after_a_switch_and_still_returns_null`** pins that
  routing assumption: a future EF change in delegation direction now fails loudly instead of silently
  losing coverage.
- **Dirty-tracker message extended** with the recovery path: "Finish the save, or discard the edits with
  `ChangeTracker.Clear()` and continue on a new context." The behaviour (every later data access on that
  instance keeps throwing until the caller discards) is intentional and recorded here.
- **Deferred AC marked as deferred** rather than unchecked-open, so later audits do not count I-002 as
  incomplete; the live-PostgreSQL apply stays a first-deployment step.
- **Per-request tenant context is pinned:** the registration test now also asserts that two DI scopes get
  different `ITenantContext` instances (a singleton tenant context is the worst isolation bug available).
- **Honesty about what is not guarded:** `ChangeTracker.Entries()`, `Entry()` and `Local` still expose
  whatever is tracked. The binding rule remains one context per tenant and one context per operation; the
  revision clear is defence in depth for the residual case, not a substitute for that rule. This is stated
  in the `ZenkinsDbContext` class doc and here.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Data/**`, `src/Shared/Tenancy/**`, `tests/Zenkins.Tests/Tenancy/**`,
  `src/Server/Program.cs`, `src/Server/appsettings.json`, `Directory.Packages.props`, `*.csproj`
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test`
  3. `ConnectionStrings__Default="Data Source=/tmp/zenkins-gate.db" dotnet ef database update --project src/Server`
     then `sqlite3 /tmp/zenkins-gate.db ".schema"` and delete the temp file
  4. `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
  5. `Database__Provider=Postgres ConnectionStrings__Default="..." dotnet ef migrations script --idempotent --project src/Server --output /tmp/initial-postgres.sql`
- Results (full output in `/tmp/zenkins-logs/I-002/`):
  - `dotnet build -c Release` -> **PASS**, 0 warnings / 0 errors -> `build-final.log`
  - `dotnet test` -> **PASS**, 14 passed / 0 failed / 0 skipped (2 pre-existing guards + 12 tenancy/data tests) -> `test-final.log`
  - `dotnet ef database update` (SQLite, temp db) -> **PASS**, `Applying migration '<Initial>'` -> `ef-database-update-sqlite.log`
  - `sqlite3 <temp>.schema` -> **PASS**: three tables, `IX_OrganizationMembers_OrganizationId_UserId UNIQUE`,
    `CK_OrganizationMembers_Role`, foreign keys, `uuid`/`timestamp with time zone` columns, history row;
    temp db deleted -> `sqlite-schema.log`
  - `dotnet ef migrations has-pending-model-changes` (Postgres) -> **PASS**, no pending changes -> `ef-pending-model-changes-postgres.log`
  - `dotnet ef migrations script --idempotent` (Postgres) -> **PASS**, `uuid`/`timestamp with time zone`/
    `character varying` plus the unique index and check constraint -> `ef-script-postgres.log`, `initial-postgres.sql`
  - Red proof 1: organization guard branch removed -> `Cross_tenant_organization_delete_and_update_are_rejected`
    fails with:
    `Assert.Throws() Failure: No exception was thrown` / `Expected: typeof(Zenkins.Server.Data.Tenancy.TenantIsolationException)`
    -> `red-proof-organization-guard.log`
  - Red proof 2: tenant-change handler disabled ->
    `Find_after_a_tenant_switch_does_not_return_the_previous_tenants_row` fails with:
    `Assert.Null() Failure: Value is not null` / `Actual: Project { … Name = "alpha-app", … }`
    -> `red-proof-tracker-clear.log`
  - Red proof 3: revision check disabled ->
    `Find_after_a_tenant_switch_does_not_leak_through_a_decorated_tenant_context` fails with:
    `Assert.Null() Failure: Value is not null` / `Actual: Project { … Name = "alpha-app", … }`
    -> `red-proof-tenant-revision.log`
  - Red proof 4: sampler removed from the non-generic `Find(Type, ...)` override ->
    `Find_after_a_tenant_switch_does_not_leak_through_a_decorated_tenant_context` fails with:
    `Assert.Null() Failure: Value is not null` / `Actual: Project { … Name = "alpha-app", … }`
    -> `red-proof-find-override.log`
  - PostgreSQL instance apply -> **NOT RUN**: `pg_isready`, `psql`, `docker` not found and port 5432 closed
    -> `postgres-availability.log`. Not claimed as verified.
