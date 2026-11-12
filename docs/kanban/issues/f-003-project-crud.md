---
id: F-003
title: Project CRUD
type: feature
priority: P1
size: M
status: done
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

An Organization Owner or Member can create, view, edit, and delete a Project (git repository + default branch + pipeline path) and see it in a list scoped to the active Organization.

## Background

A Project is metadata only at this stage — no cloning, no runs (those are Phase 2–4). PRD §3.2 fields: Name, Repository URL, Default Branch, Credentials, Pipeline Path (default `pipeline.lua`), Created At, Updated At.

Validation worth doing now, because it is cheap now and painful later:

- Repository URL must parse as HTTPS or SSH git URL; reject `file://`, `ext::`, and other transport helpers that can execute arbitrary commands.
- Branch and pipeline path are stored as text with obvious shape checks (`..` rejected in the path, no leading `/`).
- Name unique per Organization.

Credentials are a placeholder reference in this card (`GitCredentialId` nullable, no UI); `I-00x` in Phase 4 fills it in.

## Acceptance Criteria

- [x] List, create, edit, delete Project, all scoped to the active Organization.
- [x] Name is unique within an Organization; duplicate create surfaces a field-level error.
- [x] Repository URL is validated; `file://` and `ext::` transports are rejected.
- [x] Pipeline Path defaults to `pipeline.lua` and rejects `..` / absolute paths.
- [x] Delete asks for confirmation and, for now, is a hard delete (cascade semantics revisited when BuildRuns exist).
- [x] Member can create and edit; only Owner can delete. *(Decision below, from PRD §3.1.2.)*
- [x] Direct requests against another tenant's Project id return 404, not 403.

**Owner-only delete decision.** PRD §3.1.2 gives a Member "查看项目" and "创建和编辑 Pipeline", and lists 项目管理 (project management) under the Owner. Read together, creating and editing a Project is ordinary member work, while deleting one is management. Deletion is therefore Owner-only, enforced in `ProjectService.DeleteAsync`; the confirmation page hides the form from a Member, but a direct Member POST is a 403 and the UI is not the enforcement.

## Subtasks

- [x] Project create/edit form + list page.
- [x] URL + path validators with unit tests.
- [x] Per-Organization unique name (DB index + friendly error).
- [x] Delete flow with confirmation.
- [x] Replace Q-001's test-only `/__probe/projects` + `X-Test-Organization` with the real authenticated endpoint: a request authenticated as A, with B's project id in route/query/form, must return 404 and never B's data.

## Dependencies

- F-002
- D-001

## Test Cases

### TC-001: Create and appear in list

Steps:
1. Active Organization A. Create Project "game-client" with a valid repo URL.

Expected:
- Row appears with defaults (`pipeline.lua`).

Result: **PASS** — `ProjectCrudTests.Create_shows_in_the_list_with_the_pipeline_path_default`, plus the curl E2E `create project redirects …`, `detail shows pipeline.lua default`, `list shows game-client`. The stored row is read back through the system scope: `PipelinePath == "pipeline.lua"`, `GitCredentialId == null`, timestamps non-default.

### TC-002: Duplicate name rejected

Steps:
1. Create the same name twice in A.

Expected:
- Field-level error; second row not created.

Result: **PASS** — `ProjectCrudTests.Duplicate_name_is_a_field_error_and_creates_no_second_row` asserts the redirect is `/projects/new?error=name-duplicate`, the page renders `id="project-name-error"` with "A project with this name already exists in this organization.", and the organization still has one row. E2E repeats it against a live Kestrel.

### TC-003: Dangerous URL rejected

Steps:
1. Submit `file:///etc/passwd`; submit `ext::sh -c touch% /tmp/pwned`.

Expected:
- Both rejected with a validation message; nothing stored, nothing executed.

Result: **PASS** — the `Dangerous_repository_urls_are_rejected_and_store_nothing` theory plus the E2E: both redirect with `error=repository-url-invalid`, the page shows "Only HTTPS or SSH git URLs are supported", the project count stays at one, and `/tmp/pwned` does not exist afterwards.

### TC-004: Cross-tenant access

Steps:
1. As Organization A, request Organization B's project detail URL.

Expected:
- 404.

