---
id: F-033
title: Per-step worker requirements and label expressions — the runs_on the PRD already promises
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A step can require worker capabilities (`runs_on = { "windows", "dotnet" }`, plus
`&&` / `||` / `!` expressions), the run's required labels are the union of what its
steps need, and an unsatisfiable requirement is refused with an explanation instead
of running on the wrong machine.

## Background

PRD §3.4.4 documents `runs_on` in its example pipeline, and the implementation has
no such field: `PipelineStep` carries no worker requirement, and the only knob is
the run-level `RequiredTags` a person types at trigger time. Q-010 records the
consequence: the trigger accepts a union of tags no single worker can satisfy, then
the run sits in the queue forever.

Two honest paths exist, and the card must say which one it takes:

- **(a) split a run across workers** — workspace hand-off, artifact/log transfer,
  per-step leases. Large; it is the prerequisite for parallel stages and belongs
  with that work, not here.
- **(b) resolve the requirement at definition time** — compute the union, let the
  scheduler assign one worker that satisfies all of it, and refuse a definition
  whose steps cannot share one worker.

This card takes (b): it closes the PRD gap and the Q-010 asymmetry without pretending
to do cross-worker builds.

## Acceptance Criteria

- [ ] Lua accepts `runs_on = { "linux", "arm64" }` (all must match) and an
      expression string (`"linux && (arm64 || x64)"`, `!windows`) per step; the
      frozen spec stores the normalized requirement per step.
- [ ] The run's required labels are the union of its steps' requirements combined
      with the trigger's `RequiredTags`; a step whose requirement contradicts another
      step's is refused at definition time as `pipeline-invalid` with both steps
      named.
- [ ] Expression parsing (`&&`, `||`, `!`, parentheses, tag atoms) is one shared
      implementation used by save-time validation, the trigger's satisfiability check
      and the scheduler — not three interpretations.
- [ ] A trigger whose union no single worker satisfies is refused before a run row
      exists, with an actionable error naming the missing combination (Q-010's
      asymmetry closed, and Q-010's card updated to say so).
- [ ] The scheduler assigns a worker that satisfies the whole expression; a worker
      that satisfies part of it is never chosen.
- [ ] The run page shows the requirement in its readable form and, on a queued run
      whose requirement has no matching worker, says which label is missing instead
      of "queued" forever.
- [ ] Freestyle steps get the same field in the steps editor.
- [ ] PRD §3.4.4 and §3.3.5: record the expression grammar, the union rule and the
      explicit statement that a run executes on one worker.

## Subtasks

- [ ] Shared `WorkerRequirement` type: parse (Lua table or expression string),
      normalize, `SatisfiedBy(tags)`, `Union`, and a readable `ToString`.
- [ ] Spec: `PipelineStep.RunsOn` (normalized requirement), validation rules and the
      contradiction check across steps.
- [ ] Definition phase: collect per-step requirements into the spec.
- [ ] Scheduler/claim: evaluate the union against candidate workers' tags; the
      existing `RequiredTags` string stays the storage shape (or is upgraded to the
      normalized form — record the migration decision).
- [ ] Trigger path: satisfiability check against known workers' tags before insert;
      Q-010's validation asymmetry test updated to the new contract.
- [ ] UI: requirement display on the run page and in the pipelines tab; Freestyle
      field.
- [ ] Update Q-010 (close or re-scope) and the PRD sections named above.

## Dependencies

- Q-010 (the tag asymmetry this card closes)
- Q-005 (multi-worker tag assignment) — accepted, its scheduler is the evaluation
  point

## Test Cases

### TC-001: An expression selects the right worker

Steps:
1. Two workers: tags `linux,x64` and `linux,arm64`. A pipeline with one step
   `runs_on = "linux && arm64"`.

Expected:
- The arm64 worker claims it; the x64 worker never does.

### TC-002: Contradicting steps are refused at definition time

Steps:
1. Step A `runs_on = { "windows" }`, step B `runs_on = { "linux" }`; trigger.

Expected:
- The run fails as `pipeline-invalid`, naming both steps and both labels; no run
  sits queued.

### TC-003: An unsatisfiable trigger is refused, not queued

Steps:
1. Only `linux` workers exist; trigger with `RequiredTags = "windows"` (and a
   step-level `windows`).

Expected:
- The trigger is refused with a message naming the missing tag; no run row exists.
- The same request where a `windows` worker exists queues and is claimed.

### TC-004: The requirement is readable, not a raw expression blob

Steps:
1. Open a run whose requirement is `linux && arm64`.

Expected:
- The page reads "linux and arm64" (or the normalized form), and a queued run with
  no match names the missing label.

## Notes and Decisions

- Rejected in this card: cross-worker step execution. It needs workspace transfer,
  per-step leases and a different cancellation story; it is the prerequisite of
  parallel stages, and that work has its own place (parking lot).
- Requirement storage is normalized text on the spec and on the run, so a later
  scheduler change cannot reinterpret old runs.
