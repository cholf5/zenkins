---
id: F-002
title: Organization CRUD, members, switcher
type: feature
priority: P1
size: M
status: todo
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

- [ ] Switcher lists only Organizations the current user belongs to; switching changes the active tenant for all subsequent queries.
- [ ] A forged/switched-to-Organization the user does not belong to is rejected server-side and falls back to a valid membership.
- [ ] Owner can rename the Organization, change another member's role, and remove a member; the last Owner cannot be removed or demoted.
- [ ] Member sees no management controls and management endpoints reject them regardless of UI.
- [ ] Members list shows name, email, role, joined date.
- [ ] `Q-001` probes extended to cover membership-based authorization.

## Subtasks

- [ ] Server-side active-organization resolution as the source for `ITenantContext`.
- [ ] Switcher component in the shell header.
- [ ] Organization settings page (rename) with Owner guard.
- [ ] Members page: list, invite existing user, change role, remove.
- [ ] Last-owner invariant on role change and removal.
- [ ] Extend tenant isolation tests with a Member-role probe.

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

### 2026-11-12

- Card created. Not started.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Organizations/**`, `src/Server/Components/Pages/Organizations/**`
- How to run/verify: `dotnet test --filter Organization` and manual switch in the UI
- Results: not run yet
