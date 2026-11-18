---
id: F-030
title: Stage grouping — named stages in the frozen spec, with per-stage status and duration
type: feature
priority: P2
size: L
status: done
created: 2026-11-16
updated: 2026-11-18
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

- [x] Spec schema v3 adds an optional stage name per step; v1 and v2 specs still
      read, render and execute exactly as today.
- [x] Lua can declare a stage: `step { name = "Build", stage = "Build", run = {...} }`
      and a `pipeline { stages = { ... } }` sugar form that assigns the stage name
      to the steps it contains; consecutive steps with the same stage name form one
      stage, a step with no stage renders outside any stage section.
- [x] The run page groups steps into collapsible stage sections: worst-status badge,
      duration = last end − first start, step count, and the existing per-step log
      view unchanged inside each section.
- [x] A failed stage is open by default and the page jumps to the first failed step;
      successful stages are collapsed.
- [x] The frozen spec's stage names are immutable per run (they are part of the
      spec), and stage names are constrained (non-empty, ≤64 chars, no leading or
      trailing whitespace).
- [x] Freestyle (F-020) steps can optionally carry a stage name in the steps editor,
      with the same rendering.
- [x] Execution is byte-for-byte unchanged: no skipping, no reordering, no
      parallelism, and the step executor reads the same list.
- [x] PRD §3.3.5 and the spec-section of `docs/lua-runtime.md` record v3.

## Subtasks

- [x] `PipelineStep.Stage` (nullable string) plus schema v3 in `PipelineSpecSchema`;
      `TryRead` accepts 1/2/3 and refuses 4+.
- [x] Lua sugar: assign a stage to steps created inside a `stages` table entry;
      keep the existing `step`/`run` surface intact (no new execution API).
- [x] Freestyle: a stage input per step row, parsed server-side like the other
      fields (never authored by the browser).
- [x] Run page: group the merged step views (the existing merge of declared steps
      and reported step rows) by stage, with the aggregate calculations done from
      the reported start/end times.
- [x] Tests: v1/v2/v3 envelope reads, grouping order, aggregate status, and a
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

## Implementation Notes

- **The stage lives on the step, in the frozen spec.** `PipelineStep.Stage` (nullable) is serialised
  into `PipelineSpecJson` and read back from it; nothing at render time reads the step's name. The
  schema is v3 (`PipelineSpecSchema.Version`), `AcceptedVersions = [1, 2, 3]`, v4+ refused, and a v1/v2
  document that carries a `stage` is refused as a version/shape contradiction instead of being rendered
  as if the field had always been there. Null stages are omitted by the serializer, so an old spec's
  JSON is byte-for-byte what it was.
- **Two Lua spellings, one frozen shape.** `step { stage = "Build", … }` sets the field on that step;
  `pipeline { stages = { { name = "Build", steps = { … } }, … } }` writes the entry's name onto every
  step it contains and flattens the entries into the one ordered step list (an entry without `name`
  leaves its steps unstaged). `stages` is an ordered list on purpose - a Lua map has no declaration
  order to show, so the map form is refused. `steps` and `stages` are mutually exclusive.
- **Grouping is by adjacent runs.** `RunStages.Group` walks the merged `RunStepView` list
  (`RunService.BuildStepViews`, which now carries each declared step's stage) and starts a new section
  when the name changes. A repeated name after another stage is a second section; an unstaged step is an
  unnamed section rendered outside every header. Worst state is Failed > Running > Pending > Success >
  all-Skipped; duration is the last recorded end minus the first recorded start (a span, not a sum).
- **Page.** `RunDetail.razor` renders each named section as a `<details id="run-stage-{name}">` with a
  badge, duration and step count; `open` is the card's default (Failed and Running open, everything else
  collapsed). The step rows moved into `RunStepList.razor` unchanged (same ids, same "View log" jump), so
  a flat v1/v2 run renders exactly today's list. `js/run-stages.js` scrolls to the single row marked
  `data-run-first-failed` on load and on Blazor enhanced navigation, unless the reader arrived with
  their own fragment.
- **No migration.** The stage is spec JSON only; no entity column and no schema change.

## Verification

