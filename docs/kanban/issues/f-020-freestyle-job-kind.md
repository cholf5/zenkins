---
id: F-020
title: Freestyle job kind: repository-free projects with server-configured script steps
type: feature
priority: P1
size: L
status: review
created: 2026-11-15
updated: 2026-11-15
blocked: none
---

## Goal

A project can be a **Freestyle** job: no Git repository is required, its steps are configured in the
Server UI as multi-line shell scripts, and every run executes the step list that was frozen onto the
run at trigger time. The existing Pipeline kind (repository + `pipeline.lua`) is unchanged.

## Background

Until now `Project.RepositoryUrl` was required and every run cloned the repository to read
`pipeline.lua` - the GitLab-shaped, SCM-centric model. Jenkins has two job kinds for exactly this
reason: Freestyle (definition on the server) and Pipeline (definition in SCM). The insight that kept
this small: **the frozen `PipelineSpec` is already the one execution contract**, so a Freestyle job is
"a spec the server freezes at trigger time instead of the worker freezing it after the definition
phase" - the step loop, env composition, secrets, artifacts, cancellation and deadline are all the
existing machinery.

Decisions:

- **`Project.Kind` is a data column, not a type hierarchy** (`pipeline` | `freestyle`, check
  constraint `CK_Projects_Kind`, backfilled `pipeline`). The kind is **immutable after create** - a
  kind swap would strand the semantics a queued run already snapshotted - and the edit form shows it
  as a badge.
- **The repository fields became nullable.** `RepositoryUrl`/`DefaultBranch`/`PipelinePath` are null
  for Freestyle; the validation layer (not the data layer) requires them for Pipeline, and a null URL
  never reaches a clone because the Freestyle path skips it. `BuildRun.GitBranch`/`PipelinePath` stay
  NOT NULL and record the honest empty string for Freestyle.
- **Schema v2 adds the `script` step** (`PipelineStep.Script`, exactly one of run/shell/script;
  `MaxScriptChars` 32768). `TryRead` accepts **v1 and v2** - every spec frozen before this card stays
  readable. A `pipeline.lua` never produces a script step; it is the Freestyle editor's spelling only.
- **The server freezes the spec at trigger time** (`BuildRun.PipelineSpecJson`, snapshot like the
  branch). The Worker re-checks the envelope (`PipelineSpecJson.TryRead`) before executing - the same
  gate its own definitions go through - and a refusal is `pipeline-invalid`, not a crash.
- **Script steps run from a file, not a command line**: the Worker resolves the references
  (`${env:...}`, `${secret:...}`), writes the resolved text to `temp/step-N.cmd|sh` (UTF-8 **without
  a BOM** - a BOM becomes the first command on Windows), and executes it with `cmd /d /s /c` on
  Windows and `/bin/sh -e` on Unix. Still pure argv end to end; the shell interprets only the text
  the project's Member configured. PRD §8 constraint 6 is re-worded accordingly: the Pipeline/Lua
  surface keeps its no-shell guarantee, the Freestyle step is explicit shell configuration.
- **The steps editor is a plain HTML form** posting the drafts as one `StepsJson` field to
  `POST /projects/{id}/freestyle/steps`; `js/freestyle-steps.js` owns the dynamic rows and the
  serialization, and the **server** builds and envelope-checks the spec from the drafts
  (`FreestyleSteps.BuildSpecJson`) - the browser never authors a spec. Env rows are `NAME=value`
  lines and artifacts a comma list, parsed server-side so the parsing is unit-tested.
- **Secret references are captured server-side** at save time by the same `StepReference` scanner the
  Worker substitutes with (moved to `Zenkins.Shared.Pipelines` for exactly this), so the D-006
  metadata ("which secrets a spec names") stays accurate for Freestyle specs too.
- **Freestyle steps execute in `work/{run-id}/workspace`** (new computed member of the run directory;
  created only for Freestyle runs so the prepared trio stays PRD §3.4.6's), and artifacts resolve
  against that workspace. The run's log echoes the script verbatim (references as written - names are
  not values), capped at 100 lines.
- **Webhooks and git credentials do not exist for Freestyle**: creation is refused
  (`project-not-pipeline`), the delivery path carries a defence-in-depth `ignored:no-repository`
  outcome, and the settings/overview UI hides the sections.
- **The claim payload gained `pipelineSpecJson`** (null for Pipeline runs; the null `repositoryUrl`
  is what "no clone" reads as on the Worker). Worker/server ship together, and the field is absent in
  an older server's JSON, which is the Pipeline behaviour.

## Acceptance Criteria

- [x] A Freestyle project can be created from the form (kind selector, no repository fields) and its
      steps saved through the editor; saving is refused with a stable code for an invalid list, and a
      refused save never replaces the previously saved spec.
- [x] Triggering a Freestyle project without saved steps is refused (`steps-missing`); with steps, the
      run snapshots the spec (`BuildRun.PipelineSpecJson`) and an empty branch/pipeline path.
- [x] The claim payload carries the frozen spec and a null repository URL; the Worker skips the clone
      and the definition phase, executes the steps in the run's workspace, and records no commit.
- [x] A script step's failure fails the run with its real exit code and skips the rest (`step-failed`);
      a step's stdout lands attributed to that step; `continue_on_error` works unchanged.
