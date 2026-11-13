---
id: Q-002
title: Milestone acceptance harness for v0.3 (real repo, real worker, real webhook)
type: test
priority: P1
size: M
status: done
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

One suite that proves PRD's milestone goal — "a real build runs from `pipeline.lua` in a Git
repository, and a `git push` triggers it" — end to end with real processes: a real local Git
repository, a real Server, a real Worker process, a real webhook delivery and a real artifact
download. It exists so the milestone cannot be declared done on the strength of unit tests alone.

## Background

F-007/F-008 established the pattern (`WorkerRunProcessE2ETests`: real server, real worker binary,
real SQLite file) and it has already earned its keep — the F-008 review found a rejection
discriminator that never matched only because the real end-to-end path was exercised. F-011…F-017
add a lot of new seams (Lua sandbox, git, secrets, steps, artifacts, webhook auth), each of which can
pass its own unit tests while the composed product is broken.

The harness owns the composition proofs only; it does not re-test the details each card already pins.

## Acceptance Criteria

- [x] A fixture builder creates a bare repository with a `pipeline.lua` (three steps, an env var, a
      secret, an artifact) and a few commits on two branches, without network access.
- [x] Happy path, manually triggered: server + real worker → run reaches `Success`; step records show
      three `Success` rows; the persisted log contains each step's output grouped by step; the
      artifact downloads byte-identically; `GitCommit` on the run equals the repository's `HEAD`.
- [x] Happy path, webhook-triggered: a signed push payload for `main` starts a run for the pushed
      commit and it reaches `Success` with the same properties; the same delivery replayed creates no
      second run.
- [x] Failure taxonomy, all terminal and none retried: missing pipeline file, Lua syntax error,
      failing step (non-zero exit), definition runaway (`while true do end`), and a cancelled run.
      Each asserts the run's `FailureReason`/status and that nothing beyond the expected process ran.
- [x] Sandbox at product level: a `pipeline.lua` that calls `io.open`, `require`, `package.loaded.dynamic.eval`
      and `os.execute` fails the step/definition with a Lua error and writes no file outside the work
      directory.
- [x] Secret hygiene: a step echoes a secret; the persisted log, the log download endpoint and the
      run page contain `***`, never the value. **Ruling:** an artifact a step publishes keeps its
      bytes even when it contains a secret — F-016 requires byte fidelity, and redaction is log
      hygiene rather than a boundary, so rewriting artifact content would be both a lie and a
      corruption.
- [x] No-worker behavior: a webhook delivery with no connected worker leaves the run `Queued` and it
      executes once a worker connects (no loss, no duplicate run).
- [x] The suite documents its prerequisites (Release build first, like the existing E2E), runs in a
      bounded time, and is excluded from the default fast filter by category.
- [x] All assertions are against observable state (database rows, HTTP responses, files on disk,
      process exit codes) — not against worker-internal objects.

## Subtasks

- [x] `tests/Zenkins.Tests/Milestone/` fixture builder: bare repo, `pipeline.lua` variants, webhook
      signing helper, worker/server process launcher reusing the F-007/F-008 harness pieces.
- [x] Happy-path tests (manual + webhook) with artifact download and log-per-step assertions.
- [x] Failure-taxonomy tests with exact status/reason assertions.
- [x] Sandbox-at-product-level test (host filesystem side effects asserted absent).
- [x] Secret-redaction E2E.
- [x] No-worker queueing test.
- [x] `docs/development.md`: how to run the milestone suite and what it needs.

## Dependencies

- F-011, F-012, F-013, F-014, F-015, F-016, F-017 (all of them)

## Test Cases

### TC-001: Manual run, full chain

Steps:
1. `dotnet build -c Release`.
2. Start the server on a temporary SQLite file; register an Owner; create the org's project pointing
   at the fixture repository; mint a worker token; start the real worker; trigger a run on `main`.
3. Wait for a terminal state; download the artifact; fetch the log; read the step rows.

Expected:
- `Success`; commit equals the fixture's `HEAD`; three `Success` step rows; artifact SHA-256 matches;
  log shows each step's stdout under its step.

### TC-002: Webhook run and replay

Steps:
1. Configure a webhook for `main`; POST a correctly signed push payload for the fixture's tip.
2. Replay the identical delivery (same delivery id).
3. Wait for terminal state.