Result: **PASS** — `ProjectCrudTests.Another_organizations_project_id_is_404_for_list_detail_edit_and_delete` (alice is Owner of both organizations, so active-tenant scoping is the only thing that can explain the 404s), the rewritten Q-001 probes, and the E2E for detail/edit/delete-confirm/POST edit/POST delete and a query-string id. B's row is read back unchanged.

### TC-005: Member role boundaries

Steps:
1. A Member of A creates and edits a project.
2. The same Member POSTs delete for that project.

Expected:
- Create/edit succeed; delete is 403; the row survives. The confirmation page shows no delete form.

Result: **PASS** — `ProjectCrudTests.Member_can_create_and_edit_but_only_an_owner_can_delete` and the E2E Member sequence. The Owner then deletes with the confirmation POST and the row is gone.

### TC-006: An id in the query string or form cannot select another tenant

Steps:
1. Request A's project detail with B's id in the query string, then B's detail with A's id in the query string.
2. POST an edit/delete to A's route while naming B in a form field.

Expected:
- Route id is authoritative; foreign ids are 404; the form field never redirects the operation.

Result: **PASS** — Q-001's `Query_string_cannot_select_another_tenants_project` and `Form_body_cannot_select_another_tenants_project`; the form models deliberately have no project-id field.

### TC-007: Delete asks for confirmation

Steps:
1. Open a project as an Owner and follow the delete action.
2. Submit the confirmation form.

Expected:
- A confirmation step precedes the POST; the hard delete removes only the project.

Result: **PASS** — the detail page links to `/projects/{id}/delete/confirm`, which renders the project, the irreversibility warning and a real antiforgery-protected POST form; the POST redirects to `/projects?notice=deleted`. The E2E checks both the confirmation form and the resulting row counts.

## Development Log

### 2026-11-12 — implemented

**Service and data path.** `ProjectService` owns list/detail/create/edit/delete. Every method reads the organization only from `ITenantContext`; no method accepts an organization id from the caller. Lookups use the tenant-filtered `ZenkinsDbContext` through `IDbContextFactory` (one context per operation), so a foreign id resolves to `null`/`NotFound` and is indistinguishable from a missing id. `GetProjectsAsync` orders in memory, case-insensitively by name and then by id. That is a product ordering choice for a small list, not a provider workaround.

**Form endpoints.** `/projects/create`, `/projects/{id}/edit` and `/projects/{id}/delete` are form-bound minimal APIs in `ProjectEndpoints`, authenticated and validated by `UseAntiforgery` like F-002's organization endpoints. Delete binds a small `DeleteProjectForm` even though it reads no field; without a form-bound parameter the minimal API carried no antiforgery metadata and the token was not validated (found by `Delete_without_an_antiforgery_token_is_rejected_and_changes_nothing`). The route id is the only target id; the form models have no `ProjectId`, so a forged form field cannot redirect an operation. Validation/duplicate outcomes use PRG with `?error=<code>` plus the submitted values; success redirects to a detail/list page with `?notice=`. A foreign/missing id is `404`; a Member's delete is `403` with no change.

**URL validation.** `ProjectValidation` is an allow-list, not a deny-list: after trimming, a required value of at most 2048 characters with no whitespace/control characters and no leading `-` must be either
- an absolute URI whose scheme is exactly `https` or `ssh`, with a non-empty host, a non-empty path other than `/`, and no query/fragment, or
- git's scp-like `user@host:path` form with a relative, colon-free path.

Everything else is `repository-url-invalid`, including `file://`, `ext::`, `fd::`, `git://`, `http://`, `ftp://`, `ssh+git://` and option-like values. `file:///etc/passwd` and `ext::sh -c touch% /tmp/pwned` are covered by both unit tests and the live E2E.

**Branch and pipeline path.** Branch: required, ≤200, no whitespace/control, no leading `/` or `-`, no `..`, `\`, `//`, `@{`, `~^:?*[`, no trailing `/`/`.`/`.lock`. Pipeline path: blank becomes `pipeline.lua`; ≤512; repository-relative only (no leading `/` or `\`, no drive prefix, no `..`, no `\`, no `//`, no trailing `/`, no control characters). Name: required after trim, ≤200, no control characters.

