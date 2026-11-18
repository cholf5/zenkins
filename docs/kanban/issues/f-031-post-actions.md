---
id: F-031
title: Post actions — always / on-success / on-failure steps that run after the last step
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-18
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

- [x] Spec v3 adds `Post` with `always`, `success` and `failure` step lists (the
      same `PipelineStep` shape, so run/env/conditions/artifacts all apply).
- [x] After the main list ends, the matching outcome set runs, then `always` runs
      last — success-then-always or failure-then-always, never both outcome sets.
- [x] A post step's failure is recorded as its own step failure and shown on the
      page, but does **not** flip a successful run to failed by default (an explicit
      project switch can make it fail the run; documented).
- [x] On cancellation, `always` still runs inside a bounded grace window (e.g. 30
      seconds) so cleanup is not skipped; on a deadline-triggered cancel the grace is
      clamped to whatever the deadline left, and a post step that overruns is killed
      and recorded.
- [x] A canceled run reports its post steps honestly: a cleanup that succeeded after
      the cancel does not change the terminal status from `Canceled`.
- [x] Post steps appear in the run page with a `post/` prefix in their record name
      and their own logs; the step count and ordering are stable.
- [x] Freestyle gets the three post lists in the steps editor, frozen into the spec
      like the main steps.

## Subtasks

- [x] Spec v3: `PipelinePost` record on `PipelineSpec`, envelope rules (bounded
      count, unique names against the main list to keep logs unambiguous).
- [x] Lua: `post = { always = {...}, success = {...}, failure = {...} }` inside
      `pipeline{}`; the definition phase freezes it like steps.
- [x] Executor: a post phase after the main loop, fed by the terminal outcome; step
      indices continue so step records and logs keep one sequence.
- [x] Cancellation/deadline plumbing: the grace window must be visible to the run
      directory's process runner and must not resurrect a canceled run.
- [x] Freestyle editor: three post sections (own JSON array), server-side build.
- [x] Run page: the post section rendered apart from the main list, with the
      prefixed names and one note explaining why the run's status did not change.
- [x] PRD §3.3.5 / `docs/lua-runtime.md` §6.1: record the post contract and the
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

## Implementation Notes

- **Spec.** `PipelineSpec.Post` (`PipelinePost`: `success`, `failure`, `always`) holds ordinary
  `PipelineStep` records, additive to v3. Each frozen post name carries `post/` **in the spec**
  (`post/cleanup`), so a step record, a log line and the page read one unambiguous name; authors write
  the plain name and the Lua reader / Freestyle builder add the prefix (writing it by hand is refused).
  The envelope bounds each list (`MaxPostStepsPerList = 40`), requires the prefix, keeps names unique
  across the whole spec, refuses a `stage` on a post step, refuses `post` on a v1/v2 document, and
  re-checks F-033's requirements inside each list (`pipeline.post.<outcome>[n].runs_on …`). A spec
  without `post` re-serialises byte-for-byte as before.
- **Executor.** `RunMainStepsAsync` decides the run's outcome; `RunPostAsync` then plans **one** outcome
  set (`success` for a clean list, `failure` for a failed one) followed by `always`, and runs them
  through the same `ExecuteOneStepAsync` the main list uses (environment composition, `when`, secrets,
  artifacts, attempts). Post records continue the step index (`main.Length + n`), so ordering and the
  log sequence stay one list. A post failure is recorded as its own failed step, does not stop the
  remaining post steps, and by default does **not** change the run's outcome; `PostFailureFailsRun`
  (project switch, carried by the claim) turns the first post failure into a failed run (`post-failed`).
  A canceled run runs `always` only - a cancel is not a pipeline failure - and keeps `Canceled`.
- **The grace window.** A canceled run's post phase runs under a fresh token
  (`CancellationTokenSource.CancelAfter`), bounded by `PostGrace` (default 30 s) and clamped to whatever
  the run's deadline has left. A deadline cancel therefore gets only the remainder (often nothing:
  then the plan is recorded `Skipped`/`run-canceled`), an operator cancel gets the full window, and an
  overrunning step is killed and recorded - `post-grace-exceeded` for the operator window,
  `timed-out` for the deadline-clamped one. The phase bound is also what the step's own timeout is
  compared against, and the log names which limit governs.
