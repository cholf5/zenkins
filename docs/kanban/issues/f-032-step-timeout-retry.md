---
id: F-032
title: Step-level timeout and retry — a hung or flaky step is the pipeline's problem, not the run's
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-18
blocked: none
---

## Goal

A step can declare its own timeout and a bounded retry count, so a hung command or a
flaky network call is handled where it happens and reported as such, instead of
consuming the whole run's deadline.

## Background

Only the run has a timeout today (`Runs:DefaultTimeoutMinutes` with a per-run
override); a step that hangs until the run deadline makes the run look like a
timeout when one step actually broke. Jenkins wraps steps in `timeout`/`retry` and
this product's executor already kills a process tree on cancellation, so both
mechanisms have their plumbing — what is missing is the per-step declaration and the
honest reporting.

## Acceptance Criteria

- [x] Spec adds `timeoutSeconds` (1..86400) and `retry` (1..5 attempts) per step; Lua accepts
      `timeout = 600, retry = 2`; the Freestyle editor has both fields.
      → `PipelineStep.TimeoutSeconds` / `.Retry` (optional, additive: the schema version stays 2),
      `PipelineSpecSchema.Min/MaxStepTimeoutSeconds` / `Min/MaxStepRetry`,
      `PipelineSpecReader.ReadStepLimit` (Lua, field paths), `PipelineSpecJson.TryReadStepLimit`
      (the Server's envelope gate), `FreestyleSteps.ParseStepLimit` (the editor's text), the two inputs
      in `FreestyleStepsEditor.razor` (saved row + row template) and their JS serialization. Both ends of
      each range are **accepted** (`timeout = 1`, `timeout = 86400`, `retry = 1`, `retry = 5`) as well as the
      value just past the edge being refused - a `<=`/`>=` written the wrong way round would otherwise make
      a legal extreme silently unusable.
- [x] A step that exceeds its own timeout is killed (process tree included), fails with the typed
      reason `step-timeout`, and does not consume other steps' budgets; the run continues as a normal
      failure (honoring `continue_on_error`).
      → `PipelineRunOptions` keeps F-009's tree-kill path: the attempt's limit is a
      `CancellationTokenSource` linked to the run's token, so `ProcessRunner`'s existing cancel kills the
      tree. `PipelineFailureReasons.StepTimeout` (run) and `PipelineStepFailureReasons.StepTimeout`
      (step) are the same word for the same limit.
- [x] The step timeout is clamped by the run's remaining deadline — the earlier of the two wins — and
      the page/logs say which limit fired.
      → the executor installs a timer **only** when the step's own limit is the earlier one; otherwise
      F-009's deadline governs and the step records `timed-out`. The step's start line states which limit
      governs, and `step-timeout` vs `timed-out` keep the two apart on the page. When a timer *is*
      installed it is a source **linked to the run's token**, so a user cancel still kills the attempt at
      once - a step declaring 300 s under a 10-minute run dies at the cancel, not at its own limit.
- [x] Retry attempts: a failed or timed-out attempt is retried up to the declared count; a cancellation
      never retries; each attempt appends a delimited section to the step's log, and the step record
      carries the attempt count and the final exit code.
      → the attempt loop in `PipelineRunner.ExecuteStepsAsync` (delimiter
      `--- attempt N/M ---`), `BuildSteps.Attempts` (migration `AddStepAttempts`, check constraint 1..5),
      `PipelineStepReport.Attempts` → `WorkerStepRequest.Attempts` → `RunStepReport.Attempts`
      (bounded, refused on a `Running` report), `PipelineFailureReasons.StepRetryExhausted`.
- [x] The step list stays 1:1 with the definition (one record per declared step, not one per attempt).
      → one `Running` + one terminal report per step; the attempts live in the log and in `.Attempts`.
      Asserted on **both sides**: the server rows (`steps.Count` == the frozen spec's step count, in two
      facts) *and* the rendered run page (`<li id="run-step-` occurrences == that count), plus one fact
      where a really retried step crosses the **real** `/steps` endpoint (both transitions accepted, one
      row, `Attempts = 2`, the final exit code) rather than the offline sink the runner tests use.