**Unique name mechanism.** The guard is a real unique index on `Projects (OrganizationId, Name)` in `ZenkinsDbContext` (migration `20261112084601_AddProjectUniqueName`, generated against PostgreSQL with `Database__Provider=Postgres`, then applied on SQLite). There is deliberately no pre-flight "does it exist" query: the database decides, including for two concurrent creates. `ProjectService` translates the `DbUpdateException` through `DatabaseErrors.IsUniqueViolation(..., "Projects")` into `ProjectErrorCodes.NameDuplicate`, and the form renders that against the Name field. The red proof below demonstrates the index is what makes TC-002 fail.

**Owner delete.** `DeleteAsync` first resolves the project through the tenant filter (foreign id → 404), then requires an Owner membership in the active organization (Member → 403). The confirmation page is a server-rendered `/projects/{id}/delete/confirm` page with the project name, irreversibility warning and the real POST form; this is a confirmation step that works without a Blazor circuit, rather than a modal dialog that needs one. The antiforgery token still comes from `wwwroot/js/antiforgery.js` (as on every plain form in the shell), so a browser with JavaScript disabled submits a 400; rendering a per-form hidden token server-side is deliberately left to a later shell card. It is a hard delete: no dependent rows exist in v0.1, and the cascade decision is explicitly deferred to the BuildRuns card.

**Pages and shell.** The list moved from `Components/Pages/Projects.razor` to `Components/Pages/Projects/Index.razor` (a `Projects` page class and a `Pages.Projects` namespace cannot share a name) and now links each row to detail. New pages: `NewProject`, `EditProject`, `ProjectDetail`, `DeleteProject`, plus the shared `ProjectFormCard`. All use the existing BlazorBlueprint shell components (`BbCard`, `BbButton`, `BbBadge`, `BbEmpty`); the editable fields use plain HTML inputs inside real forms, matching F-002, because the POST must work without a circuit. Every page resolves `UserSession` before touching project data (the F-001/F-002 circuit-tenant hazard), and no component injects `ZenkinsDbContext`.

**404 semantics.** The detail/edit/delete pages set `404` when the project is missing or foreign. `UseStatusCodePagesWithReExecute` is disabled for that request via `IStatusCodePagesFeature`, because re-executing `/not-found` in the same request fails once the component renderer owns the `NavigationManager` ("already initialized"); the HTTP response is a bodyless 404, and `NotFoundPage` is still rendered for circuit navigation. `Cross_tenant_detail_is_404_for_an_html_navigation_too` pins the browser `Accept: text/html` path.

**Q-001 probe replacement.** `TenantProbeApplicationFactory`/`TestOnlyProjectProbeMiddleware` and the `X-Test-Organization` header are gone. `AuthenticatedTenantApplicationFactory` only points the real app at the fixture's SQLite file; `TwoOrgsFixture` now gives alice/bob/carol password hashes and email user names, and the probes sign in through the product login form. `TenantIsolationHttpProbeTests` drives the real pages/endpoints for route, query, form and list shapes, plus positive controls. The malformed-id case is now a routing 404 (the real route has a `:guid` constraint) instead of the old probe's 400.

**Tests.** No existing test was weakened or skipped; the F-002 organization tests still pass, including `AssertSwitchChangesVisibleDataAsync`, which now exercises the real list. The Q-001 data-layer probes are unchanged. New tests: `ProjectValidationTests` (URL/name/branch/path allow-list), `ProjectCrudTests` (CRUD, duplicate, dangerous transports, roles, timestamps, confirmation, cross-tenant detail/edit/delete and HTML-navigation 404), and the rewritten authenticated Q-001 HTTP probes.

### 2026-11-12 — verification fixes

- The first live HTML-navigation probe of a foreign project exposed the `/not-found` re-execute failure described above; fixed and pinned by a test.
- The E2E's second organization is inserted directly because v0.1 has no create-organization endpoint. Its second-precision `CreatedAt` can sort before the starter membership's, so the script pins alice's `ActiveOrganizationId` back to Starter before the cross-tenant checks; that is test setup, not product behaviour.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| B1 | blocking | A browser 404 for a foreign project id hit `UseStatusCodePagesWithReExecute`, re-rendered `/not-found` in the same request and failed with `RemoteNavigationManager already initialized` (500) | Fixed | Set 404 and disable `IStatusCodePagesFeature` for that request; test with `Accept: text/html` added |
| B2 | blocking | The delete endpoint had no form-bound parameter, so the minimal API carried no antiforgery metadata and a token-less POST deleted the project | Fixed | Bind `DeleteProjectForm`; `Delete_without_an_antiforgery_token_is_rejected_and_changes_nothing` asserts 400 and no change |

