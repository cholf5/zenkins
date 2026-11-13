---
id: F-011
title: Lua runtime host and sandbox (Worker)
type: feature
priority: P1
size: M
status: done
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

The Worker can execute arbitrary Lua source under the contract in
[`docs/lua-runtime.md`](../../lua-runtime.md): a hardened sandbox, a host-driven coroutine with a
wall-clock budget and cancellation, and errors carrying a source position. No run executes a script
yet — this card delivers the runtime and its tests, not the pipeline feature.

## Background

PRD §3.3.6 requires a pure C# runtime, only the Zenkins SDK exposed, no arbitrary CLR access, no
assembly loading, unwanted standard libraries off, and limits on execution time and resources.
`docs/lua-runtime.md` turns those requirements into a concrete configuration; this card implements
§1, §3, §4, §5 and §8 of that document.

Decisions:

- **Pin `MoonSharp` 2.0.0 exactly** in `Directory.Packages.props`, with the rationale in a comment
  (the file already carries this style of note for EF Core). Referenced by `Zenkins.Worker` only:
  the Server never runs a script, and `Zenkins.Shared` stays engine-free.
- **Explicit module composition, never a preset** (§3): `Basic | ErrorHandling | Metatables |
  Coroutine | String | Table | TableIterators | Math | Bit32 | OS_Time | GlobalConsts`. Presets are
  measured and rejected: `Preset_HardSandbox` drops `pcall`/`xpcall`/`setmetatable`/`getmetatable`/
  `coroutine` and all of `os`, while `Preset_SoftSandbox` keeps the CLR bridge (`dynamic`, `json`).
  `GlobalConsts` is composed in because without it `_G` and `_VERSION` are `nil`. The resulting
  globals are asserted by test, not assumed.
- **Post-construction hardening** (§3.1): `dynamic`, `json`, `package`, `_MOONSHARP` are nilled and
  `print` is re-bound to the run-log sink. `dynamic`/`json` cannot even exist under this composition,
  but the assignments stay as the second line of defence — if anyone ever composes `Dynamic` or `Json`
  back in, nil-ing the globals is provably *not* enough on its own, because `package.loaded` keeps
  the table alive (`package.loaded.dynamic.eval('1+1')` → `2`). Hence `package` is nilled too, and
  the regression test composes `Dynamic` on purpose to prove the hazard is handled.
- **The seam is ours**: `ILuaRuntime` exposing
  `Execute(source, name, deadline, CancellationToken)` → either a result or a
  `LuaScriptException` carrying `(Message, Source, Line, Column)`. `deadline` is the run's remaining
  wall clock (`BuildRun.DeadlineAt`, frozen at accept time by F-009 — `docs/lua-runtime.md` §5.3),
  not a Lua-specific budget type. No MoonSharp type appears outside `ILuaRuntime`'s implementation
  and the host-primitive registration surface.
- **Suspension is a first-class seam capability** even though no primitive yields yet: the driver
  handles both yield kinds and the seam exposes `RegisterHostFunction` for primitives that return a
  yield request. F-014/F-015 need it, and retrofitting it later would mean rewriting the driver.
- **A suspension is not a host call.** `CoroutineState.Suspended` is produced both by a primitive
  asking for I/O and by the script calling `coroutine.yield` itself (measured: `state=Suspended
  res.Type=String res=user-payload pending=0`). The driver must branch on the request queue (or, in
  production, on a private sentinel — `docs/lua-runtime.md` §5.1), because the naive
  `pending.Dequeue()` on an empty queue throws `InvalidOperationException: Queue empty.`, which is
  not one of the two exceptions the loop catches.
- **One `Script` per execution**, thread-affine, never pooled (§5.1). Abandonment = stop resuming,
  discard the `Script`.
- **No CLR registration anywhere**: no `UserData.RegisterType/RegisterAssembly`, no proxy types.

## Acceptance Criteria

- [x] `Directory.Packages.props` pins MoonSharp 2.0.0 with a rationale comment; only
      `Zenkins.Worker` references it; `dotnet build -c Release` stays at 0 warnings with
      `TreatWarningsAsErrors` and the repository's analyzer set.
- [x] `THIRD-PARTY-NOTICES.md` exists at the repository root and carries, in this order: the
      MoonSharp `LICENSE` copied **verbatim** (including the `{organization}` placeholder as
      shipped), the Marco Mastropaolo copyright and `All rights reserved.` lines, the KopiLua/LoDC
      attribution **plus the full MIT text**, the `vscode-mono-debug` attribution **plus the full
      MIT text**, the Eclipse icon attribution (with a note that upstream states no licence and we
      do not redistribute the icons), the MoonSharp icon attribution, and a scope note (only the
      `MoonSharp.Interpreter` assembly is redistributed — no debugger console, no REPL, no
      extension). Verbatim alone is not enough: the 2.0.0 package ships no `LICENSE` file at all,
      and the 3.0.0-beta.1 text names two MIT-licensed parts without carrying either MIT body
      (`docs/lua-runtime.md` §8). The README license section links it.
