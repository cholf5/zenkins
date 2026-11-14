---
id: D-006
title: Secrets page polish: creator column and pipeline references
type: feature
priority: P2
size: S
status: done
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

- [x] The Secrets list shows the creating user (display name or email) and, where known, which
      pipelines/runs reference the secret's name.
- [x] The definition phase records every `secret("NAME")` reference a pipeline makes, surviving spec
      freezing and PostgreSQL/SQLite round-trips.
- [x] Rotate and delete flows surface the references (delete warns with the referencing pipelines;
      rotate notes last rotation creator) without revealing values anywhere.
- [x] A secret name referenced by no recorded pipeline states that honestly, including the
      pre-capture unknown case.
- [x] Owner-only access unchanged (F-012's authorization untouched); tenant isolation probes from
      Q-001 still pass for the new queries.

## Subtasks

- [x] Definition-phase capture of referenced secret names into the frozen record.
- [x] Page: creator column, references column/detail, delete and rotate flow updates.
- [x] Tests: capture accuracy, unknown-reference case, Owner-only and tenant probes.

## Dependencies

- none

## Test Cases

### TC-001: References come from the definition, not the repo — PASS

Steps:
1. Create a pipeline whose Lua calls `secret("API_KEY")` twice and `secret("OTHER")` once; run its
   definition; open the Secrets page.

Expected:
- `API_KEY` and `OTHER` show the pipeline as referencing; a third, unreferenced secret shows no
  references; no secret value is displayed anywhere on the page.

Result (observed, 2026-11-14): PASS. A real worker definition report freezes a spec whose Lua calls
`secret("API_KEY")` twice and `secret("OTHER")` once; the page lists both names with the run link,
the unreferenced third secret says `No pipeline references this name.`, and none of the three values
appears in the rendered page. Capture is deduplicated (two calls, one name) and also picks up
hand-written `${secret:…}` markers in argv/env, because the capture unions the sandbox calls with the
markers the executor resolves.

### TC-002: Rotate and delete are reference-aware — PASS

Steps:
1. Rotate a referenced secret; then attempt to delete it; then delete an unreferenced one.

Expected:
- Rotate succeeds with the creator recorded; delete of the referenced secret warns with the
  referencing pipelines (F-012's existing delete semantics decide whether it is refused or confirmed);
  the unreferenced one deletes cleanly.

Result (observed, 2026-11-14): PASS. Rotate records the rotator (the row shows `by <rotator>` and the
reference list is unchanged), an older rotation with no attribution shows `not recorded` instead of a
blank, and the Created column is pinned separately in the pre-attribution render so a regression of
the creator label cannot hide behind the rotator label. Deleting a referenced secret shows the
confirm text naming the pipeline (`Referenced by d006-pipeline`), deleting an unreferenced one
succeeds (302, row gone) while the referenced row survives. No value, ciphertext or unprotect call
reaches the page, and `SecretEndpoints.cs` has no diff — no new route, and no get-value route.

### TC-003: Pre-capture honesty — PASS

Steps:
1. Open the page for a secret only referenced by runs whose definitions predate the capture change.

Expected:
- The page says references are unknown for those runs rather than reporting zero.

Result (observed, 2026-11-14): PASS, with the three states kept distinct in code and in tests: a spec
frozen before capture reads back as null and is still accepted (no historical run loses its pending
steps), and the page says `unknown (recorded before capture)`; a spec captured after this card with no
references serializes `"secretReferences":[]` and the page says `No pipeline references this name.`;
and when both kinds coexist the page reports the known references *and* `N earlier run(s) predate
reference capture`, both in the column and in the delete warning.

## Development Log

### 2026-11-14

- Planned from roadmap v0.9's parked F-012 polish. The capture point is the definition phase's
  sandbox (F-014), where `secret()` calls are already visible; the page stays repo-free.
- Implemented. `PipelineSpec` gains an optional trailing `secretReferences` list; the Worker's
definition phase records names into a per-run capture (a new instance per run, drained after the
freeze) and unions them with every `${secret:…}` marker the executor will resolve, so a hand-written
marker is not missed. The server normalizes the list on read (dedupe, drop illegal or over-long
entries) and **never** rejects a spec because of it: absent stays null (unknown), empty stays `[]`
(captured, no references) — the distinction the whole card rests on, and the reason a historical run
never loses its pending steps.
- Model change, one column: `Secrets.RotatedByUserId` (nullable, no foreign key — the creator has
never had one) via migration `20261114102208_AddSecretRotationAttribution`, generated with the
PostgreSQL provider so the shipped shape is the Npgsql one (`character varying(450)`), with the model
snapshot updated. **PostgreSQL was not applied or round-tripped on a live instance (none here); the
PG half of AC-2 is contract verification only** — zero schema change to the spec JSON plus a green
`has-pending-model-changes` gate — and is recorded that way rather than claimed.
- Page: three columns (Created by, Rotated by, Referenced by). User ids resolve through one batched
query and degrade to the raw id when the account is gone; a rotated row with no attribution says
`not recorded`; references come from a single per-page scan of the tenant's spec-bearing runs
(`SecretReferenceIndex`), intersecting recorded names with this organization's secrets — no repository
read, no code parsing, no values. Three states, not two: known (up to five pipeline names with run
links, then "and N more"), none, and unknown for pre-capture specs. The scan is one pass, bounded in
memory, and its cost is proportional to the tenant's spec-bearing runs; the trade-off is written at
the class and deliberately not capped, because "no references" is only trustworthy if it covers
everything (a window would turn old references invisible, which is exactly what makes deletion
unsafe). If that cost ever matters, the fix is to materialize (run, name) pairs at definition time,
not to add a LIMIT.
- Two review findings were test-evidence gaps, not defects, and both were closed with falsification
rather than assertion: the tenant-isolation probe originally froze a *differently named* secret in
the other organization, so an intersection with this organization's names hid a leaked row — the
probe now uses the same name, and inserting `IgnoreQueryFilters()` temporarily turns it red (verified
independently by the reviewer, restored byte-identical); and the migration "old row still readable"
test that the first report implied did not exist, so `SecretRotationMigrationTests` now migrates to
the previous migration, inserts a rotated pre-attribution row, migrates forward and asserts the row
survives with a null attribution.
- Two smaller blind spots were also closed: the `Known + uncaptured runs` combination (the subtlest
state, previously uncovered) and the Created column's own assertion, which the rotator label would
otherwise have satisfied on its behalf.
- Adjudicated trade-off: when *every* entry of a reported list is illegal, the normalizer yields `[]`
(captured, no references) rather than null. A trusted worker can already report an empty list, so
this adds no surface, and calling a corrupt list "recorded before capture" would be a false statement
about when capture happened — which is the honesty TC-003 exists to protect.
- Verification (dev runs + independent review runs): `dotnet build -c Release` 0 warnings;
  `Category=Secrets` 81 passed; `Category=Pipeline` 96; `Category=TenantIsolation` 114; fast suite
  1017 passed / 0 failed / 0 skipped; PG model gate green; `git diff --stat SecretEndpoints.cs`
  empty. `RunLogReconnectTests` reported one load-dependent flake under a full run (observed in an
  earlier card too, green in isolation and in the reviewer's full run) — recorded, not this card's.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | No product defect found. Two initial findings were test-evidence gaps: a tenant-isolation probe that could not fail (differently named secret made the intersection hide a leak) and a migration round-trip test that was claimed but absent. | Closed | Both closed with falsification evidence (temporary `IgnoreQueryFilters()` turns the probe red, restored byte-identical; migration test migrates back, inserts a pre-attribution row, migrates forward and asserts it survives). |

## Verification

- Related files: `src/Server/Secrets/*`, `src/Shared/Pipelines/PipelineSpec.cs`, definition-phase
  capture site (`src/Worker/Pipelines/PipelineDefinition.cs` / Server-side validation per F-014),
  Secrets page components, `tests/Zenkins.Tests/Secrets/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Secrets"`
  (the trait is `Secrets`, the test directory is `tests/Zenkins.Tests/Secrets/`);
  `--filter "Category=TenantIsolation"`; the fast suite; PG model gate when the model changes:
  `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
  (the provider prefix is required or the gate misreports pending changes on a clean tree).
- Results (observed 2026-11-14; dev runs, independent review runs, and a re-verification):
  `dotnet build -c Release` 0 warnings / 0 errors; `Category=Secrets` 81/81; `Category=Pipeline` 96/96;
  `Category=TenantIsolation` 114/114; fast suite
  `--filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot"`
  1017/1017, 0 skipped; PG model gate `No changes have been made to the model since the last
  migration.`; `src/Server/Secrets` has no `IgnoreQueryFilters`/`SystemQueryAsync`; `SecretEndpoints.cs`
  unchanged. Known limits recorded: PostgreSQL apply/round-trip is contract-only (no live instance),
  and the reference scan is uncapped by decision (rationale in the development log).