- [x] A step's declared artifacts are published from the workspace and downloadable.
- [x] Editing the steps after triggering does not move a queued or running run (snapshot).
- [x] Webhook creation for a Freestyle project is refused; deliveries to one never queue a run.
- [x] v1 specs (pre-card runs) still read back; the envelope refuses v3 and a script step that also
      declares run/shell.

## Subtasks

- [x] Shared: `PipelineStep.Script`, schema v2 (v1 read-back), `StepReference` into Shared,
      `ProjectKinds`.
- [x] Server: entity + migration (kind, FreestyleSpecJson, nullable repository fields), kind-aware
      validation/create/edit, steps save endpoint, freestyle trigger branch, webhook guards.
- [x] Worker: claim mirror, `WorkspacePath`, freestyle `PipelineRunner` path, script file execution.
- [x] UI: kind selector + toggle script, steps editor + row script, kind-aware tabs/overview/settings,
      project list badge.
- [x] Tests: schema, builder, trigger/claim (in-memory), process E2E (success, failure taxonomy,
      workspace, artifacts).

## Dependencies

- none

## Test Cases

### TC-001: A Freestyle run executes its saved scripts in the workspace and publishes artifacts

Steps:
1. Create a Freestyle project through the form; save three steps (echo; write `report.txt` +
   declare the artifact; print the working directory).
2. Register a worker and trigger a run.

Expected:
- Run `Success` with exit 0, `GitCommit` null, `repository/` empty, `workspace/` created.
- Three `Success` step rows; the script's output attributed per step; the cwd line names the
  workspace; the artifact is published from the workspace and downloads with its content.

### TC-002: A failing script fails its step and the run, and skips the rest

Steps:
1. Save `echo` / `exit 3` / `echo never`; trigger a run.

Expected:
- Run `Failed` with exit code 3 and `step-failed`; step 1 Success, step 2 Failed with exit 3, step 3
  Skipped (`previous-step-failed`).

### TC-003: The trigger freezes the saved spec and the claim carries it without a repository

Steps:
1. Create a Freestyle project, save steps, trigger, claim as a worker.

Expected:
- The run row snapshots the spec (envelope-valid v2) with empty branch/pipeline path; the claim JSON
  carries `pipelineSpecJson` and an explicit `repositoryUrl: null`; a Freestyle project without saved
  steps is refused with `steps-missing` and queues nothing; a refused save leaves the previous spec
  untouched.

## Development Log

### 2026-11-15

- Implemented end to end (Shared contract, Server data/services/UI, Worker execution path, tests).
- E2E found the missing `PipelineSpecJson` wiring into `PipelineRunOptions` (the worker still cloned,
  failing `git-invalid-url` on a null URL) - fixed, both process tests green.
- Suite note: the milestone suite runs the Release binaries and does not compile them; rebuild
  Release before re-running it.
- F-021 follow-up fixed three pre-existing assertions that encoded the old contract: the definition
  endpoint's "v2 is invalid" case now uses v3, the Pipelines tab asserts the frozen version rather
  than a literal v1, and the Q-006 inventory gained the two new definition endpoints. The full
  non-Milestone suite is green (1111/1111 with F-021's tests).
- Review fixes: the steps save's PRG target was the overview (`/projects/{id}?tab=steps`), and the
  overview reads neither `error` nor a steps notice - a refused save showed nothing and a successful
  one dropped the editor. It now redirects to `/projects/{id}/pipelines`, the route that owns the
  editor, which renders the notice and the code; the overview's two now-unused notice entries are
  gone. The settings "Project basics" card no longer renders blank repository/branch/pipeline rows
  for a Freestyle project. The envelope's JSON-refusal message no longer claims version 1.
- Suite fixes: `MilestoneFreestyleTests` read the log straight after the run turned terminal, which
  races the log flush - it failed once in four runs and passed alone. It now waits for the first and
  last step's own output (the step attribution assertions are unchanged).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Shared/Pipelines/PipelineSpec.cs`, `src/Shared/Pipelines/StepReference.cs`,
  `src/Shared/Projects/ProjectKinds.cs`, `src/Server/Projects/FreestyleSteps.cs`,
  `src/Server/Data/Migrations/20261115145021_AddProjectKindFreestyleSpec.cs`,
  `src/Worker/Pipelines/PipelineRunner.cs`, `src/Worker/WorkDirectory.cs`,
  `src/Server/Components/Shared/FreestyleStepsEditor.razor`, `src/Server/wwwroot/js/freestyle-steps.js`
- How to run/verify: `dotnet build -c Release && dotnet test tests/Zenkins.Tests -c Release` (schema,
  builder, trigger/claim, and the `Milestone` freestyle process E2E); or by hand: `dotnet run
  --project src/Server`, create a Freestyle project, save steps, start a run.
- Results: schema/builder/trigger-claim unit suites and both `MilestoneFreestyleTests` process tests
  pass on the Release build (macOS arm64). After the review fixes the non-Milestone suite is 1113
  passed / 0 failed and `MilestoneFreestyleTests` + `MilestoneParameterTests` are 4 passed / 0 failed
  (two consecutive runs).
