---
id: F-002
title: Organization CRUD, members, switcher
type: feature
priority: P1
size: M
status: done
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
- [x] Owner can rename the Organization, change another member's role, and remove a member; the last Owner cannot be removed or demoted. *Atomic on PostgreSQL by construction: an organization-row `FOR UPDATE` lock precedes the Owner count in both mutation paths; not end-to-end verified on a live PG instance (none here), while SQLite serializes writers.*
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
`ActiveOrganizationId` when it pointed here. The organization row is locked (`FOR UPDATE` under
PostgreSQL) before the count, so the guard is atomic on PG; review round 2 records that this is
fixed-by-construction, not executed against a live PG instance.

**Projects page.** It now renders the active organization's projects read-only (name, repository, branch,
pipeline path) so TC-004 can be observed and the switch is visibly tenant-scoped. Project create/edit/
delete remains F-003's scope; no F-003 code was added.

**Tests.** The 70 pre-existing tests are unchanged and green; 15 were added in this round (9 authenticated
HTTP tests in `OrganizationEndpointTests` and 6 data-layer resolution tests in
`ActiveOrganizationResolutionTests`; review round 2 adds three more, for 88 total).
Q-001's harness is extended by `MembershipAuthorizationProbes`, which drives the real endpoints with
cookies and antiforgery tokens: Member cannot manage (403s, no state change, no controls), non-member
cannot switch (403, selection unchanged), and switching between two memberships changes the rendered
project data. No test was adapted, weakened or skipped.

Known gaps recorded, not fixed: no invitation email or "pending" membership state (the membership is
created immediately for an existing account); the browser forms need `antiforgery.js` (server-side API
clients use the meta token); no display name exists yet, so the members-list "Name" column shows the
account name (registrations set it to the email); PostgreSQL end-to-end is still unverified (no live PG
instance), as in I-002/F-001.

### 2026-11-12 — review round 2 (B1 + notes)

**B1 (blocking) — the last-owner guard is now serialized.** `IOrganizationRowLock` /
`OrganizationRowLock` takes `SELECT "Id" AS "Value" FROM "Organizations" WHERE "Id" = @id FOR UPDATE`
inside the existing mutation transaction (no `#if`; `Database.IsNpgsql()` decides) before either
`ChangeRoleAsync` or `RemoveMemberAsync` counts Owners. Under PostgreSQL READ COMMITTED two concurrent
demotions can no longer both observe two Owners: the second waits on the organization row lock and
recounts after the first commits, so it either refuses (`last-owner`) or proceeds against a genuinely
two-Owner organization. SQLite is a deliberate no-op because it serializes writers at the database
level - which is why the reviewer's SQLite probe already held. Tests:
`OrganizationRowLockTests.Lock_query_is_a_for_update_on_the_organization_row_under_npgsql` asserts the
executed statement's shape under Npgsql, and
`...Role_change_and_removal_both_take_the_organization_lock` records both mutation paths with a fake
lock, failing if either path drops the call. Red proof: with both `LockAsync` calls removed in a /tmp
copy the wiring test FAILS (`Expected [A, A] / Actual []`) -> `red-proof-lock.log`; restored -> PASS
-> `red-proof-lock-restored.log`. **Caveat:** the PG race is fixed by construction, not executed
end-to-end (no PostgreSQL instance in this environment), the same gap I-002/F-001 record. Both
mutations now open their transaction with `IsolationLevel.ReadCommitted` explicitly, so a connection
string default such as `Default Isolation Level=RepeatableRead` cannot restore the pre-wait snapshot
and reopen B1: under READ COMMITTED the counting statement after the lock wait sees the first
committer's Owner changes.

**Authorization before validation.** `Rename` authorizes before validating the name, `Invite`
authorizes before validating the email, and `ChangeRole`/`RemoveMember` now take the raw form strings
and parse id/role only after `IsOwnerAsync`. A Member therefore gets 403 for malformed
ids/roles/emails/names instead of a `?error=` validation redirect. The endpoints no longer pre-parse;
`MembershipAuthorizationProbes` adds four malformed Member POSTs and the curl E2E repeats them.