## Verification

- Related files:
  - `src/Server/Projects/{ProjectValidation,ProjectService,ProjectEndpoints,ProjectServiceCollectionExtensions}.cs`
  - `src/Server/Components/Pages/Projects/{Index,NewProject,EditProject,ProjectDetail,DeleteProject}.razor`,
    `src/Server/Components/Shared/ProjectFormCard.razor`
  - `src/Server/Data/ZenkinsDbContext.cs`, `src/Server/Data/Migrations/20261112084601_AddProjectUniqueName*`,
    `src/Server/Data/Migrations/ZenkinsDbContextModelSnapshot.cs`, `src/Server/Program.cs`,
    `src/Server/Components/_Imports.razor`
  - `tests/Zenkins.Tests/Projects/**`, `tests/Zenkins.Tests/Tenancy/{AuthenticatedTenantApplicationFactory,TenantIsolationHttpProbeTests,TwoOrgsFixture}.cs`,
    `tests/Zenkins.Tests/Organizations/OrganizationTestHost.cs`
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test -c Release`
  3. `dotnet test -c Release --filter "Category=Projects"`
  4. `dotnet test -c Release --filter "Category=TenantIsolation"`
  5. Empty migration gate:
     `rm -f /tmp/zenkins-f003-migrate-empty-final.db*` then
     `Database__Provider=Sqlite ConnectionStrings__Default="Data Source=/tmp/zenkins-f003-migrate-empty-final.db" dotnet ef database update --project src/Server`
     then inspect `__EFMigrationsHistory` and `IX_Projects_OrganizationId_Name`, then delete the file.
  6. Populated F-002 gate: apply `20261112080443_AddActiveOrganization`, seed two organizations/two users/two memberships/two projects, then `dotnet ef database update --project src/Server`; verify the rows, users, memberships and unique index survive, then delete the file.
  7. Postgres model gate: `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
  8. `bash /tmp/orca-logs/F-003/e2e-curl.sh` (starts the real app on 5399, Development, empty `/tmp/zenkins-f003-e2e/zenkins.db`).
