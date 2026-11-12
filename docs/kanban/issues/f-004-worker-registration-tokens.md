---
id: F-004
title: Worker registration, tokens and heartbeat (Server)
type: feature
priority: P1
size: M
status: done
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

An Organization Owner can mint single-use registration tokens, and a Worker process can register
itself with the Server (name, OS, architecture, version, tags, CPU count, max concurrency) and prove
its identity on every later call with its own worker token; heartbeats keep the worker Online.

## Background

PRD §3.4.2/§3.4.3 and §6 (Worker, WorkerToken entities). Roadmap constraint: communication must be
authenticated from the first call — the first call authenticates with a registration token an Owner
issued; every later call authenticates with the per-worker token returned at registration. Workers
are tenant-owned (`ITenantOwned`, `OrganizationId`): the registration token is minted inside an
active Organization and the worker joins that Organization.

Decisions (review may challenge, but implement these unless the reviewer objects):

- Registration tokens and worker tokens are stored **hashed** (SHA-256) with a readable prefix
  (`zreg_…`, `zwrk_…`); plaintext is shown exactly once at creation. Single-use registration tokens.
- Registering with a name that already exists in the Organization updates that Worker row
  (capabilities, version), revokes its previous active worker tokens and returns a fresh worker
  token — container restarts must not duplicate workers.
- Worker API lives under `/api/workers/*` and is exempt from cookie auth; the token resolves the
  tenant, never a cookie. All auth failures are `401` with no tenant detail.
- Minimal UI: `/workers` page — all Organization Members can view workers; only the Owner creates,
  rotates and revokes registration tokens.

## Acceptance Criteria

- [x] `Worker` and `WorkerToken` entities, tenant-owned, with a migration generated under the
      PostgreSQL contract (`has-pending-model-changes` clean).
- [x] Owner can create a registration token (shown once, stored hashed), list tokens
      (created / last-used / revoked, no secrets) and revoke; revoke immediately invalidates.
- [x] `POST /api/workers/register` with a valid, unused registration token creates (or updates by
      name, per the decision above) the Worker row and returns a worker token; the registration
      token is burned on first use.
- [x] Registration payload records name, OS, architecture, version, tags, CPU count, max
      concurrency; heartbeat can refresh capability fields and `LastHeartbeatAt`.
- [x] `POST /api/workers/heartbeat` with a valid worker token succeeds; with a missing, unknown or
      revoked token returns `401` and changes nothing.
- [x] `/workers` page: list workers of the active Organization only (name, OS, arch, version, tags,
      status, CPU count, last heartbeat); foreign-org workers are invisible (tenant filter).
- [x] Worker-API requests are authenticated from the first call; no worker endpoint accepts a
      session cookie as authorisation for a worker action.

## Subtasks

- [ ] Entities + PostgreSQL-contract migration + `DbSet`s in `ZenkinsDbContext`.
- [ ] Token hashing/verification helpers + service (issue/burn/revoke/register/heartbeat).
- [ ] `/api/workers/register` + `/api/workers/heartbeat` endpoints with token auth middleware/handler.
- [ ] `/workers` page + token management form endpoints (antiforgery, Owner-only, PRG errors).
- [ ] Tests (`Category=Workers`): registration happy path, burned token, revoked token, bad worker
      token 401, cross-tenant invisibility, name-collision update path.

## Dependencies

- none (data layer, Identity and Owner/Member roles already exist)

## Test Cases

### TC-001: Registration with a valid token

Steps:
1. Owner mints a registration token in Organization A.
2. `POST /api/workers/register` with that token and capability fields.

Expected:
- `200` with a worker token; the worker appears on `/workers` for A with its capabilities;
  the registration token can no longer be used (second register → `401`).

### TC-002: Bad credentials are 401 and change nothing

Steps:
1. Register with an unknown, revoked or already-burned registration token.
2. Heartbeat with an unknown worker token.

Expected:
- `401` every time; no Worker row created or modified; plaintext tokens never logged.

### TC-003: Cross-tenant invisibility

Steps:
1. Register a worker into Organization A. Authenticate as a user whose active Organization is B.

Expected:
- `/workers` shows no row for A's worker; worker rows carry `OrganizationId` under the global
  query filter (no `ExecuteUpdate`/raw SQL bypasses on these entities outside a system scope).

