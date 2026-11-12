---
id: F-002
title: Organization CRUD, members, switcher
type: feature
priority: P1
size: M
status: review
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

A user can hold memberships in several Organizations, switch the active one, and an Owner can rename the Organization, invite members, change roles, and remove them — with every screen and query scoped to the active Organization.

## Background

The switcher is where tenancy stops being abstract: the active Organization must be re-established from the *server-side* membership list on every request, so switching cannot be forged by editing a cookie or URL. Selection is therefore stored server-side (session or a user preference row) and validated against `OrganizationMember` on each resolution.

Role rules (PRD §3.1.2): Owner-only for Organization settings, member management, Worker management, Secret management, Project management. Member can view projects, edit pipelines, trigger runs, view logs, download artifacts.

Invitation email delivery is out of scope for v0.1 — an invite may be represented by a pending membership the Owner creates for an existing user account.

## Acceptance Criteria

- [x] Switcher lists only Organizations the current user belongs to; switching changes the active tenant for all subsequent queries.
- [x] A forged/switched-to-Organization the user does not belong to is rejected server-side and falls back to a valid membership.
- [x] Owner can rename the Organization, change another member's role, and remove a member; the last Owner cannot be removed or demoted.
- [x] Member sees no management controls and management endpoints reject them regardless of UI.
- [x] Members list shows name, email, role, joined date.
- [x] `Q-001` probes extended to cover membership-based authorization.

## Subtasks

- [x] Server-side active-organization resolution as the source for `ITenantContext`.
- [x] Switcher component in the shell header.
- [x] Organization settings page (rename) with Owner guard.
- [x] Members page: list, invite existing user, change role, remove.
- [x] Last-owner invariant on role change and removal.
- [x] Extend tenant isolation tests with a Member-role probe.

## Dependencies

- F-001
- Q-001

## Test Cases

### TC-001: Switcher is membership-only

Steps:
1. alice belongs to A and B; carol belongs to A only.
2. As carol, request the switch-to-B action.

Expected:
- Rejected; active Organization remains A.

### TC-002: Last owner protected

Steps:
1. Organization with a single Owner. Attempt to demote or remove them.

Expected:
- Both refused with a clear message.

### TC-003: Member cannot manage

Steps:
1. As a Member, POST the rename and remove-member endpoints directly.

Expected:
- 403; no state change.

### TC-004: Switch changes visible data

Steps:
1. Project in A only. Switch to B, open Projects.

Expected:
- Empty list for B; switching back shows it.

## Development Log

### 2026-11-12 — implemented

**Server-side active organization.** The selection is persisted as `User.ActiveOrganizationId` (nullable
`uuid`, FK to `Organizations` with `ON DELETE SET NULL`, indexed) in migration
`20261112080443_AddActiveOrganization`, generated against PostgreSQL (the I-002 convention) and verified
to apply on SQLite. It is *not* a cookie or URL value: no request can name its own tenant.

Resolution order, implemented once in `UserOrganizationDirectory.ResolveAsync` and used by both the HTTP
pipeline and the interactive circuit:

1. the persisted `ActiveOrganizationId`, **but only if** the user still has an `OrganizationMember` row
   for it;
2. otherwise a deterministic valid membership of theirs (Owner first, then earliest membership, then
   organization id), which is written back so the selection is stable;
3. otherwise no organization (and any stale persisted value is cleared).

`OrganizationMember` is tenant-filtered and no tenant exists while the selection is being resolved, so
this path deliberately runs inside `ISystemDataScope` (no `IgnoreQueryFilters` was added) and reads only
the caller's own membership rows; users are read by primary key or by the tenant's membership ids, never
enumerated. `TenantResolutionMiddleware` sets `ITenantContext` from the resolved value before the page
renders; `UserSession.GetAsync()` does the same for each circuit (resolving it also selects the circuit
tenant, which is why the organization/members/projects pages call it before reading tenant data - the
F-003 hazard). `SwitchAsync` is the only writer of the selection and writes it only after finding a
matching membership; a forged or stale target returns `null` and the old selection is untouched.

