---
id: F-035
title: Step snippets — reusable, insertable step definitions shared across an organization
type: feature
priority: P3
size: M
status: done
created: 2026-11-16
updated: 2026-11-18
blocked: none
---

## Goal

An organization can keep named step snippets (a script step, an env set, a small Lua
helper block) and insert one into a project's definition, so the tenth project that
needs "publish to the internal registry" does not copy the same 20 lines.

## Background

A Freestyle step's script is a text field on one project; an inline pipeline's script
is a text body on one project (F-022); a repository pipeline cannot share code at all
(the Lua sandbox has no `require`, and the definition phase is one script). Jenkins
solves the same need with Shared Libraries, which are a plugin-scale mechanism. The
cheap, honest 80% is a text library that is **frozen at save time**: no runtime
dependency, no new execution surface, no version skew between a snippet and a run.

## Acceptance Criteria

- [x] An organization can create snippets: name, kind (`script-step` for Freestyle,
      `lua-fragment` for inline/repository pipelines), body, description; Owner or
      Member edit per the repo's existing rules.
- [x] Inserting a snippet into a Freestyle steps list or a script body copies its text
      at that moment; the snippet is not referenced at run time, so editing a snippet
      never changes a queued run, a frozen spec or a saved project silently.
- [x] The UI shows what a snippet looks like before insertion and offers a preview of
      the resulting step (name, env, artifacts) for a script-step snippet.
- [x] An inserted snippet's secret references are captured by the same scanner as
      hand-written text (no bypass of the D-006 metadata).
- [x] Deleting a snippet never touches projects that used it (no dangling references,
      because there are none) — the page says so explicitly.
- [x] A snippet body is bounded like the target it feeds (script step ≤ 32 KiB, Lua
      fragment ≤ the inline cap) and is validated with the same rules.
- [x] Repository pipelines get an "insert snippet" affordance that copies text into
      the clipboard/editor — the repository file stays the source of truth, and the
      product never rewrites a user's repository.

## Subtasks

- [x] `StepSnippet` entity (org, name, kind, body, description, attribution) + unique
      (org, name) index.
- [x] Endpoints for create/edit/delete with antiforgery and the usual 404/403 shapes.
- [x] Freestyle editor: a snippet picker that appends a fully-formed step row.
- [x] Pipelines tab (inline): a fragment picker that inserts text at the caret.
- [x] Secret reference scanning of the inserted text at save time (existing
      `StepReference` scanner, no new parser).
- [x] PRD §3.15 (new): snippets, their freeze-on-insert rule and their limits.

## Dependencies

- F-020 (script steps, the steps editor) — in review
- F-022 (inline script editor) — for the Lua-fragment half

## Test Cases

### TC-001: An inserted snippet is copied, not referenced

Steps:
1. Create a `script-step` snippet; insert it into a Freestyle project; save.
2. Edit the snippet's body; open the project and trigger a run.

Expected:
- The project's spec still holds the inserted text; the new run executes it.
- The snippet page states that projects are not updated.

### TC-002: Secret references survive the insertion

Steps:
1. A snippet body containing `${secret:DEPLOY_TOKEN}`; insert and save.

Expected:
- The frozen spec's `secretReferences` includes `DEPLOY_TOKEN`, exactly as if it
  had been typed.

### TC-003: Deleting a snippet breaks nothing

Steps:
1. Insert a snippet, save the project, delete the snippet, trigger a run.

Expected:
- The run builds normally; no page errors; the project shows the text it froze.

## Notes and Decisions

- Rejected: referencing snippets at run time (a "library" resolved by the worker).
  It would make a run depend on mutable organization state and turn a snippet edit
  into a behaviour change for every project.
- Rejected: a Lua `require`/module loader. It changes the sandbox contract
  (`docs/lua-runtime.md` §3) for a sharing feature that text insertion already covers.

## Implementation Notes

- **Text, not a link.** `StepSnippet` is a tenant-owned row (`OrganizationId`, `Name`, `Kind`, `Body`,
  `Description`, attribution timestamps/ids) with a unique `(OrganizationId, Name)` index and the global
  tenant query filter. Nothing in a project, a frozen spec or a run points at it: the editors copy the
  body, and the normal save freezes the copied text. That is why an edit or a delete cannot reach saved
  work - there is no reference to follow, and the pages say so.
- **Two kinds, bound like their targets.** `StepSnippetKinds` (`script-step`, `lua-fragment`) live in
  `src/Shared` with the limits: a script step is bounded like a Freestyle script (32 KiB), a Lua fragment
  like an inline pipeline script (64 KiB). Names are ≤100 characters and unique per organization (the
  index is the guard; the service translates the violation into `name-duplicate`), descriptions ≤500.
  `StepSnippetValidation` is the one validator; the endpoints redirect with stable codes.
- **Endpoints and access.** `POST /snippets/create`, `/snippets/{id:guid}/edit`,
  `/snippets/{id:guid}/delete`, form-bound with antiforgery, Member **or** Owner (the card's rule, unlike
  Owner-only secrets). A foreign or missing id is a bodyless 404; a non-member is 403. No route serves a
  snippet to a run - there is no runtime consumer at all.
- **UI.** `/snippets` (sidebar, next to Secrets) lists the library, the body, the resulting-step preview
  for a script step (name / env / artifacts and the `StepReference` names the body carries), and the edit
  / delete forms. The Freestyle steps editor offers `script-step` snippets (a click appends a fully formed
  row; the rows are the same component as always). An inline pipeline's editor inserts a `lua-fragment` at
  the caret; a repository pipeline's Pipelines tab offers a clipboard copy and states that the product
  never rewrites the repository. Bodies travel once in the page's `#snippets-data` JSON (default JSON
  escaping makes `</script>` impossible); `js/freestyle-steps.js` and `js/snippets.js` copy them
  client-side.