Expected:
- Exactly one run, `TriggerType = Webhook`, correct commit, `Success`; the replay returns 202 and
  creates nothing.

### TC-003: Failure taxonomy

Steps:
1. Run each fixture variant: no pipeline file, syntax error, second step exits 7, definition
   `while true do end`, and a run cancelled during step 1.

Expected:
- `pipeline-missing`, `pipeline-syntax`, `step-failed` (exit 7), `pipeline-timeout`, `Canceled`
  respectively; each terminal, each logged with a specific reason, and the "step 3" marker absent
  where the run must stop.

### TC-004: Sandbox holds at the product boundary

Steps:
1. Pipeline whose step calls `io.open("/tmp/zenkins-escape", "w")`, `require("os")`,
   `package.loaded.dynamic.eval("1+1")`, `os.execute("touch /tmp/zenkins-escape")`.
2. Assert the run's status and inspect `/tmp/zenkins-escape`.

Expected:
- Step fails with a Lua error naming the missing function; the file does not exist; no process ran;
  the worker stays alive and the next run works.

## Development Log

### 2026-11-13

- Planned from the v0.3 plan; no work started. Deliberately a separate card, not a subtask of
  F-017: the value is in an independent author writing the assertions from the PRD, so the last gate
  of the milestone is not the same person who built the pieces.

### 2026-11-13 — implemented, reviewed, accepted

- Six tests, about thirty-five seconds, in their own category so the fast suite stays untouched at
  883. One bare repository built on disk, one real server process on a temporary database and port,
  one real worker executable as a separate OS process, one signed webhook delivery, one artifact
  compared by SHA-256 on both sides.
- **The "real process" claim had to move inside the suite.** The first review accepted the evidence
  only because it had been sampled from outside; that means a harness degrading into an in-process
  fake would stay green. The host now exposes the worker's pid and the tests assert three things
  about it: it is not the test process, it exists in the process table with the same start time, and
  the `Workers` row the server wrote points at it.
- **Failed runs are checked for what did not happen.** Each taxonomy case asserts the terminal state,
  reason and exit code and the absent marker line, step rows and artifact; then the run is left alone
  and re-checked for a few seconds - run state, step signatures and marker line counts - so a slow
  run cannot pass as settled.
- Two interpretations were ruled on rather than silently weakened. An artifact keeps its bytes even
  when a step wrote a secret into it: F-016's byte fidelity wins and redaction is log hygiene, not a
  boundary, so the assertion is that the log, the log download and the page carry `***` while the
  artifact download matches the bytes the pipeline wrote. And the sandbox refusal carries
  `pipeline.lua:(line,col)` plus the engine's wording instead of naming the missing global, because
  that is what MoonSharp reports and what `docs/lua-runtime.md` already documents; changing the
  product's error text is a separate card's job, not an acceptance card's.
- Sandbox probes get their own escape path per probe under the run's temporary root, asserted absent
  before and after, with the whole root scanned afterwards and a healthy run on the same worker as a
  positive control.
- Gate: `dotnet build -c Release` 0 warnings 0 errors; `Category=Milestone` 6/0/0 in ~35s;
  `Category!=Milestone` exactly 883 - asserted to be 883 and not zero, since a filter the runner does
  not understand would report success over an empty set; full suite 889/0/0, zero skipped. No
  leftover processes, no repository pollution.
- Noted, not fixed: a run that overruns during definition can be claimed by either the worker's
  deadline or the server's sweeper, so the runaway case pins the timeout settings to make the worker
  win deterministically; and the fixture rewrites a project's repository URL by direct SQL because a
  `file://` URL does not pass the product's own validation - a harness limitation, deliberately
  documented rather than worked around in the product.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `tests/Zenkins.Tests/Milestone/*`, `docs/development.md`
- How to run/verify: `dotnet build -c Release` then
  `dotnet test -c Release --filter "Category=Milestone"`.
- Results: accepted 2026-11-13. `dotnet build -c Release` 0 warnings 0 errors; `Category=Milestone`
  6/0/0 (~35s); `Category!=Milestone` 883/0/0; full suite 889/0/0, zero skipped; no leftover
  processes and no repository pollution after a run.
