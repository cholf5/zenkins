# Lua runtime contract

What Zenkins promises about running `pipeline.lua`, and the constraints that follow from it.
This is the implementation contract for the v0.3 cards (`F-011`…`F-016`) — the cards reference
sections here instead of restating them.

> **Verified on:** macOS 26.5 (arm64) with .NET SDK **10.0.403**, 2026-11-13, by running both
> candidate engines through the same probe set in a throwaway `net10.0` console app. Every number
> and every `nil` in this document is an observation, not an estimate. The probe source is in
> [Appendix A](#appendix-a--the-probe) so the results can be re-derived after an engine or runtime
> upgrade. Nothing from the probe is compiled into this repository.

## 1. Decision

**Engine: MoonSharp 2.0.0, pinned exactly, referenced by `Zenkins.Worker` only.**

- PRD §3.3.6 already names MoonSharp and requires a pure C# runtime (no native Lua binary), so the
  engine *family* was not an open question — only the version and the surface are.
- The runtime sits behind our own seam (`ILuaRuntime` / `IPipelineScriptHost`, see §6), so the
  engine appears in one file's constructor call and nowhere else.
- `3.0.0-beta.1` is **behaviourally identical to 2.0.0 for everything we use**, measured, not
  assumed (§2). Choosing between them is therefore a stabilisation question, not a feature
  question, and a shipped stable tag wins.

Why not the alternatives:

| Candidate | Why not |
|---|---|
| MoonSharp `3.0.0-beta.1` | A single prerelease (NuGet 2026-07-04; GitHub release tagged 2026-03-03) that **already lags its own upstream by three months**: `pcall`-with-`__call` was fixed upstream in 2026-10 (`Honor __call in pcall and xpcall`) and is *not* in the package — our probe reproduces the defect on the beta. Being on a prerelease buys neither the fix nor a stable API, and costs a Central Package Management prerelease entry. |
| Hand-rolled interpreter | Not a v0.3-sized task: it is a Lua implementation plus a sandbox plus a debugger story, competing with a mature 10-year-old interpreter on a schedule that has webhooks and git in it. |
| NLua / KeraLua / native Lua | Violates PRD §3.3.6 ("不依赖 Native Lua Runtime") and multiplies the cross-platform packaging problem (three RIDs, three native binaries) on a product whose selling point is `dotnet run`. |
| MoonSharp `2.x` + local patches | Only if a defect blocks us. See the switch triggers below. |

**Switch triggers** (any one re-opens this decision):

1. MoonSharp 3.0 ships as a stable, non-prerelease package → move, run the §9 conformance
   fixtures, expect no source changes (proved in §2: the same probe source compiles and runs
   unchanged on both).
2. We hit an interpreter defect we cannot route around → either upgrade to a 3.0 build (its
   upstream is actively fixed; the 2.x line has not changed since 2016-10) or carry a patch.
3. A security advisory lands against 2.0 with no workaround → same as 2, plus a re-read of §3.

## 2. Measured evidence

Same probe source, two packages, `net10.0`:

| Probe | MoonSharp 2.0.0 | MoonSharp 3.0.0-beta.1 |
|---|---|---|
| Best asset for `net10.0` | `netstandard1.6` | `netstandard2.0` |
| Transitive dependencies | **0** | **0** |
| Build with `TreatWarningsAsErrors` + `latest-all` analyzers | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `lua result` on a two-function script | `HELLO sum=5` | identical |
| `//`, `<<`, `&`, `math.type`, `utf8`, `<const>` | unsupported (5.2) | unsupported (5.2) |
| `goto` / `::label::` | works | works |
| Sandbox module sets | identical | identical |
| Runtime error text | `pipeline.lua:(2,28-41): bad thing` | identical |
| Syntax error text | `pipeline.lua:(1,10-11): unexpected symbol near '='` | identical |
| Declarative Lua fixture (closures, varargs, metatables, string lib, coroutines, `goto`, `xpcall`) | all pass | identical output |
| 1 000 000-iteration loop | 203–208 ms | 203 ms |
| 100 000 Lua→C# callbacks | 29 ms | 29 ms |
| 400 KB built by O(n²) `..` concatenation | 831 ms | 773 ms |
| `while true do end` with `AutoYieldCounter` | host regains control every N instructions (~6 465 yields in a 300 ms budget) | identical |
| 8 scripts in 8 threads, one `Script` each | 398 ms, no exceptions | identical |
| `pcall(callable table)` | `attempt to pcall a non-function` | same defect |

Two conclusions worth keeping:

- The language-level ceiling is **Lua 5.2** on both. Pipeline documentation, SDK examples and the
  PRD's §3.3.2 sample must stay inside 5.2 — no `//`, no bitwise operators, no `utf8`, no
  `<const>`.
- **The engine is not a performance concern.** A million VM iterations cost ~200 ms; what will
  actually be slow is Lua-side string building (831 ms for 400 KB), which is why logging is a C#
  primitive (§7).

## 3. Sandbox contract

The engine is configured by an explicit module composition, not by a preset. The presets are
traps: `Preset_HardSandbox` — the one whose name suggests the most safety — **removes `pcall`,
`xpcall`, `setmetatable` and `coroutine`** (measured), i.e. it is designed for untrusted *data*,
not for orchestrating a pipeline that needs error handling and async suspension.

```csharp
var modules = CoreModules.Basic | CoreModules.ErrorHandling | CoreModules.Metatables
            | CoreModules.Coroutine | CoreModules.String | CoreModules.Table
            | CoreModules.TableIterators | CoreModules.Math | CoreModules.Bit32
            | CoreModules.OS_Time;
```

Resulting globals (measured, exact):

```text
assert bit32 collectgarbage coroutine error getmetatable ipairs math next os
pack package pairs pcall print rawequal rawget rawlen rawset select setmetatable
string table tonumber tostring type unpack xpcall
```

`os` exposes exactly `clock`, `date`, `difftime`, `time`. There is no `io`, `load`, `loadfile`,
`dofile`, `require` or `debug` (all `nil`).

### 3.1 Mandatory hardening after construction

```csharp
script.Globals["dynamic"] = DynValue.Nil;   // CLR bridge: eval / prepare
script.Globals["json"] = DynValue.Nil;      // not part of the SDK surface
script.Globals["package"] = DynValue.Nil;   // presence-only table, but see below
script.Globals["_MOONSHARP"] = DynValue.Nil;// banner/version/platform metadata
script.Globals["print"] = /* the run-log sink */;
```

**`dynamic = nil` alone is not enough** — this is the sharpest finding of the probe. The module
registry keeps the table alive:

```lua
dynamic = nil
local d = package.loaded.dynamic   -- still a table
d.eval('1+1')                      -- → 2, works
```

`package.loaded` also holds `coroutine`, `math`, `os`, `string`, `table`, `bit32`. So the
hardening must reach the registry as well: either nil the `package` global (what the contract
says — `package` in this configuration has no `path`, no `loadlib`, no searchers, so it exists
only as a handle to `loaded`) or clear `package.loaded.dynamic` explicitly. Nil-ing the global is
one statement, is verified here (`type(package)` → `nil`, `type(package.loaded)` → `nil`), and
leaves nothing to remember later.

### 3.2 Self-check, in tests and at startup

Forbidden names must be unreachable **through the globals table**, not merely absent from the
module list: a unit test asserts, for every name in
`{ io, os.execute, os.exit, os.getenv, os.remove, os.rename, os.tmpname, load, loadfile, loadstring, dofile, require, debug, dynamic, json, package, _MOONSHARP, collectgarbage-unavailable-apis }`,
that `script.DoString("return type(<name>)")` is `nil`; plus a probe that
`package.loaded` is unreachable after hardening, and that no `UserData` registration exists
(`UserData.RegistrationPolicy` stays at the default with **no** `RegisterType` calls anywhere in
the worker).

No CLR type is ever registered. There is no `dynamic`, no `require` of assemblies, and no
`UserData.RegisterAssembly` call — PRD §3.3.6's four prohibitions are satisfied by construction
rather than by filtering.

### 3.3 What the sandbox is not

PRD §3.3.6 already says this; the contract makes it operational: **a Lua-level escape is a
permission problem, not a build problem.** The worker process runs untrusted repository content,
so:

- The worker runs as a dedicated, unprivileged account with its own work root (§5.3 of
  `docs/development.md`), not as a developer's login.
- `work/{run-id}/` isolation (F-007) is the filesystem boundary.
- API limits (secrets API, worker token) are scoped per organization and per worker; a script that
  escapes the Lua sandbox must still find nothing worth reading.

## 4. Language subset

Lua 5.2 as implemented by MoonSharp. Verified unsupported: `//`, `<<`, `>>`, `&`, `|`, `~`,
`math.type`, `utf8.*`, `<const>`/`<close>` attributes. Verified supported: closures and upvalues,
varargs and `select('#')`, `goto`/labels, `__index`/`__add`/`__tostring`/`__call` metatables,
`pcall`/`xpcall`, error objects as strings, `coroutine.create/yield/resume/status`, `bit32`, full
`string`/`table`/`math` libraries, `os.time`/`os.date`.

Consequences for anything we publish:

- `docs/prd.md` §3.3.2's declarative sample and every SDK snippet stay 5.2-only.
- Decoding JSON in a pipeline is not available (`json` is removed); structured input arrives as
  plain Lua tables through SDK functions, or through `env()` strings.

## 5. Execution model

### 5.1 One `Script` per run, driven by the host

A run is executed as **a coroutine driven by the host**, never by `DoString`. `DoString` cannot be
interrupted — it returns when the script returns — so a `while true do end` in `pipeline.lua`
would hang the worker's execution thread with no path to cancellation or timeout.

```csharp
var coroutine = script.CreateCoroutine(filtered).Coroutine;
coroutine.AutoYieldCounter = 10_000;        // hand control back to the host every 10k VM instructions

var result = coroutine.Resume();
while (coroutine.State != CoroutineState.Dead)
{
    // Reached after either an explicit SDK suspension (§5.2) or an AutoYieldCounter yield.
    if (stopwatch.Elapsed > budget || cancellation.IsCancellationRequested) { /* abandon */ }
    result = coroutine.Resume();
}
```

Measured: `while true do end`, `local i=0 while true do i=i+1 end` and `while true do local t={} end`
all return control to the host within milliseconds and can be abandoned at any yield point
(~6 465 resumes in a 300 ms budget). **Abandoning means: stop resuming, kill the process tree, and
throw the `Script` away.** There is no way to resume it afterwards, and nothing else may reuse it.

Thread affinity: `ScriptOptions.CheckThreadAccess` stays at its default (on). One `Script` belongs
to one thread for its whole life; a run owns its own `Script`. Eight concurrent runs = eight
`Script` instances on eight threads, measured clean (398 ms for 8×300k iterations).

### 5.2 The host-call protocol (this is how `run()` works)

A C# primitive that needs real I/O suspends the script by returning a yield request; the host does
the work and resumes with the answer, which becomes the value of the call site in Lua. Verified
end to end:

```csharp
script.Globals["run_process"] = (Func<string, DynValue>)(argv =>
    DynValue.NewYieldReq(new[] { DynValue.NewString("::await::" + argv) }));
```

```text
host: run_process('make build') called
host: state=Suspended lastResult=::await-process::make build -> resuming
lua->log: got: exit=0
host: run_process('make test') called
host: state=Suspended lastResult=::await-process::make test -> resuming
lua->log: got: exit=0
final: state=Dead type=String value=done
```

Rules that follow:

- Only I/O primitives may yield: `run`, `shell`, `checkout`, `artifact`. Everything else
  (`log`, `env`, `secret`, `is_*`, `fail`, `pipeline`, `step`) is synchronous and must not.
- The driver loop is `state != Dead`, **not** `result.Type == YieldRequest` — a yield returns the
  yield request's own return values as the `Resume` result (measured: the first observation was
  `String value=resume-token`, not a `YieldRequest`), so branching on the result type silently
  stops the loop with a suspended coroutine and a run that never ends.