- **Secret references (D-006).** Inserted text is ordinary draft text, so `FreestyleSteps.TryBuildStep`
  scans it with the same `StepReference` scanner; the freeze fact proves `${secret:DEPLOY_TOKEN}` lands in
  the frozen spec's `secretReferences`. A snippet cannot bypass the metadata because there is no second
  input path.
- **No execution surface.** A source-level fact (`No_worker_or_shared_code_knows_about_snippets…`) pins
  that the Worker never references the entity/service and `src/Shared` carries only kinds + bounds.
- **Tenancy (Q-006).** `StepSnippet` is in `TenantIsolationProbes.CoveredEntityTypes` with the generic
  read/write/find probes; the HTTP probe covers both mutation routes and the page scan includes
  `/snippets` plus the foreign snippet's name and body. The inventory gained the four routes. While
  running the inventory this card found F-031's `/projects/{id}/post-failure` was registered but not
  inventoried; the row (and the foreign-id fact in `Member_of_a_cannot_read_or_mutate_bs_project`) is
  added here, because an unlisted surface is a test failure by construction.

## Verification

- Related files: `src/Shared/Snippets/StepSnippetContract.cs`,
  `src/Server/Data/Entities/StepSnippet.cs`, `src/Server/Data/ZenkinsDbContext.cs`,
  `src/Server/Data/Migrations/20261118151256_AddStepSnippets.cs`, `src/Server/Snippets/*`,
  `src/Server/Program.cs`, `src/Server/Components/Pages/Snippets.razor`,
  `src/Server/Components/Layout/NavMenu.razor`,
  `src/Server/Components/Pages/Projects/ProjectPipelines.razor`,
  `src/Server/Components/Shared/FreestyleStepsEditor.razor`,
  `src/Server/wwwroot/js/freestyle-steps.js`, `src/Server/wwwroot/js/snippets.js`,
  `src/Server/Components/App.razor`, `docs/prd.md` (§3.15), `CHANGELOG.md`,
  `docs/audits/q-006-surface-inventory.md`.
- New/changed tests: `tests/Zenkins.Tests/Snippets/SnippetEndpointsTests.cs`,
  `SnippetFreezeTests.cs` (the copied-not-referenced facts and the source-level pin),
  `SnippetAffordanceTests.cs`, `SnippetTestHost.cs`; tenant side:
  `TwoOrgsFixture` seed, `TenantIsolationProbes.CoveredEntityTypes`, three generic probe facts, the
  `/snippets` page/mutation probes, and F-031's missing post-failure inventory row.
- How to run: `dotnet build -c Release`; then
  `dotnet test -c Release --filter "FullyQualifiedName~Snippet|FullyQualifiedName~TenantIsolation"`.
- Results (macOS arm64): build 0 warnings / 0 errors. `--list-tests` with
  `Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres&Category!=Load`
  discovers **1646** tests (main had 1628; this card adds 18). The focused suites are **160 passed /
  0 failed** (snippets + tenancy + model snapshot) and **95 passed / 0 failed** (project UI, Freestyle
  editor/run, run UI, project CRUD, shared-assembly guard). No full-suite or PostgreSQL run.
- Mutation checks (each broke one mechanism, confirmed red, restored): duplicate-name code changed; the
  kind-specific body bound removed; the snippet tenant query filter removed; the preview dropping its
  secret references; the freeze statement removed from the page; the Freestyle picker hidden; the page's
  `scriptStep` payload key renamed; the inline insert target broken; the repository copy affordance
  renamed away; the Freestyle builder's script-text secret capture removed; a Worker file made to mention
  the snippet library (the "no execution surface" pin).

## Close-out verification (2026-11-18)

Snippets are a text library that is frozen at save time - the cheap, honest 80% the card asked for, and the
decision that keeps the feature from becoming Shared Libraries.

- **Insertion copies, it does not reference.** Inserting a snippet writes its body into the draft being edited;
  saving freezes that text into the spec. Editing the snippet afterwards leaves the project's spec byte-identical,
  a run's snapshot unchanged, and deleting the snippet leaves the project, the older run page and a new run all
  working with the same body. A fact asserts the spec contains no snippet id at all, which is what "no runtime
  dependency" means concretely.
- **No new execution surface**: a source-level fact states that neither `Worker` nor `Shared` knows the library
  exists, so a snippet cannot grow its own execution semantics without that fact failing.
- **Secret references go through the same scanner**, because the inserted text *is* draft text: a snippet
  carrying `${secret:DEPLOY_TOKEN}` ends up in the spec's `secretReferences` with no D-006 bypass.
- **The repository pipeline is never rewritten.** A repository project gets a copy affordance and a sentence
  saying so; the inline editor inserts at the caret; the Freestyle editor offers `script-step` snippets. There is
  no code path that writes a snippet into a repository.
- **Body limits match the target each kind feeds** (32 KiB for a script step, 64 KiB for a Lua fragment) rather
  than one number for both, because a limit that is looser than the target lets the library accept text the
  product will then refuse.
- **Tenancy**: `StepSnippet` is a tenant entity with the generic read, write and find probes plus a route probe
  (foreign id edit or delete is one bare 404 and the other tenant's row is untouched) - the fourth card in this
  series to be caught by that gate, which is why the checklist keeps it.
- **Counts.** fast 1655 -> 1679 measured on the merged tree (18 facts here plus the integration fix's own), and
  everything is 1712. This is the last card of the series; the full suite and Postgres run once, after it.
