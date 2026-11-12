---
id: F-007
title: Per-run work directory and process runner (Worker)
type: feature
priority: P1
size: M
status: review
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

- [x] Work directory trio created per run; two concurrent runs never share a writable directory;
      a malformed run id never touches the filesystem.
- [x] Process runs with exact argv fidelity (arguments with spaces/quotes/`$` reach the child
      verbatim), captured exit code, no shell involved.
- [x] stdout and stderr are delivered line-by-line as produced (callback API), each stream in
      order; the runner streams, it does not wait for process exit to hand over output.
- [x] Process-tree kill terminates children (used by cancellation), and disposal always reaps the
      process (no zombie) even when the consumer abandons it.
- [x] Temp cleanup removes `temp/` on success and failure when enabled, and leaves it when
      disabled.
- [x] Worker remains ASP.NET-free; all behaviour unit/integration tested cross-platform (macOS
      here; code paths must be Windows/Linux-safe by construction).

## Subtasks

- [x] `WorkDirectory` service (root config, GUID validation, creation, temp cleanup).
- [x] `ProcessRunner` (argv → ProcessStartInfo, line callbacks, exit code, kill-tree, dispose-safe).
- [x] Wire into the claim → execute → report loop from F-006 (worker side).
- [x] Test asset: tiny `ArgvEcho` console project under `tests/Zenkins.Tests/TestAssets` used to
      prove argv fidelity cross-platform (no shell, no platform-specific echo).
- [x] Tests (`Category=WorkerRuntime`): directory isolation, argv fidelity, line streaming
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

**WorkDirectory service.** `src/Worker/WorkDirectory.cs`: `WorkDirectoryService(workRoot)` with
`Prepare` / `CleanupTemp`. The run id is parsed as a GUID **before any path is built**
(`IsValidRunId`, also accepting the raw string overload the loop uses), and the directory name is
the GUID's canonical `D` rendering — a malformed id (traversal, garbage, empty) throws
`WorkDirectoryException` and the file system is never touched, while an exotic-but-parseable
spelling (`{UPPER-CASE}`, `N` form) can only ever map onto the canonical traversal-free name (pinned
by a test). Creation is plain `Directory.CreateDirectory` per member — idempotent under retries and
restarts, existing contents kept. `CleanupTemp` deletes only `temp/`; `repository/` and
`artifacts/` are kept (retention is a later card). Work root: `ZENKINS_WORK_ROOT`, default `work/`
relative to the worker's current directory (`Path.GetFullPath` at construction; absolute values
pass through).

**ProcessRunner.** `src/Worker/ProcessRunner.cs`: `ProcessStartInfo` with `FileName` = argv[0],
`ArgumentList` = argv[1..] added verbatim, `UseShellExecute=false`, both streams redirected,
`CreateNoWindow` — no shell, no quoting, no interpolation anywhere (PRD §8 constraint 6). Output
callbacks via `BeginOutputReadLine`/`BeginErrorReadLine` (one dedicated reader thread per stream, so
per-stream order is preserved; cross-stream order is not promised), invoked **as lines are
produced** — the streaming-before-exit test observes the first line through a TCS while
`HasExited` is still false. Exit code captured after the parameterless `WaitForExit()` (which also
drains the async readers, so the last lines are delivered before the exit code is returned).
Cancellation of `RunAsync` kills the whole tree, reaps the handle (pipes drained, no orphan) and
surfaces `OperationCanceledException`. `KillTree()` is public and cross-platform
(`Process.Kill(entireProcessTree: true)`) — F-009's cancellation reuses it as-is. Dispose safety: a
consumer that abandons the runner leaves no zombie — `Dispose` kills the tree, and the run's own
completion path reaps the handle (`_runInFlight` makes sure `Dispose` and an in-flight `RunAsync`
never dispose one `Process` concurrently; a bounded 5s reap wait covers the not-in-flight case).
Child environment: `ZENKINS_RUN_ID`, `ZENKINS_WORKER_NAME`, `ZENKINS_PROJECT_ID` set on top of the
fully inherited parent environment (pinned by running `env` probes in the child).