**Switcher.** The D-001 placeholder in the shell header is now a real switcher: it offers exactly
`UserSessionInfo.Organizations` (the memberships resolved above), marks the active one, and each item is a
plain HTML `<form method="post" action="/organizations/switch">` with the target id in a hidden field. The
endpoint re-validates membership server-side: a non-member switch is `403` and leaves the active
organization unchanged. A visually hidden summary of the membership names makes the list available to
assistive technology (and to HTTP tests, because the dropdown content only exists while open).

**Organization settings page** (`/organizations/settings`): Owner-only rename form. Members get an
"Owner access required" card and no form. The POST is guarded by the same server-side Owner check.

**Members page** (`/organizations/members`): list of name (`UserName`; v0.1 has no separate display name),
email, role and joined date; Owner-only invite (existing account by exact normalized email; no mail is
sent in v0.1), role change and removal. A Member sees the list but none of the management forms. Every
management method calls `IsOwnerAsync`, which reads the tenant-filtered `OrganizationMembers` table, so it
cannot be satisfied by ownership of another organization; hidden controls are never the enforcement.

**Management endpoints.** `/organizations/settings`, `/organizations/members/invite`,
`/organizations/members/role`, `/organizations/members/remove`, `/organizations/switch`. They are
form-bound minimal APIs, authenticated, and automatically antiforgery-validated by `UseAntiforgery` (a
POST without a token is `400`, pinned by a test). Outcomes: `403` for a non-Owner or a non-member switch,
`404` for a membership id outside the active tenant, `303/302`+`?notice=` on success, and PRG with
`?error=` for validation/last-owner failures (the page renders the message). The antiforgery request token
is rendered once in `App.razor`'s `<head>` (static SSR has `HttpContext`) and `wwwroot/js/antiforgery.js`
copies it into `form[data-zenkins-antiforgery]` on submit; an automated client can read the same token
from the meta tag (the curl E2E does).

**Last-owner invariant.** `ChangeRoleAsync` and `RemoveMemberAsync` open an explicit transaction, look the
member up through the tenant filter, and when the target is an Owner count the remaining Owners inside the
same transaction; `<= 1` rolls back with `error=last-owner` ("The last Owner cannot be demoted or removed.
Make another member an Owner first.") and no state change. Removal also clears the removed user's
`ActiveOrganizationId` when it pointed here. Known gap: no row lock is taken, so two simultaneous Owner
demotions by two different Owners could interleave on PostgreSQL; recorded rather than claimed solved.

**Projects page.** It now renders the active organization's projects read-only (name, repository, branch,
pipeline path) so TC-004 can be observed and the switch is visibly tenant-scoped. Project create/edit/
delete remains F-003's scope; no F-003 code was added.

**Tests.** The 70 pre-existing tests are unchanged and green; 15 were added: 9 authenticated HTTP tests
(`OrganizationEndpointTests`) and 6 data-layer resolution tests (`ActiveOrganizationResolutionTests`).
Q-001's harness is extended by `MembershipAuthorizationProbes`, which drives the real endpoints with
cookies and antiforgery tokens: Member cannot manage (403s, no state change, no controls), non-member
cannot switch (403, selection unchanged), and switching between two memberships changes the rendered
project data. No test was adapted, weakened or skipped.