### TC-004: Heartbeat refreshes state

Steps:
1. Register, then heartbeat twice with updated CPU count/tags.

Expected:
- `200`; `LastHeartbeatAt` and capability fields updated; status remains Online.

## Development Log

### 2026-11-13

**Implementation decisions.**

- **Entities.** `Worker` and `WorkerToken` (both `ITenantOwned`) in
  `src/Server/Data/Entities/`; `WorkerStatus` is a string (`Online`/`Offline`) with a check
  constraint so the model stays provider-neutral. Migration `20261112170526_AddWorkers` was
  generated under the **Npgsql** provider (`Database__Provider=Postgres`); unique index on
  `Workers (OrganizationId, Name)` decides same-name races, `TokenHash` is the unique lookup key.
- **Token scheme.** `WorkerTokenCrypto` mints `zreg_…`/`zwrk_…` tokens: 32 random bytes,
  base64url, 43 chars of entropy — only the lowercase-hex SHA-256 is stored; plaintext is returned
  exactly once at mint/registration and never persisted or logged (logs carry an 8-char hash
  prefix).
- **Endpoint authentication.** `/api/workers/*` is exempt from cookie auth. Both register and
  heartbeat do the same thing: SHA-256 the presented Bearer plaintext, look it up by `TokenHash`
  **equality** through `SystemQueryAsync<WorkerToken>` inside an `ISystemDataScope` (no tenant
  exists yet), then the token row selects the tenant and everything after runs behind the normal
  tenant filter. Every auth failure is `401` with no tenant detail (asserted in
  `Worker_authentication_failures_leak_no_tenant_information`). A cookie can never authorize a
  worker action.
- **Same-name registration updates the row.** Re-registering with a known name updates the
  existing Worker row, revokes its active worker tokens and issues a fresh one — container
  restarts replace identity, never duplicate workers. Burn of the registration token, the worker
  write and the new worker token land in one `SaveChanges`; a concurrent name race is decided by
  the unique index and surfaces as `name-duplicate`.
- **`/workers` page.** All Members view workers; token management (mint/list/revoke) is Owner-only
  form endpoints with antiforgery and PRG notices. Tenant isolation for the new entities is also
  probed by the Q-001 harness (`TenantIsolationProbes.CoveredEntityTypes` now includes
  `Worker`/`WorkerToken`).
- **Red proof (revocation check).** Temporarily replaced `workerToken is null ||
  workerToken.RevokedAt is not null` with `workerToken is null` in `WorkerService.HeartbeatAsync`
  (revocation check disabled) and ran `dotnet test -c Release --filter
  "FullyQualifiedName~WorkerEndpointTests.Re_registering"`:
  `Re_registering_with_the_same_name_updates_the_row_and_revokes_the_old_token` failed with
  `Assert.Equal() Failure: Expected: Unauthorized, Actual: OK` (heartbeat with the revoked old
  worker token was accepted). The check was restored immediately afterwards and the test is green
  again in the final run.
- **Dead code removed: `WorkerTokenCrypto.Verify`.** The final implementation has no production
  call site for it — both auth paths hash the plaintext and query the database by `TokenHash`
  equality, so a constant-time comparison is never executed (the hash is not secret, and equality
  is enforced by the SQL lookup / unique index). `Verify` and the two Verify-only unit tests were
  removed (Generate/Hash tests retained); the class doc no longer mentions `FixedTimeEquals`.

**Known gaps.**

- `has-pending-model-changes` is a design-time model comparison; no live PostgreSQL instance
  exists in this environment (documented since I-002), so the gate proves model/snapshot sync
  under the Npgsql provider, not a running database.
- Heartbeat-with-revoked-token is covered via the re-registration test's revoked-token heartbeat
  (there is no dedicated revoked-heartbeat test case). — **resolved in the 2026-11-13 review
  fixes below** (dedicated test added).
- Registration requires non-empty `tags` (server-side required text field); a worker started
  without `ZENKINS_WORKER_TAGS` cannot register — see the F-005 log for the worker-side
  consequence. Left as-is here; flagged for review. — **resolved in the 2026-11-13 review fixes
  below** (tags are optional, per PRD §3.4.4).

### 2026-11-13 — review fixes

