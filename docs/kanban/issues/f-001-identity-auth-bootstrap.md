---
id: F-001
title: Identity auth + first-run Owner bootstrap
type: feature
priority: P1
size: M
status: done
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

A new deployment can register its first user, who automatically becomes Owner of a starter Organization, and every subsequent request is authenticated or rejected.

## Background

PRD §3.1.2 defines exactly two roles, Owner and Member, and they are **Organization** roles, not global identity roles. Someone can be Owner of A and Member of B. Do not model them as ASP.NET Identity roles.

Bootstrap rule: if the database has zero users, the first successful registration creates a User + Organization + `OrganizationMember(Owner)` in one transaction, and this path is closed afterwards. Registration beyond that point requires an Owner invitation (deferred to `F-002`) — for v0.1, subsequent sign-ups have no Organization until invited, which must be visible in the UI rather than a broken empty state.

## Acceptance Criteria

- [x] Register, login, logout work end-to-end; passwords use Identity's hasher.
  - All three are Razor components rendered as **static SSR** (`[ExcludeFromInteractiveRouting]`), so the form POST can write/clear the auth cookie. Proven by curl and by `AuthenticationFlowTests` (15 tests).
- [x] First registration atomically creates User + Organization + Owner membership; the bootstrap path is refused once a user exists.
  - One EF transaction on the shared scoped `ZenkinsDbContext` (the Identity store uses the same instance), and a single-row `BootstrapMarker` primary key as the race guard. A second registration still creates an account but never an organization; a raw second bootstrap attempt returns `AlreadyCompleted` and writes nothing.
- [x] Authenticated pages redirect anonymous users to login; login redirects back to the requested page.
  - `AuthorizeRouteView` + `RedirectToLogin` (and, observed in practice, endpoint authorization from the cookie handler which also emits `ReturnUrl`). Verified for `/projects` with curl and a test.
- [x] A user with no Organization membership sees an explicit "no organization" state with a next step, not a crash or empty dashboard.
  - `/no-organization` page, a notice card on the dashboard, and a banner in the shell on every app page. Registration of a second account lands on `/no-organization`.
- [x] No password hash, token, or cookie value appears in logs.
  - Verified by grepping the real app log after register/login/logout; EF command logging is filtered to `Warning` in `Program.cs`.
- [x] Cookie is `HttpOnly`, `SameSite=Lax`, and `Secure` when the request is HTTPS or forwarded-proto says so.
  - `CookieSecurePolicy.SameAsRequest` + `UseForwardedHeaders` (`X-Forwarded-Proto`); both paths observed with curl (HTTP: no `secure`; forwarded HTTPS: `secure`).

## Subtasks

- [x] Add `Microsoft.AspNetCore.Identity.EntityFrameworkCore`; extend `User`, add Identity stores on `ZenkinsDbContext`.
  - The context is re-based on `IdentityUserContext<User>` (users/claims/logins/tokens only). Identity **role** tables are deliberately absent: Owner/Member are organization roles on `OrganizationMember`. `OrganizationMember.UserId` now has a real foreign key to `AspNetUsers` (cascade), and SQLite runs with `PRAGMA foreign_keys=ON` so development enforces it too.
- [x] Configure password/lockout policy and cookie options per above.
  - Password: 12 chars, 4 unique, upper + lower + digit + symbol; unique email; no email confirmation. Lockout: 5 failures / 15 minutes, persisted in `AspNetUsers`. Cookie `Zenkins.Auth`, `HttpOnly`, `SameSite=Lax`, `SameAsRequest` Secure, 8h sliding.
- [x] Register/login/logout Razor components (Blazor Server form model, not MVC pages).
  - `Components/Pages/Account/{Register,Login,Logout}.razor` with `EditForm` + `[SupplyParameterFromForm]` + `DataAnnotationsValidator`, static SSR only.
- [x] Implement bootstrap transaction with a race-safe guard (unique constraint on bootstrap, not a "count == 0" check alone).
  - `BootstrapMarker` with a fixed primary key (`"bootstrap"`), inserted in the same `SaveChanges` as the organization and Owner membership, inside the registration transaction, under `ISystemDataScope` with an explicit `OrganizationId`. `RegistrationService` depends on the one-method `IBootstrapService` seam so the two rare branches (lost race, bootstrap write failure) are testable without production branches or flags.