Known gaps recorded, not fixed: no invitation email or "pending" membership state (the membership is
created immediately for an existing account); the browser forms need `antiforgery.js` (server-side API
clients use the meta token); no display name exists yet, so the members-list "Name" column shows the
account name (registrations set it to the email); PostgreSQL end-to-end is still unverified (no live PG
instance), as in I-002/F-001.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Server/Organizations/**` (`OrganizationEndpoints`, `OrganizationManagementService`,
    `OrganizationServiceCollectionExtensions`)
  - `src/Server/Auth/UserOrganizationDirectory.cs`, `UserSession.cs`, `TenantResolutionMiddleware.cs`
  - `src/Server/Components/Pages/Organizations/{OrganizationSettings,OrganizationMembers}.razor`,
    `src/Server/Components/Pages/Projects.razor`, `src/Server/Components/Layout/{MainLayout,NavMenu}.razor`,
    `src/Server/Components/App.razor`, `src/Server/wwwroot/js/antiforgery.js`
  - `src/Server/Data/Entities/User.cs`, `src/Server/Data/ZenkinsDbContext.cs`,
    `src/Server/Data/Migrations/20261112080443_AddActiveOrganization*`,
    `src/Server/Data/Migrations/ZenkinsDbContextModelSnapshot.cs`, `src/Server/Program.cs`
  - `tests/Zenkins.Tests/Organizations/**`, `tests/Zenkins.Tests/Tenancy/ActiveOrganizationResolutionTests.cs`
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test`
  3. `rm -f /tmp/zenkins-f002-migrate.db` then
     `ConnectionStrings__Default="Data Source=/tmp/zenkins-f002-migrate.db" dotnet ef database update --project src/Server`
     then check `.schema AspNetUsers` and delete the file
  4. `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
  5. run the app on one port with an empty temp DB and drive it with curl (full script in
     `/tmp/orca-logs/F-002/e2e-curl.sh`):
     ```bash
     ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Default="Data Source=/tmp/zenkins-f002-e2e.db" \
       ASPNETCORE_URLS=http://127.0.0.1:5299 dotnet run --project src/Server --no-launch-profile
     # register alice (owner bootstrap); GET / -> "Starter Organization"
     # POST /organizations/settings Name="Acme Corp" -> 302 ?notice=renamed; DB name changed
     # POST /organizations/members/role (alice -> Member) -> 302 ?error=last-owner; role still Owner
     # POST /organizations/members/remove (alice)        -> 302 ?error=last-owner; membership survives
     # register bob; POST /organizations/members/invite Email=bob@... -> 302 ?notice=invited; list shows bob + date
     # POST /organizations/members/role (bob -> Owner)    -> 302 ?notice=role-changed; DB role Owner
     # register carol; invite; as carol: GET /organizations/members has no manage forms,
     #   POST settings/remove/role -> 403, name/role/membership unchanged
     # seed Bravo + alice's membership + alpha-app/beta-app; POST /organizations/switch Bravo -> 302 /projects
     #   GET /projects -> beta-app only; switch back -> alpha-app only
     # as carol (non-member) POST /organizations/switch Bravo -> 403; ActiveOrganizationId unchanged
     ```
- Results (full output in `/tmp/orca-logs/F-002/`):
  - `dotnet build -c Release` -> **PASS**, 0 warnings / 0 errors -> `build-final3.log`
  - `dotnet test` -> **PASS**, **85 passed / 0 failed / 0 skipped** (70 before this card; 15 added) ->
    `test-final.log`
  - `dotnet ef database update` on an empty temp SQLite file -> **PASS**, applied
    `20261112080443_AddActiveOrganization`; schema has `ActiveOrganizationId uuid NULL`,
    `IX_AspNetUsers_ActiveOrganizationId`, `FK_AspNetUsers_Organizations_ActiveOrganizationId ... ON DELETE SET NULL`;
    temp file deleted -> `ef-database-update-sqlite.log`, `sqlite-schema.log`; the same migration also
    applies on top of a populated F-001 database (legacy organization, user and membership preserved,
    `ActiveOrganizationId` null until first resolution) -> `ef-upgrade-f001-state.log`
  - `dotnet ef migrations has-pending-model-changes` (Postgres) -> **PASS**, no pending changes ->
    `ef-pending-model-changes-postgres.log`
  - curl E2E -> **PASS**, 47/47 checks -> `e2e-curl.log`; observed `302` on register/rename/invite/role/switch,
    `403` on every Member management POST and on the non-member switch, last-owner attempts redirected to
    `?error=last-owner` with the role/membership unchanged, and the Projects page showing `alpha-app` vs
    `beta-app` depending on the active organization; app log contains no password/antiforgery token/auth
    cookie (`e2e-app.log`)
  - red proof, last-owner guard: both `ownerCount <= 1` checks disabled in a scratch copy under `/tmp` ->
    `Last_owner_cannot_be_demoted_or_removed_but_the_guard_is_conditional` **FAILS**
    (`Expected: "/organizations/members?error=last-owner" / Actual: "...?notice=role-changed"`) ->
    `red-proof-last-owner.log`; guard restored -> **PASS** -> `red-proof-last-owner-restored.log`
  - red proof, Member server-side rejection: `IsOwnerAsync` reduced to "is a member" in the same scratch
    copy -> `Member_cannot_manage_and_sees_no_management_controls` **FAILS**
    (`Expected: Forbidden / Actual: Found`) -> `red-proof-member-guard.log`; guard restored -> **PASS** ->
    `red-proof-member-guard-restored.log`; scratch copy deleted.