Fixes from the independent F-004/F-005 review. Red proofs below were produced by temporarily
reverting each fix, running the new test against the reverted state, and restoring the fix:
6/6 red before restore, 6/6 green after.

- **B1 (blocking), server half — tags are optional.** `ValidateRegistration` used `ValidateText`
  (empty → `field-invalid`) while `ValidateHeartbeat` used `ValidateOptionalText`; PRD §3.4.4
  makes tags optional. Registration now validates tags like the heartbeat does and normalizes to
  the empty string, so a worker started without `ZENKINS_WORKER_TAGS` registers instead of
  receiving a 400 it would retry on forever (the worker half of B1 is on F-005).
  Test: `Registration_without_tags_succeeds_and_stores_an_empty_capability` — register with
  `tags: null` → 200, `Tags == ""`, registration token burned, exactly one Worker row.
  Red proof: with the old `ValidateText` restored, the test failed with 400.
- **M1 (major) — the one-time burn is guarded by EF optimistic concurrency.**
  `WorkerToken.UsedAt` and `WorkerToken.RevokedAt` are EF concurrency tokens now
  (`IsConcurrencyToken` in `OnModelCreating`), closing both the burn race and a
  revoke-vs-register race. `RegisterAsync` catches `DbUpdateConcurrencyException` and maps it to
  `InvalidToken` (401): the loser of the race holds, from the protocol's point of view, an
  invalid token, and nothing is written (the whole SaveChanges rolls back). No new migration:
  plain concurrency tokens change no column and no index — confirmed by the PostgreSQL gate
  below (`No changes have been made to the model since the last migration.`).
  Tests: `Concurrent_registrations_with_one_token_burn_it_exactly_once` — two `Task.WhenAll`
  registrations with the same `zreg_` token and two different names → exactly one 200, one 401,
  one Worker row, one burned Registration token (different names keep the Workers unique index
  out of the picture so only the token burn decides). Stability: 5 consecutive runs, all green.
  On SQLite the writers serialize, so the loser often re-reads the already-burned token and gets
  the regular 401 — the contract outcome the test pins; the mapping itself is pinned by
  `WorkerRegistrationConcurrencyTests.A_lost_burn_race_is_mapped_to_401_and_writes_nothing`,
  which constructs the `DbUpdateConcurrencyException` directly (save interceptor on a migrated
  SQLite database) and asserts `InvalidToken` with no Worker row written and the token still
  unused. Red proof: with the catch removed, that test failed with the raw
  `DbUpdateConcurrencyException` propagating out of `RegisterAsync`.
- **Review minor 1 — heartbeat re-checks revocation.** The tenant-side re-read of the worker
  token now filters `RevokedAt == null`, and the heartbeat `SaveChanges` also maps
  `DbUpdateConcurrencyException` to 401 (a revoke committing between the re-read and the write
  makes its UPDATE match no rows).
- **Review minor 2 — dedicated revoked-worker-token heartbeat test.**
  `Heartbeat_with_a_revoked_worker_token_is_401_and_changes_nothing` — the worker token is
  revoked directly (the server-side effect of a same-name re-registration), a first heartbeat
  lands, then the heartbeat with the revoked token is 401 and the worker row is untouched.
  Red proof: with the revoked check disabled in `HeartbeatAsync` (same technique as the
  implementation log above), the test failed with `OK` instead of `Unauthorized`.
- **Review minor 3 — authentication before payload validation.** Both worker endpoints resolve
  the bearer token (401) before validating the payload (400), so a tokenless caller cannot probe
  the contract through the 400-vs-401 difference.
  Test: `An_unauthenticated_caller_gets_401_even_with_an_invalid_payload` — no bearer plus an
  invalid payload (empty name; `cpuCount: 0`) → 401 on both endpoints.
  Red proof: with validation-first restored, the test failed with 400.

### 2026-11-13 — second review fixes

Fixes from the second F-004/F-005 review round, item by item. B is this card's fix; A/C/D are
Worker-host fixes that live on F-005's files and are logged in full there.