- Results (full output in `/tmp/orca-logs/F-003/`):
  - `dotnet build -c Release` -> **PASS**, 0 warnings / 0 errors -> `build-final.log`
  - `dotnet test -c Release` -> **PASS**, **153 passed / 0 failed / 0 skipped** (88 pre-card) -> `test-all-review-fix.log`
  - `dotnet test -c Release --filter "Category=Projects"` -> **PASS**, 67 / 0 / 0 -> `test-projects-review-fix.log`
  - `dotnet test -c Release --filter "Category=TenantIsolation"` -> **PASS**, 37 / 0 / 0 -> `test-tenant-isolation-final.log`
  - `dotnet ef database update` on an empty SQLite file -> **PASS**, all four migrations applied, `IX_Projects_OrganizationId_Name` is `CREATE UNIQUE INDEX … (OrganizationId, Name)`; temp deleted -> `ef-database-update-empty-final.log`
  - `dotnet ef database update` on a populated F-002-shaped SQLite file -> **PASS**, `AddProjectUniqueName` applied, 2 users / 2 memberships / 2 projects preserved (`Alpha:alpha-app:main`, `Beta:beta-app:develop`), unique index present; a duplicate insert then fails with `UNIQUE constraint failed: Projects.OrganizationId, Projects.Name`; temp deleted -> `ef-database-update-populated-final.log`, `ef-populated-to-f002-final.log`
  - `dotnet ef migrations has-pending-model-changes` (Postgres) -> **PASS**, "No changes have been made to the model since the last migration." -> `ef-pending-model-changes-postgres-final.log`
  - curl E2E -> **PASS**, **64/64 checks** -> `e2e-curl.log`, script `e2e-curl.sh`:
    - register alice (Owner), create `game-client` with a valid HTTPS URL; list + detail show it with `pipeline.lua`
    - duplicate name -> `error=name-duplicate`, Name field error, still one row
    - `file:///etc/passwd` and `ext::sh -c touch% /tmp/pwned` -> `error=repository-url-invalid`, validation message, no row, `/tmp/pwned` absent
    - edit all fields -> `notice=updated`, DB `DefaultBranch=develop`
    - register/invite bob; bob creates and edits; bob delete -> 403 with the row intact and no delete form on the confirmation page
    - alice deletes with the confirmation POST -> `notice=deleted`
    - second organization/project (Bravo/`beta-app`) inserted directly; alice active Starter: list has no `beta-app`, detail/edit/delete-confirm/query/POST edit/POST delete all 404, Bravo row unchanged; bob (Member of A) gets 404 too
    - switch alice to Bravo: list shows only `beta-app`, Starter's project becomes 404
  - red proof, unique index: in `/tmp/f003-red-unique` the migration's `unique: true` was changed to `unique: false`; `Duplicate_name_is_a_field_error_and_creates_no_second_row` **FAILS** (`Assert.StartsWith`: actual `/projects/<id>?notice=created`) -> `red-proof-unique-index.log`; restored -> **PASS** -> `red-proof-unique-index-restored.log`
  - red proof, Owner-only delete: in `/tmp/f003-red-delete` the `IsOwnerAsync` guard was removed; `Member_can_create_and_edit_but_only_an_owner_can_delete` **FAILS** (`Expected: Forbidden / Actual: Found`, the Member's delete succeeded) -> `red-proof-owner-delete.log`; restored -> **PASS** -> `red-proof-owner-delete-restored.log`

### 2026-11-12 — review fixes

- **No-JS wording (item 1).** Chose documentation over a per-form token: the confirmation page and the project forms work **without a Blazor circuit**, not without JavaScript. The antiforgery token is still copied by `wwwroot/js/antiforgery.js`, so JS-disabled submits are 400. The card now states that explicitly; a server-rendered per-form hidden token is recorded as a shell follow-up rather than added here.
- **Sort rationale (item 2).** `ProjectService.GetProjectsAsync` and the card now say what the code does: in-memory case-insensitive name ordering (then id) for a small list, not a `DateTimeOffset` provider workaround.
- **Delete confirmation (item 3).** Dropped the unused `DeleteProjectForm.Confirmed` field and its hidden input; the confirmation page is the confirmation, and the only required payload is the antiforgery token. `Owner_delete_requires_only_the_antiforgery_token` pins the chosen behaviour (token-only POST -> `notice=deleted`), while `Delete_without_an_antiforgery_token_is_rejected_and_changes_nothing` pins the 400.

## Known gaps / follow-ups

- **Project 404 bodies are empty on the wire.** The status is correct and no data leaks, but `UseStatusCodePagesWithReExecute` cannot safely re-render `/not-found` in the same component request, so a browser navigating to a foreign/missing project id gets a blank body (the circuit still renders `NotFoundPage`). A later shell card can add a middleware-level HTML 404 if a friendly browser body is required.
- Delete cascade semantics are deliberately undefined until BuildRuns/Artifacts exist; today the delete is a single-row hard delete.
- Name uniqueness is case-sensitive (`Game-Client` and `game-client` can coexist). If product wants case-insensitive uniqueness, that needs a normalized column or a functional index, not a validation trick.
- HTTPS URL userinfo (`https://user:token@host/repo`) is accepted by the URL allow-list and is then stored in plaintext, and the validation redirect echoes the submitted URL into `?repositoryUrl=`. Reject userinfo (or redact it from the redirect) before private-repository/Phase 4 guidance; tokens belong in `GitCredential`/Secrets.
- scp-like repository paths are only shape-checked for a leading `-`/`/` and a colon; they still accept `..` segments and shell metacharacters (`git@host:../../etc`). When git lands, reject `..` segments and keep using `ProcessStartInfo.ArgumentList` (never a shell) so a path cannot become a command.
- The unique-name migration fails on an existing database that already contains duplicate `(OrganizationId, Name)` rows. Operators upgrading such a database must dedupe before `dotnet ef database update`; the migration does not attempt a silent repair.
- The delete confirmation is a dedicated server-rendered page rather than a modal BlazorBlueprint dialog. It works without a Blazor circuit, but **not without JavaScript**: the antiforgery token is copied into the form by `wwwroot/js/antiforgery.js`, so JS-disabled submits are 400. Rendering a hidden token per form (when `HttpContext` exists) would make the stronger claim true and is a shell-level follow-up.
- No PostgreSQL instance exists in this environment, so the migration was generated against Npgsql and the model check passes, but the populated-data migration gate was executed on SQLite only (same known gap as I-002/F-002).
