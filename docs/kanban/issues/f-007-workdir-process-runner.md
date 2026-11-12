---
id: F-007
title: Per-run work directory and process runner (Worker)
type: feature
priority: P1
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

The Worker executes a claimed run's argv command as a native process inside an isolated per-run work
directory (`work/{run-id}/repository|artifacts|temp`), captures stdout/stderr incrementally as lines,
records the exit code, and cleans `temp` afterwards per configuration.

## Background

PRD §3.4.6 (independent work directory per run; different runs never share writable directories;
cleanup per config), §8 constraint 6 (`ProcessStartInfo`, never a shell string). No git yet
(Phase 4): `repository/` is created empty as the command's working directory so the Phase 4 layout
lands without moving anything. Artifacts collection itself is Phase 3; the directory just exists.

Decisions:

- Command cwd: `work/{run-id}/repository`. `artifacts/` and `temp/` are created alongside.
- `ProcessStartInfo`: `FileName` = argv[0], `ArgumentList` = argv[1..], `UseShellExecute=false`,
  stdout/stderr redirected with line callbacks (per-stream order preserved; cross-stream order is
  not promised). No quoting, interpolation or shell.
- Kill support exposed now (used by F-009): kill the whole process tree, cross-platform.
- Minimal environment for the child: `ZENKINS_RUN_ID`, `ZENKINS_WORKER_NAME`, `ZENKINS_PROJECT_ID`;
  rest inherited.
- `run-id` validated as a GUID before any path is built (no traversal); work root configurable
  (`ZENKINS_WORK_ROOT`, default `work/` relative to the worker).
- `ZENKINS_CLEANUP_TEMP` (default `true`): delete `temp/` after the run; `repository/` and
  `artifacts/` are kept for now (retention policy is a later card).

## Acceptance Criteria

- [ ] Work directory trio created per run; two concurrent runs never share a writable directory;
      a malformed run id never touches the filesystem.
- [ ] Process runs with exact argv fidelity (arguments with spaces/quotes/`$` reach the child
      verbatim), captured exit code, no shell involved.
- [ ] stdout and stderr are delivered line-by-line as produced (callback API), each stream in
      order; the runner streams, it does not wait for process exit to hand over output.
- [ ] Process-tree kill terminates children (used by cancellation), and disposal always reaps the
      process (no zombie) even when the consumer abandons it.
- [ ] Temp cleanup removes `temp/` on success and failure when enabled, and leaves it when
      disabled.
- [ ] Worker remains ASP.NET-free; all behaviour unit/integration tested cross-platform (macOS
      here; code paths must be Windows/Linux-safe by construction).

## Subtasks

- [ ] `WorkDirectory` service (root config, GUID validation, creation, temp cleanup).
- [ ] `ProcessRunner` (argv → ProcessStartInfo, line callbacks, exit code, kill-tree, dispose-safe).
- [ ] Wire into the claim → execute → report loop from F-006 (worker side).
- [ ] Test asset: tiny `ArgvEcho` console project under `tests/Zenkins.Tests/TestAssets` used to
      prove argv fidelity cross-platform (no shell, no platform-specific echo).
- [ ] Tests (`Category=WorkerRuntime`): directory isolation, argv fidelity, line streaming
      (interleaved writers), exit codes, kill-tree, temp cleanup, traversal rejection.

## Dependencies

- F-006

## Test Cases

### TC-001: Directory isolation under concurrency

Steps:
1. Execute two runs concurrently.

Expected:
- Two distinct `work/{run-id}/` trees; neither run observes the other's files (each run writes a
  probe file named by its run id and asserts only its own exists).

### TC-002: Argv fidelity

Steps:
1. Run `ArgvEcho` with args containing spaces, quotes, `$`, `;`, `&&`, a leading `-`.

Expected:
- The child receives exactly the argument list; its echoed output matches; exit code 0.

### TC-003: Streaming before exit

Steps:
1. Run a child that writes a line, sleeps, then writes another line and exits.

Expected:
- The first line is delivered via callback before the process exits (observable via a task
  completion source), per-stream order preserved, final exit code correct.

### TC-004: Kill-tree

Steps:
1. Run a child that spawns a grandchild and sleeps; kill the run.

Expected:
- Child and grandchild terminate; runner reports the kill; no orphan remains (best-effort check).

### TC-005: Cleanup

Steps:
1. Run with cleanup on and off.

Expected:
- `temp/` removed / retained accordingly; `repository/` and `artifacts/` untouched in both cases.

## Development Log

### 2026-11-13

- (pending implementation)

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: (pending)
- How to run/verify: `dotnet build -c Release` && `dotnet test -c Release --filter "Category=WorkerRuntime"`
- Results: (pending)