- [x] A sandbox self-check runs as a test over the full deny list, asserting each name through
      `DoString("return type(<name>)")` (not through the C# `Globals` enumeration, which still lists
      nil-valued keys): `io`, `load`, `loadfile`, `loadstring`, `dofile`, `require`, `debug`,
      `dynamic`, `json`, `package`, `_MOONSHARP`, `os.execute`, `os.exit`, `os.getenv`, `os.remove`,
      `os.rename`, `os.tmpname` are all `nil`; `os.clock/date/difftime/time` are functions;
      `pcall`, `xpcall`, `setmetatable`, `getmetatable`, `coroutine` and `_G` are present.
- [x] The registry hazard is pinned by a test that composes `Dynamic` deliberately and proves the
      hardened runtime exposes no registry at all — the test fails if the `package` hardening is
      removed (measured shape: `type(package)` is `nil`, `pcall(function() return
      package.loaded.dynamic end)` is `false` with `attempt to index a nil value`; without the
      hardening the same source returns `2`).
- [x] A runaway script (`while true do end`, and a tight allocation loop) is abandoned at its
      budget: the call returns within the budget plus a small margin, the coroutine is never
      resumed again, and the discarded `Script` is not reused.
- [x] Cancellation passed to `Execute` stops a script at the next yield point (within
      `AutoYieldCounter` instructions), with the same abandon semantics.
- [x] Syntax and runtime errors surface `source:(line,col-col)` in one line — no CLR stack trace in
      the message — and a script that raises after N log lines keeps those lines.
- [x] The host-call suspension protocol works end to end: a test registers a primitive that yields,
      the driver resumes it with a value, and the Lua call site receives that value — including
      through a Lua wrapper, `pcall` and an `__index` metatable (fixtures 15–16 in
      `docs/lua-runtime.md` §9). The driver branches on `CoroutineState`, so a budget yield is
      resumed with no arguments and an SDK call is resumed with its answer.
- [x] A script that yields on its own (`coroutine.yield('x')`) is treated as a script violation: the
      driver does not dequeue an empty request queue, it abandons the run — the
      `InvalidOperationException: Queue empty.` path must not exist (`docs/lua-runtime.md` §5.1 and
      fixture 18).
- [x] `print` from Lua reaches the runtime's log sink, not the worker's console.
- [x] Existing behavior is unchanged: the whole suite still passes and argv runs (F-006/F-007) still
      execute exactly as before; the Worker stays ASP.NET-free (`WorkerRuntimeConfigTests`).

## Subtasks

- [ ] Package pin, project reference, notices file, README link.
- [ ] `src/Worker/Lua/{ILuaRuntime,LuaRuntime,LuaScriptException,LuaLogSink}.cs`
      (new): composition, hardening, driver, error mapping, thread affinity.
- [ ] `LuaSandboxProbe` (test helper) that builds a script through the real runtime and asserts the
      deny list, so the fixtures cannot drift from the implementation.
- [ ] Tests (`Category=Lua`): deny list, registry escape, runaway abandon, cancellation, error
      positions, suspension round-trip, `print` routing, two runtimes on two threads concurrently.
- [ ] Document the runtime knobs (instruction counter, budget defaults) next to the existing worker
      configuration pins (`WorkerConfigurationTests`).

## Dependencies

- DOC-002 (the contract this implements)

## Test Cases

### TC-001: Sandbox deny list through the real runtime

Steps:
1. Build a runtime with the production composition.
2. For each forbidden name, evaluate `return type(<name>)`.

Expected:
- Every forbidden name is `nil`; `os.time`/`os.date`/`pcall`/`xpcall`/`setmetatable`/`coroutine`
  are present; no `UserData` type is registered.

### TC-002: The registry hazard is handled where it can occur

Steps:
1. Build a runtime whose composition deliberately includes `CoreModules.Dynamic` (to exercise the
   hazard), harden it exactly as production does, then evaluate `return type(package)` and
   `local ok, e = pcall(function() return package.loaded.dynamic end) return tostring(ok) .. '|' ..
   tostring(e)`.
2. Build the production composition and assert `type(package)` is `nil` and that `package.loaded`
   is unreachable.

Expected:
- (1) `type(package)` is `nil`; the `pcall` returns `false` and a message containing
  `attempt to index a nil value` (measured: `false | pipeline.lua:(1,31-60): attempt to index a nil
  value`). Removing the `package` hardening makes the same source return `2` via
  `package.loaded.dynamic.eval('1+1')` — that shape is fixture 12a, recorded only to show what the
  hazard looks like, and is **not** the assertion.