- **Server.** A step report beyond the main list is accepted for any **declared** post name (the worker
  assigns the positions in execution order; the server cannot predict which set runs), within
  `main.Length + post.Count`; the ordering/write-once checks are unchanged. `RunStepView` gained
  `IsPost`, `PostOutcome` and `Argv`; the page renders `PostSteps` in their own `<section id="run-post">`
  after the main list, and `PostFailureNote` explains an unchanged result or the switch-driven failure.
  The main list and `StageGroups` ignore post rows.
- **Project switch.** `Project.PostFailureFailsRun` (default false) + `ProjectSettings` form
  (`POST /projects/{id}/post-failure`) + service setter. The claim carries it
  (`postFailureFailsRun`) to the worker that executes the phase. Migration
  `20261118142939_AddProjectPostFailureSwitch` (PostgreSQL provider; boolean, default false).
- **Freestyle.** Three post lists in the editor (`AlwaysStepsJson`, `SuccessStepsJson`,
  `FailureStepsJson`), each a row list sharing the one row component; `js/freestyle-steps.js`
  serializes each list into its own hidden field. The server builder parses and prefixes exactly like
  the Lua reader. Saved post rows show the authored name (the prefix is stripped for display).

## Verification

- Related files: `src/Shared/Pipelines/PipelineSpec.cs`,
  `src/Shared/Pipelines/PipelineFailureReasons.cs`, `src/Shared/Pipelines/PipelineStepContract.cs`,
  `src/Worker/Pipelines/PipelineSpecReader.cs`, `src/Worker/Pipelines/PipelineRunner.cs`,
  `src/Worker/Lua/prelude.lua`, `src/Worker/WorkerApiClient.cs`, `src/Worker/WorkerRunLoop.cs`,
  `src/Server/Runs/RunService.cs`, `src/Server/Runs/RunEndpoints.cs`,
  `src/Server/Components/Pages/Runs/RunDetail.razor`,
  `src/Server/Components/Shared/RunStepList.razor`, `src/Server/Components/Shared/RunDisplay.cs`,
  `src/Server/Data/Entities/Project.cs`, `src/Server/Data/Migrations/20261118142939_AddProjectPostFailureSwitch.cs`,
  `src/Server/Projects/ProjectService.cs`, `src/Server/Projects/ProjectEndpoints.cs`,
  `src/Server/Projects/FreestyleSteps.cs`,
  `src/Server/Components/Shared/FreestyleStepsSection.razor`,
  `src/Server/Components/Shared/FreestyleStepsEditor.razor`,
  `src/Server/Components/Pages/Projects/ProjectSettings.razor`,
  `src/Server/Components/Pages/Projects/ProjectPipelines.razor`,
  `src/Server/wwwroot/js/freestyle-steps.js`, `docs/prd.md`, `docs/lua-runtime.md`.
- New/changed tests: `tests/Zenkins.Tests/Pipelines/PipelinePostTests.cs`,
  `tests/Zenkins.Tests/Pipelines/PostStepEndpointTests.cs`,
  `tests/Zenkins.Tests/RunUi/RunPostPageTests.cs`, plus additions to `PipelineRunnerTests` (order,
  default/switch failure semantics, cancel grace, grace overrun, deadline clamp, no-window skip),
  `FreestyleStepsTests` and `FreestyleRunTests` (builder + editor field), and a
  `ManualClock.Advance` helper for the deadline-clamp facts.
- How to run: `dotnet build -c Release`; then
  `dotnet test -c Release --filter "FullyQualifiedName~PipelinePostTests|FullyQualifiedName~PostStepEndpointTests|FullyQualifiedName~RunPostPageTests|FullyQualifiedName~PipelineRunnerTests"`.
- Results (macOS arm64): build 0 warnings / 0 errors. `--list-tests` with
  `Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres&Category!=Load`
  discovers **1611** tests (main had 1577; this card adds 34). The focused suites are **184 passed /
  0 failed** (pipeline post/spec/runner, post endpoints, run page, run UI, step records, model snapshot)
  and a second affected set (`Freestyle*`, `ProjectUi`, `ProjectCrud`, `SecretUiReferences`, `Steps`,
  API v1 runs, run validation/endpoints) is **177 passed / 0 failed**. No full-suite or PostgreSQL run.