- Related files: `src/Shared/Pipelines/PipelineSpec.cs`, `src/Worker/Pipelines/PipelineSpecReader.cs`,
  `src/Worker/Lua/prelude.lua`, `src/Server/Runs/RunStages.cs`, `src/Server/Runs/RunService.cs`,
  `src/Server/Components/Pages/Runs/RunDetail.razor`,
  `src/Server/Components/Shared/RunStepList.razor`, `src/Server/wwwroot/js/run-stages.js`,
  `src/Server/Projects/FreestyleSteps.cs`,
  `src/Server/Components/Shared/FreestyleStepsEditor.razor`,
  `src/Server/wwwroot/js/freestyle-steps.js`, `docs/prd.md`, `docs/lua-runtime.md`.
- New/changed tests: `tests/Zenkins.Tests/Pipelines/PipelineStageTests.cs`,
  `tests/Zenkins.Tests/RunUi/RunStagePageTests.cs`, plus additions to `PipelineRunnerTests`
  (execution order, v2 still executes), `FreestyleRunTests` (editor field + frozen spec),
  `FreestyleStepsTests` (server-side parse), `PipelineSpecTests` (v3 is current, v4 refused),
  `RunUi/RunUiHost.cs` (staged-run helper).
- How to run: `dotnet build -c Release`; then
  `dotnet test -c Release --filter "FullyQualifiedName~PipelineStageTests|FullyQualifiedName~RunStagePageTests"`.
  The page facts use the real server and worker API through `RunUiHost`, not a rendered component.
- Results (macOS arm64): build 0 warnings / 0 errors. `--list-tests` with
  `Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres&Category!=Load`
  discovers **1561** tests (main had 1510; this card adds 51). The affected suites (pipeline spec/runner,
  run page, Freestyle builder/run, step records, secret references, Pipelines tab) are **228 passed /
  0 failed** on two consecutive runs; one earlier run of the same set had a single unrelated flake whose
  rerun passed.
- Mutation checks (each broke one mechanism, confirmed red, restored): stage lost from the sugar; v3
  dropped from `AcceptedVersions`; the adjacency run broken; duration replaced by the sum of step
  durations; worst-state order changed; Running sections collapsed by default; the page's failed-step
  marker removed; Freestyle stage dropped from the builder; v2+stage contradiction allowed; step order
  reversed in the frozen list (which also reddened the pre-existing in-order execution fact).

## Close-out verification (2026-11-18)

Stages arrived as a presentation change and stayed one: the executor still reads the same flat `Steps`, in
the same order, and a fact proves it with a real git clone and real processes whose marker files show
restore, build, test and package in that order.

- **The name lives in the frozen spec, never in a step-name prefix.** Schema v3 adds an optional stage per
  step; v1 and v2 still read, render and execute exactly as before, re-serialise without the field, and - the
  part that keeps the display honest - a v1/v2 document that carries a stage is refused as a contradiction
  rather than being rendered as if it had always had one.
- **Two Lua spellings, one frozen spec.** A per-step `stage =`, and a `pipeline { stages = { … } }` sugar that
  assigns the entry name and flattens in declaration order; `steps` and `stages` are mutually exclusive, a
  map form is refused because a Lua map has no declaration order, and a step inside a named entry that also
  declares its own stage is refused rather than silently overridden. The two spellings producing the same
  frozen spec is a fact, not a convention.
- **Grouping follows adjacency, not names.** Consecutive steps sharing a name are one section and a later
  repeat is another section, which is the rule that makes "Build → Test → Build" honest.
- **The section's numbers are the section's, not the sum of its parts.** Duration is last end minus first
  start - including the gap between steps - and the badge is the worst state among Failed, Running, Pending,
  Success and all-Skipped. Both have mutations that redden only their own assertion.
- **Opening behaviour is a policy with two defaults**: failed or running sections open, everything else
  collapsed, and the page scrolls to the single step marked as the first failure unless the reader arrived
  with their own fragment.
- **A step outside every stage renders outside every stage section** rather than being absorbed into the
  previous one - the dishonest option is the convenient one here.
- **Freestyle gets the same field** through its own draft and the shared name validator, so the three
  spellings of a pipeline (Lua, Freestyle, stored JSON) cannot drift apart on what a stage name is.
- **Counts.** fast 1526 -> 1577 measured on the merged tree (51 facts: spec, grouping, run page, executor and
  Freestyle), Postgres stays 9, the slow categories 24, everything 1610. The single batch run at the end of
  the series is where the full suite and Postgres are verified.
