---
id: D-006
title: Secrets page polish: creator column and pipeline references
type: feature
priority: P2
size: S
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

The Secrets page answers the two questions an Owner asks before rotating or deleting: who created
this secret, and which pipelines reference this name — without ever revealing the value.

## Background

Roadmap v0.9 records F-012's polish explicitly as parked, not lost: "the remaining polish (creator
column, 'which pipelines reference this name')". The creator half is pure UI — `SecretService` already
records `CreatedByUserId` (and the entity keeps it through rotate). The references half needs a small
definition-phase change first: the frozen `PipelineSpec` does not record which secret names a pipeline
uses — `secret("NAME")` resolves at execution time on the Worker — so today the Server cannot answer
the question without parsing repository content. The definition phase (F-014's sandbox) sees every
`secret()` call, so it can capture the names where they become known.

Decisions:

- **Capture at definition, not at render**: the definition phase records the secret names referenced
  by a pipeline (on the run record / spec JSON), and the page queries that — no repo access, no
  parsing on the page.
- **Names only, never values**: the reference list shows names and run/pipeline links; values stay
  unrevealable (F-012's invariant).
- **Empty is informative**: a secret referenced by nothing says so, which is what makes deletion safe;
  a secret referenced by runs whose spec predates capture says "unknown (recorded before capture)"
  rather than pretending zero.

## Acceptance Criteria

- [ ] The Secrets list shows the creating user (display name or email) and, where known, which
      pipelines/runs reference the secret's name.
- [ ] The definition phase records every `secret("NAME")` reference a pipeline makes, surviving spec
      freezing and PostgreSQL/SQLite round-trips.
- [ ] Rotate and delete flows surface the references (delete warns with the referencing pipelines;
      rotate notes last rotation creator) without revealing values anywhere.
- [ ] A secret name referenced by no recorded pipeline states that honestly, including the
      pre-capture unknown case.
- [ ] Owner-only access unchanged (F-012's authorization untouched); tenant isolation probes from
      Q-001 still pass for the new queries.

## Subtasks

- [ ] Definition-phase capture of referenced secret names into the frozen record.
- [ ] Page: creator column, references column/detail, delete and rotate flow updates.
- [ ] Tests: capture accuracy, unknown-reference case, Owner-only and tenant probes.

## Dependencies

- none

## Test Cases

### TC-001: References come from the definition, not the repo

Steps:
1. Create a pipeline whose Lua calls `secret("API_KEY")` twice and `secret("OTHER")` once; run its
   definition; open the Secrets page.

Expected:
- `API_KEY` and `OTHER` show the pipeline as referencing; a third, unreferenced secret shows no
  references; no secret value is displayed anywhere on the page.

### TC-002: Rotate and delete are reference-aware

Steps:
1. Rotate a referenced secret; then attempt to delete it; then delete an unreferenced one.

Expected:
- Rotate succeeds with the creator recorded; delete of the referenced secret warns with the
  referencing pipelines (F-012's existing delete semantics decide whether it is refused or confirmed);
  the unreferenced one deletes cleanly.

### TC-003: Pre-capture honesty

Steps:
1. Open the page for a secret only referenced by runs whose definitions predate the capture change.

Expected:
- The page says references are unknown for those runs rather than reporting zero.

## Development Log

### 2026-11-14

- Planned from roadmap v0.9's parked F-012 polish. The capture point is the definition phase's
  sandbox (F-014), where `secret()` calls are already visible; the page stays repo-free.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Secrets/*`, `src/Shared/Pipelines/PipelineSpec.cs`, definition-phase
  capture site (`src/Worker/Pipelines/PipelineDefinition.cs` / Server-side validation per F-014),
  Secrets page components, `tests/Zenkins.Tests/SecretsUi/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=SecretsUi"`;
  full suite; PostgreSQL contract gate if the model changes.
- Results: not run (card not started).
