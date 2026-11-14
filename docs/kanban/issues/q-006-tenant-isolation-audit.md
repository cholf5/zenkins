---
id: Q-006
title: Cross-tenant isolation audit (data, logs, artifacts, git credentials)
type: test
priority: P1
size: L
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

A written audit plus regression tests proving that one organization cannot reach another's data, logs,
artifacts, git credentials or secrets through any surface — HTTP routes, the SignalR hub, webhook
endpoints, or file-system paths (PRD §10 scenario 5).

## Background

Roadmap v1.0 ("Cross-tenant isolation audit"). Isolation was built in slice by slice and each slice
was tested by its own card: Q-001 probes the query filters and form models, F-012 encrypts secrets,
F-013 stores git credentials, F-016 keeps artifacts path-safe, F-008 streams logs. Nobody has audited
the composition across every surface at once, and composition is exactly where F-008's review once
found a rejection discriminator that never matched. The PRD's promise is per-organization isolation of
"projects, workers, secrets and build records" — this card makes the promise explicit and enumerable.

Decisions:

- **Enumerate before probing.** The first deliverable is a surface list (every route, hub method,
  stream id and store location), committed with the audit, so a probe that is missing is a visible
  gap rather than an unknown.
- **Id-swap style, like Q-001**: for every surface, org A's session asks for org B's resource by id,
  path or name, and the answer must be 404/403 without leaking existence.
- **Filesystem-level checks for stores.** Artifacts and git credentials live on disk keyed by run or
  project; the audit checks the layout itself (no org-B id reachable under org A's access, no
  credential material inside org A's artifact or log bytes), not only the HTTP status.
- **Holes are fixed here**, each with its failing probe kept as a regression test.

## Acceptance Criteria

- [ ] A committed surface inventory lists every tenant-reachable surface: MVC/Blazor routes, SignalR
      hub methods, webhook endpoint, artifact store layout, credential and secret storage.
- [ ] Every inventoried surface passes the id-swap probe: org A requesting org B's resource by id,
      name or stream id is refused with 404/403, and the refusal does not confirm existence.
- [ ] Artifact bytes are unreachable across tenants: another org's run id is refused on download, and
      no artifact path under org A's access resolves into org B's files (path-traversal re-check
      included, F-016 rules re-verified from the audit).
- [ ] Git credentials and secret values never appear in another tenant's responses, logs or artifacts;
      F-015's redaction is re-verified cross-tenant, not only within one org.
- [ ] The SignalR log stream refuses org A's browser session for an org B run id (hub-level
      authorization, not only page-level).
- [ ] Every hole found is fixed in this card and its probe is a permanent regression test.

## Subtasks

- [ ] Build the surface inventory (routes, hub methods, streams, stores) and commit it.
- [ ] Id-swap probe suite over the inventory, reusing Q-001's two-org fixture.
- [ ] Filesystem audit of artifact layout and credential/secret storage locations.
- [ ] Hub-level authorization checks for log streaming.
- [ ] Fix findings; keep each failing probe as a regression.

## Dependencies

- none (Q-001, F-008, F-012, F-013, F-015, F-016, F-017 are accepted)

## Test Cases

### TC-001: The inventory is complete against the router

Steps:
1. Enumerate registered endpoints and hub methods from the built Server assembly; diff against the
   committed inventory.

Expected:
- No undocumented tenant-reachable surface; the audit fails loudly when a new endpoint is added
  without entering the inventory.

### TC-002: Cross-tenant artifact and credential reach is zero

Steps:
1. As org A, request org B's run artifacts by id; attempt path-traversal spellings; search org A's
   logs and artifact bytes for org B's credential or secret material.

Expected:
- All requests refused without existence leaks; zero credential or secret bytes in org A's surfaces.

### TC-003: The hub is tenant-honest

Steps:
1. Open org A's browser session and subscribe to an org B run's log stream id.

Expected:
- Refusal at the hub method; no log line from org B's run ever reaches the session.

## Development Log

### 2026-11-14

- Planned from roadmap v1.0. Inventory-first so the audit survives future cards: an endpoint outside
  the inventory is a test failure by construction.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/Zenkins.Tests/*` (Q-001 fixture), `src/Server/**` route/hub definitions,
  `src/Server/Secrets/*`, `src/Server/Git/*`, `src/Server/Artifacts or equivalent` (F-016 store),
  `src/Server/LogStreaming/*`
- How to run/verify: `dotnet test -c Release --filter "FullyQualifiedName~TenantIsolation"`;
  full suite.
- Results: not run (card not started).
