---
id: F-021
title: Parameterized builds: project parameter definitions, trigger values and a frozen run snapshot
type: feature
priority: P1
size: M
status: done
created: 2026-11-15
updated: 2026-11-16
blocked: none
---

## Goal

A project can declare **build parameters** (string / text / boolean / choice). The trigger form renders
an input per definition, the Server resolves the supplied values against the definitions, the run
freezes the resolved values, and the Worker injects them into every step's environment - for both
Pipeline and Freestyle projects.

## Background

Freestyle (F-020) made the job model flexible; the second half of Jenkins's flexibility is the
parameterized build. The decision that kept this small: **a parameter is an environment variable and a
run snapshot**, not a new templating language. Definitions live on the project; the resolved name →
value object lives on the run (frozen at trigger time, like the branch and the spec), travels in the
claim payload, and joins the same server-provided environment layer the `ZENKINS_*` variables occupy.
A `pipeline.lua` step reads `$NAME` / `${env:NAME}` and a Freestyle script reads `%NAME%` / `$NAME`
with no new syntax at all.

Decisions:

- **Definitions and values are two documents.** `Project.ParametersJson` is the definition list;
  `BuildRun.ParametersJson` is the resolved snapshot. Editing a definition never moves a queued or
  running run.
- **Four closed types**: `string`, `text`, `boolean`, `choice`. Names reuse
  `PipelineSpecSchema.IsReferenceNameShape` (they become environment variables), are unique per
  project (Ordinal), and are bounded (32 parameters, 64-character names, 200/8192-character values,
  32 choices, 200-character descriptions).
- **Resolution is total and strict**: every defined name gets exactly one value - supplied, else the
  definition default, else the type's neutral default (empty string; `false` for boolean; the first
  choice) - and a supplied name the project does not define (`parameter-unknown`) or a value that
  breaks its type/bound (`parameter-invalid`) is refused before any run row exists.
- **A headless trigger uses the defaults.** A webhook delivery has no person to fill the form, so it
  snapshots the definitions' defaults - never a previous manual trigger's values.
- **Parameters are not secrets.** They are visible on the run page and carried in the claim; the
  pages say so and point at the Secrets store. No redaction, no encryption, no secret-reference
  capture for them.
- **The trigger form's boolean is a radio pair, not a checkbox.** An unchecked checkbox posts nothing
  at all, which would be indistinguishable from "this parameter was not rendered" and would silently
  resolve to a `true` default; a radio group always posts exactly one of the two values.
- **The run page shows the snapshot.** A `Build parameters` card lists the frozen name → value pairs,
  and the definitions editor is its own routed tab (`/projects/{id}/parameters`), reachable for both
  kinds and listed in the Q-006 surface inventory.

## Acceptance Criteria

- [x] A project's parameters can be defined, edited and cleared through the Parameters tab; an invalid
      list is refused with a stable code and never replaces the saved definitions.
- [x] The trigger form renders one input per definition (text, textarea, true/false radio, select),
      pre-filled with the defaults.
- [x] Supplied values win, missing values resolve to defaults, and the resolved values are frozen on
      the run (a later definition edit does not move it).
- [x] The claim payload carries the resolved values, and the worker injects them into the
      server-provided environment layer for both kinds (verified by a real Freestyle script and a real
      Pipeline step reading the variable).
- [x] An unknown parameter name is refused (`parameter-unknown`); a boolean/choice/bound violation is
      refused (`parameter-invalid`); nothing is queued in either case.
- [x] A webhook run resolves the defaults, not another trigger's values.
- [x] The run page shows this run's parameter snapshot.

## Subtasks

- [x] Shared: `ProjectParameter`, `ProjectParameterTypes`, `ProjectParameterLimits`,
      `ProjectParameterJsonContext` (source-generated, AOT-safe).
- [x] Server: `Project.ParametersJson` + `BuildRun.ParametersJson` + migration, definitions save
      endpoint, `ProjectParameters.Validate`/`ResolveValues`, trigger/webhook resolution, claim
      payload, `RunView.Parameters`.
- [x] Worker: `WorkerClaimResponse.Parameters`, run-environment layer merge.
- [x] UI: Parameters tab + row editor, dynamic trigger inputs, run-page snapshot card.
- [x] Tests: catalog/resolution unit tests, form/trigger/claim/snapshot/refusal tests (both kinds),
      webhook-default tests, two process E2Es.
- [x] Docs: PRD §3.2/§3.5/§3.11/§4.2/§7, Q-006 inventory + probes, roadmap/board/card.

## Dependencies

- F-020 (the Freestyle kind the parameters are tested beside; the Pipeline kind alone would have been
  enough for the model, but the milestone E2E needs both).

## Test Cases

### TC-001: The trigger snapshots the supplied values and the claim carries them

Steps:
1. Save four definitions (string with default, text, boolean, choice) through the Parameters tab.
2. Trigger a Pipeline run with three supplied values.

Expected:
- The run row's snapshot holds the supplied values plus the empty-string neutral default for the text
  parameter; the claim carries the same object; an untouched trigger resolves every default.