- [x] Secret references are resolved once per step, not per attempt: a retry never re-fetches material.
      → resolution stays **before** the attempt loop (`StepValueResolver` + `ResolveEnvironmentAsync`),
      so a retry re-runs the process with the values it already has. The fact's source hands out a
      **different value per fetch** (`v1`, `v2`, ...) and the child writes the value it received into a
      file, so the second attempt must still show `v1`: a re-resolution that produced a fresh value cannot
      hide.
- [x] Default behaviour is unchanged: a step without either field behaves exactly as today.
      → both fields are absent (`null`) by default, `AttemptCount` is 1, no timer is created, and the
      existing 126 `Category=Pipeline` facts (byte-identical log lines included) still pass unchanged. A
      step that declares nothing now runs **6.5 s of real work** in a fact, so an invented default
      (`?? 5s`) is visible instead of silently shrinking what a pipeline may do.

## Subtasks

- [x] Spec: two nullable fields with schema validation and Lua sugar; envelope acceptance rules
      unchanged in version (additive optional fields).
- [x] Executor: per-attempt process launch with its own timeout token, combined with the run's
      cancellation token and deadline via a linked source; attempt loop; attempt delimiters written
      through the existing step log sink.
- [x] Failure reasons: add `step-timeout` and `step-retry-exhausted` to `PipelineFailureReasons`, plus
      the run page's wording. (`RunDisplay.FailureReasonText` and `RunDetail.StepReasonText` carry both
      sentences; the step row shows `attempts N` when N > 1.)
