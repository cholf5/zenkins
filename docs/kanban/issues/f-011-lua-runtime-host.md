---
id: F-011
title: Lua runtime host and sandbox (Worker)
type: feature
priority: P1
size: M
status: todo
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
  `Execute(source, name, LuaExecutionBudget, CancellationToken)` → either a result or a
  `LuaScriptException` carrying `(Message, Source, Line, Column)`. No MoonSharp type appears outside
  `ILuaRuntime`'s implementation and the host-primitive registration surface.
- **Suspension is a first-class seam capability** even though no primitive yields yet: the driver
  handles both yield kinds and the seam exposes `RegisterHostFunction` for primitives that return a
  yield request. F-014/F-015 need it, and retrofitting it later would mean rewriting the driver.
- **One `Script` per execution**, thread-affine, never pooled (§5.1). Abandonment = stop resuming,
  discard the `Script`.
- **No CLR registration anywhere**: no `UserData.RegisterType/RegisterAssembly`, no proxy types.

## Acceptance Criteria

- [ ] `Directory.Packages.props` pins MoonSharp 2.0.0 with a rationale comment; only
      `Zenkins.Worker` references it; `dotnet build -c Release` stays at 0 warnings with
      `TreatWarningsAsErrors` and the repository's analyzer set.
- [ ] `THIRD-PARTY-NOTICES.md` exists at the repository root and copies the MoonSharp `LICENSE`
      **verbatim** (the 2.0.0 package ships no such file; the text is in the upstream repository and
      the 3.0.0 package, and carries the KopiLua string-library notice, the MIT-licensed
      `vscode-mono-debug` portions, the Eclipse debugger-icon attribution and the MoonSharp icon
      attribution); the README license section links it.
- [ ] A sandbox self-check runs as a test over the full deny list, asserting each name through
      `DoString("return type(<name>)")` (not through the C# `Globals` enumeration, which still lists
      nil-valued keys): `io`, `load`, `loadfile`, `loadstring`, `dofile`, `require`, `debug`,
      `dynamic`, `json`, `package`, `_MOONSHARP`, `os.execute`, `os.exit`, `os.getenv`, `os.remove`,
      `os.rename`, `os.tmpname` are all `nil`; `os.clock/date/difftime/time` are functions;
      `pcall`, `xpcall`, `setmetatable`, `getmetatable`, `coroutine` and `_G` are present.
- [ ] The registry hazard is pinned by a test that composes `Dynamic` deliberately and proves
      `package.loaded.dynamic.eval` is unreachable afterwards — the test fails if the `package`
      hardening is removed.
- [ ] A runaway script (`while true do end`, and a tight allocation loop) is abandoned at its
      budget: the call returns within the budget plus a small margin, the coroutine is never
      resumed again, and the discarded `Script` is not reused.
- [ ] Cancellation passed to `Execute` stops a script at the next yield point (within
      `AutoYieldCounter` instructions), with the same abandon semantics.
- [ ] Syntax and runtime errors surface `source:(line,col-col)` in one line — no CLR stack trace in
      the message — and a script that raises after N log lines keeps those lines.
- [ ] The host-call suspension protocol works end to end: a test registers a primitive that yields,
      the driver resumes it with a value, and the Lua call site receives that value — including
      through a Lua wrapper, `pcall` and an `__index` metatable (fixtures 15–16 in
      `docs/lua-runtime.md` §9). The driver branches on `CoroutineState`, so a budget yield is
      resumed with no arguments and an SDK call is resumed with its answer.
- [ ] `print` from Lua reaches the runtime's log sink, not the worker's console.
- [ ] Existing behavior is unchanged: the whole suite still passes and argv runs (F-006/F-007) still
      execute exactly as before; the Worker stays ASP.NET-free (`WorkerRuntimeConfigTests`).

## Subtasks

- [ ] Package pin, project reference, notices file, README link.
- [ ] `src/Worker/Lua/{ILuaRuntime,LuaRuntime,LuaScriptException,LuaExecutionBudget,LuaLogSink}.cs`
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
   hazard), harden it as production does, then evaluate
   `dynamic = nil; local d = package.loaded.dynamic; return d and d.eval('1+1') or 'gone'`.
2. Build the production composition and assert `type(package)` is `nil` and that `package.loaded`
   is unreachable.

Expected:
- (1) `gone` — the `package` hardening closes the registry door even when the module is composed in;
  removing that hardening makes it return `2`, which is what the test is for.
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

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `Directory.Packages.props`, `src/Worker/Zenkins.Worker.csproj`,
  `src/Worker/Lua/*`, `tests/Zenkins.Tests/Lua/*`, `THIRD-PARTY-NOTICES.md`, `README.md`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Lua"`.
- Results: not run (card not started).
