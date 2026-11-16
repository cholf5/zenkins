---
id: F-030
title: Stage grouping — named stages in the frozen spec, with per-stage status and duration
type: feature
priority: P2
size: L
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A pipeline can group its steps into named stages, and the run page shows those
stages with their aggregate status and duration, so a 40-step build reads like
Build → Test → Package instead of a flat list.

## Background

The frozen `PipelineSpec` is a flat `PipelineStep[]` (schema v2). Steps execute
linearly and that stays true — DAG and parallel execution are explicitly out of
scope (PRD §7.2, parking lot) — but *presentation* is a real gap: Jenkins users
navigate by stage, and the run page currently renders every step at one level.

The constraint that shapes the design: the stage name must live **inside the frozen
spec**, not be derived later from step-name prefixes, because a run's display must
never change after the fact. The envelope must keep accepting v1/v2 specs, which
means the version bump has to be additive and the old shape has to render as "one
unnamed stage" or "no stages" honestly.

## Acceptance Criteria

- [ ] Spec schema v3 adds an optional stage name per step; v1 and v2 specs still
      read, render and execute exactly as today.
- [ ] Lua can declare a stage: `step { name = "Build", stage = "Build", run = {...} }`
      and a `pipeline { stages = { ... } }` sugar form that assigns the stage name
      to the steps it contains; consecutive steps with the same stage name form one
      stage, a step with no stage renders outside any stage section.
- [ ] The run page groups steps into collapsible stage sections: worst-status badge,
      duration = last end − first start, step count, and the existing per-step log
      view unchanged inside each section.
- [ ] A failed stage is open by default and the page jumps to the first failed step;
      successful stages are collapsed.
- [ ] The frozen spec's stage names are immutable per run (they are part of the
      spec), and stage names are constrained (non-empty, ≤64 chars, no leading or
      trailing whitespace).
- [ ] Freestyle (F-020) steps can optionally carry a stage name in the steps editor,
      with the same rendering.
- [ ] Execution is byte-for-byte unchanged: no skipping, no reordering, no
      parallelism, and the step executor reads the same list.
- [ ] PRD §3.3.5 and the spec-section of `docs/lua-runtime.md` record v3.

## Subtasks

- [ ] `PipelineStep.Stage` (nullable string) plus schema v3 in `PipelineSpecSchema`;
      `TryRead` accepts 1/2/3 and refuses 4+.
- [ ] Lua sugar: assign a stage to steps created inside a `stages` table entry;
      keep the existing `step`/`run` surface intact (no new execution API).
- [ ] Freestyle: a stage input per step row, parsed server-side like the other
      fields (never authored by the browser).
- [ ] Run page: group the merged step views (the existing merge of declared steps
      and reported step rows) by stage, with the aggregate calculations done from
      the reported start/end times.
- [ ] Tests: v1/v2/v3 envelope reads, grouping order, aggregate status, and a
      golden case for "step without a stage".

## Dependencies

- F-014/F-015 (frozen spec, step records) — accepted
- F-020 (the script step and the v2 bump being additive) — in review

## Test Cases

### TC-001: A three-stage pipeline renders three groups

Steps:
1. Define a pipeline with stages Build (2 steps), Test (1 step), Package (1 step).
2. Run it to success; open the run page.

Expected:
- Three stage sections in declaration order, each with its steps and duration.
- The spec JSON stores each step's stage name.

### TC-002: A failed step marks its whole stage

Steps:
1. Define two steps in stage Test where the second fails.

Expected:
- The Test section's badge is failed and it is expanded by default.
- Build (earlier, successful) and any later stage keep their own honest status.

### TC-003: Old specs still render

Steps:
1. Seed a run whose frozen spec is v2 (no stage field); open its page.

Expected:
- The steps render in a single unnamed list (no empty stage headers), and the run
  page does not fail to read the spec.

## Notes and Decisions

- Rejected: `Stages` as a nested array in the spec. It would fork the executor and
  the step index, and every consumer (run page, step reports, artifacts) would need
  two shapes. A string on the step keeps one list and one index.
- Rejected: deriving stages from `name` prefixes. A rename must not rewrite
  history.