### TC-002: Refusals happen before a run exists

Steps:
1. Trigger with an undeclared name; then with a boolean `yes`; then with a choice outside its list.

Expected:
- `parameter-unknown` / `parameter-invalid` PRG errors; no run rows.

### TC-003: A real worker's child process reads the parameter (both kinds)

Steps:
1. Freestyle: one script step printing `GREETING=$GREETING`; trigger with `GREETING=from-param`.
2. Pipeline: `pipeline.lua` with one ArgvEcho `env GREETING` step; trigger with
   `GREETING=pipeline-param`.

Expected:
- Both runs are `Success`; the persisted log line is exactly the supplied value (never the default);
  both run rows carry the snapshot.

## Development Log

### 2026-11-15

- Implemented end to end: Shared records/limits/JSON context, project + run columns and migration,
  definition save endpoint and validation, trigger resolution and snapshot, claim payload, worker env
  merge, Parameters tab/editor, dynamic trigger inputs, run-page snapshot card.
- Changed the boolean input from a checkbox to a true/false radio pair after working through the
  unchecked-checkbox binding gap (see Background).
- Extended the Q-006 surface inventory and its probes with the Parameters page and the two save
  endpoints; updated two F-014/F-020-era assertions that assumed schema v1 and version 2 being invalid.
- Review fixes: the parameters save's PRG target was the overview, which reads no `error` - a refused
  definition list was silently swallowed. It now redirects to `/projects/{id}/parameters`, the route
  that owns the editor, which renders the notice and the code. `ResolveValues` no longer indexes
  `Choices[0]` unguarded, so a row corrupted after the save resolves to the empty string instead of
  throwing inside a trigger; the worker writes the `ZENKINS_*` metadata after the parameter layer, so
  a parameter of the same name cannot make a step read a different run id or worker name.
- Next: independent review (the card stays in Todo with `status: review`).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Shared/Projects/ProjectParameter.cs`, `src/Server/Projects/ProjectParameters.cs`,
  `src/Server/Data/Entities/Project.cs`, `src/Server/Data/Entities/BuildRun.cs`,
  `src/Server/Runs/RunService.cs`, `src/Server/Runs/RunEndpoints.cs`,
  `src/Server/Components/Pages/Projects/ProjectParameters.razor`,
  `src/Server/Components/Shared/ProjectParametersEditor.razor`,
  `src/Server/Data/Migrations/20261115152746_AddProjectParameters.cs`,
  `src/Worker/WorkerRunLoop.cs`, `src/Worker/WorkerApiClient.cs`
- How to run/verify: `dotnet build -c Release && dotnet test tests/Zenkins.Tests -c Release`
  (catalog/resolution, form/trigger/claim/snapshot, webhook defaults, and the two `Milestone` process
  E2Es); by hand: `dotnet run --project src/Server`, define a parameter, trigger with a value, read
  the run page's snapshot and the step's output.
- Results: `dotnet build -c Release` green (0 warnings); the non-Milestone suite is **1111 passed /
  0 failed** (5m36s) including the new catalog, trigger/claim/snapshot, both-kind, webhook-default and
  UI tests; `MilestoneFreestyleTests` + `MilestoneParameterTests` are **4 passed / 0 failed** (macOS
  arm64, real Server + Worker); `dotnet publish src/Worker -r osx-arm64 -p:PublishAot=true` succeeds,
  so the claim's new JSON members stay source-generated and AOT-safe. After the review fixes the
  non-Milestone suite is **1113 passed / 0 failed**, including the refused-save PRG target and the
  corrupt-choice resolution.

### 2026-11-16 — independent review: PASS

Adversarial re-verification of the committed state (`da3c513`), in the reviewer's own clean
checkout, not the shared tree the developer works in.

- Build: `dotnet build -c Release` — 0 warnings, 0 errors. Suite: full `dotnet test -c Release`
  **1129 passed / 0 failed / 0 skipped** (6m28s); `--filter Category=Milestone` **12/12**.
- All seven criteria satisfied with file:line evidence. The resolution really is total and strict
  (`ProjectParameters.cs:180-230`) and it really runs before the run row exists
  (`RunService.cs:438-445` vs `:460`); both kinds carry real-process proof that a child reads the
  parameter (`MilestoneParameterTests.cs:19-60`, `:62-102`); the webhook path resolves the
  definitions' defaults and not a previous manual trigger's values (`RunService.cs:566-569`).
- Two criteria were under-tested in the suite: the trigger form's pre-filled defaults, and the
  out-of-bounds refusal, which only reaches the unit layer and never the trigger endpoint. The
  reviewer proved the form behaviour with its own probe; the bound gap is recorded on Q-012.
- Two low-severity inconsistencies found and recorded on Q-012: duplicate `choice` entries are
  accepted (two identical `<option>`s render), and `Project.PipelinePath` still defaults to
  `pipeline.lua` at the entity level — a Freestyle row created outside `ProjectService`
  validation would carry it into a run snapshot.
- **Verdict: PASS.** Status → done; board row moved to Done (2026-11-16).
