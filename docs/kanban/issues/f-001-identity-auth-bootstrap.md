---
id: F-001
title: Identity auth + first-run Owner bootstrap
type: feature
priority: P1
size: M
status: review
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
  - `BootstrapMarker` with a fixed primary key (`"bootstrap"`), inserted in the same `SaveChanges` as the organization and Owner membership, inside the registration transaction, under `ISystemDataScope` with an explicit `OrganizationId`.
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
bootstrap_markers=1, users=2`. The concurrent variant
(`Concurrent_first_registrations_create_exactly_one_organization`) starts two first registrations in
parallel and still ends with exactly one Organization, one Owner membership and one marker.
`Bootstrap_stays_closed_after_the_first_account_is_deleted` also proves deleting the first account
cannot re-open the bootstrap: the marker has no foreign key to the user, so it survives while the
membership cascades away.

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
the constraint is the guard. Deliberate decision recorded in TC-002: registration stays open so invited
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

**Counts.** 34 → **50 tests**, 0 skipped (16 new: 9 HTTP flow cases + 7 bootstrap/data-layer cases).

Handoffs:

- **F-002** replaces `UserOrganizationDirectory.FindDefaultForUserAsync`'s "default" rule and
  `TenantResolutionMiddleware` with the selected-organization switch; `UserSession` is the seam the
  shell already uses. Invitations are the only way to add the missing membership, and the
  `/no-organization` copy points there.
- **F-003** can attach real data reads to the tenant that `TenantResolutionMiddleware` / `UserSession`
  select; components must still read through `IDbContextFactory`, never an injected `ZenkinsDbContext`.
- A future "remember me" option only needs `PasswordSignInAsync(isPersistent: true)`; nothing in the
  cookie policy blocks it.

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
  - `dotnet test` -> **PASS**, **50 passed / 0 failed / 0 skipped** (34 before this card) -> `test.log`
  - `dotnet ef migrations list` -> **PASS**, `20261112051428_Initial` and
    `20261112070211_AddIdentityAndBootstrap` listed -> `ef-migrations-list.log`
  - `dotnet ef database update` (SQLite, empty temp file) -> **PASS**
    (`Applying migration 'AddIdentityAndBootstrap'`); schema has `AspNetUsers`, `BootstrapMarkers`,
    `FK_OrganizationMembers_AspNetUsers_UserId`, unique `(OrganizationId, UserId)`; temp file deleted
    -> `ef-database-update-sqlite.log`, `sqlite-tables.log`
  - upgrade path (`database update Initial` then latest) -> **PASS**, migration applied on top of the
    existing table; the rebuilt table keeps both FKs and the check constraint ->
    `ef-upgrade-from-initial.log`
  - `dotnet ef migrations has-pending-model-changes` (Postgres) -> **PASS**, no pending changes ->
    `ef-pending-model-changes-postgres.log`
  - curl end-to-end -> **PASS**, every step above plus 12 `PASS` lines; observed:
    `Set-Cookie: Zenkins.Auth=<redacted>; path=/; samesite=lax; httponly`,
    `Location: …/account/login?ReturnUrl=%2Fprojects`,
    `Location: …/projects`, `Set-Cookie: Zenkins.Auth=; expires=Thu, 01 Jan 1970`,
    counts `users=2 organizations=1 memberships=1 owner_memberships=1 bootstrap_markers=1`,
    `Secure` present only when `X-Forwarded-Proto: https`, and 0 secret hits in the app log ->
    `e2e-curl.log`, `e2e-app.log`
  - concurrency stability: `dotnet test --filter "FullyQualifiedName~Concurrent_first_registrations"`
    run three times -> 1/1 passed each time.