- Resuming a `CoroutineState.ForceSuspended` coroutine (the `AutoYieldCounter` case) is legal and
  is the normal path; `CoroutineState.Suspended` is the explicit `YieldRequest` case. Both are
  resumed by the same call.
- A resumed value is delivered to the **innermost** yield point only, so SDK functions that wrap an
  I/O primitive in Lua (`step`, `with_env`) must be Lua functions or must propagate the suspension
  themselves. This is one of the reasons for the layering in §6.

### 5.3 Time and resource budgets

- **Wall clock** is the host's: the driver loop compares `stopwatch.Elapsed` against the run's
  remaining budget (`BuildRun.DeadlineAt`, frozen at accept time by F-009) and the step's own
  limit. On expiry: stop resuming, kill the process tree, report a terminal state with
  `FailureReason` (`timeout` / `canceled-by-user`), never a retry.
- **Cancellation** reuses the existing `RunCancelSignal`: the loop checks it at every yield point
  (every 10k instructions), so a script honours a cancel within milliseconds even if it never
  calls an SDK function.
- **Memory has no interpreter-level limit, and cannot get one**: `collectgarbage('count')` returns
  `false` in this build and there is no memory-limit option. A Lua-side allocation bomb is bounded
  only by the OS. The deployment requirement is therefore explicit: the worker runs under a
  process memory cap (`ulimit -v` on Linux/macOS, a Job Object limit on Windows) sized above the
  largest real build, and a run killed by the OS is reported as `Failed`, not silently retried.
  Tracking this as an implementation gap is deliberate — see §10.
