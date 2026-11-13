---
id: F-015
title: Step records, per-step logs, env, continue_on_error, secrets and redaction
type: feature
priority: P1
size: L
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

Every step of a pipeline becomes a first-class record (status, timing, exit code, its own log
segment), steps honour `continue_on_error`, conditions and the pipeline/step environment, and a
pipeline can read an organization secret through `secret()` with the value redacted from the log
stream — this is PRD §3.3.5 in full, plus §3.8's `secret()` half.

## Background

PRD §3.3.5: steps run in order; a failing step stops the run by default; each step has its own log;
each step records start/end time and exit code; the run's result is decided by all steps' final
states; `continue_on_error` is explicit; pipeline- and step-level env; conditions; Lua functions,
loops and conditionals are supported. PRD §3.8: `secret()` in pipelines, secrets never in logs,
redaction explicitly *not* a boundary. PRD §6 lists `BuildStep`.

F-014 froze the spec with the fields this card honours (`env`, `continue_on_error`, `when`), so no
schema v2 is needed.

Decisions:

- **Conditions**: definition-time conditions are already plain Lua (`if is_linux() then
  steps[#steps+1] = … end`) and need no new mechanism. This card adds *execution-time* conditions as
  a declared, non-executable field — `when = { branch = "main" | platform = "linux" | env = { K = "v" } }`
  — evaluated by C# against the run's frozen facts (branch, worker OS, effective env). A script
  cannot make a condition do anything a step could not; a skipped step is recorded, not omitted, so
  the run page never shows a step that silently vanished.
- **Environment precedence** is explicit and testable: inherited process environment < pipeline
  `env` < step `env` < server-provided run environment (`ZENKINS_RUN_ID`, `ZENKINS_PROJECT_ID`,
  `ZENKINS_WORKER_NAME`, F-007) < secret env (`secret()` returns a value the script passes
  explicitly). `env("NAME")` reads the effective environment, so a script sees exactly what the child
  will see.
- **`secret(name)` is a worker-side fetch, not a claim-payload field** (same reasoning as F-013's
  git credential): `GET` with the worker token scoped to the run and its organization, decrypted by
  the Server, cached per run in memory, never written to disk. An unknown name is a clear error
  naming the *name*; a foreign organization's secret is indistinguishable from missing.
- **Redaction is a streaming filter with an explicit, honest boundary**: the worker registers every
  value it decrypts (git credentials from F-013 and secrets here) with a per-run filter that replaces
  literal occurrences with `***` on each log line before it is streamed — stdout and stderr, and the
  synthetic lines the worker itself emits. Boundaries that are documented rather than pretended:
  a value split across two writes (no newline) is not caught; an encoded/split/transformed value is
  not caught; the worker holds the plaintext in memory for the duration of the run. Anyone who can
  edit `pipeline.lua` is treated as having the build's permissions (PRD §3.8).
- **Short values**: values shorter than 4 characters are still redacted (a literal match), because
  any threshold is a bypass; the cost is occasional false positives in logs, which is the safer
  direction, and the docs advise against 1–3 character secrets.
- **`BuildStep` is the record of truth** for step outcomes, but the run's terminal status stays owned
  by the existing state machine: `Failed` if any non-skipped step failed (even when
  `continue_on_error` let the rest run), `Success` only if every non-skipped step succeeded,
  `Canceled`/`Lost` unchanged. Per-step rows are written as the step changes state, so a worker that
  dies mid-step leaves the step `Running` and the run is resolved by F-010's `Lost` path.
- **Per-step log grouping**: `BuildLog.StepId` (nullable) is stamped by the worker on every line
  emitted while a step runs; lines from the definition phase and between steps carry `null`. The run
  page renders step headers using this (the full viewer with per-step filtering stays v0.9).

## Acceptance Criteria

- [ ] `BuildStep` entity (`OrganizationId`, `RunId`, `Index`, `Name`, `Status`, `StartedAt`,
      `EndedAt`, `ExitCode`, `FailureReason`) + migration, unique `(RunId, Index)`, cascade from the
      run; PostgreSQL contract gate clean.
- [ ] Each step is reported before it starts (`Running`) and after it ends (`Success`/`Failed`/
      `Skipped`) with timing and exit code; a run page opened mid-run shows completed, current and
      pending steps.
- [ ] `continue_on_error = true` runs subsequent steps after a failure; the run still ends `Failed`
      naming the failing step; with `false` the default (F-014) is unchanged.
- [ ] `when` skips a step (`Skipped`) without spawning a process, records the reason in the run log,
      and a skipped step does not affect the run's success. Conditions evaluate `branch`, `platform`
      and `env` exactly as declared; an unknown key in `when` is a validation error.
- [ ] Environment precedence holds in both `env()` (Lua) and the child process, pinned by a test that
      sets the same variable at all three levels and reads it back from both.