- [x] Step records: `Attempts` and the final exit code on the existing step row (no schema fork), with
      the EF migration (`20261118050222_AddStepAttempts`) and the snapshot. The **upgrade** of a populated
      database (the defaulted column's whole point) is a `Category=Postgres` fact, not an assumption.
- [x] Freestyle editor: numeric fields with server-side parsing/validation.
- [x] `docs/lua-runtime.md` §5.3 and PRD §3.3.5: record the two knobs and the clamping rule.

## Dependencies

- F-009 (cancellation propagation, deadline) — accepted

## Test Cases

### TC-001: A hanging step fails fast with step-timeout

Steps:
1. Define one step that sleeps 30s with `timeout = 2`, followed by a normal step,
   and a 60-minute run timeout.

Expected:
- The step fails within ~2s with reason `step-timeout`.
- The following step did not run (default stop); the run's failure reason names the
  step timeout, not the run deadline.

### TC-002: A flaky step is retried and can still succeed

Steps:
1. A step whose script fails the first attempt and succeeds the second, with
   `retry = 2`.

Expected:
- One step record, `Attempts = 2`, final status success, exit code 0.
- The log shows two attempt sections; the run is `Success`.

### TC-003: Cancel is not retried

Steps:
1. Start a step with `retry = 3`; cancel the run during the first attempt.

Expected:
- No further attempt starts; the run is `Canceled`; the step's record shows one
  attempt.

## Evidence

### The facts

| AC | Fact | File | What it asserts |
|---|---|---|---|
| 1 | `The_step_timeout_and_retry_are_optional_bounded_knobs_the_frozen_record_carries` | `tests/Zenkins.Tests/Pipelines/PipelineSpecTests.cs` | Lua `timeout = 600, retry = 2` reaches the spec (and `OwnTimeout`/`AttemptCount`), a step without them stays `null`/1, the frozen JSON round-trips both and omits the absent pair. |
| 1 | `An_invalid_definition_is_rejected_with_its_field_path` (+7 rows) | same file | `timeout = 0`/`86401`/`1.5`/`"600"` and `retry = 0`/`6`/`true` are refused with the field path. |
| 1 | `The_envelope_gate_refuses_a_step_knob_outside_its_bounds` (+4 rows) | same file | The Server's own gate refuses `timeoutSeconds`/`retry` outside their bounds, so a worker bug cannot freeze them. |
| 1 | `The_editors_two_numeric_fields_are_parsed_and_bounded_server_side` | `tests/Zenkins.Tests/Projects/FreestyleStepsTests.cs` | The editor's text is parsed server-side (`" 600 "` → 600, blank → absent) and five bad inputs are refused with the envelope's phrasing. |
| 1 | `The_steps_editor_carries_the_two_step_knobs_and_prefills_what_is_stored` | `tests/Zenkins.Tests/Runs/FreestyleRunTests.cs` | The Pipelines tab renders both inputs on the saved row (prefilled `600`/`2`, with the server's `max`) **and** in the row template. |
| 2 | `A_hanging_step_is_killed_at_its_own_timeout_with_its_process_tree` | `tests/Zenkins.Tests/Pipelines/PipelineRunnerTests.cs` | A 2 s limit against a 30 s child: `step-timeout` on the step and the run, no exit code, the next step skipped, the log names the limit, and the step's **grandchild** is gone. |
| 2 | `A_step_timeout_with_continue_on_error_is_a_normal_failure_and_the_run_goes_on` | same file | With `continue_on_error` the next step really runs (its marker file), the step record says `step-timeout`, and the run still ends Failed naming that limit. |
| 3 | `The_step_limit_is_clamped_by_the_runs_remaining_deadline_and_the_log_says_which_fired` | same file | Both directions: a 3600 s step limit under a 2 s deadline dies as `timed-out` with the log saying the run's deadline governs; a 2 s limit under a 2 min run dies as `step-timeout` with the reverse sentence. |
| 4 | `A_flaky_step_is_retried_and_still_succeeds_in_one_step_record` | same file | Attempt 1 exits 3, attempt 2 succeeds: one record with `Attempts = 2` and exit 0, the child's own counter file reads 2, the log holds the delimiter and two attempt sections, and the next (knob-less) step reports `Attempts = 1`. |
| 4 | `A_cancellation_is_never_retried_even_when_the_step_declares_attempts` | same file | `retry = 3`, cancelled during attempt 1: `Canceled`, one attempt, no delimiter, one start line. |
| 4 | `A_step_record_carries_the_attempt_count_and_the_final_exit_code` | `tests/Zenkins.Tests/Steps/StepRecordTests.cs` | The stored row keeps `Attempts = 3` with exit 3; `attempts` is refused on a `Running` report and outside 1..5; a knob-less step stores 1; the page renders `attempts 3` and not `attempts 1`. |
| 1 | `The_bounds_of_the_two_knobs_are_inclusive` (+4 rows) | `tests/Zenkins.Tests/Pipelines/PipelineSpecTests.cs` | `timeout = 1`, `timeout = 86400`, `retry = 1`, `retry = 5` are **accepted** by the Lua reader and by the envelope - the other half of the bound. |
| 3 | `A_user_cancel_beats_a_long_step_timeout_because_the_attempt_is_linked_to_the_run` | `tests/Zenkins.Tests/Pipelines/PipelineRunnerTests.cs` | A step declaring 300 s under a 10-minute run (so its own timer really is installed) is cancelled by the user: `Canceled` inside the fact's 15 s bound, `Attempts = 1`, reason `canceled` (not `step-timeout`), no retry. |
| 5 | `A_retried_step_crosses_the_real_step_endpoint_as_one_accepted_record` | `tests/Zenkins.Tests/Steps/StepRecordTests.cs` | `retry = 2` with a child that fails once and succeeds: through the **real** `/api/workers/runs/{id}/steps` endpoint the server records exactly one row (`Attempts = 2`, exit 0), accepts both transitions (2 reports), and the page lists 2 `<li>` for 2 declared steps. |
| 5 | `A_step_whose_retries_are_exhausted_ends_one_failed_record_with_the_last_exit_code` | `tests/Zenkins.Tests/Steps/StepRecordTests.cs` | `retry = 2` with a child that fails **every** attempt (the terminal branch the success fact can never reach): through the real endpoint the step ends one row, `Failed`, `Attempts = 2`, exit code 3, no failure reason; the run is `step-retry-exhausted`; the next step is `Skipped`; two reports for that one index; the page still shows two `<li>` and `attempts 2`. |
| 5 | (page half of `A_step_record_carries_the_attempt_count_and_the_final_exit_code`) | same file | The page lists exactly as many `<li id="run-step-` as the frozen spec declares - two - although one step made three attempts. |
| — | `The_model_snapshot_describes_exactly_the_current_model` | `tests/Zenkins.Tests/Pipelines/ModelSnapshotTests.cs` | `Database.HasPendingModelChanges()` is false for the PostgreSQL-configured model: the migrations and `ZenkinsDbContextModelSnapshot.cs` describe one model. This is the guard for the trap this card hit (generating a migration without `Database__Provider=Postgres` rewrites the snapshot with SQLite store types, which is invisible until the *next* migration contains thousands of `AlterColumn`s). |
| 6 | `A_retry_does_not_resolve_the_steps_secrets_a_second_time` | `tests/Zenkins.Tests/Pipelines/PipelineRunnerTests.cs` | The counting source is asked **once** while the step's process runs twice, the value differs per fetch (`v1`, `v2`, …), and the file the second child wrote still reads `TOKEN=v1`. |
| 7 | `A_step_without_a_timeout_gets_no_implicit_one` | same file | A step with no declared limit runs 6.5 s of real work and succeeds - no invented default. |
| 4 | `Upgrading_a_populated_database_backfills_attempts_with_one_and_keeps_the_bound` | `tests/Zenkins.Tests/Pipelines/PostgresStepAttemptsUpgradeTests.cs` | **`Category=Postgres`**: a database migrated to the revision *before* this card, seeded with an organization/project/run and two recorded steps, then migrated to head — the upgrade applies, `CK_BuildSteps_Attempts` is `convalidated`, both historical rows read `Attempts = 1` through the model *and* in raw SQL with their names/statuses/exit codes intact, `Attempts = 0`/`6` are refused on insert *and* on update, and a new step stores `Attempts = 3`. |

`TC-001` → the first fact, `TC-002` → the fourth, `TC-003` → the fifth.

### Mutation proofs (21 mutations, 21 reds, all restored)

| # | AC | What was broken | Fact that went red |
|---|---|---|---|
| M1 | 1 | `PipelineSpecReader.ReadStepLimit` keeps only the whole-number check (bounds dropped) | `An_invalid_definition_is_rejected_with_its_field_path` (`timeout = 86401` accepted) |
| M2 | 1 | `PipelineSpecSchema.TryReadStepLimit` never refuses | `The_envelope_gate_refuses_a_step_knob_outside_its_bounds` |
| M3 | 1 | `FreestyleSteps.ParseStepLimit` always answers "not declared" | `The_editors_two_numeric_fields_are_parsed_and_bounded_server_side` (expected 600, actual null) |
| M4 | 1 | the saved row's timeout input loses its `data-freestyle-field` | `The_steps_editor_carries_the_two_step_knobs_and_prefills_what_is_stored` (the template alone does not satisfy it) |
| M5 | 2 | the step's own kill is recorded as `not-started` | `A_hanging_step_…` (expected `step-timeout`, actual `step-failed` on the run; the step's reason differs too) |
| M6 | 4 | the retry loop never repeats (`attempt < 1`) | `A_flaky_step_…` (exit 3) |
| M7 | 4 | the cancel catch stops distinguishing the run's token | `A_cancellation_is_never_retried_…` |
| M8 | 3 | the clamp ignores what the run has left (`ownTimeoutGoverns` = "a limit is declared") | `The_step_limit_is_clamped_…` (the log says the step's own limit governs under a 2 s deadline) |
| M9 | 6 | the step's references are resolved again per attempt | `A_retry_does_not_resolve_…` (fetches 1 → 3) |
| M10 | 5 | a `Running` report per attempt | `A_flaky_step_…` (3 reports for one step instead of 2) |
| M11 | 4 | the Server stops bounding `attempts` | `A_step_record_carries_the_attempt_count_…` (attempts 6 answered 200) |
| N1 | 5 | `BuildStepViews` renders one row per attempt (`Attempts > 1` adds the view again) | `A_step_record_carries_the_attempt_count_…` **and** `A_retried_step_crosses_the_real_step_endpoint_…` (page lists 3 `<li>` for 2 steps) |
| N2 | 5 | the terminal report stops carrying `attempts` | `A_retried_step_crosses_the_real_step_endpoint_…` (stored `Attempts` 2 → 1) |
| N3 | 1 | the Worker's bound check becomes exclusive (`<= min`, `>= max`) | `The_bounds_of_the_two_knobs_are_inclusive` (4/4 rows) |
| N4 | 7 | an implicit `?? 5s` default limit | `A_step_without_a_timeout_gets_no_implicit_one` (the 6.5 s step is killed) |
| N5 | 3 | the attempt's timer is a plain CTS, not linked to the run's token | `A_user_cancel_beats_a_long_step_timeout_…` (the cancel no longer reaches the attempt) |
| N6 | 6 | the step's references resolved again per attempt | `A_retry_does_not_resolve_…` (fetches 1 → 3; the second child's file would read `v2`) |
| N7 | 4 | the migration's `AddColumn` loses `defaultValue: 1` (a default-less non-nullable column) | `Upgrading_a_populated_database_backfills_attempts_with_one_and_keeps_the_bound` — PostgreSQL refuses the upgrade itself: `23502: column "Attempts" of relation "BuildSteps" contains null values` (run against a real PostgreSQL 16) |
| N8 | 5 | the terminal report's index grows per attempt (`i + attempt`) | `A_step_whose_retries_are_exhausted_…` — the server refuses the second attempt's index, the row stays `Running` instead of `Failed` |
| N9 | — | `ZenkinsDbContextModelSnapshot.cs` loses the column's `HasDefaultValue(1)` | `The_model_snapshot_describes_exactly_the_current_model` (its failure message names the wrong-provider trap) |

### Counts and checks

- `dotnet build -c Release`: **0 warnings / 0 errors** (warnings-as-errors, whole solution).
- `--list-tests` under the sextuple fast filter: **1377 before** (branch point `3459761`) → **1407 now**,
  +30: 1 spec-knobs fact + 7 Lua-refusal rows + 4 inclusive-bound rows + 4 envelope rows (16 in
  `PipelineSpecTests`), 1 `FreestyleStepsTests` fact, 1 editor-page fact, 8 runner facts, 2 step-record
  facts (the ingest one, the real-endpoint retry, the exhausted-retry one), 1 model-snapshot fact. No
  existing test was edited (the harness/asset changes - `PipelineRunHarness`'s sink/secrets knobs,
  `RunTestHost.ReportStepAsync`'s `attempts`, ArgvEcho's `flaky`/`flaky-env` modes - add no test of
  their own).
- The review's follow-up round added the seven facts above (page-side AC-5, the inclusive bounds, the
  6.5 s no-default step, the cancel/link fact, and the distinct-value secret assertion) plus the
  real-endpoint retry; `Zenkins.Tests.Pipelines`+`Steps`+`RunUi` = 193 passed after them.
