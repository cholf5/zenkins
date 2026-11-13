---
id: F-014
title: Pipeline runs: definition phase, frozen spec, sequential steps
type: feature
priority: P1
size: L
status: done
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

A run is a pipeline execution: the Worker checks out the project's repository at the run's branch,
runs the project's pipeline file under the Lua sandbox, validates the declared spec, freezes it on
the run, and then executes its steps in order — stopping at the first failure — using the existing
process runner and log stream. `pipeline.lua` in a Git repository becomes the thing that runs.

## Background

PRD §3.3.2 (the declarative sample is the shape we implement), §3.3.5 (steps run in order, a failing
step stops the run by default, each step has a log, start/end/exit code are recorded), §3.5 (a run is
a pipeline execution), §6 (`BuildRun` must record the Git commit and pipeline identity).
[`docs/lua-runtime.md`](../../lua-runtime.md) §5 and §6 fix the execution model and the layering this
card implements.

Decisions:

- **A run has one shape: a pipeline run.** `BuildRun.CommandJson` is retired in favour of
  `PipelinePath` (snapshot of the project's path at trigger time), `GitBranch`, `GitCommit` (F-013)
  and `PipelineSpecJson` (the frozen, validated spec). The alternative — keeping an argv run mode
  next to a pipeline mode — is two code paths, two trigger forms and two test surfaces for a feature
  the PRD does not have. Pre-alpha argv runs are backfilled with the project's default branch/path
  and stay terminal (they are never re-executed); `run()` remains an SDK primitive, not a run mode.
- **The trigger chooses a branch**, not a command line: project page → "Run pipeline" with a branch
  (default: `project.DefaultBranch`), tags and timeout. The argv entry box disappears.
- **Definition and execution are separate phases, and the split is server-visible.** The Worker
  reports the resolved commit and the validated spec before it runs the first step, so the run page
  can show the step list of a run that is only just starting, and a pipeline that cannot be
  validated never spawns a process.
- **The spec is a data schema, not a script.** At definition time the prelude offers only pure
  helpers (`pipeline`, `step`, `log`, `is_windows/linux/macos`) — no I/O primitive is available, so
  a pipeline cannot mutate the workspace while being described. Validation is strict and
  deterministic (below), and the frozen JSON is what the executor reads; the script is not re-run.
- **Execution is C#, not Lua.** Steps are executed by the worker runtime in order with the existing
  `ProcessRunner`; `continue_on_error`, per-step env, conditions and per-step records belong to
  F-015, which is why the spec already carries the fields but the executor honours only the default
  behavior here.
- **Failure taxonomy** is a closed set of `FailureReason` values so the UI and the tests agree:
  `git-*` (F-013), `pipeline-missing`, `pipeline-syntax`, `pipeline-invalid`, `pipeline-timeout`,
  `step-failed`, plus F-009's `timeout` / `canceled-by-user`.
- **The definition phase shares the run's budget and cancellation**: a `while true do end` in
  `pipeline.lua` (including inside a step table constructor) is abandoned by the driver at the
  deadline. `pipeline-timeout` is a terminal `Failed`, never a retry.

Spec schema (v1), rejected on any unknown key:

```lua
pipeline {
  name    = "Build",                          -- required, non-empty, ≤ 200 chars
  env     = { KEY = "value" },                -- optional map of string → string (F-015 applies it)
  steps   = {                                 -- required, 1..200, names unique
    { name = "Restore", run = { "dotnet", "restore" } },
    { name = "Build",   run = { "dotnet", "build", "-c", "Release" },
      env = { CONFIG = "Release" },           -- F-015
      continue_on_error = false },            -- F-015
    { name = "Package", shell = { "bash", "-c", "tar czf out.tgz bin" } },
  },
  triggers = { branches = { "main" } },       -- optional; informational for manual runs (F-017 reads it)
}
```

Each step carries exactly one of `run` (argv array, 1..64 non-empty strings) or `shell`
(program + argv array). `triggers` and `env` are accepted and frozen now, applied in F-015/F-017.

## Acceptance Criteria

- [x] Migration: `BuildRun` gains `PipelinePath`, `GitBranch`, `PipelineSpecJson`, keeps `GitCommit`;
      `CommandJson` is dropped; existing rows are backfilled (branch from the project default, path
      from the project) and remain readable; PostgreSQL contract gate clean.
- [x] Manual trigger takes a branch (defaulting to the project's default branch), tags and timeout;
      the argv form is gone from the project page and its endpoint.
- [x] On accept the Worker clones/checks out the run's branch through F-013, reports the resolved
      commit, and then reads the pipeline file from the checked-out tree at `PipelinePath`.
- [x] The definition phase runs under the F-011 sandbox with only the pure helpers available; a
      script that calls an I/O primitive (`run`, `shell`, `checkout`, `artifact`, `secret`) from the
      definition gets a clear "available inside steps only" error, not a null-reference crash.
- [x] Validation rejects, with the offending field path in the message: unknown keys (pipeline,
      step), missing/empty/duplicate step names, missing `steps`, more than 200 steps, a step with
      neither or both of `run`/`shell`, non-string or empty argv elements, non-string env values, and
      a non-boolean `continue_on_error`.
- [x] A validated spec is frozen on the run (`PipelineSpecJson`), reported before execution starts,
      shown on the run page (step names in order), and re-reading it never re-runs the script.
- [x] Missing pipeline file → `Failed/pipeline-missing` naming the path; syntax error →
      `Failed/pipeline-syntax` with `source:(line,col)` and nothing executed; invalid spec →
      `Failed/pipeline-invalid` with the field path and nothing executed; definition runaway →
      `Failed/pipeline-timeout`.
- [x] Steps execute in order with `ProcessRunner` in `work/{run-id}/repository`, each with the run's
      log stream; a failing step (non-zero exit) stops the run and yields `Failed/step-failed` with
      that exit code recorded on the run; every step's exit code and duration are in the log lines
      this card emits (per-step records are F-015).
- [x] Cancellation (F-009) during definition or between steps ends the run `Canceled` and kills the
      running process tree; the deadline produces `timeout`.
- [x] A pipeline run on a worker that has no `git` binary fails with a message that names `git` and
      does not leave the run `Running`.
- [x] Existing suites stay green; the queue/claim/lease, cancellation and offline behaviors are
      unchanged, and the Worker stays ASP.NET-free.

## Subtasks

- [x] Schema + migration + backfill; `BuildRun.PipelineSpecJson` immutability rule.
- [x] Server: trigger endpoint/page branch + spec storage; `definition` report endpoint (commit +
      spec validation server-side: schema version, step count, size cap); run page step list.
- [x] Worker: definition phase (F-011 runtime + prelude `pipeline`/`step`/`log`/`is_*`), spec
      validator with field-path errors, `PipelineSpec` model, executor loop over `run`/`shell` steps
      using `ProcessRunner`, failure taxonomy mapping.
- [x] Prelude (`src/Worker/Lua/prelude.lua`, embedded resource) + a golden test that it is shipped in
      the worker binary.
- [x] Tests (`Category=Pipeline`): schema round-trip, every validation rejection, definition-phase
      I/O refusal, missing file, syntax error position, definition runaway, ordered execution with a
      marker-file proof that the third step did not run after the second failed, branch selection,
      spec frozen before execution.

## Dependencies

- F-011 (runtime)
- F-013 (git workspace + commit report)
- F-012 (only for the project page's credential reference; the clone itself is F-013's)

## Test Cases

### TC-001: A three-step pipeline runs in order

Steps:
1. Fixture repo whose `pipeline.lua` declares Restore/Build/Package steps that append to a marker
   file and echo a line each.
2. Trigger the run on the default branch; wait for terminal state.

Expected:
- Marker file shows the three steps in order; run `Success`; the log contains the three echoes; the
  run's spec lists three step names before the first step starts.

### TC-002: A failing step stops the run

Steps:
1. Same pipeline, second step exits 7.
2. Inspect the marker file, the run's status, exit code and log.

Expected:
- Third step did not run (marker absent); run `Failed/step-failed`; exit code 7 recorded; the log
  shows the failing step and does not invent a reason.

### TC-003: Definition errors execute nothing

Steps:
1. Four fixture branches: no pipeline file, a syntax error, an unknown top-level key, and
   `while true do end` as the whole file.
2. Run each with a 30 s timeout.

Expected:
- `pipeline-missing`, `pipeline-syntax` (with line/col), `pipeline-invalid` (naming the key) and
  `pipeline-timeout` respectively; no process spawned in any case (argv-echo fixture never invoked).

### TC-004: Spec is frozen, not re-derived

Steps:
1. Trigger a run; while the first step runs, change `pipeline.lua` on the branch.
2. Let the run finish; re-open the run.

Expected:
- The run executes the spec it froze (unchanged step list), reports the pinned commit, and the page
  shows the frozen steps — a later commit does not alter a finished run's record.

## Development Log

### 2026-11-13

- Planned from the v0.3 plan; no work started. Deliberately excludes `continue_on_error`, per-step
  env/conditions, per-step records and `secret()` — those are F-015, and the spec schema already
  reserves their fields so the JSON does not need a v2.

### 2026-11-13 — implemented, reviewed, reworked, accepted

- **The commit is reported before the definition runs, not after.** The first review failed the card
  on exactly this: the worker reported commit and spec together once validation succeeded, so
  `pipeline-missing`, `pipeline-syntax`, `pipeline-invalid` and `pipeline-timeout` runs ended with a
  null `GitCommit` and a run page reading "Git commit: -" — the opposite of what this card and PRD §6
  describe. The endpoint now takes two sends: commit alone right after the clone (stamp only, through
  the same compare-and-set that refuses a second value), then commit plus the frozen spec once
  validation passes.
- **A run the server has disowned is abandoned, not finished.** The second review round found that a
  definitively refused definition report was still followed by every step, because the reporter's
  return value was dropped. A refusal now stops the run before any step and sends no completed report;
  the worker logs one line saying the run is not its to finish, and still drains the log so nothing is
  lost. Only 400/404/409/410 count as definitive: 408/425/429 retry, so a gateway or a rate limiter
  cannot make a worker quietly walk away from a run it still owns.
- **The Lua seam had to grow first.** `LuaExecutionResult` carries a structured `LuaHostValue` now
  (the string remains for the log), because the card's contract is "the script builds a spec table,
  the host validates and serialises it". Writing JSON inside Lua and parsing it in C# would have made
  that sentence false. Tables are walked to `MaxResultDepth` and rejected beyond it rather than
  truncated, and a mixed-key table is refused instead of guessed at.
- **Accepting a run was check-then-act, handed over from F-013.** It is now a compare-and-set whose
  WHERE clause carries the status and the live lease, in the same transaction as the commit stamp, so
  two concurrent accepts cannot both start the same run.
- The definition phase loads `prelude.lua` as its own chunk rather than concatenating it, so
  `source:(line,col)` stays true for the user's file. Both are pinned: a syntax error on line 1 and a
  runtime error on line 4 of a four-line script, the latter being the only evidence that a runtime
  position does not shift.
- Gates that would have been green and wrong: "the third step did not run" is a marker file the third
  step writes itself, not a log line; "the spec is frozen" edits the pipeline file in the checked-out
  work tree **and** pushes a different one to the branch, because a clone already put the file
  locally; the prelude test reads the embedded resource out of the assembly, so
  `File.ReadAllText("src/Worker/Lua/prelude.lua")` would be a red flag rather than a pass.
- Gate: `dotnet build -c Release` 0 warnings 0 errors; `Category=Pipeline` 92/0/0; full suite 669/0/0;
  `Category=Lua` 94, `Category=Secrets` 64, `Category=Git` 48 unchanged; PostgreSQL contract gate exits
  0 with no running database and the migration carries Npgsql type shapes only.
- Registered, not changed: pipeline fixtures seed the database directly, so the product path from a
  project's repository URL to a clone has no end-to-end test yet (Q-002's real-repository harness is
  the place for it); the abandoned-run branch has runner-level tests but no loop-level one; and nothing
  asserts that a second definition report carrying a *different* commit cannot overwrite the first —
  that is guarded by the same compare-and-set the database tests already cover.
- The migration's `Down` cannot restore argv, so it writes `["argv-not-recoverable"]`: a legal
  single-element argv whose element is a self-describing sentinel. An empty string would decode as an
  empty argv, which reads as "this run never had a command" — a quiet lie about a run that really ran.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Data/Entities/BuildRun.cs`, `src/Server/Runs/*`, `src/Server/Projects/*`,
  `src/Server/Components/Pages/Projects/*`, `src/Server/Components/Pages/Runs/*`,
  `src/Server/Data/Migrations/*`, `src/Worker/Lua/prelude.lua`, `src/Worker/Pipelines/*`,
  `src/Worker/WorkerRunLoop.cs`, `src/Shared/*`, `tests/Zenkins.Tests/Pipelines/*`,
  `tests/Zenkins.Tests/WorkerRuntime/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Pipeline"`;
  full suite; PostgreSQL contract gate.
- Results: accepted 2026-11-13 after two review rounds and one rework. `dotnet build -c Release`
  0 warnings 0 errors; `Category=Pipeline` 92/0/0; full suite 669/0/0, zero skipped; `Category=Lua` 94,
  `Category=Secrets` 64, `Category=Git` 48 unchanged; PostgreSQL contract gate exits 0 without a
  running database.