- **Nesting**: a step's budget is the smaller of its own limit and the run's remaining wall clock.
  A single `run()` is bounded by F-009's per-run timeout independently of Lua.

## 6. Layering: a small C# kernel, a Lua prelude

```text
pipeline.lua  (user code, Lua 5.2)
      │
      ├── Lua prelude (embedded resource, shipped by us): pipeline{}, step{}, with_env(), conditions
      │
      └── C# primitives (the only I/O and the only host access):
          run(argv…)  shell(program, argv…)  checkout()  artifact(path)
          log(text)  env(name)  secret(name)  is_windows/linux/macos  fail(message)
```

Why this split:

- Every primitive that touches the host is one auditable C# function with a fixed signature; there
  is no general-purpose bridge to .NET.
- `pipeline{}` and `step{}` are ordinary Lua functions that build a validated table — they need no
  host access, so they live in the prelude where they can be unit-tested as Lua.
- The coroutine suspension (§5.2) is confined to the five I/O primitives; the prelude never has to
  know about it.
- Swapping the engine (§1) means porting the primitives and the prelude, not the user-facing
  pipeline semantics — which are pinned by the conformance fixtures in §9.

### 6.1 Execution form: declarative by default

`pipeline{ ... }` is the primary form (PRD §3.3.2). The script builds a spec table; the host
validates it, serialises it, and executes the steps **in C#**:

- Step sequencing, timing, exit codes, `continue_on_error`, env composition and cancellation live
  in C# — a hostile or buggy script cannot skip them, and a run's step list is known before the
  first step starts.
- The coroutine driver (§5.1) is still used for the *definition* phase, because a script that never
  returns would otherwise hang the worker. The same budget and the same cancellation apply.
- Imperative orchestration (`step("x", function() ... end)` with `run()` inside) is a later
  capability, not a v0.3 one; the host-call protocol (§5.2) is built now precisely so it can land
  without re-architecting.

### 6.2 Error contract

- `error()` with a **table** does not survive: `pcall` hands Lua a *string* and the caller sees
  `e.code` as `nil` (measured: `pcall(function() error({code=42}) end)` → `false, string:nil`).
  The SDK therefore accepts a message string only: `fail("message")`. Structured detail travels as
  log lines or exit codes, never as a Lua table across `pcall`.
- Syntax and runtime errors carry `source:(line,col-col)` — e.g.
  `pipeline.lua:(2,28-41): bad thing` — which the run log shows verbatim as one line. The
  exception's `CallStack` has names but **no locations for inner frames** (measured: only the
  frame that raised `error` had a `Location`), so we render the position from the message and the
  frame names separately. We do not dump CLR stack traces into the build log.
- `pcall(callableTable)` is broken in both candidate packages ("attempt to pcall a non-function").
  The prelude must not rely on it (no callable-table steps), and no SDK documentation suggests it.

## 7. Logging and redaction

- **`log()` is a C# primitive**, not Lua string building. Measured: building 400 KB by `..` costs
  ~800 ms because Lua strings are immutable; 100 000 C# callback invocations cost 29 ms.
- `print` is re-bound to the same sink so a user's `print("...")` appears in the run log instead of
  the worker's console (the engine's default `DebugPrint` writes to `Console` — i.e. outside the
  log pipeline, invisible in the UI and unredactable).
- Every line carries the current step id so the UI can group per step (PRD §3.3.5).
- **Redaction is defence in depth, never a guarantee** (PRD §3.8): the worker knows each secret's
  plaintext for the duration of the run and can replace occurrences before streaming. Scripts can
  still encode, split or hash a value; anyone who can edit `pipeline.lua` is treated as having the
  build's permissions. The contract records this so nobody later documents redaction as a boundary.