- **B (major), this card — the Owner revoke path is guarded against lost concurrency races.** With
  `UsedAt`/`RevokedAt` as EF concurrency tokens (M1), `RevokeRegistrationTokenAsync`'s
  `SaveChanges` raises `DbUpdateConcurrencyException` when the revoke races a second revoke (a
  double-click) or the burn of the same token — and that exception was unhandled, i.e. a 500. The
  save is now wrapped: on a lost race the token is re-read through the tenant filter and mapped to
  exactly the outcomes the pre-checks produce — another revoke won → `token-already-revoked`, the
  burn won → `token-already-consumed`, row gone or invisible → the pre-check's `NotFound` (404).
  Never a 500.
  Tests:
  `WorkerRegistrationConcurrencyTests.A_revoke_racing_another_revoke_is_mapped_to_token_already_revoked`
  and `A_revoke_racing_a_burn_is_mapped_to_token_already_consumed` — a save interceptor first
  commits the rival's UPDATE as raw SQL on the same row and then throws the concurrency exception,
  so the service's re-read observes the real post-race state; the mapping, not the storage engine,
  is what the assertions observe. Plus the HTTP-level double revoke:
  `WorkerEndpointTests.Revoking_a_registration_token_twice_gives_a_friendly_result_not_a_500` —
  the second revoke lands on `/workers?error=token-already-revoked`, not a 500.
  Red proof: with the try/catch removed, both interceptor tests failed with the raw
  `DbUpdateConcurrencyException ("The token was revoked or burned by a concurrent request.")`
  propagating out of `RevokeRegistrationTokenAsync` (the 500 the review described); guard
  restored, both green.
- **A (major), Worker half — see F-005.** 408/409/425/429 are no longer definitive answers: a 409
  `name-duplicate` (WorkerEndpoints.cs:70) is what the loser of a same-name first-start race
  receives, and its retry must succeed once the winner's row is committed. Server-side nothing
  changed; the worker-side fix and its process-level red proof are on F-005.
- **C (minor), Worker half — see F-005.** A registration the server permanently rejected now
  exits 3 (configuration errors stay 2). No server change.
- **D (minor), Worker half — see F-005.** The worker's per-request ceiling now also bounds the
  response-body read. No server change.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Server/Data/Entities/Worker.cs`, `src/Server/Data/Entities/WorkerToken.cs`
  - `src/Server/Data/Migrations/20261112170526_AddWorkers.cs` (+ `.Designer.cs`),
    `src/Server/Data/Migrations/ZenkinsDbContextModelSnapshot.cs`, `src/Server/Data/ZenkinsDbContext.cs`
  - `src/Server/Workers/WorkerTokenCrypto.cs`, `WorkerService.cs`, `WorkerEndpoints.cs`,
    `WorkerServiceCollectionExtensions.cs`
  - `src/Server/Components/Pages/Workers.razor`, `src/Server/Components/_Imports.razor`,
    `src/Server/Program.cs`
  - `tests/Zenkins.Tests/Workers/WorkerEndpointTests.cs`, `WorkerTokenCryptoTests.cs`,
    `WorkerTestHost.cs`, `WorkerRegistrationConcurrencyTests.cs`
  - `tests/Zenkins.Tests/Tenancy/TenantIsolationProbes.cs`, `TenantIsolationProbeTests.cs`,
    `TwoOrgsFixture.cs` (isolation probes extended to `Worker`/`WorkerToken`)
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test -c Release` (Workers cases carry `Category=Workers`)
  3. PostgreSQL contract gate:
     `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
- Results (2026-11-13, after second review fixes):
  - Build: **PASS** — `dotnet build -c Release`, 0 warnings, 0 errors
    (`TreatWarningsAsErrors` active).
  - Tests: **PASS** — 212/212 across the suite (was 208 after the first review; +4 second-review
    tests: 2 revoke-race mappings in `WorkerRegistrationConcurrencyTests`, the HTTP double-revoke
    in `WorkerEndpointTests`, the 409-retry process E2E on F-005). Red proofs: B reverted → red
    (raw `DbUpdateConcurrencyException` out of `RevokeRegistrationTokenAsync`) → restored →
    green; A's red proof is logged on F-005.
  - Concurrency stability: **PASS** — `Concurrent_registrations_with_one_token_burn_it_exactly_once`
    run 5× consecutively, all green.
  - PostgreSQL contract gate: **PASS** — `No changes have been made to the model since the last
    migration.` (exit 0). The second review fixes change no model at all, so
    `20261112170526_AddWorkers` remains the latest migration — no new migration needed.