- Mutation checks (each broke one mechanism, confirmed red, restored): post names not prefixed; the
  wrong outcome set chosen on failure; `always` planned before the outcome set; a post failure always
  flipping the run; the project switch ignored; no cancel grace; swapped grace reasons; a deadline
  cancel ignoring the remaining time; the no-window guard removed; the server accepting an undeclared
  post name; the server accepting an index beyond the spec; the claim dropping the switch; the page's
  post section never rendering; the page note missing the `post-failed` case; the Freestyle builder
  dropping the post lists; a spec without `post` no longer executing.

## Close-out verification (2026-11-18)

Post actions arrived as frozen steps rather than a second execution path, which is what made the rest of the
card tractable: environment, conditions, secrets, artifacts, attempts and timeouts all apply because a post
step *is* a step, executed by the same method as a main step, with its phase boundary and index carried
alongside.

- **Order and exclusivity.** After the main list, exactly one outcome set plans - success or failure, never
  both - and `always` runs last in either case, proved with real marker files (`main -> success -> always`,
  `failure -> always`) and mutations that swap the order or pick the wrong set.
- **A failed post step does not flip a successful run by default.** It is recorded as its own failed step,
  the page says why the result is unchanged, and a project switch can make it count - with a claim-level
  fact that the switch travels, because a worker that never learns it cannot honour it. Both directions have
  their own mutation; the page note has two.
- **Cancellation does not skip cleanup.** `always` runs inside a bounded grace: thirty seconds for a user
  cancel, clamped down to whatever the deadline leaves for a deadline cancel, killed with a reason that
  distinguishes `post-grace-exceeded` from `timed-out`, and no phase at all when the window is already zero.
  Four facts, four mutations, and the cancelled run stays cancelled whether the cleanup succeeded or failed.
- **The frozen spec carries prefixed post names** (`post/...`), the envelope bounds each list at forty steps
  and checks global uniqueness, a v1/v2 document carrying post is refused as a version contradiction, and a
  post step carrying a stage is refused - the phase and the stage group would otherwise disagree about what a
  section is.
- **The server accepts a declared post name at a continuing index** rather than predicting which set ran,
  bounded by `main.Length + post.Count`, so the report path stays positional for the main list and honest for
  the phase.
- **`spec.Post` being absent is the old shape**: a spec without post re-serialises without the key, renders
  without a section, and runs through the same code path - and the fact that a post-less spec would throw
  was mutated to prove the older facts really do exercise that path.
- **Counts.** fast 1594 -> 1628 measured on the merged tree (34 facts), Postgres stays 9, the slow categories
  24, everything 1661. The batch run at the end of the series verifies the full suite and Postgres.

## Follow-up: the downgrade path was dropping F-042's indexes (2026-11-18)

Landing F-031 beside F-038 and F-042 turned four integration reds into one real bug in the migrations rather
than in the tests.

`AddProjectPostFailureSwitch.Down` (this card) and `AddBuildRetention.Down` (F-038) were generated before F-042
existed. On SQLite a `DropColumn` rebuilds the table using the index set that was known when the migration was
generated, so stepping *down* through either of them silently removed `IX_Projects_FolderId` and
`IX_Projects_OrganizationId_FolderKey_NameKey`, and F-042's own `Down` then failed with `no such index`. In
other words the downgrade path lost schema that the upgrade path had created - invisible to every test that only
ever migrates forward, which is all of them except the three upgrade facts that caught it.

Both `Down` methods now use the native `ALTER TABLE ... DROP COLUMN` statement, which both providers accept and
which touches nothing else on the table. The round trip head -> AddApiTokens -> head and
head -> AddSecretRotationAttribution -> head both keep the folder indexes, and the model snapshot is unchanged
because only the backwards SQL moved.

The same commit registers F-031's `post-failure` endpoint in the Q-006 inventory, and - because the review
pointed out that the named probe never actually called the route - adds that POST to the tenant probe it names,
so the inventory row and the test it points at describe the same thing.