- `Category=Postgres`: **7** facts (6 before this card's upgrade fact), and all 7 were run green against a
  locally started PostgreSQL 16 (throwaway cluster, disposable `zenkins_test` database) - so the upgrade
  path is a measured fact, not a compiled one. The fast count is unchanged by it (the category is excluded
  from the filter above).
- Targeted on the macOS host (the full fast suite and `Category=Postgres` belong to the Windows machine):
  `Zenkins.Tests.Pipelines` 126 passed, `Zenkins.Tests.Runs` 106 passed,
  `Zenkins.Tests.Steps`+`Projects`+`WorkerRuntime`+`Pipelines` 300 passed (the ArgvEcho change included).
- The migration is generated with the **PostgreSQL** provider (the checked-in snapshot is
  Postgres-shaped): `has-pending-model-changes` says "No changes" after it, and the SQLite dev database
  migrates from it (the `PipelineMigrationTests`/step facts above run through both providers' paths).

## Notes and Decisions

- Rejected: a per-attempt step record. It would break "step N of the frozen spec"
  identity used by artifacts, conditions and the page's merge logic.
- **The clamp installs a timer only when the step's own limit is the earlier one.** The alternative -
  always installing `min(stepLimit, remaining)` - would move the *timing* of a run whose deadline fires
  (the executor would kill a step a fraction before F-009's cancel arrives), which is a behaviour change
  the card explicitly forbids for steps that declare nothing, and it would make "which limit fired" a
  matter of nanoseconds. Relying on the run's token when the deadline is earlier keeps one path for the
  deadline, one for the step, and the log states which one is in force before the process starts.
- **`step-timeout` vs `timed-out`, in both vocabularies.** The run-level word (`step-timeout`) says the
  run failed because a step hit its own limit; the step-level word says the same about the step, while
  `timed-out` stays "the run's deadline killed this step". Two words, two stories - the alternative
  (reusing `step-failed` for both) would leave the page unable to answer "which limit?".
- **`step-retry-exhausted` is chosen by the last attempt, not by "any attempt failed".** A step whose last
  attempt was killed by its own limit keeps `step-timeout` (the cause the operator must act on); a step
  whose last attempt was a plain non-zero exit after several attempts records
  `step-retry-exhausted`; a single-attempt failure keeps the pre-F-032 `step-failed`. That also keeps
  every existing run's reason stable.
- **One record, not one per attempt** - the card's rejected alternative stays rejected. `Attempts` rides
  the existing row (and the frozen spec's step identity keeps working for artifacts, conditions and the
  page), and the attempts themselves are delimited log sections, which is where a reader needs them.
- **`BuildSteps.Attempts` carries the schema's bounds as a check constraint** (`1..5`) and a database
  default of 1, so the column cannot disagree with what the executor may do and existing rows backfill to
  the honest "one attempt".
- **Found and left alone (worth naming):** generating a migration without `Database__Provider=Postgres`
  rewrites `ZenkinsDbContextModelSnapshot.cs` with SQLite type names and produces a migration with
  thousands of `AlterColumn`s. That is exactly what `docs/development.md` §3 warns about; this card hit
  it once, deleted the output and regenerated with the provider set. No code change needed - the doc is
  already right, it just has to be read before the command is run.
- **Found and not touched (out of scope):** `docs/development.md` §2's fast count (1377, committed by
  `3459761`) is now stale by this card's +28 (1405 by `--list-tests`). Re-baselining needs a counted run
  of all seven suites, which belongs to whoever owns the gate.
- **The upgrade path is a fact now, and it needed to be.** `defaultValue: 1` is what makes an existing
  database's steps legal after `AddStepAttempts` (PostgreSQL 11+ adds a defaulted non-null column without
  rewriting the table, so history *reads* 1). A from-empty `Migrate()` never exercises that: the column is
  created and no row ever meets the default. The new `Category=Postgres` fact migrates to the revision
  before this card, writes real rows, upgrades, and then checks the backfill, the constraint on both
  insert and update, and that the rows are still readable through the model. Its mutation (dropping
  `defaultValue`) makes PostgreSQL refuse the upgrade with `23502: column "Attempts" … contains null
  values` - which is exactly the failure an operator would have hit.
- **The snapshot drift trap is a fast fact now.** `The_model_snapshot_describes_exactly_the_current_model`
  calls `Database.HasPendingModelChanges()` on a **PostgreSQL-configured** context (no connection is opened;
  the snapshot is the PostgreSQL one per development.md §3). It is the counterpart to the §3 prose: the
  author of this card lost twenty minutes to a snapshot rewritten with SQLite store types, and that damage
  is invisible until the next `migrations add`. **Its scope is the model snapshot, not the migrations'
  designers:** `HasPendingModelChanges` never reads `<timestamp>_<Name>.Designer.cs`, so editing a designer
  alone stays green (the review's mutation N9b). Covering that would need a per-migration model-differ
  harness for a file only `dotnet ef` writes - recorded as a known blind spot in the fact's own doc comment
  rather than left as an implied promise.
- **The review's AC-5 finding, and what closed it:** the first round asserted the step list only through
  the server rows, so a page that rendered one row per attempt stayed green. Two things were missing and
  are now facts: the rendered page's `<li>` count against the frozen spec, and one retried step driven
  through the **real** step endpoint (the runner tests use the offline sink, which validates neither the
  index nor the name nor the order). A second real-endpoint fact covers the branch the first could not
  reach - retries **exhausted** - where a terminal report addressed at a per-attempt index would be refused
  and leave the row `Running`. Its first version of the cancel/link fact was also green for the
  wrong reason - it declared `timeout = 3600` under a 2-minute run, where the clamp installs no timer at
  all - so it now declares 300 s under a 10-minute run, the only shape in which the linked source exists.

## Close-out verification (2026-11-18)

Three review rounds, twenty-one mutations, and the card only merged after the reviewer's first verdict
was **refuse**. That verdict is the most useful thing in this record: seven of its mutations stayed
green, and each green was a property nobody was guarding.

- **The blocking one was the view.** The first submission proved "one record per declared step" by
  counting the reports the executor sent and by sending four reports by hand to the service. It never
  asserted the page. Making `BuildStepViews` render one row per attempt left 165 page and run tests
  green. Two facts closed it: the step-record fact now counts `run-step-` list items against the frozen
  spec, and a retry that exhausts crosses the **real** `ServerStepSink` — where the service rejects a
  second index with `StepUnknown` — and still ends as one row. The reviewer's own mutation now reddens
  at `Expected "Failed" / Actual "Running"`.
- **Six more greens became facts.** The inclusive bounds of both knobs were never accepted by a test
  (a `<=` or `>=` written the other way would silently reject `timeout = 1`, `86400`, `retry = 1` or
  `5`); a step with no declared timeout never proved that no timer is installed; the attempt's linked
  token never proved that a user cancel beats a declared `timeout = 300`; and the pass-through of the
  secret value per attempt needed a bare counting source with distinct values and a child process that
  writes what it actually received, because a cached resolver or a "the value did not change" assertion
  would have been vacuous.
- **The step timeout is clamped, and both directions are pinned separately.** A step declaring 2 s with
  a 60 s deadline dies as `step-timeout`; a step declaring 600 s with a 2 s deadline dies as the run's
  `timed-out`, and the two log sentences are mutually exclusive, each asserting the other's absence. The
  reviewer's two mutations redden exactly one direction each — `M3b'` only (b), `M3c` only (a) — which
  is what proves neither direction hides behind the other.
- **`Attempts` and the final exit code live on the existing step row**, with a `1..5` check constraint,
  and the migration was generated through the PostgreSQL provider: generating it without
  `Database__Provider=Postgres` rewrites the snapshot with SQLite type names and produces thousands of
  `AlterColumn` lines. That trap is now guarded by a fact: `ModelSnapshotTests` calls
  `HasPendingModelChanges()` against a PostgreSQL-configured context that never connects, so it runs in
  the fast suite without a database.
- **The upgrade path is a fact, not an assumption.** Creating a schema from scratch can never exercise
  a column default, so the new `Category=Postgres` fact migrates to the migration *before* this one
  (found through `GetMigrations()`, not hard-coded), inserts two real step rows with real foreign keys,
  migrates forward, and asserts the backfill is `1` read both through the model and through raw SQL,
  that `pg_constraint.convalidated` is true, that `Attempts` of `0` or `6` is refused on insert **and**
  on update, and that a new `3` stores and reads back. Changing the migration's default to `2` reddens
  it at the assertion rather than at `MigrateAsync` — which is the difference between "the migration ran"
  and "the migration backfilled the right value".
- **Counts and gates.** The authoritative run is on the Windows host: **1407 total, 1403 passed, 1
  failed, 3 skipped, 574 s**. The single failure is main's own `ProcessRunnerTests` handle race and the
  three skips are the POSIX-only facts, so the new-failure set is empty. `Category=Postgres` went
  **6 -> 7** and passed 7/7 with the upgrade fact at 3.7 s and no skip; the backfilled rows read
  `Restore=1 / Build=1 / Deploy=3` as intended. `ModelSnapshotTests` passes 1/1 with no
  `ZENKINS_TEST_POSTGRES` set, confirming it is not a skipping fact.
- **The merged tree is the tested tree.** The last commit (`e2c8eb2`) only rewrites documentation in
  `ModelSnapshotTests`: the reviewer's `N9b` showed that `HasPendingModelChanges()` compares the runtime
  model against the snapshot and never reads a migration's own designer, so the original class summary
  claimed coverage it did not have. The paragraph now states the blind spot and why paying for a
  per-migration model differ is not worth it. The delta was mechanically checked to contain no
  executable line, `--list-tests` stayed at 1407, and the full gate ran on the comment commit itself, so
  the result above and the merged tree are the same tree.
