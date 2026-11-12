---
id: F-003
title: Project CRUD
type: feature
priority: P1
size: M
status: todo
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

- [ ] List, create, edit, delete Project, all scoped to the active Organization.
- [ ] Name is unique within an Organization; duplicate create surfaces a field-level error.
- [ ] Repository URL is validated; `file://` and `ext::` transports are rejected.
- [ ] Pipeline Path defaults to `pipeline.lua` and rejects `..` / absolute paths.
- [ ] Delete asks for confirmation and, for now, is a hard delete (cascade semantics revisited when BuildRuns exist).
- [ ] Member can create and edit; only Owner can delete. *(Decision to confirm against PRD §3.1.2, which grants Members project creation but does not say delete.)*
- [ ] Direct requests against another tenant's Project id return 404, not 403.

## Subtasks

- [ ] Project create/edit form + list page.
- [ ] URL + path validators with unit tests.
- [ ] Per-Organization unique name (DB index + friendly error).
- [ ] Delete flow with confirmation.
- [ ] Replace Q-001's test-only `/__probe/projects` + `X-Test-Organization` with the real authenticated endpoint: a request authenticated as A, with B's project id in route/query/form, must return 404 and never B's data.

## Dependencies

- F-002
- D-001

## Test Cases

### TC-001: Create and appear in list

Steps:
1. Active Organization A. Create Project "game-client" with a valid repo URL.

Expected:
- Row appears with defaults (`pipeline.lua`).

### TC-002: Duplicate name rejected

Steps:
1. Create the same name twice in A.

Expected:
- Field-level error; second row not created.

### TC-003: Dangerous URL rejected

Steps:
1. Submit `file:///etc/passwd`; submit `ext::sh -c touch% /tmp/pwned`.

Expected:
- Both rejected with a validation message; nothing stored, nothing executed.

### TC-004: Cross-tenant access

Steps:
1. As Organization A, request Organization B's project detail URL.

Expected:
- 404.

## Development Log

### 2026-11-12

- Card created. Not started.
- Open question for the user: may a Member delete a Project? PRD §3.1.2 lists "创建和执行项目" for Member and 项目管理 for Owner — reading it as Owner-only for delete.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Projects/**`, `src/Server/Components/Pages/Projects/**`
- How to run/verify: `dotnet test --filter Project` and manual CRUD in the UI
- Results: not run yet