## 8. Dependency and build contract

- **Pin:** `<PackageVersion Include="MoonSharp" Version="2.0.0" />` in `Directory.Packages.props`,
  with a comment recording *why* (no transitive dependencies, `netstandard1.6` is the best asset
  for `net10.0`, measured equivalent to 3.0.0-beta.1) — the same style as the existing EF Core
  pin note.
- **Scope:** referenced by `Zenkins.Worker` only. The Server never runs a script (PRD §8
  constraint 1), and the dependency must not leak into `Zenkins.Shared` (wire contracts) or
  `Zenkins.Server`.
- **Analyzers:** the package must build at 0 warnings with `TreatWarningsAsErrors` and the analyzer
  set in `Directory.Build.props` — verified for both candidates.
- **Audit:** 0 transitive dependencies means nothing new for NuGet audit to flag. `dotnet list
  package --vulnerable` stays clean.
- **License:** BSD-3-Clause (the package's `LICENSE`: three-clause text, plus MIT-licensed parts
  from `vscode-mono-debug`). Commercially usable; **requires a third-party notices file** shipped
  with the worker (`THIRD-PARTY-NOTICES.md` at the repository root, referenced from the README's
  license section). That file is part of the card that adds the dependency, not a later chore.

## 9. Conformance fixtures — the engine-switch gate

These are the behaviours any engine must reproduce. They are written once as test fixtures and run
against whatever the runtime is bound to; a version upgrade that changes any of them is a
deliberate decision, not a surprise. (The three marked *pinned defect* are documented behaviour
today and are *not* a requirement on a future engine — a fix is welcome, a change must be noticed.)

| # | Fixture | Expected |
|---|---|---|
| 1 | `return ("x"):rep(3) .. tostring(2^10)` | `xxx1024` |
| 2 | closures, varargs, `goto`, `bit32`, `string.format("%5.2f")` | values as measured in Appendix A |
| 3 | metatables: `__index`, `__add`, `__tostring`, `__call` | `V(5)`, `5` |
| 4 | `pcall` / `xpcall` with `error("msg")` | `false`, message containing `source:(line,col-col)` |
| 5 | `error({code=42})` through `pcall` | second value is a **string**, `e.code` is `nil` (pinned defect) |
| 6 | `pcall(callableTable)` | `attempt to pcall a non-function` (pinned defect) |
| 7 | `//`, `<<`, `&`, `math.type`, `utf8.len`, `<const>` | syntax/runtime error (Lua 5.2 ceiling) |
| 8 | `type(io)`, `type(load)`, `type(require)`, `type(debug)`, `type(os.execute)` | `nil` |
| 9 | `dynamic = nil; package.loaded.dynamic.eval('1+1')` | unreachable (registry hardened) |
| 10 | `collectgarbage('count')` | `false` (no memory introspection — pinned defect) |
| 11 | `while true do end` with `AutoYieldCounter` | host regains control; loop is abandonable |
| 12 | primitive yields, host resumes with a value | Lua call site receives the value; run completes |
| 13 | 8 threads × 1 `Script` each | no exception, no cross-talk |

## 10. Known limitations and open risks

1. **No interpreter-level memory limit** (§5.3) — enforced by the OS, documented as a deployment
   requirement, not implemented by us.
2. **No Lua 5.3+** — 64-bit integers, bitwise operators, `utf8`, integer division are unavailable.
   Pipeline authors coming from Lua 5.3 will be surprised; the docs must say "Lua 5.2" explicitly.
3. **`pcall` on a callable table is broken** in both available packages; only a build from upstream
   `main` fixes it. The prelude avoids callable tables.
4. **The 2.x line is unmaintained** (last release 2016-10-14). Defects are ours to patch or escape
   via trigger 2 in §1.
5. **No debugger integration**: `IDebugger` exists and supports line-based breakpoints, but a
   remote pipeline debugger is a product feature nobody has asked for yet; the driver in §5.1 is
   the only interruption mechanism we rely on.
6. **`os.date` uses the worker's local time zone.** Correct for build logs, but a source of
   "different machines, different timestamps" reports; the run page shows UTC.

## Appendix A — the probe

The full probe set behind §2, run against both packages (`dotnet add package MoonSharp --version
2.0.0` and `--version 3.0.0-beta.1`). Run it in a scratch console app, never in this solution.