**Worker loop wiring.** `src/Worker/WorkerRunLoop.cs` consumes F-006's endpoints one cycle per
iteration: `claim` (204 → wait `ZENKINS_CLAIM_SECONDS`, default 2s; transport failures and request
timeouts retry through the existing `BackoffScheduler`; definitive 4xx backs off too — a restored
credential picks work up without a restart) → `accepted` (the lease-clearing edge; a definitive 4xx
gives the run up before anything executes) → `WorkDirectoryService.Prepare` → execute with
`work/{run-id}/repository` as cwd → stdout/stderr streamed line-by-line into `temp/stdout.log` /
`temp/stderr.log` (fresh files per execution, AutoFlush so an observer sees lines as they are
produced; **SignalR upstream is F-008 — this card's only output handling is local files**) →
delete `temp/` when `ZENKINS_CLEANUP_TEMP` (default true) → `completed {exitCode}` (0 → Success,
non-zero → Failed server-side; the server owns the mapping). **Cleanup deliberately precedes the
completed report**, so a run recorded terminal has its temp already gone — the first E2E runs
exposed the report-first order as a real observation race (terminal in the DB, temp still on disk
for milliseconds) and the order was swapped. Reporting retries transport failures with backoff but
never retries a definitive 4xx (a 409 on a completed retry means the first report already landed).
A shutdown while a process runs kills the whole tree and skips the completed report — the run's
Lost transition is F-010's business.

**Concurrency.** `WorkerRunner.RunAsync` now starts `ZENKINS_WORKER_CONCURRENCY`
(`ZENKINS_WORKER_CONCURRENCY`, default 1, registered/heartbeated to the server as `MaxConcurrency`)
`WorkerRunLoop` instances next to the unchanged F-005 heartbeat loop, all tied to the shutdown
token. Each loop executes at most one run at a time, so the worker never exceeds its registered
concurrency, and every run lives in its own `work/{run-id}` tree — no writable path is shared
between runs. The heartbeat loop's behaviour (announce-once, reconnect messages, backoff, final
heartbeat) is untouched; the loops are separate tasks so heartbeats keep flowing while a build runs.

**Worker API additions.** `WorkerApiClient` gains `ClaimAsync` (204 → null; the worker never learns
why it got nothing), `AcceptAsync` and `CompleteAsync` (`{exitCode}`, source-generated JSON). Error
mapping: `EnsureSuccessAsync` gained `treatAllClientErrorsAsDefinitive` — on the run-reporting calls
every 4xx is a definitive per-run outcome (`WorkerApiRejectedException`), while register/heartbeat
keep the transient-409 retry semantics (A, second review fix, unchanged). Unexpected per-cycle
failures (a bug, a torn claim response) are logged, backed off and the loop keeps claiming — one
broken cycle must not kill the loop.

**Configuration.** `WorkerConfiguration` gains `WorkRoot` (`ZENKINS_WORK_ROOT`, default `work`),
`CleanupTemp` (`ZENKINS_CLEANUP_TEMP`, default true, strict true/false), `MaxConcurrency`
(`ZENKINS_WORKER_CONCURRENCY`, default 1, 1–1024; replaces the old constant and feeds registration
and heartbeat payloads) and `ClaimPollInterval` (`ZENKINS_CLAIM_SECONDS`, default 2, 0.1–3600,
 invariant float so tests can poll fast). Every failure is the house one-line configuration error.