- [x] Add `[Authorize]` at the layout level and a `RedirectToLogin` fallback.
  - `[Authorize]` on Home/Projects/Workers/Secrets/NoOrganization; `AuthorizeRouteView` wraps the routed page in `Routes.razor`; `RedirectToLogin` preserves the path + query.
- [x] Add the no-organization state page.
  - `/no-organization` (`Pages/NoOrganization.razor`), plus `Shared/NoOrganizationNotice.razor` used by the dashboard and the shell banner.

## Dependencies

- I-002
- D-001

## Test Cases

### TC-001: Bootstrap creates exactly one owner

Steps:
1. Empty database. Register `alice`.
2. Inspect User / Organization / OrganizationMember.

Expected:
- One of each; `alice` is Owner of the starter Organization.

Result: **PASS** — `BootstrapTests.First_registration_creates_one_user_organization_owner_membership_and_marker`
and the curl gate (`Register` → 302 + `Set-Cookie`, then SQLite counts `users=1, organizations=1,
memberships=1, owner_memberships=1, bootstrap_markers=1`).

### TC-002: Bootstrap cannot run twice

Steps:
1. With `alice` present, POST the bootstrap path again (second registration).

Expected:
- Refused; no second Organization created.

Result: **PASS, with the card's Background applied.** The bootstrap **path** is refused (the marker
exists; `BootstrapTests.Bootstrap_insert_is_refused_when_the_marker_already_exists` calls the raw
bootstrap insert and gets `AlreadyCompleted`, still one Organization). Registration itself stays open,
because Background explicitly says later sign-ups exist without an Organization until F-002 invites
them; the second account is created with **no** membership and lands on `/no-organization`.
Curl gate: second registration → 302 `/no-organization`, then `organizations=1, memberships=1,
bootstrap_markers=1, users=2`.
`Bootstrap_stays_closed_after_the_first_account_is_deleted` also proves deleting the first account
cannot re-open the bootstrap: the marker has no foreign key to the user, so it survives while the
membership cascades away. The real race at the guard is pinned by
`BootstrapTests.Bootstrap_guard_lets_exactly_one_of_two_concurrent_attempts_create_the_organization`
(two scopes, two connections, both parked before `TryCreateOwnerOrganizationAsync`) → exactly one
`Created`, one `AlreadyCompleted`, `organizations=1, memberships=1, markers=1`, no exception.
The registration-level fallback is covered separately by
`RegistrationTransactionTests` through the `IBootstrapService` seam (see the review round-1 notes
for why SQLite's own locking makes that branch unreachable through the HTTP flow).

### TC-003: Anonymous access is blocked

Steps:
1. Clear cookies; request `/projects`.

Expected:
- 302 to login, then back to `/projects` after authenticating.

Result: **PASS** — curl: `GET /projects` → `302 Location: /account/login?ReturnUrl=%2Fprojects`;
after the login POST → `302 Location: /projects` and `GET /projects` → `200`. Pinned by
`AuthenticationFlowTests.Anonymous_projects_request_redirects_to_login_with_return_url_and_returns_there`.

### TC-004: No secrets in logs

Steps:
1. Perform register + login with `Information` logging.
2. Grep captured log output.

Expected:
- No password, hash, or cookie value present.

Result: **PASS** — curl gate step 10: password literal 0, cookie value (`CfDJ8…`) 0, `PasswordHash`
0, antiforgery token 0. The only F-001 Information lines log a `UserId` (never an email/password).

## Development Log

### 2026-11-12 — implemented

**Static SSR for the account pages (the Blazor cookie constraint).** The shell renders
`<Routes @rendermode="InteractiveServer" />`, and a form handled inside an interactive circuit cannot
set an authentication cookie, so login/register/logout would be broken in a subtle way. Fix:
`App.razor` computes the render mode per request with
`HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null`, and the three account pages carry
`[ExcludeFromInteractiveRouting]`. Every other page keeps the interactive shell; interactive rendering
was **not** disabled app-wide. The account pages therefore render as plain HTML forms:

```html
<form method="post" class="bb:space-y-4" action="/account/register">
  <input type="hidden" name="__RequestVerificationToken" …>
  <input type="hidden" name="_handler" value="register">
```

`EditForm` + `[SupplyParameterFromForm]` does the SSR form mapping; `app.UseAntiforgery()` validates the
token (a POST without it is a 400, pinned by a test); the handler calls `SignInManager`/`SignOutAsync`,
which write `Set-Cookie` before the response starts, then `NavigationManager.NavigateTo(...)` turns into
a 302 (the project sets `BlazorDisableThrowNavigationException`). Account pages use their own
`AccountLayout` so the static form does not depend on interactive shell components.

**Identity model.** `User : IdentityUser` (string key, `CreatedAt`); `ZenkinsDbContext :
IdentityUserContext<User>` with `base.OnModelCreating` first. No Identity roles: PRD §3.1.2 roles are
per organization. `OrganizationMember.UserId` finally gets the real FK to `AspNetUsers`
(I-002's handoff), plus `IX_OrganizationMembers_UserId`; deleting a user cascades its memberships.
Because SQLite ignores foreign keys unless the pragma is on, `UseZenkinsDatabase` enables
`Foreign Keys=True` for SQLite, so the FK is enforced in development as it is in PostgreSQL
(`BootstrapTests.Membership_requires_an_existing_user`).

**Bootstrap transaction and race safety.** `RegistrationService` opens one transaction on the scoped
`ZenkinsDbContext` (the same instance the Identity EF store uses, so `UserManager.CreateAsync` enlists
in it), creates the user, then when no marker exists calls `BootstrapService.TryCreateOwnerOrganizationAsync`
which - under `ISystemDataScope` and with an explicit `OrganizationId` - adds Organization +
`OrganizationMember(Owner)` + `BootstrapMarker` and saves. The one-row marker's primary key is what makes
this race-safe: two concurrent first registrations both attempt the insert, the database accepts exactly
one, and the loser gets a unique-constraint violation, rolls its transaction back (including its user),
and re-creates the account without an organization. The pre-check (`AnyAsync`) is only an optimization;
the constraint is the guard.
**Correction (review round 1):** the earlier "concurrent registrations" test did not actually reach the
loser branch - SQLite starts a `BEGIN IMMEDIATE` transaction for the registration write, so the second
request is serialized until the first commits and then sees `HasCompletedAsync == true`. The guard is
now pinned directly at `BootstrapService` (two scopes parked before the insert, one `Created` + one
`AlreadyCompleted`), and the two registration branches are pinned through `IBootstrapService` stubs
(`RegistrationTransactionTests`). The registration-level fallback remains provider-unreachable on
SQLite and is a PostgreSQL-path behaviour; that is now stated, not claimed as end-to-end verified. Deliberate decision recorded in TC-002: registration stays open so invited
members can create accounts, but the *bootstrap* is closed forever after the first commit.

**Passwords, lockout, cookies.** Policy configured explicitly in `IdentitySetup` (see AC notes).
Registration signs the new user in with `SignInManager.SignInAsync`; login uses
`PasswordSignInAsync(..., lockoutOnFailure: true)` and shows a generic "Incorrect email or password."
Lockout is server-side state (in `AspNetUsers`), so a correct password while locked still fails
(pinned by a test). Cookie flags: `HttpOnly`, `SameSite=Lax`, `SameAsRequest` Secure, name `Zenkins.Auth`;
`UseForwardedHeaders` runs before authentication, so `X-Forwarded-Proto: https` from a trusted proxy
sets `Secure` (curl-verified). `ReturnUrl` is sanitized to a site-relative path, so `//evil.example` is
discarded (test).

**Tenant selection.** `TenantResolutionMiddleware` runs after authentication and resolves the user's
default organization (Owner first, then earliest membership) through
`UserOrganizationDirectory`, which reads the membership rows inside `ISystemDataScope` because
`OrganizationMember` is tenant-filtered and no tenant exists yet (the I-002 handoff). Interactive
circuits have their own DI scope, so `UserSession` (used by the shell) resolves the same information and
sets the circuit-scoped `TenantContext`; both share the directory. The organization is never taken from a
route/query/header. F-002 replaces the "default organization" rule with an explicit switcher.

**No-organization state.** A signed-in account without a membership is a normal state, not an error:
the dashboard shows a dedicated card, the shell shows a banner on every app page, the switcher says
"No organization", and `/no-organization` explains the next step (ask an Owner, access stays
unavailable until then, sign out). Registration of a non-first account redirects there.

**Startup migration** (I-002 handoff): `Program` applies migrations when
`Database:ApplyMigrationsOnStartup` is true (default in Development, false otherwise), inside an async
scope, before serving requests. Production keeps the explicit `dotnet ef database update` step.

**Test fixture.** `TwoOrgsFixture` now seeds three real Identity users (`alice`, `bob`, `carol`, with
SecurityStamp/ConcurrencyStamp and emails) so the new FK is satisfiable; ids stay the readable strings
the existing probes use, so **all 34 pre-existing tests keep their assertions unchanged** and stay green.
No test was skipped or weakened.

**Counts.** 34 → **70 tests**, 0 skipped (9 HTTP flow cases, 4 bootstrap/transaction cases, 17 return-url sanitizer payloads, 1 host-configuration case, plus the pre-existing I-002/Q-001 suite).

Handoffs:

- **F-002** replaces `UserOrganizationDirectory.FindDefaultForUserAsync`'s "default" rule and
  `TenantResolutionMiddleware` with the selected-organization switch; `UserSession` is the seam the
  shell already uses. Invitations are the only way to add the missing membership, and the
  `/no-organization` copy points there.
- **F-003 hazard:** the circuit tenant is established by `MainLayout.razor` resolving `UserSession`, not
  by the HTTP middleware (interactive circuits get a new DI scope). A page whose own
  `OnInitializedAsync` reads tenant data can therefore run before the shell and see
  `OrganizationId = null` (fail-closed, no leak, but an empty page). F-003 pages must resolve
  `UserSession.GetAsync()` first or read through a service that does; do not rely on ordering against a
  layout.
- **Production migration path:** the startup migration is Development-only
  (`Database:ApplyMigrationsOnStartup`); production runs `dotnet ef database update --project src/Server`
  once before starting. No live PostgreSQL instance exists in this environment (same gap as I-002), so
  the PostgreSQL path is schema/script parity only, not a live apply.
- A future "remember me" option only needs `PasswordSignInAsync(isPersistent: true)`; nothing in the
  cookie policy blocks it.

### 2026-11-12 — review round 1 (FAIL -> fixes)

**B1 (blocking) — the "concurrent" registration test never reached the loser branch, and the card
claimed that branch as verified.** Fixed:
- `BootstrapTests.Bootstrap_guard_lets_exactly_one_of_two_concurrent_attempts_create_the_organization`:
  two scopes/connections, both parked on an async gate immediately before
  `TryCreateOwnerOrganizationAsync`, then racing; asserts exactly one `Created`, one
  `AlreadyCompleted`, and `users=2 organizations=1 memberships=1 owner_memberships=1 markers=1` with
  no exception. (On SQLite two *explicit* transactions serialise at `BEGIN`, so the race is forced at
  the insert instead of at transaction start; the unique-constraint branch is the same.)
- `RegistrationTransactionTests.Registration_rolls_back_the_account_when_the_bootstrap_write_fails`:
  `IBootstrapService` stub throws after the account insert → the transaction rolls the account back
  (`users=0 organizations=0 memberships=0 markers=0`). Red proof: committing the transaction right
  after `UserManager.CreateAsync` makes it fail with `Expected: 0 / Actual: 1`.
- `RegistrationTransactionTests.Registration_falls_back_to_an_account_without_organization_when_the_bootstrap_loses_the_race`:
  stub returns `AlreadyCompleted` → the first account insert is rolled back, the account is re-created
  without an organization, and the result is a successful `AlreadyCompleted` registration.
- **Why the fallback cannot be reached end-to-end on SQLite:** the registration transaction begins
  with `BEGIN IMMEDIATE`, writes the account, and only then attempts the marker, so a competing writer
  cannot commit between the pre-check and the insert; the second request simply sees the marker. The
  unique-violation → rollback → re-create path is a PostgreSQL-concurrency behaviour, now exercised
  through the seam and explicitly *not* claimed as an SQLite end-to-end proof. Red proof for the guard:
  giving the marker a random id makes the guard test report `Expected: 1 / Actual: 2`.

**Cheap items, all done:**
1. **Upgrade path with data.** The observed `dotnet ef database update` gate covered *empty* databases
   only. A pre-F-001 dev database that already has `OrganizationMembers` rows without matching users
   fails when the migration rebuilds the table and adds the FK (`SQLite Error 19: FOREIGN KEY
   constraint failed`; the rebuild rolls back and is retryable). Recovery: delete the dev database
   (`src/Server/App_Data/zenkins.db`) or delete the orphan membership rows, then run the update.
   The migration was not regenerated (the rebuild is the provider's normal way to add a FK).
2. **Forwarded headers are actually bound now.** `Program.cs` reads
   `ForwardedHeaders:KnownProxies:<n>` (IPs) and `ForwardedHeaders:KnownNetworks:<n>` (CIDR) and adds
   them to `ForwardedHeadersOptions.KnownProxies`/`KnownIPNetworks`; loopback stays trusted by default.
   `HostConfigurationTests.Forwarded_proxy_allowlist_is_bound_from_configuration` pins both keys. The
   verified property is unchanged: a non-loopback client's `X-Forwarded-Proto` is ignored, so it
   cannot change the cookie's `Secure` flag.
3. **Registration enumerates accounts (accepted v0.1 property).** A duplicate email produces
   `Username '...' is already taken.` (open registration needs to tell the user the address exists);
   login deliberately stays generic ("Incorrect email or password.") so it does not confirm accounts.
4. **Logout does not revoke tickets (accepted v0.1 limitation).** `SignOutAsync` expires the three
   cookies, but there is no `UpdateSecurityStampAsync`, so a captured cookie remains usable until it
   expires (8h sliding). Future fix: bump the security stamp on sign-out (plus revalidation) or move to
   a per-user session store; no revocation path exists in v0.1.
5. **`ReturnUrlSanitizer` payload theory.** `ReturnUrlSanitizerTests` covers `//evil.example`,
   `https://evil.com`, `/\evil.com`, `%2f%2fevil.com`, `/%2f%2fevil.com`, `///evil.com`,
   `..%2f..%2f`, `http:/evil.com`, `//evil.com/%2e%2e`, and CR/LF payloads; each is resolved against
   the login URL and must keep scheme + authority. The sanitizer now also rejects control characters
   (header-injection defence), not just protocol-relative forms.

## Known gaps (from the review)

- **Malformed forwarded-header config fails startup** with `ArgumentOutOfRangeException (prefixLength)`
  for a bad CIDR (`ForwardedHeaders:KnownNetworks:0=10.0.0.0/99`); a bare IP in `KnownProxies` or a scalar
  (non-`:0`) value is silently ignored. Fail-safe but inconsistent — add range checks with an error
  naming the config key before this reaches an operator.
- **PostgreSQL end-to-end is unverified** (same gap I-002 records): the migration is Npgsql-generated and
  has no pending model changes, but no registration/bootstrap run has happened on a live PG instance.
- **Logout does not revoke tickets** (see the accepted-limitation entry above) and registration enumerates
  accounts (open registration); both are recorded, not fixed.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Auth/**`, `src/Server/Components/Pages/Account/**`,
  `src/Server/Components/{App,Routes}.razor`, `src/Server/Components/Layout/{MainLayout,AccountLayout,RedirectToLogin}.razor`,
  `src/Server/Components/Pages/{Home,Projects,Secrets,Workers,NoOrganization}.razor`,
  `src/Server/Components/Shared/NoOrganizationNotice.razor`, `src/Server/Data/**` (User, BootstrapMarker,
  context, options, migration), `src/Server/Program.cs`, `src/Server/wwwroot/app.css`, `tests/Zenkins.Tests/Auth/**`, `tests/Zenkins.Tests/Tenancy/TwoOrgsFixture.cs`,
  `Directory.Packages.props`, `src/Server/Zenkins.Server.csproj`
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test`
  3. `dotnet ef migrations list --project src/Server`
  4. `ConnectionStrings__Default="Data Source=/tmp/zenkins-f001-gate.db" dotnet ef database update --project src/Server`
     then `sqlite3 /tmp/zenkins-f001-gate.db ".schema OrganizationMembers"` and delete the temp file
  5. `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
  6. Run the app on one port and drive it with curl (full script in `/tmp/orca-logs/F-001/e2e-curl.sh`):
     ```bash
     ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Default="Data Source=/tmp/f001-e2e.db" \
       ASPNETCORE_URLS=http://127.0.0.1:5297 dotnet run --project src/Server --no-launch-profile
     # GET /account/register            -> 200, extract __RequestVerificationToken + _handler=register
     # POST /account/register           -> 302 + Set-Cookie: Zenkins.Auth=…; path=/; samesite=lax; httponly
     # GET /  (cookie)                  -> 200, "Starter Organization", "Open user menu for alice@example.test"
     # GET /projects (no cookie)        -> 302 Location: …/account/login?ReturnUrl=%2Fprojects
     # POST /account/login?ReturnUrl=…  -> 302 Location: …/projects + Set-Cookie
     # GET /projects (cookie)           -> 200 "Projects · Zenkins"
     # GET /account/logout              -> 200 form; POST -> 302 + Set-Cookie: Zenkins.Auth=; expires=1970…
     # GET / (same jar)                 -> 302 to login (session gone)
     # second registration              -> 302 /no-organization; organizations stays 1, markers stays 1
     # sqlite3 … "select count(*) from AspNetUsers/Organizations/OrganizationMembers/BootstrapMarkers"
     # grep the app log for the password/hash/cookie/token
     ```
- Results (full output in `/tmp/orca-logs/F-001/`):
  - `dotnet build -c Release` -> **PASS**, 0 warnings / 0 errors -> `build.log`
  - `dotnet test` -> **PASS**, **70 passed / 0 failed / 0 skipped** (34 before this card) -> `test.log`
  - `dotnet ef migrations list` -> **PASS**, `20261112051428_Initial` and
    `20261112070211_AddIdentityAndBootstrap` listed -> `ef-migrations-list.log`
  - `dotnet ef database update` (SQLite, empty temp file) -> **PASS**
    (`Applying migration 'AddIdentityAndBootstrap'`); schema has `AspNetUsers`, `BootstrapMarkers`,
    `FK_OrganizationMembers_AspNetUsers_UserId`, unique `(OrganizationId, UserId)`; temp file deleted
    -> `ef-database-update-sqlite.log`, `sqlite-tables.log`
  - upgrade path (`database update Initial` then latest) -> **PASS**, migration applied on top of the
    existing table; the rebuilt table keeps both FKs and the check constraint ->
    `ef-upgrade-from-initial.log`. **Limitation:** this gate used an empty database; a pre-F-001 dev
    database with membership rows and no users fails the FK step (SQLite Error 19) and needs the
    recovery described in review round 1, item 1.
  - `dotnet ef migrations has-pending-model-changes` (Postgres) -> **PASS**, no pending changes ->
    `ef-pending-model-changes-postgres.log`
  - curl end-to-end -> **PASS**, every step above plus 16 `PASS` lines; observed:
    `Set-Cookie: Zenkins.Auth=<redacted>; path=/; samesite=lax; httponly`,
    `Location: …/account/login?ReturnUrl=%2Fprojects`,
    `Location: …/projects`, `Set-Cookie: Zenkins.Auth=; expires=Thu, 01 Jan 1970`,
    counts `users=2 organizations=1 memberships=1 owner_memberships=1 bootstrap_markers=1`,
    `Secure` present only when `X-Forwarded-Proto: https`, and 0 secret hits in the app log ->
    `e2e-curl.log`, `e2e-app.log`
  - guard red proof: the marker id in `BootstrapService` (`SingletonId` use site, not the entity default) changed to a fresh GUID (uniqueness disabled) ->
    `Bootstrap_guard_lets_exactly_one_of_two_concurrent_attempts_create_the_organization` **FAILS**,
    `Expected: 1 / Actual: 2` -> `red-proof-bootstrap-guard.log`; tree reverted immediately.
  - atomicity red proof: `transaction.CommitAsync` inserted right after `UserManager.CreateAsync` ->
    `Registration_rolls_back_the_account_when_the_bootstrap_write_fails` **FAILS**,
    `Expected: 0 / Actual: 1` -> `red-proof-atomicity.log`; tree reverted immediately.
  - `dotnet test --filter Category=Auth` -> **PASS**, 36 passed / 0 failed / 0 skipped (includes the
    guard test, the two `IBootstrapService`-seam tests, 17 `ReturnUrlSanitizer` payloads and the
    forwarded-header binding test) -> `auth-tests.log`.
