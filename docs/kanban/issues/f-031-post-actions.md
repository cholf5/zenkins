---
id: F-031
title: Post actions — always / on-success / on-failure steps that run after the last step
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A pipeline can declare steps that run after the main list: a set for success, a set
for failure, and an `always` set that runs whatever happened (cleanup, artifacts,
notification), so cleanup no longer needs `continue_on_error` on a real step.

## Background

The only escape hatch today is `continue_on_error`: mark the cleanup step tolerant,
and then the step's own failure is invisible and the build looks clean when the
cleanup broke. Jenkins' `post { always / success / failure }` is the standard answer
and it composes with the existing executor (a post action *is* a step).

Related facts the card must respect: the run has a deadline (`RunOptions.DefaultTimeout`,
per-run override) and a cancellation path (F-009) — cleanup is most needed exactly
when a run is canceled, so "cancel skips the post section" would defeat the feature.

## Acceptance Criteria

- [ ] Spec v3 adds `Post` with `always`, `success` and `failure` step lists (the
      same `PipelineStep` shape, so run/env/conditions/artifacts all apply).
- [ ] After the main list ends, the matching outcome set runs, then `always` runs
      last — success-then-always or failure-then-always, never both outcome sets.
- [ ] A post step's failure is recorded as its own step failure and shown on the
      page, but does **not** flip a successful run to failed by default (an explicit
      project switch can make it fail the run; documented).
- [ ] On cancellation, `always` still runs inside a bounded grace window (e.g. 30
      seconds) so cleanup is not skipped; on a deadline-triggered cancel the grace is
      clamped to whatever the deadline left, and a post step that overruns is killed
      and recorded.
- [ ] A canceled run reports its post steps honestly: a cleanup that succeeded after
      the cancel does not change the terminal status from `Canceled`.
- [ ] Post steps appear in the run page with a `post/` prefix in their record name
      and their own logs; the step count and ordering are stable.
- [ ] Freestyle gets the three post lists in the steps editor, frozen into the spec
      like the main steps.

## Subtasks

- [ ] Spec v3: `PipelinePost` record on `PipelineSpec`, envelope rules (bounded
      count, unique names against the main list to keep logs unambiguous).
- [ ] Lua: `post = { always = {...}, success = {...}, failure = {...} }` inside
      `pipeline{}`; the definition phase freezes it like steps.
- [ ] Executor: a post phase after the main loop, fed by the terminal outcome; step
      indices continue so step records and logs keep one sequence.
- [ ] Cancellation/deadline plumbing: the grace window must be visible to the run
      directory's process runner and must not resurrect a canceled run.
- [ ] Freestyle editor: three post sections (own JSON array), server-side build.
- [ ] Run page: the post section rendered apart from the main list, with the
      prefixed names and one note explaining why the run's status did not change.
- [ ] PRD §3.3.5 / `docs/lua-runtime.md` §6.1: record the post contract and the
      cancel-grace rule.

## Dependencies

- F-030 (the v3 bump) — this card must not introduce v3 twice; if stages land first,
  post uses the same version; if not, this card owns the bump and F-030 follows.
- F-009 (cancellation and deadline) — accepted

## Test Cases

### TC-001: Success runs success then always, in that order

Steps:
1. Define `success` = step S, `failure` = step F, `always` = step A; run to success.

Expected:
- S then A run; F does not appear or appears as skipped.
- The run's step records show the `post/` names in order.

### TC-002: A failing post step does not hide the build result

Steps:
1. `always = { <a step that exits 1> }`; main steps succeed.

Expected:
- The run stays `Success`; the post step is recorded failed with its log.
- The page says the post step failed and why the run result is unchanged.

### TC-003: Cancellation still runs always, within the grace

Steps:
1. Start a long step, cancel the run.
2. `always` writes a file the test can see.

Expected:
- The run ends `Canceled`; the `always` step ran and its artifact/file exists.
- A `always` step that sleeps past the grace window is killed and recorded, and the
  terminal state is still `Canceled`.

## Notes and Decisions

- Rejected: `always` running before the outcome set. Cleanup after the outcome report
  is what most pipelines want, and Jenkins' order matches; deviating would surprise
  every migrating Jenkinsfile.