```csharp
using System.Diagnostics;
using MoonSharp.Interpreter;

var modules = CoreModules.Basic | CoreModules.ErrorHandling | CoreModules.Metatables
            | CoreModules.Coroutine | CoreModules.String | CoreModules.Table
            | CoreModules.TableIterators | CoreModules.Math | CoreModules.Bit32
            | CoreModules.OS_Time;
var script = new Script(modules);
script.Globals["print"] = (Action<string>)(m => Console.WriteLine("   " + m));
script.Globals["dynamic"] = DynValue.Nil;
script.Globals["json"] = DynValue.Nil;
script.Globals["package"] = DynValue.Nil;
script.Globals["_MOONSHARP"] = DynValue.Nil;

// 1. language surface
foreach (var sn in new[] { "return ('x'):rep(3) .. tostring(2^10)", "return 7 // 2", "return 1 << 4",
                           "return math.type(1)", "return utf8.len('hello')", "local t <const> = 1",
                           "goto x ::x:: return 'goto-ok'" })
{
    try { Console.WriteLine($"ok   {sn,-40} -> {script.DoString(sn).ToPrintString()}"); }
    catch (Exception ex) { Console.WriteLine($"FAIL {sn,-40} -> {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
}

// 2. sandbox reachability
foreach (var n in new[] { "io", "load", "loadfile", "dofile", "require", "debug", "package",
                          "dynamic", "json", "os.execute", "os.exit", "os.getenv" })
    Console.WriteLine($"type({n}) = {script.DoString("return type(" + n + ")").ToPrintString()}");
Console.WriteLine("collectgarbage('count') = " +
                  script.DoString("return tostring(collectgarbage('count'))").String);

// 3. error contract
try { script.DoString("local x = 1\nlocal function inner() error('bad thing') end\nlocal function outer() inner() end\nouter()", null, "pipeline.lua"); }
catch (ScriptRuntimeException ex)
{
    Console.WriteLine("message: " + ex.DecoratedMessage);
    foreach (var f in ex.CallStack) Console.WriteLine($"  frame name={f.Name} loc={f.Location}");
}
Console.WriteLine("table error: " + script.DoString(
    "local ok, e = pcall(function() error({code=42}) end) return tostring(ok) .. ' ' .. type(e) .. ' ' .. tostring(e.code)").ToPrintString());

// 4. host-call suspension
var yielded = new Queue<string>();
script.Globals["run_process"] = (Func<string, DynValue>)(argv =>
{
    yielded.Enqueue(argv);
    return DynValue.NewYieldReq(new[] { DynValue.NewString("exit=0") });
});
var fn = script.LoadString("local a = run_process('make build') print('got: ' .. a)\nreturn 'done'", null, "pipeline.lua");
var co = script.CreateCoroutine(fn).Coroutine;
co.AutoYieldCounter = 10_000;
var res = co.Resume();
while (co.State != CoroutineState.Dead) { Console.WriteLine($"host: {co.State} -> resume"); res = co.Resume(); }
Console.WriteLine("final: " + res.ToPrintString());

// 5. runaway script
var runaway = script.CreateCoroutine(script.LoadString("while true do end", null, "pipeline.lua")).Coroutine;
runaway.AutoYieldCounter = 10_000;
var sw = Stopwatch.StartNew();
runaway.Resume();
int ticks = 0;
while (runaway.State != CoroutineState.Dead && sw.ElapsedMilliseconds < 300) { ticks++; runaway.Resume(); }
Console.WriteLine($"runaway: {runaway.State} after {ticks} yields in {sw.ElapsedMilliseconds} ms");

// 6. throughput
sw.Restart();
script.DoString("local s=0 for i=1,1000000 do s = s + i % 7 end return s");
Console.WriteLine($"1e6 iterations: {sw.ElapsedMilliseconds} ms");
long calls = 0;
script.Globals["tick"] = (Action)(() => calls++);
sw.Restart();
script.DoString("for i=1,100000 do tick() end");
Console.WriteLine($"100k callbacks: {sw.ElapsedMilliseconds} ms ({calls} calls)");
sw.Restart();
script.DoString("local t={} for i=1,20000 do t[i]=('x'):rep(20) end local s='' for i=1,20000 do s=s..t[i] end return #s");
Console.WriteLine($"400 KB by concatenation: {sw.ElapsedMilliseconds} ms");
```