**Test asset.** `tests/Zenkins.Tests/TestAssets/ArgvEcho` — a tiny console project with probe
modes (`echo`, `echo-each`, `probe`, `write-slow`, `stream`, `spawn`, `env`, `exit`), referenced by
the test project with `ReferenceOutputAssembly=false` (plus a `Compile Remove="TestAssets/**"`
guard — without it the SDK glob pulled the asset's sources and obj files into the test assembly).
Tests locate the newest built DLL under its `bin` and run it through `dotnet exec`, so the fidelity
proof is a real child process of the real dotnet host on every platform.

**Tests (`Category=WorkerRuntime`, 23) + E2E (`Category=Workers`, 1) + config pins (10).**
Directory trio + idempotence + malformed-id refusal (traversal/empty/non-GUID, file system verified
untouched) + exotic-GUID canonicalization + temp-only cleanup (`WorkDirectoryTests`); argv fidelity
(spaces/quotes/`$`/`;`/`&&`/leading-dash/empty), exit-code capture, start-failure surfacing,
streaming-before-exit with per-stream order (TCS + `HasExited`), token-cancel kill-tree with
grandchild-orphan check via pid, direct `KillTree`, dispose-reaps-abandoned-process, child env +
inheritance, and the two-run concurrent isolation probe where each run's `repository/` ends up with
exactly its own file (`ProcessRunnerTests`); the claim loop against the real in-memory server
pipeline — two queued runs both terminal Success with exit 0 and worker attribution, exit-3 → Failed
with the loop continuing to the next run, temp retained with both log files when cleanup is
disabled (`WorkerRunLoopTests`); the process-level E2E (`WorkerRunProcessE2ETests`, F-005's real
server + real worker pattern) triggers `dotnet <ArgvEcho.dll> echo-each 2000 first second third`
through the real project form and asserts: run Success with exit 0, the trio under
`ZENKINS_WORK_ROOT`, the stdout log growing to all three lines **while the run is still Running**
(proving incremental capture), stderr log present, temp cleaned, `repository/`/`artifacts/` kept,
then a second run `exit 3` → Failed with exit 3, and a graceful SIGTERM stop. Config pins extend
`WorkerConfigurationTests` (defaults, knob parsing, out-of-range errors).

**Red proofs (both restored immediately afterwards, re-verified green).**
1. argv fidelity: `ArgumentList` replaced by a shell-style re-join
   (`_startInfo.Arguments = string.Join(" ", options.Arguments)`) →
   `Arguments_reach_the_child_verbatim_with_no_shell_involved`: **`Assert.Equal() Failure:
   Collections differ at index 0. Expected: "hello world", Actual: "hello"`** — the child received
   the space-separated argument as two.
2. per-run isolation: `WorkDirectoryService.Prepare` pinned to one shared directory name (run id
   ignored) → `Two_concurrent_runs_in_distinct_work_directories_never_share_writable_paths`:
   **`Expected: ["probe-dac89480…txt"], Actual: ["probe-dac89480…txt", "probe-09b55341…txt"]`** —
   each run saw the other's probe file.

**Known gaps.**
- The worker's stdout/stderr handling is local temp files only; SignalR upstream (F-008) reads
  these files or takes over the callbacks.
- Cancellation/cancel-reporting semantics for a running build are F-009's; shutdown currently
  kills the tree and leaves the run to F-010's Lost detection.
- `FailureReason` stays null (F-009/F-010 own it); the reason for a run that never produced an exit
  code (unstartable command, work-directory failure) is only in the worker log, reported as exit
  code 70 (`WorkerRunLoop.InfrastructureFailureExitCode`, non-zero → Failed).
- Kill-tree orphan checks are best-effort pid liveness polls (documented in TC-004); a killed
  grandchild reparents to init and is reaped there.
- A run whose `completed` report keeps failing on the transport while the worker stays Online
  stays Running forever: F-010's Lost detection today only covers the Offline case, and (since
  this card's review fix) a definitively given-up 429-throttled completed report lands in the
  same state. Compensation (worker-side re-report or server-side reconciliation) is left to an
  F-010 extension to decide.
- The E2E (like F-005's) needs `dotnet build -c Release` before running; it locates the newest
  built Server/Worker/ArgvEcho binaries.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| B1 | minor | The first loop implementation reported `completed` **before** deleting `temp/`, so a test polling the server for the terminal state could observe `Success` while `temp/` was still on disk for milliseconds — an observation race that failed the E2E once in isolation (temp survived cleanup) | Fixed | Cleanup now precedes the completed report: when the server records the terminal state, the run's temp is already gone; documented in the Development Log |
| B2 | major | `EnsureSuccessAsync`'s `treatAllClientErrorsAsDefinitive` was a **dead parameter**: the definitive-4xx condition simplified to `!IsTransientStatus(statusCode)` regardless of the flag, so 408/409/425/429 on the reporting calls (accepted/completed) fell through to the retryable branch and were retried forever — a completed report whose first attempt landed but whose response was lost is retried into a 409, then re-retried every ≤30s until shutdown (concurrency-1 worker starves); fixed in the 2026-11-13 review fixes below | Fixed | Condition is now `statusCode is >= 400 and < 500 && (!IsTransientStatus(statusCode) || treatAllClientErrorsAsDefinitive)`; pinned by three stub-server tests with a red proof |

## Review Fixes

### 2026-11-13 — review fixes (1 major, 4 minors)

**Major (B2) — dead `treatAllClientErrorsAsDefinitive` parameter (WorkerApiClient).** The old
condition `status in 4xx && !IsTransient(status) && !(flag && IsTransient(status))` is, for a
transient status, always false in its second clause — the flag changed nothing, so every
408/409/425/429 on the reporting calls was retried forever. Fix: `statusCode is >= 400 and < 500
&& (!IsTransientStatus(statusCode) || treatAllClientErrorsAsDefinitive)` — transient 4xx stay
retryable on register/heartbeat/claim (default `false`) and become definitive exactly on the
reporting calls (accepted/completed pass `true`).

New pins (`tests/Zenkins.Tests/WorkerRuntime/WorkerApiDefinitiveReportingTests.cs`,
Category=WorkerRuntime, stub `HttpMessageHandler` server with per-run scripted answers and
attempt counters):
- `A_409_on_the_completed_report_gives_the_run_up_and_the_loop_claims_the_next_run`: the stub
  answers 409 to run 1's completed (the "first report landed, response lost" scenario); the loop
  gives run 1 up after exactly one completed attempt and claims/executes/completes run 2.
- `A_409_on_the_accepted_report_gives_the_run_up_and_the_loop_claims_the_next_run`: the stub
  answers 409 to run 1's accepted; the loop gives run 1 up (one accepted attempt, zero executed,
  zero completed) and carries run 2 to a completed report.
- `The_claim_call_keeps_its_retryable_409_and_definitive_other_4xx_semantics`: client-level —
  claim (`treatAllClientErrorsAsDefinitive=false`) answers 409 with a retryable
  `HttpRequestException` carrying the status (unchanged by the fix) and 400 with
  `WorkerApiRejectedException`.

**Red proof** (dead-parameter condition restored temporarily, re-verified, fix restored): both
loop pins went red — the log shows the starvation loop verbatim (`could not reach the server to
report run … completed (the server answered 409 Conflict.). Retrying in 1.2s.` repeating until
the 30s give-up deadline; run 2 never claimed), while the claim pin stayed green (proving the
`treatAllClientErrorsAsDefinitive=false` path is identical under both conditions). Result:
**3 total / 2 failed / 1 succeeded** red → **3/3 green** after restoring the fix.

**Minor — ProcessRunner OCE path now drains the readers.** The cancellation branch killed the
tree and reaped via `WaitForExitAsync`, which does not wait for the asynchronous output readers —
a lingering `OutputDataReceived`/`ErrorDataReceived` callback could fire into the consumer's
already-disposed `StreamWriter` after the `OperationCanceledException` surfaced. It now uses the
parameterless `process.WaitForExit()` before throwing (same as the normal exit path), making the
"pipes drained" promise true on the cancellation path too.

**Minor — Dispose-vs-start race closed.** `Dispose` landing between `Process.Start()` and the
`_process` assignment saw a null field and returned without killing the tree; the start path now
re-checks `_disposed` right after the assignment and kills the tree itself (the in-flight run's
own completion path still reaps, per the existing `_runInFlight` guard).

**Minor — Known gap registered** (see Known gaps): a persistently failing completed report on an
Online worker leaves the run Running forever — F-010's Lost detection only covers Offline today,
and the definitive 429-on-completed give-up (introduced by the major fix) has the same
consequence. Compensation is F-010 extension territory.

**Minor — stale failure text (WorkerRuntimeConfigTests).** The fail-closed message claimed
`Zenkins.Tests.csproj` must keep `ReferenceOutputAssembly="false"` on the Worker reference; the
project file has carried a full reference since F-005 (that is what copies the apphost, deps and
runtimeconfig.json into the test output). The message — and the neighbouring comment — now
describe the actual reference.

Review-fix gate results (2026-11-13):
- Build: **PASS** — `dotnet build -c Release`, 0 warnings / 0 errors.
- Tests: **PASS** — **292/292** (289 pre-review + 3 stub-server pins).
- Red proof: **PASS** — as recorded above.
- PostgreSQL contract gate: **PASS** — "No changes have been made to the model since the last
  migration" (no schema change, no new packages on this card).

## Verification

- Related files:
  - `src/Worker/WorkDirectory.cs`, `src/Worker/ProcessRunner.cs`, `src/Worker/WorkerRunLoop.cs` (new)
  - `src/Worker/WorkerRunner.cs` (heartbeat loop extracted; N run loops started next to it),
    `src/Worker/WorkerApiClient.cs` (claim/accepted/completed + all-4xx-definitive reporting),
    `src/Worker/WorkerConfiguration.cs` (`ZENKINS_WORK_ROOT`, `ZENKINS_CLEANUP_TEMP`,
    `ZENKINS_WORKER_CONCURRENCY`, `ZENKINS_CLAIM_SECONDS`), `src/Worker/Program.cs` (work root
    resolution + service construction)
  - `tests/Zenkins.Tests/TestAssets/ArgvEcho/{ArgvEcho.csproj,Program.cs}` (new test asset)
  - `tests/Zenkins.Tests/WorkerRuntime/{ArgvEchoBinary,WorkDirectoryTests,ProcessRunnerTests,WorkerRunLoopTests,WorkerRunProcessE2ETests}.cs` (new)
  - `tests/Zenkins.Tests/Zenkins.Tests.csproj` (ArgvEcho build-only reference + TestAssets glob guard)
  - `tests/Zenkins.Tests/Workers/WorkerConfigurationTests.cs` (new-knob pins)
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test -c Release --filter "Category=WorkerRuntime"`
  3. `dotnet test -c Release` (the E2E carries `Category=Workers`; the whole suite must stay green)
  4. PostgreSQL contract gate:
     `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
- Results (2026-11-13):
  - Build: **PASS** — `dotnet build -c Release`, 0 warnings / 0 errors (`TreatWarningsAsErrors` active).
  - Tests: **PASS** — **289/289** across the suite (255 pre-card + 34: 23 `Category=WorkerRuntime`,
    1 process-level E2E under `Category=Workers`, 10 configuration pins), the E2E additionally run
    5× consecutively, all green.
  - `Category=WorkerRuntime`: **PASS** — 23/23.
  - WorkerRuntimeConfigTests (ASP.NET-free worker): **PASS** — runtimeconfig carries no
    `Microsoft.AspNetCore*` framework; the Worker still references only the shared project.
  - PostgreSQL contract gate: **PASS** — "No changes have been made to the model since the last
    migration" (no schema change on this card; no new NuGet packages either).
  - Red proof 1: **PASS** — argv re-joined into a quoted shell-style string →
    `Arguments_reach_the_child_verbatim_with_no_shell_involved` red (Expected: `"hello world"`,
    Actual: `"hello"`) → ArgumentList restored → green.
  - Red proof 2: **PASS** — per-run directory stripped to one shared name →
    `Two_concurrent_runs_in_distinct_work_directories_never_share_writable_paths` red (each
    repository contained both runs' probe files) → restored → green.
  - TC-001…TC-005 all covered as listed above (isolation, fidelity, streaming-before-exit,
    kill-tree with orphan check, cleanup on/off).
