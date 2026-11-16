---
id: F-022
title: Inline pipeline script — a Pipeline project whose pipeline.lua lives on the Server, not in SCM
type: feature
priority: P1
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A `pipeline` project can take its definition from the Server — a Lua body typed
and edited in the UI — instead of a repository, so a job can exist with no Git
remote and still get the full definition phase (a `pipeline{...}` table, Lua
logic that chooses steps, a `triggers` block, pipeline-level `env`).

## Background

Jenkins gives a Pipeline job two Definitions: **Pipeline script** (inline) and
**Pipeline script from SCM**. Zenkins only has the second one. F-020 added the
other *kind* (Freestyle), but Freestyle is not the same thing:

- Freestyle freezes a **spec** the Server built from UI step drafts; there is no
  definition phase, so no `pipeline{}` table, no Lua logic, no `triggers` block,
  no Lua-declared pipeline `env`, and every step is a `script` step.
- An inline pipeline runs the **script** on the Worker exactly like a repository
  pipeline; only the source of the bytes changes.

The enabling insight: `Project.Kind` is data, the frozen spec is the one
execution contract for both kinds, and the Worker's claim payload already carries
either a repository or a frozen spec (F-020). An inline pipeline needs a third
shape: no repository, no frozen spec *yet*, and a script text the Worker runs the
definition phase against.

## Acceptance Criteria

- [ ] A `pipeline` project can be created with source `inline` and a Lua body;
      the repository fields are null and the injectable defaults (branch, commit,
      pipeline path) record the honest empty value, exactly as Freestyle does.
- [ ] A run of an inline project skips the clone and runs the definition phase
      from the script; the frozen spec, step records, logs, secrets, artifacts,
      cancellation and deadline behave exactly as for a repository pipeline.
- [ ] The script is **snapshotted onto the run** at trigger time: editing the
      project afterwards does not change a queued or running run.
- [ ] A syntax or runtime error in the definition phase fails the run as
      `pipeline-invalid` with the same `pipeline.lua:(line,col)` diagnostic a
      repository pipeline produces.
- [ ] Webhook delivery is refused for an inline project (no repository, nothing
      to push) with the same guard shape Freestyle already has, and the Git
      credential settings are hidden and refused.
- [ ] The Pipelines tab offers a Validate action that runs the definition phase
      on a Worker and shows the resulting step list without executing a step and
      without creating a run.
- [ ] PRD §3.2/§3.3 record the inline source as part of the Pipeline kind (and
      §7.2's "no visual pipeline editor" note stays true: the editor is text).

## Subtasks

- [ ] `Project.PipelineSource` (`repository` | `inline`, immutable after create
      like `Kind`) plus `Project.InlineScript` (bounded, e.g. 64 KiB).
- [ ] `BuildRun.PipelineScriptText` snapshot, carried in the claim payload
      alongside `repositoryUrl` and `pipelineSpecJson`.
- [ ] `RunService.CreateRunAsync` freeze path: inline projects snapshot the
      script, skip the credential/branch requirements, and keep the parameter and
      required-tag validation unchanged.
- [ ] Worker: when the claim carries a script and no repository, write it to
      `work/{run-id}/temp/pipeline.lua` and run the definition phase against that
      path so error positions and the frozen spec name stay `pipeline.lua`.
- [ ] Validate action: a definition-only task (no clone, no steps, no run row)
      that returns the spec or the diagnostic; it must not consume a build slot.
- [ ] UI: script editor on the Pipelines tab for the inline source (plain
      `<textarea>`, no editor library), save through the existing POST + antiforgery
      pattern, Validate button with its result.
- [ ] Secret reference capture: prove the inline path records
      `secretReferences` the same way the repository path does.

## Dependencies

- F-020 (kind column, server-frozen spec, script steps) — in review
- F-014/F-015 (definition phase, frozen spec, step records) — accepted

## Test Cases

### TC-001: An inline pipeline builds with no repository

Steps:
1. Create an inline pipeline project whose script declares two `run` steps.
2. Trigger a run; let the test worker claim and execute.

Expected:
- The run is `Success` with two step records and their logs.
- The run's `PipelinePath` is `pipeline.lua`, branch and commit are empty.
- The worker's work directory has no `repository/` entry.

### TC-002: The script is frozen per run

Steps:
1. Trigger a run of an inline project; leave it queued (no worker).
2. Edit the project script; then let a worker execute the queued run.

Expected:
- The queued run executes the **old** script, and the run page's spec matches it.
- A newly triggered run uses the new script.

### TC-003: A broken script is a pipeline failure, not a crash

Steps:
1. Save a script with a syntax error; trigger a run.

Expected:
- The run ends `Failed` with failure reason `pipeline-invalid` and one log line
  carrying `pipeline.lua:(line,...)`.
- No step record was created; the worker is still healthy and claims the next run.

## Notes and Decisions

- Rejected: a third `Kind` (`inline-pipeline`). Kind is immutable and every guard
  (webhook, credential, artifact paths) would multiply; a source column keeps one
  kind with two definition sources.
- Rejected: routing the inline Lua through the Freestyle spec editor. Two
  editors, two validation paths, and the definition phase would be lost.
- Validation on save runs on a Worker (the Server has no Lua engine, I-004). The
  card's Validate action is the honest version of Jenkins' "validate on save";
  saving alone must never claim a script is valid.