**Circuit tenant cache removed.** `UserSession.GetAsync` resolves on every call instead of memoising
the first result. A membership revoked while a tab is open stops granting that organization on the
next page render/navigation (the circuit's `TenantContext` revision changes), without a full browser
reload. Price: the resolution query set (two SELECTs plus at most one fallback UPDATE) per page render
that asks for the session. Pinned by
`UserSession_re_resolves_so_a_revoked_member_loses_read_access_without_a_reload`.

**Housekeeping.** Removed the untracked literal-backslash directory `C:\tmp\orca-logs\F-001-review`
(never tracked), dead `MembershipAuthorizationProbes.HasFormAction`, the stale
`ActiveOrganizationResolver` doc reference in `User.cs`, and split the misleading
`Switching_to_a_valid_membership_persists_the_selection` test: its rejection half already existed as
`Switching_to_an_organization_the_user_does_not_belong_to_is_rejected`.

No schema change: the migration set is unchanged, and `dotnet ef database update` was re-run on both
empty and populated databases.

Known gaps recorded for v0.1 (accepted, not implemented): invite by exact email is a global
account-existence oracle for any Owner (cross-tenant signal); mutating forms depend on
`wwwroot/js/antiforgery.js` and return 400 with JavaScript blocked (`AntiforgeryToken` is not usable
in an interactive circuit); `SystemDataScope` is a counter on one scoped instance and is now
entered from inside a circuit where an await can interleave with another render - reads stay
tenant-filtered and no tenant writes originate in a circuit, but "one scope, one request" no longer
holds for circuits; resolution writes on read (idempotent fallback repair, 2 queries + at most 1
UPDATE per request).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Server/Organizations/**` (`OrganizationEndpoints`, `OrganizationManagementService`,
    `OrganizationRowLock`, `OrganizationServiceCollectionExtensions`)
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
  - `dotnet build -c Release` -> **PASS**, 0 warnings / 0 errors -> `build-final-isolation.log`
  - `dotnet test` -> **PASS**, **88 passed / 0 failed / 0 skipped** (70 pre-card; 18 added, including
    the two row-lock tests) -> `test-final-isolation.log`
  - `dotnet ef database update` on an empty temp SQLite file -> **PASS**, all three migrations applied,
    `ActiveOrganizationId`, its index and the `ON DELETE SET NULL` FK present; temp deleted ->
    `ef-database-update-empty-review.log`
  - `dotnet ef database update` on a populated F-001 database -> **PASS**, legacy organization/user/
    membership preserved, `ActiveOrganizationId` null; no new migration was needed ->
    `ef-database-update-populated-review.log`
  - `dotnet ef migrations has-pending-model-changes` (Postgres) -> **PASS**, no pending changes ->
    `ef-pending-model-changes-postgres-review.log`
  - curl E2E -> **PASS**, 52/52 checks -> `e2e-curl-review.log`; all earlier rename/invite/role/
    last-owner/switch/403 checks still pass, plus four malformed Member POSTs (bad membership id, role,
    email, empty name) each `403` with state unchanged.
  - red proof, last-owner lock: both `LockAsync` calls removed in a /tmp copy ->
    `Role_change_and_removal_both_take_the_organization_lock` **FAILS**
    (`Expected [a0000000..., a0000000...] / Actual []`) -> `red-proof-lock.log`; restored -> **PASS** ->
    `red-proof-lock-restored.log`.
  - earlier red proofs still stand: last-owner count guard disabled -> FAIL -> `red-proof-last-owner.log`;
    Owner guard disabled -> FAIL -> `red-proof-member-guard.log`; both restored -> PASS ->
    `red-proof-*-restored.log`.