- [ ] `secret("NAME")` returns the decrypted value inside a step; unknown name → step fails with a
      message naming the name; a Member-triggered run can still read the organization's secrets (the
      script is trusted code — that is the PRD's ruling), but a *different organization's* run
      cannot.
- [ ] Redaction: a secret or git-credential value echoed by a child appears as `***` in the streamed
      and persisted log, on both stdout and stderr, and in worker-generated lines; the plaintext is
      absent from the database, the download endpoint and the UI. A line-level split is documented as
      a known gap, not silently accepted.
- [ ] Per-step log segments: `BuildLog.StepId` is set for lines emitted during a step, `null`
      otherwise; the run page groups lines under step headers.
- [ ] Cancellation and deadlines still kill the running process tree and end the run `Canceled`/
      `timeout`, with the in-flight step left `Failed` and the remaining steps `Skipped` (or pending,
      if the run ends before it reaches them — the same terminal-state discipline as elsewhere).
- [ ] Worker-side secret cache is per run and never persisted; a worker restart re-fetches; no
      plaintext appears in worker stdout, temp files or crash logs.

## Subtasks

- [ ] `BuildStep` entity + migration + `BuildLog.StepId`; server ingestion of step transitions with
      tenant/run validation and ordering.
- [ ] Worker executor: step state transitions, `when` evaluation, env composition, `continue_on_error`,
      per-step log stamping.
- [ ] `secret()` primitive: run-scoped worker fetch endpoint, per-run cache, error contract; reuse
      F-013's redaction filter (now fed by both credential and secret values).
- [ ] Run page: step list with statuses, step headers in the log panel.
- [ ] Tests (`Category=Steps`): records and ordering, skip semantics, continue_on_error, env
      precedence, secret happy/unknown/cross-org, redaction (both streams, worker lines, persisted
      rows), per-step log grouping, cancel/deadline step states, tenancy probes for `BuildStep`.

## Dependencies

- F-014 (pipeline runs)
- F-012 (secrets storage) and F-013 (credential + redaction filter)

## Test Cases

### TC-001: Step records tell the truth

Steps:
1. Run a three-step pipeline where step 2 exits 3 and `continue_on_error = false`.

Expected:
- Step 1 `Success` with timing and exit code 0; step 2 `Failed` exit 3; step 3 `Skipped` (never
  claimed `Running`, no `StartedAt`); run `Failed`, failure reason names step 2.

### TC-002: continue_on_error keeps going but does not launder the failure

Steps:
1. Same pipeline with `continue_on_error = true` on step 2 (step 3 succeeds).

Expected:
- All three steps record a terminal state; run is `Failed` (not `Success`), log states which step
  failed, and step 3's output is present.

### TC-003: Conditions and env precedence

Steps:
1. Pipeline with `when = { branch = "main" }` on one step and `when = { platform = "linux" }` on
   another; run on a non-main branch on macOS.
2. Set `CONFIG` in the inherited environment, the pipeline `env` and the step `env`; have the step
   echo it and also call `env("CONFIG")`.

Expected:
- Both conditions skip as appropriate, recorded as `Skipped` with the reason logged.
- Child and `env()` both report the step-level value (highest precedence wins).

### TC-004: A secret never reaches the log

Steps:
1. Store secret `DEPLOY_TOKEN`; pipeline step echoes it to stdout, to stderr, and via `print`.
2. Fetch the run's persisted log, the download endpoint and the UI text.

Expected:
- Every occurrence is `***` in all three surfaces; the plaintext is absent from the database, and the
  secret's own response/API is unchanged. An unknown secret name fails the step with a message that
  contains the name but no value.

### TC-005: Tenant and worker scoping of `secret()`

Steps:
1. Run a pipeline as organization B that calls `secret("A_ONLY")` (a name that only exists in A).
2. Have a worker of B attempt to fetch A's run secret.

Expected:
- (1) fails the step naming the missing secret; no cross-organization value is returned.
- (2) is the same generic 404 as a non-existent run, with no distinguishable body.

## Development Log

### 2026-11-13

- Planned from the v0.3 plan; no work started. Redaction is explicitly a log hygiene measure, not a
  boundary — the card's acceptance criteria and the contract document must keep saying so, because a
  future reader will otherwise treat `***` as a guarantee.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Data/Entities/{BuildStep,BuildLog}.cs`, `src/Server/Data/ZenkinsDbContext.cs`,
  `src/Server/Data/Migrations/*`, `src/Server/Runs/*`, `src/Server/Secrets/*`,
  `src/Server/Components/Pages/Runs/*`, `src/Worker/Pipelines/*`, `src/Worker/Git/GitLogFilter.cs`,
  `src/Worker/Lua/*`, `tests/Zenkins.Tests/Steps/*`, `tests/Zenkins.Tests/Tenancy/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Steps"`;
  full suite; PostgreSQL contract gate.
- Results: not run (card not started).