- (2) `nil` both, and `type(dynamic)`/`type(json)` are `nil`.

### TC-003: Runaway script is abandoned, not hung

Steps:
1. Execute `while true do end` with a 200 ms budget.
2. Execute `while true do local t = {} end` with the same budget.

Expected:
- Both return (or throw a budget exception) within ~budget + 100 ms; no thread left spinning; a
  second execution on a fresh runtime succeeds afterwards.

### TC-004: Cancellation and error positions

Steps:
1. Execute a script that loops calling an SDK stub; cancel after 50 ms.
2. Execute `local x = 1\n\nboom()` where `boom` is undefined, named `pipeline.lua`.

Expected:
- (1) returns the cancellation outcome within one yield interval.
- (2) reports `pipeline.lua:(3,…)` with `attempt to call a nil value`, and no CLR frames.

## Development Log

### 2026-11-13

- Planned from the v0.3 plan; no work started. Implementation must follow
  [`docs/lua-runtime.md`](../../lua-runtime.md) §3 and §5, including the `package.loaded` escape.

### 2026-11-13 — implemented, independently reviewed, accepted

- `ILuaRuntime` exposes `Execute(source, name, deadline, CancellationToken)` returning a
  `LuaExecutionResult` whose outcome is `Completed` / `TimedOut` / `Canceled` / `ScriptViolation`, plus
  `RegisterHostFunction` for primitives that suspend. `deadline` is the run's `BuildRun.DeadlineAt`;
  there is no Lua-specific budget type. `ILuaRuntime.cs` stays engine-free.
- The driver tells the two suspension kinds apart with the request queue **and** a per-execution
  sentinel table, so a script calling `coroutine.yield` itself is abandoned as `ScriptViolation`
  instead of dequeuing an empty queue. The `InvalidOperationException: Queue empty.` path does not
  exist. One `Script` per execution, thread-affine, never pooled.
- Notices: the 2.0.0 package ships no `LICENSE` file, so the shipped text was taken from the
  3.0.0-beta.1 package and diffed verbatim (line-end whitespace normalised, `{organization}`
  placeholder kept once). Both MIT bodies are carried in full rather than by reference.
- Gate: `dotnet build -c Release` 0 warnings 0 errors; `--filter "Category=Lua"` 85 passed / 0 failed /
  0 skipped; full suite 433 passed / 0 failed / 0 skipped (348 before this card). MoonSharp 2.0.0 is
  referenced by `Zenkins.Worker` only, with no transitive package. An independent review then re-ran
  every gate from a clean tree and re-derived each acceptance criterion from the source.
- **Deviation, recorded deliberately:** the subtask asked for the runtime knobs (instruction counter,
  fallback budget) next to the existing worker configuration pins in `WorkerConfigurationTests`. They
  live on `LuaRuntimeOptions` instead, pinned by `tests/Zenkins.Tests/Lua/LuaRuntimeKnobsTests`,
  because neither knob is an environment variable. The intent — defaults pinned with a reason — is
  met; the location is not the one the subtask named.
- **Follow-ups handed to F-014** (found in review, none blocking this card):
  1. Host primitives exchange values with the script as visible text (`string[]` in,
     `DynValue.NewString` out). That is narrower than [`docs/lua-runtime.md`](../../lua-runtime.md)
     §5.2 and carries a silent trap: a boolean reply arrives as the string `"false"`, which is truthy
     in Lua, so `if is_windows()` would never be false. **Decided 2026-11-13: widen the channel** —
     a host reply will be able to carry a number or a boolean, with the change confined to
     `LuaRuntime` (no new engine type at the seam). Queued behind F-012 so the worker keeps one card
     at a time; it lands before F-014 starts.
  2. `LuaRuntimeOptions.AdditionalModules` is public and typed `CoreModules`, added only so the
     registry test can compose `Dynamic`; production always passes the default. Make it `internal`
     with `InternalsVisibleTo` (this repository already does that for the tests) before a pipeline
     card references it.
  3. `LuaCancellationTests` accepts a 1000 ms upper bound on the pure-compute case; the constraint
     that actually bites is the frozen call count. Optional tightening to about 250 ms.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `Directory.Packages.props`, `src/Worker/Zenkins.Worker.csproj`,
  `src/Worker/Lua/*`, `tests/Zenkins.Tests/Lua/*`, `THIRD-PARTY-NOTICES.md`, `README.md`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Lua"`.
- Results: accepted 2026-11-13. `dotnet build -c Release` 0 warnings 0 errors; `Category=Lua`
  85/0/0; full suite 433/0/0; MoonSharp 2.0.0 on `Zenkins.Worker` only, no transitive package. This
  card touches no schema, so no migration gate applies. Independent review found no major finding;
  the three minors are recorded above as F-014 follow-ups.
