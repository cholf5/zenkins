---
id: F-001
title: Identity auth + first-run Owner bootstrap
type: feature
priority: P1
size: M
status: todo
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

- [ ] Register, login, logout work end-to-end; passwords use Identity's hasher.
- [ ] First registration atomically creates User + Organization + Owner membership; the bootstrap path is refused once a user exists.
- [ ] Authenticated pages redirect anonymous users to login; login redirects back to the requested page.
- [ ] A user with no Organization membership sees an explicit "no organization" state with a next step, not a crash or empty dashboard.
- [ ] No password hash, token, or cookie value appears in logs.
- [ ] Cookie is `HttpOnly`, `SameSite=Lax`, and `Secure` when the request is HTTPS or forwarded-proto says so.

## Subtasks

- [ ] Add `Microsoft.AspNetCore.Identity.EntityFrameworkCore`; extend `User`, add Identity stores on `ZenkinsDbContext`.
- [ ] Configure password/lockout policy and cookie options per above.
- [ ] Register/login/logout Razor components (Blazor Server form model, not MVC pages).
- [ ] Implement bootstrap transaction with a race-safe guard (unique constraint on bootstrap, not a "count == 0" check alone).
- [ ] Add `[Authorize]` at the layout level and a `RedirectToLogin` fallback.
- [ ] Add the no-organization state page.

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

### TC-002: Bootstrap cannot run twice

Steps:
1. With `alice` present, POST the bootstrap path again (second registration).

Expected:
- Refused; no second Organization created.

### TC-003: Anonymous access is blocked

Steps:
1. Clear cookies; request `/projects`.

Expected:
- 302 to login, then back to `/projects` after authenticating.

### TC-004: No secrets in logs

Steps:
1. Perform register + login with `Information` logging.
2. Grep captured log output.

Expected:
- No password, hash, or cookie value present.

## Development Log

### 2026-11-12

- Card created. Not started.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Auth/**`, `src/Server/Components/Pages/Account/**`
- How to run/verify: `dotnet run --project src/Server` then register/login; plus `dotnet test --filter Auth`
- Results: not run yet
