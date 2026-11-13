# Lua runtime contract

What Zenkins promises about running `pipeline.lua`, and the constraints that follow from it.
This is the implementation contract for the v0.3 cards (`F-011`…`F-017`) — the cards reference
sections here instead of restating them.

> **Verified on:** macOS 26.5 (arm64) with .NET SDK **10.0.403**, 2026-11-13, by running the probe
> in [Appendix A](#appendix-a--the-probe) against `MoonSharp` 2.0.0 and 3.0.0-beta.1 in a throwaway
> `net10.0` console app. Every number, `nil` and error string in this document is an observation
> from that run; the two facts that come from package/repository metadata instead (publication
> dates, dependency lists) are marked as such at the point they appear. Note that the probe is a
> shortcut for re-verification, not a substitute for the fixtures in §9 — the first version of this
> document quoted two results that the probe could not actually produce, and a review caught it.
> Nothing from the probe is compiled into this repository.

## 1. Decision

**Engine: MoonSharp 2.0.0, pinned exactly, referenced by `Zenkins.Worker` only.**

- PRD §3.3.6 already names MoonSharp and requires a pure C# runtime (no native Lua binary), so the
  engine *family* was not an open question — only the version and the surface are.
- The runtime sits behind our own seam (`ILuaRuntime` / `IPipelineScriptHost`, see §6), so the
  engine appears in one file's constructor call and nowhere else.
- `3.0.0-beta.1` is **behaviourally identical to 2.0.0 for everything we use**, measured (§2).
  Choosing between them is therefore a stabilisation question, not a feature question, and a
  shipped stable tag wins.

Why not the alternatives:

| Candidate | Why not |
|---|---|
| MoonSharp `3.0.0-beta.1` | A single prerelease. Package metadata (NuGet registration; the `.nupkg` entry timestamps say 2026-07-04, the upstream tag says 2026-03-03) *suggests* it lags upstream, but that was not verified from this machine and is not load-bearing: the beta already reproduces the `pcall`-with-`__call` defect that upstream fixed in 2026-10, so being on a prerelease buys neither the fix nor a stable API, and costs a Central Package Management prerelease entry. |
| Hand-rolled interpreter | Not a v0.3-sized task: it is a Lua implementation plus a sandbox plus a debugger story, competing with a mature interpreter on a schedule that also has git, artifacts and webhooks in it. |
| NLua / KeraLua / native Lua | Violates PRD §3.3.6 ("不依赖 Native Lua Runtime") and multiplies the cross-platform packaging problem (three RIDs, three native binaries) on a product whose selling point is `dotnet run`. |
| MoonSharp `2.x` + local patches | Only if a defect blocks us. See the switch triggers below. |

**Switch triggers** (any one re-opens this decision):

1. MoonSharp 3.0 ships as a stable, non-prerelease package → move, run the §9 fixtures, expect no
   source changes (proved in §2: the same probe source compiles and runs unchanged on both).
2. We hit an interpreter defect we cannot route around → either upgrade to a 3.0 build (its
   upstream is actively fixed; the 2.x line has not changed since 2016-10) or carry a patch.
3. A security advisory lands against 2.0 with no workaround → same as 2, plus a re-read of §3.

## 2. Measured evidence

Same probe source, two packages, `net10.0`:

| Probe | MoonSharp 2.0.0 | MoonSharp 3.0.0-beta.1 |
|---|---|---|
| Best asset for `net10.0` | `netstandard1.6` | `netstandard2.0` |
| Transitive dependencies | **0** (nuspec has no `<dependencies>`) | **0** |
| Build with `TreatWarningsAsErrors` | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `lua result` on a two-function script | `HELLO sum=5` | identical |
| `//`, `<<`, `&`, `math.type`, `utf8`, `<const>` | unsupported (5.2) | unsupported (5.2) |
| `goto` / `::label::`, `bit32`, `select('#')` | work (`goto-ok`, `8`, `3`) | identical |
| Sandbox module sets | identical | identical |
| Runtime error text | `pipeline.lua:(2,28-41): bad thing` | identical |
| Syntax error text | `pipeline.lua:(1,10-11): unexpected symbol near '='` | identical |
| `collectgarbage('count')` | `nil` | `nil` |
| `pcall(callable table)` | `attempt to pcall a non-function` | same defect |
| 1 000 000-iteration loop | 203–229 ms | 203 ms |
| 100 000 Lua→C# callbacks | 27–29 ms | 29 ms |
| `while true do end`, 300 ms budget | 7 190 budget yields | 7 137 |
| 8 scripts on 8 threads | 234–398 ms, no exceptions | identical |
| 400 KB by O(n²) `..` concatenation | 0.8–3.6 s across three sessions | 0.77 s |

Two conclusions worth keeping:

- The language-level ceiling is **Lua 5.2** on both. Pipeline documentation, SDK examples and the
  PRD's §3.3.2 sample must stay inside 5.2 — no `//`, no bitwise operators, no `utf8`, no
  `<const>`.
- **The engine is not a performance concern for orchestration.** A million VM iterations cost ~220 ms
  and 100 000 host callbacks ~28 ms. What is slow is Lua-side string building: the same 400 KB took
  0.8 s in one session and 1.6–3.6 s in others, because immutable-string concatenation is O(n²) and
  depends on allocation/GC timing — which is why logging is a C# primitive (§7), and why nobody
  should budget on the lower figure.

## 3. Sandbox contract

The engine is configured by an explicit module composition, not by a preset. Measured preset
behaviour (Appendix A §C):

| | explicit (contract) | `Preset_SoftSandbox` | `Preset_HardSandbox` |
|---|---|---|---|
| `pcall` / `xpcall` | function | function | **nil** |
| `setmetatable` / `getmetatable` | function | function | **nil** |
| `coroutine` | function | function | **nil** |
| `dynamic` / `json` | nil | **table** | nil |
| `os` | table (no `execute`/`exit`/`getenv`) | table (same) | **nil** (so no `os.time`) |
| `io` / `load` / `dofile` / `require` / `debug` | nil | nil | nil |

Neither preset is usable: `SoftSandbox` ships the CLR bridge (`dynamic`, `json`), `HardSandbox`
strips the error handling, metatables and coroutine support a pipeline needs and also removes `os`
entirely. The composition below is the contract:

```csharp
var modules = CoreModules.Basic | CoreModules.ErrorHandling | CoreModules.Metatables
            | CoreModules.Coroutine | CoreModules.String | CoreModules.Table
            | CoreModules.TableIterators | CoreModules.Math | CoreModules.Bit32
            | CoreModules.OS_Time | CoreModules.GlobalConsts;
```

`GlobalConsts` is included deliberately: without it `_G` and `_VERSION` are `nil` (measured), which
breaks ordinary Lua code that addresses globals through `_G`. With it, `_VERSION` reports
`MoonSharp 2.0.0.0` — that is the engine's own string, left as-is on purpose: a script asking for
the version gets the truth.

### 3.1 Mandatory hardening after construction

```csharp
script.Globals["dynamic"] = DynValue.Nil;    // CLR bridge: eval / prepare
script.Globals["json"]    = DynValue.Nil;    // not part of the SDK surface
script.Globals["package"] = DynValue.Nil;    // registry handle; see below
script.Globals["_MOONSHARP"] = DynValue.Nil; // banner/version/platform metadata
script.Globals["print"]   = /* the run-log sink */;
```

**The registry is a second door, but only if `Dynamic`/`Json` is composed in.** With the contract
composition, `package.loaded` contains `bit32, coroutine, math, os, string, table` — no `dynamic`,
nothing to reach. Compose `Dynamic` back in (or use `Preset_Default`) and `dynamic = nil` no longer
holds:

```lua
dynamic = nil
package.loaded.dynamic.eval('1+1')   -- 2   (composition includes Dynamic)
```

Measured for all three shapes (Appendix A §D): `explicit` → escape unreachable, `explicit|Dynamic`
→ `2`, `Preset_Default` → `2`. So the rule is not "nil the global and you are done"; it is **never
compose `Dynamic` or `Json`, and nil the globals as well** so that a future edit that adds a module
does not silently reopen the door. `package` exists only as a handle to `loaded` here — no `path`, no
`loadlib`, no searchers — which is why nil-ing it costs nothing.

### 3.2 Self-check, in tests and at startup

Forbidden names must be unreachable **as a script sees them**. Assert with
`DoString("return type(<name>)")`, not with the C# `Globals` enumeration: `Globals["x"] = DynValue.Nil`
leaves a key that the C# `Pairs` API still yields (measured: 33 keys vs 29 visible to Lua), so a
C#-side assertion can pass or fail for the wrong reason.

The denied set: `io`, `load`, `loadfile`, `loadstring`, `dofile`, `require`, `debug`, `dynamic`,
`json`, `package`, `_MOONSHARP`, `os.execute`, `os.exit`, `os.getenv`, `os.remove`, `os.rename`,
`os.tmpname`. The kept set: `assert`, `error`, `pcall`, `xpcall`, `setmetatable`, `getmetatable`,
`ipairs`, `pairs`, `next`, `select`, `type`, `tostring`, `tonumber`, `unpack`/`pack`, `rawequal`,
`rawget`, `rawlen`, `rawset`, `_G`, `_VERSION`, `string`, `table`, `math`, `bit32`, `coroutine`,
`os.clock/date/difftime/time`, `collectgarbage`, `print` (re-bound).

`collectgarbage` is kept for compatibility but is inert for our purposes: `count`, `collect` and the
other queries return `nil` in this build (measured), so no script can observe or bound memory from
Lua — which is why §5.3 puts the limit in the OS.

No CLR type is ever registered: no `UserData.RegisterType/RegisterAssembly`, no proxy types, no
`dynamic`. PRD §3.3.6's four prohibitions are satisfied by construction rather than by filtering.

### 3.3 What the sandbox is not

PRD §3.3.6 already says this; the contract makes it operational: **a Lua-level escape is a
permission problem, not a build problem.** The worker process runs untrusted repository content, so:

- The worker runs as a dedicated, unprivileged account with its own work root, not as a
  developer's login — see [Production hardening](development.md#7-production-hardening).
- `work/{run-id}/` isolation (F-007) is the filesystem boundary.
- API limits (secrets API, worker token) are scoped per organization and per worker; a script that
  escapes the Lua sandbox must still find nothing worth reading.

## 4. Language subset

Lua 5.2 as implemented by MoonSharp. Verified unsupported: `//`, `<<`, `>>`, `&`, `|`, `~`,
`math.type`, `utf8.*`, `<const>`/`<close>` attributes. Verified supported: closures and upvalues,
varargs and `select('#')`, `goto`/labels, `__index`/`__add`/`__tostring`/`__call` metatables,
`pcall`/`xpcall`, string error messages, `coroutine.create/yield/resume/status`, `bit32`, the full
`string`/`table`/`math` libraries, `os.time`/`os.date`, `_G`, `_VERSION`.

Consequences for anything we publish:

- `docs/prd.md` §3.3.2's declarative sample and every SDK snippet stay 5.2-only.
- Decoding JSON in a pipeline is not available (`json` is removed); structured input arrives as
  plain Lua tables through SDK functions, or through `env()` strings.

## 5. Execution model

### 5.1 One `Script` per run, driven by the host

A run is executed as **a coroutine driven by the host**, never by `DoString`. `DoString` cannot be
interrupted — it returns when the script returns — so a `while true do end` in `pipeline.lua` would
hang the worker's execution thread with no path to cancellation or timeout.

The driver must branch on the coroutine state. This is the part that is easy to get wrong and it is
pinned by measurement (Appendix A §E):

```csharp
var co       = script.CreateCoroutine(fn).Coroutine;
var pending  = new Queue<HostRequest>();     // what the primitives asked the host to do
co.AutoYieldCounter = 10_000;                // hand control back every 10k VM instructions

DynValue result;
try
{
    result = co.Resume();
    while (co.State != CoroutineState.Dead)
    {
        if (stopwatch.Elapsed > budget || cancellation.IsCancellationRequested)
        {
            return Abandon(co);              // never resume again; kill the process tree
        }

        result = co.State == CoroutineState.ForceSuspended
            ? co.Resume()                                   // args MUST be empty
            : co.Resume(DynValue.NewString(await RunAsync(pending.Dequeue()))); // answer the call
    }
}
catch (ScriptRuntimeException ex)   { /* script failed: render ex.DecoratedMessage */ }
catch (SyntaxErrorException ex)     { /* did not compile: render ex.DecoratedMessage */ }
```

- `CoroutineState.ForceSuspended` is the instruction-budget yield: `Resume(value)` throws
  `ArgumentException: When resuming a force-suspended coroutine, args must be empty.` (measured).
- `CoroutineState.Suspended` is an SDK call asking the host for something: `Resume()` with no
  argument delivers `nil` to the call site (measured: `got:nil`), so the host must supply the answer.
- The `Resume` result is informational, not the discriminator: after a budget yield it is a
  `YieldRequest`, after an explicit yield it is that yield's own return values (`Nil` when the
  primitive yields none). Branch on `State`.
- Starting the loop with `RecursionGuard`: nothing here re-enters the script from a C# callback;
  if a future primitive ever needs to, that is a new design (see §6.1), not a variation of this loop.
- Measured: `while true do end`, `local i=0 while true do i=i+1 end` and `while true do local t={} end`
  all return control to the host in milliseconds and are abandonable at any yield point (7 190
  budget yields inside a 300 ms budget).

**Abandonment** means: stop resuming, kill the process tree, discard the `Script`. The coroutine is
technically still resumable, but we never resume it — the engine has no way to stop a frame cleanly
and the run is already terminal.

Thread affinity: `ScriptOptions.CheckThreadAccess` stays at its default (on). One `Script` belongs
to one thread for its whole life; a run owns its own `Script`. Eight concurrent runs = eight
`Script` instances on eight threads, measured clean (234–398 ms for 8×300 k iterations).

### 5.2 The host-call protocol (this is how `run()` works)

A C# primitive that needs real I/O suspends the script by returning a yield request; the host does
the work and resumes with the answer. Measured transcript, produced by §E of the probe:

```text
after first Resume: state=Suspended res.Type=Nil pending=1
state=Suspended (host call 'make build') -> Resume("exit=0")
  lua->log: build -> exit=0
state=Suspended (host call 'make test') -> Resume("exit=0")
  lua->log: test -> exit=0
final: state=Dead value=pipeline-ok
```

Rules that follow:

- Only I/O primitives may yield: `run`, `shell`, `checkout`, `artifact`. Everything else
  (`log`, `env`, `secret`, `is_*`, `fail`, `pipeline`, `step`) is synchronous and must not.
- The resumed value is an ordinary `DynValue` and may be a string, number, table or nil (all four
  measured). The SDK keeps the public surface string/number/boolean; anything richer is a `Table`
  built by the host through the seam, never a live CLR object.
- **A resumed value crosses Lua frames.** Measured: a Lua wrapper function, `pcall`, `xpcall`, a
  `for` loop body and an `__index` metamethod all receive it (`wrapped:VAL`, `pcall:true,VAL`,
  `xpcall:true,VAL`, `for:VAL;VAL;`, `meta:VAL`). So the Lua prelude may wrap an I/O primitive
  freely — it costs one suspension per call, not a rewrite. What does *not* work is yielding from a
  C# callback on behalf of a Lua frame that is waiting inside another C# callback; no primitive in
  §6 does that, and a future one must be designed for it.
- A primitive that yields must record its request before returning, because the driver answers the
  request it dequeues — the yield's own return values are not a request channel (measured: `res.Type`
  was `Nil` for a yield with no return values).

### 5.3 Time and resource budgets

- **Wall clock** is the host's: the driver compares `stopwatch.Elapsed` against the run's remaining
  budget (`BuildRun.DeadlineAt`, frozen at accept time by F-009) and the step's own limit. On
  expiry: stop resuming, kill the process tree, report a terminal state with `FailureReason`
  (`timeout` / `canceled-by-user`), never a retry.
- **Cancellation** reuses `RunCancelSignal`: the loop checks it at every yield point (every 10k
  instructions), so a script honours a cancel within milliseconds even if it never calls an SDK
  function.
- **Memory has no interpreter-level limit, and cannot get one.** `collectgarbage('count')` returns
  `nil` in this build (not a number, not `false`), and MoonSharp has no memory-limit option. The
  limit therefore belongs to the operating system, and it is not portable:
  - **Linux**: `ulimit -v` for the worker process, or better a cgroup v2 `memory.max` on the
    service, sized above the largest real build.
  - **Windows**: a Job Object memory limit on the worker process.
  - **macOS**: no reliable address-space cap — `ulimit -v 300000` fails with
    `setrlimit failed: invalid argument` on macOS 26.5 and the process keeps allocating (measured).
    Accepted and recorded: on macOS the worker's memory bound is "the machine", which is why the
    worker should not be the only thing running there. A run killed by the OS is reported `Failed`,
    never silently retried.
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
- Because a resumed value crosses Lua frames (§5.2), the prelude can wrap primitives without any
  suspension plumbing of its own; the driver stays the only place that knows about coroutines.
- Swapping the engine (§1) means porting the primitives and the prelude, not the user-facing
  pipeline semantics — which are pinned by the fixtures in §9.

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
  `e.code` as `nil` (measured: `false,string,nil`). The SDK therefore accepts a message string only:
  `fail("message")`. Structured detail travels as log lines or exit codes, never as a Lua table
  across `pcall`.
- Syntax and runtime errors carry `source:(line,col-col)` — `pipeline.lua:(2,28-41): bad thing`,
  `pipeline.lua:(1,10-11): unexpected symbol near '='` — which the run log shows verbatim as one
  line. The exception's `CallStack` has a `Location` only on the raising frame (measured:
  `error@[0]* (2, 28) -> (2, 41)` then `inner (local)@`, `outer (local)@`, `@`), so we render the
  position from the decorated message and the frame names separately. We do not dump CLR stack
  traces into the build log.
- `pcall(callableTable)` is broken in both candidate packages (`false,attempt to pcall a
  non-function`). The prelude must not rely on it (no callable-table steps), and no SDK
  documentation suggests it.
- A script error surfaces from `Resume` as `ScriptRuntimeException` (compilation failures as
  `SyntaxErrorException`); the driver catches both at the loop boundary (§5.1), so neither can
  escape as an unhandled exception in the worker.

## 7. Logging and redaction

- **`log()` is a C# primitive**, not Lua string building. Measured: 100 000 C# callback invocations
  cost 27–29 ms, while building 400 KB by `..` cost 0.8–3.6 s (O(n²), GC-dependent).
- `print` is re-bound to the same sink so a user's `print("...")` appears in the run log instead of
  the worker's console (the engine's default `DebugPrint` writes to `Console` — i.e. outside the log
  pipeline, invisible in the UI and unredactable).
- Every line carries the current step id so the UI can group per step (PRD §3.3.5).
- **Redaction is defence in depth, never a guarantee** (PRD §3.8): the worker knows each secret's
  plaintext for the duration of the run and can replace occurrences before streaming. Scripts can
  still encode, split or hash a value; anyone who can edit `pipeline.lua` is treated as having the
  build's permissions. The contract records this so nobody later documents redaction as a boundary.

## 8. Dependency and build contract

- **Pin:** `<PackageVersion Include="MoonSharp" Version="2.0.0" />` in `Directory.Packages.props`,
  with a comment recording *why* (no transitive dependencies, `netstandard1.6` is the best asset for
  `net10.0`, measured equivalent to 3.0.0-beta.1) — the same style as the existing EF Core pin note.
- **Scope:** referenced by `Zenkins.Worker` only. The Server never runs a script (PRD §8
  constraint 1), and the dependency must not leak into `Zenkins.Shared` (wire contracts) or
  `Zenkins.Server`.
- **Analyzers:** the package must build at 0 warnings with `TreatWarningsAsErrors` — verified for
  both candidates.
- **Audit:** 0 transitive dependencies means nothing new for NuGet audit to flag.
- **License: BSD-3-Clause, and the notices file needs the whole text.** The 2.0.0 package ships no
  `LICENSE` file at all (only a `licenseUrl`); the text is in the repository and in the 3.0.0
  package, and it is not a plain BSD-3 header: it also carries attribution for the KopiLua-derived
  string library (Copyright LoDC), MIT-licensed code from `vscode-mono-debug` (Copyright Microsoft),
  Eclipse-project debugger icons, and the MoonSharp icon (Copyright Isaac). BSD-3 requires
  reproducing the copyright notice and conditions, so `THIRD-PARTY-NOTICES.md` must copy the file
  verbatim rather than paraphrase it. That file is part of the card that adds the dependency
  (F-011), not a later chore.

## 9. Conformance fixtures — the engine-switch gate

These are the behaviours any engine must reproduce, written as assertions with the exact observed
output. (Rows marked *pinned defect* are today's documented behaviour and are *not* a requirement on
a future engine — a fix is welcome, a change must be noticed.)

| # | Fixture | Expected (measured) |
|---|---|---|
| 1 | `return ('x'):rep(3) .. tostring(2^10)` | `xxx1024` |
| 2 | `local function c() local n=0 return function() n=n+1 return n end end; local f=c(); f(); return f()` | `2` |
| 3 | `local n,a,b = select('#', 'x','y'), ...` → `return n .. a .. b` with `('x','y')` | `2xy` |
| 4 | `bit32.band(12,10)`; `select('#',1,2,3)`; `goto x ::x:: return 'goto-ok'` | `8`; `3`; `goto-ok` |
| 5 | `V` with `__index/__add/__tostring/__call`; `tostring(v2) .. ' ' .. v2('x')` | `V(5) 5` |
| 6 | `pcall(function() error('m') end)` | `false`, message containing `source:(line,col-col)` |
| 7 | `xpcall(function() error('m') end, function(e) return 'h:' .. e end)` | `false`, handler result prefixed `h:` |
| 8 | `pcall(function() error({code=42}) end)` | `false,string,nil` (pinned defect) |
| 9 | `pcall(setmetatable({}, {__call=function() return 1 end}))` | `false,attempt to pcall a non-function` (pinned defect) |
| 10a | `return 7 // 2` | `SyntaxErrorException: unexpected symbol near '/'` |
| 10b | `return 1 << 4` | `SyntaxErrorException: unexpected symbol near '<'` |
| 10c | `return 3 & 5` | `SyntaxErrorException: unexpected symbol near '&'` |
| 10d | `return math.type(1)` | `ScriptRuntimeException: attempt to call a nil value` |
| 10e | `return utf8.len('hello')` | `ScriptRuntimeException: attempt to index a nil value` |
| 10f | `local t <const> = 1` | `SyntaxErrorException: unexpected symbol near '<'` |
| 11 | For each denied name: `return type(<name>)` | `nil` (and `type(package)` in particular `nil`) |
| 12 | Registry, **only when `Dynamic`/`Json` is composed in**: `dynamic = nil; package.loaded.dynamic.eval('1+1')` | `2` under `explicit\|Dynamic` and `Preset_Default` — the regression this fixture exists to catch if someone composes those modules back |
| 13 | `collectgarbage('count') == nil` | `true` (pinned defect) |
| 14 | `while true do end` with `AutoYieldCounter = 10_000` | state `ForceSuspended`, > 1 000 resumes within 300 ms |
| 15 | Two host calls with answers, then `return 'pipeline-ok'` | logs `build -> exit=0`, `test -> exit=0`, final value `pipeline-ok` |
| 16 | Resumed value through wrapper / `pcall` / `for` / `__index` | `wrapped:VAL`, `pcall:true,VAL`, `for:VAL;VAL;`, `meta:VAL` |
| 17 | 8 threads × 1 `Script` each | no exception; all results identical |

## 10. Known limitations and open risks

1. **No interpreter-level memory limit** (§5.3) — Linux and Windows have an OS answer, macOS has
   none; documented rather than papered over.
2. **No Lua 5.3+** — 64-bit integers, bitwise operators, `utf8`, integer division are unavailable.
   Pipeline authors coming from Lua 5.3 will be surprised; the docs must say "Lua 5.2" explicitly.
3. **`pcall` on a callable table is broken** in both available packages; only a build from upstream
   `main` fixes it. The prelude avoids callable tables.
4. **`_VERSION` reports `MoonSharp 2.0.0.0`**, not `Lua 5.2`; a script branching on `_VERSION` sees
   the engine, which is honest but worth knowing before someone writes a compatibility shim.
5. **The 2.x line is unmaintained** (last release 2016-10-14). Defects are ours to patch or escape
   via trigger 2 in §1.
6. **No debugger integration**: `IDebugger` exists and supports line-based breakpoints, but a remote
   pipeline debugger is a product feature nobody has asked for yet; the driver in §5.1 is the only
   interruption mechanism we rely on.
7. **`os.date` uses the worker's local time zone.** Correct for build logs, but a source of
   "different machines, different timestamps" reports; the run page shows UTC.

## Appendix A — the probe

A scratch console app (`net10.0`), run against `dotnet add package MoonSharp --version 2.0.0` and
`--version 3.0.0-beta.1`. It prints one section per claim in this document; the output below is from
the 2.0.0 run on the machine named at the top.

```csharp
// Zenkins Lua runtime probe. Scratch console app (net10.0) — not part of the repository.
// Run against MoonSharp 2.0.0 and 3.0.0-beta.1; every number in docs/lua-runtime.md comes from here.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using MoonSharp.Interpreter;

var explicitModules = CoreModules.Basic | CoreModules.ErrorHandling | CoreModules.Metatables
                    | CoreModules.Coroutine | CoreModules.String | CoreModules.Table
                    | CoreModules.TableIterators | CoreModules.Math | CoreModules.Bit32
                    | CoreModules.OS_Time | CoreModules.GlobalConsts;

Script Build(CoreModules modules = default)
{
    var s = new Script(modules == default ? explicitModules : modules);
    s.Globals["dynamic"] = DynValue.Nil;
    s.Globals["json"] = DynValue.Nil;
    s.Globals["package"] = DynValue.Nil;
    s.Globals["_MOONSHARP"] = DynValue.Nil;
    return s;
}

string Try(Script s, string src)
{
    try { return s.DoString(src).ToPrintString(); }
    catch (Exception ex) { return "EX " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]; }
}

void Section(string name) => Console.WriteLine("\n### " + name);

Section("A. package: TFM and dependencies");
{
    var asm = Assembly.Load("MoonSharp.Interpreter");
    var tfm = asm.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
    Console.WriteLine($"assembly={asm.GetName().Version} tfm={tfm}");
    Console.WriteLine("references=" + string.Join(",", asm.GetReferencedAssemblies().Select(a => a.Name)));
    Console.WriteLine("(dependencies: unzip -p MoonSharp.<ver>.nupkg MoonSharp.nuspec)");
}

Section("B. globals a script sees (Lua pairs), fresh vs hardened");
foreach (var (label, script) in new (string, Script)[]
         {
             ("fresh", new Script(explicitModules)),
             ("hardened", Build()),
         })
{
    var names = script.DoString("local k={} for n in pairs(_G) do k[#k+1]=n end table.sort(k) return table.concat(k, ' ')").String;
    Console.WriteLine($"{label}: {names}");
    Console.WriteLine($"  _G={Try(script, "return type(_G)")} _VERSION={Try(script, "return tostring(_VERSION)")} _MOONSHARP={Try(script, "return type(_MOONSHARP)")}");
}

Section("C. module compositions");
foreach (var (label, modules) in new (string, CoreModules)[]
         {
             ("explicit", explicitModules),
             ("Preset_SoftSandbox", CoreModules.Preset_SoftSandbox),
             ("Preset_HardSandbox", CoreModules.Preset_HardSandbox),
         })
{
    var s = new Script(modules);
    string[] names = { "pcall", "xpcall", "setmetatable", "getmetatable", "coroutine.create", "io", "load", "dofile", "require", "debug", "dynamic", "json", "os", "os.execute", "os.exit", "os.getenv", "os.time", "collectgarbage" };
    string Probe(Script sc, string name)
    {
        var v = Try(sc, "return type(" + name + ")");
        return v.StartsWith("EX ", StringComparison.Ordinal) ? "BLOCKED" : v == "nil" ? "nil" : v;
    }
    Console.WriteLine($"{label}: " + string.Join(" ", names.Select(n => $"{n}={Probe(s, n)}")));
}

Section("D. package.loaded (the registry escape)");
foreach (var (label, modules) in new (string, CoreModules)[]
         {
             ("explicit", explicitModules),
             ("explicit|Dynamic", explicitModules | CoreModules.Dynamic),
             ("Preset_Default", CoreModules.Preset_Default),
         })
{
    var s = new Script(modules);
    var loaded = s.DoString("local k={} for n in pairs(package.loaded) do k[#k+1]=n end table.sort(k) return table.concat(k, ',')").String;
    var escape = s.DoString("dynamic = nil local d = package.loaded.dynamic if d == nil then return 'unreachable' end return tostring(d.eval('1+1'))").String;
    Console.WriteLine($"{label,-16} loaded=[{loaded}] escape={escape}");
}

Section("E. drive protocol");
{
    var s = Build();
    var pending = new Queue<string>();
    s.Globals["run_process"] = (Func<string, DynValue>)(argv => { pending.Enqueue(argv); return DynValue.NewYieldReq(Array.Empty<DynValue>()); });
    s.Globals["log"] = (Action<string>)(m => Console.WriteLine("    lua->log: " + m));
    var co = s.CreateCoroutine(s.LoadString("local a = run_process('make build')\nlog('build -> ' .. tostring(a))\nlocal b = run_process('make test')\nlog('test -> ' .. tostring(b))\nreturn 'pipeline-ok'", null, "pipeline.lua")).Coroutine;
    co.AutoYieldCounter = 10_000;
    var res = co.Resume();
    Console.WriteLine($"  after first Resume: state={co.State} res.Type={res.Type} pending={pending.Count}");
    while (co.State != CoroutineState.Dead)
    {
        if (co.State == CoroutineState.ForceSuspended)
        {
            Console.WriteLine($"  state=ForceSuspended (instruction budget) -> Resume() with no args, res.Type={res.Type}");
            res = co.Resume();
        }
        else
        {
            var argv = pending.Dequeue();
            Console.WriteLine($"  state=Suspended (host call '{argv}') -> Resume(\"exit=0\")");
            res = co.Resume(DynValue.NewString("exit=0"));
        }
    }
    Console.WriteLine($"  final: state={co.State} value={res.ToPrintString()}");

    var wrong = Build();
    wrong.Globals["run_process"] = (Func<string, DynValue>)(_ => DynValue.NewYieldReq(Array.Empty<DynValue>()));
    var co2 = wrong.CreateCoroutine(wrong.LoadString("local a = run_process('x') return 'got:' .. tostring(a)", null, "pipeline.lua")).Coroutine;
    co2.Resume();
    Console.WriteLine($"  Resume() with no arg on a Suspended coroutine -> {co2.Resume().ToPrintString()}");

    var run = Build();
    var co3 = run.CreateCoroutine(run.LoadString("while true do end", null, "pipeline.lua")).Coroutine;
    co3.AutoYieldCounter = 500;
    co3.Resume();
    try { co3.Resume(DynValue.NewString("x")); Console.WriteLine("  Resume(value) on ForceSuspended -> accepted (unexpected)"); }
    catch (Exception ex) { Console.WriteLine($"  Resume(value) on ForceSuspended -> {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
}

Section("F. does a resumed value cross Lua frames?");
foreach (var (label, src) in new[]
         {
             ("lua wrapper", "local function wrap() return run_process('x') end return 'wrapped:' .. tostring(wrap())"),
             ("inside pcall", "local ok, v = pcall(function() return run_process('x') end) return 'pcall:' .. tostring(ok) .. ',' .. tostring(v)"),
             ("inside xpcall", "local ok, v = xpcall(function() return run_process('x') end, function(e) return 'E' end) return 'xpcall:' .. tostring(ok) .. ',' .. tostring(v)"),
             ("inside for", "local out = '' for i = 1, 2 do out = out .. run_process('i') .. ';' end return 'for:' .. out"),
             ("via __index", "local t = setmetatable({}, {__index = function() return run_process('x') end}) return 'meta:' .. tostring(t.any)"),
         })
{
    var s = Build();
    s.Globals["run_process"] = (Func<string, DynValue>)(_ => DynValue.NewYieldReq(Array.Empty<DynValue>()));
    var co = s.CreateCoroutine(s.LoadString(src, null, "pipeline.lua")).Coroutine;
    var r = co.Resume();
    int resumes = 0;
    while (co.State != CoroutineState.Dead && resumes++ < 20) r = co.Resume(DynValue.NewString("VAL"));
    Console.WriteLine($"  {label,-14} state={co.State} resumes={resumes} -> {r.ToPrintString()}");
}

Section("G. memory introspection");
{
    var s = Build();
    Console.WriteLine("  collectgarbage('count') = " + Try(s, "return tostring(collectgarbage('count'))"));
    Console.WriteLine("  == nil -> " + Try(s, "return tostring(collectgarbage('count') == nil)"));
    Console.WriteLine("  collectgarbage('collect') = " + Try(s, "return tostring(collectgarbage('collect'))"));
}

Section("H. errors");
{
    var s = Build();
    try { s.DoString("local x = 1\nlocal function inner() error('bad thing') end\nlocal function outer() inner() end\nouter()", null, "pipeline.lua"); }
    catch (ScriptRuntimeException ex)
    {
        Console.WriteLine("  runtime: " + ex.DecoratedMessage);
        Console.WriteLine("  frames: " + string.Join(" | ", ex.CallStack.Select(f => $"{f.Name}@{f.Location}")));
    }
    try { s.DoString("local x = = 1", null, "pipeline.lua"); }
    catch (SyntaxErrorException ex) { Console.WriteLine("  syntax : " + ex.DecoratedMessage); }
    Console.WriteLine("  error(table) through pcall: " + Try(s, "local ok, e = pcall(function() error({code=42}) end) return tostring(ok) .. ',' .. type(e) .. ',' .. tostring(e.code)"));
    Console.WriteLine("  pcall(callable table)     : " + Try(s, "local c = setmetatable({}, {__call = function() return 1 end}) local ok, e = pcall(c) return tostring(ok) .. ',' .. tostring(e)"));
}

Section("I. language ceiling");
foreach (var sn in new[] { "return 7 // 2", "return 1 << 4", "return 3 & 5", "return math.type(1)", "return utf8.len('hello')", "local t <const> = 1", "goto x ::x:: return 'goto-ok'", "return bit32.band(12, 10)", "return select('#', 1, 2, 3)" })
    Console.WriteLine($"  {sn,-34} -> {Try(Build(), sn)}");

Section("J. throughput, runaway, concurrency");
{
    var s = Build();
    var sw = Stopwatch.StartNew();
    var hot = s.DoString("local n=0 for i=1,1000000 do n = n + i % 7 end return n");
    Console.WriteLine($"  1e6 iterations: {sw.ElapsedMilliseconds} ms (result {hot.Number})");

    long calls = 0;
    s.Globals["tick"] = (Action)(() => calls++);
    sw.Restart();
    s.DoString("for i=1,100000 do tick() end");
    Console.WriteLine($"  100k C# callbacks: {sw.ElapsedMilliseconds} ms ({calls})");

    var concat = new List<long>();
    for (var i = 0; i < 3; i++)
    {
        var cs = Build();
        sw.Restart();
        var big = cs.DoString("local t={} for i=1,20000 do t[i]=('x'):rep(20) end local b='' for i=1,20000 do b=b..t[i] end return #b");
        concat.Add(sw.ElapsedMilliseconds);
    }
    Console.WriteLine($"  400 KB by concatenation (3 runs): {string.Join(", ", concat)} ms");

    foreach (var body in new[] { "while true do end", "while true do local t = {} end" })
    {
        var rs = Build();
        var co = rs.CreateCoroutine(rs.LoadString(body, null, "pipeline.lua")).Coroutine;
        co.AutoYieldCounter = 10_000;
        sw.Restart();
        co.Resume();
        int ticks = 0;
        while (co.State != CoroutineState.Dead && sw.ElapsedMilliseconds < 300) { ticks++; co.Resume(); }
        Console.WriteLine($"  runaway '{body}': state={co.State} yields={ticks} elapsed={sw.ElapsedMilliseconds} ms");
    }

    sw.Restart();
    var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        Build().DoString("local n=0 for i=1,300000 do n = n + i % 13 end return n").Number)).ToArray();
    Task.WaitAll(tasks);
    Console.WriteLine($"  8 scripts on 8 threads: {sw.ElapsedMilliseconds} ms, values={string.Join(",", tasks.Select(t => t.Result.ToString("F0")).Distinct())}");
}
```

Output (MoonSharp 2.0.0, macOS 26.5 arm64, .NET SDK 10.0.403 — one representative run; timings vary
between sessions on the same machine, results do not):

```text
### A. package: TFM and dependencies
assembly=2.0.0.0 tfm=.NETStandard,Version=v1.6
references=System.Runtime,System.Runtime.InteropServices,System.Diagnostics.Debug,System.Collections,System.IO,System.Reflection,System.Text.Encoding,System.IO.FileSystem.Primitives,System.Linq.Expressions,System.Text.RegularExpressions,System.Runtime.Extensions,System.Linq,System.Reflection.Extensions,System.Globalization,System.IO.FileSystem,System.Console,System.Threading,System.Threading.Tasks,System.Text.Encoding.Extensions
(dependencies: unzip -p MoonSharp.<ver>.nupkg MoonSharp.nuspec)

### B. globals a script sees (Lua pairs), fresh vs hardened
fresh: _G _MOONSHARP _VERSION assert bit32 collectgarbage coroutine error getmetatable ipairs math next os pack package pairs pcall print rawequal rawget rawlen rawset select setmetatable string table tonumber tostring type unpack xpcall
  _G=table _VERSION=MoonSharp 2.0.0.0 _MOONSHARP=table
hardened: _G _VERSION assert bit32 collectgarbage coroutine error getmetatable ipairs math next os pack pairs pcall print rawequal rawget rawlen rawset select setmetatable string table tonumber tostring type unpack xpcall
  _G=table _VERSION=MoonSharp 2.0.0.0 _MOONSHARP=nil

### C. module compositions
explicit: pcall=function xpcall=function setmetatable=function getmetatable=function coroutine.create=function io=nil load=nil dofile=nil require=nil debug=nil dynamic=nil json=nil os=table os.execute=nil os.exit=nil os.getenv=nil os.time=function collectgarbage=function
Preset_SoftSandbox: pcall=function xpcall=function setmetatable=function getmetatable=function coroutine.create=function io=nil load=nil dofile=nil require=nil debug=nil dynamic=table json=table os=table os.execute=nil os.exit=nil os.getenv=nil os.time=function collectgarbage=function
Preset_HardSandbox: pcall=nil xpcall=nil setmetatable=nil getmetatable=nil coroutine.create=BLOCKED io=nil load=nil dofile=nil require=nil debug=nil dynamic=nil json=nil os=nil os.execute=BLOCKED os.exit=BLOCKED os.getenv=BLOCKED os.time=BLOCKED collectgarbage=function

### D. package.loaded (the registry escape)
explicit         loaded=[bit32,coroutine,math,os,string,table] escape=unreachable
explicit|Dynamic loaded=[bit32,coroutine,dynamic,math,os,string,table] escape=2
Preset_Default   loaded=[bit32,coroutine,dynamic,io,json,math,os,string,table] escape=2

### E. drive protocol
  after first Resume: state=Suspended res.Type=Nil pending=1
  state=Suspended (host call 'make build') -> Resume("exit=0")
    lua->log: build -> exit=0
  state=Suspended (host call 'make test') -> Resume("exit=0")
    lua->log: test -> exit=0
  final: state=Dead value=pipeline-ok
  Resume() with no arg on a Suspended coroutine -> got:nil
  Resume(value) on ForceSuspended -> ArgumentException: When resuming a force-suspended coroutine, args must be empty.

### F. does a resumed value cross Lua frames?
  lua wrapper    state=Dead resumes=1 -> wrapped:VAL
  inside pcall   state=Dead resumes=1 -> pcall:true,VAL
  inside xpcall  state=Dead resumes=1 -> xpcall:true,VAL
  inside for     state=Dead resumes=2 -> for:VAL;VAL;
  via __index    state=Dead resumes=1 -> meta:VAL

### G. memory introspection
  collectgarbage('count') = nil
  == nil -> true
  collectgarbage('collect') = nil

### H. errors
  runtime: pipeline.lua:(2,28-41): bad thing
  frames: error@[0]* (2, 28) -> (2, 41) | inner (local)@ | outer (local)@ | @
  syntax : pipeline.lua:(1,10-11): unexpected symbol near '='
  error(table) through pcall: false,string,nil
  pcall(callable table)     : false,attempt to pcall a non-function

### I. language ceiling
  return 7 // 2                      -> EX SyntaxErrorException: unexpected symbol near '/'
  return 1 << 4                      -> EX SyntaxErrorException: unexpected symbol near '<'
  return 3 & 5                       -> EX SyntaxErrorException: unexpected symbol near '&'
  return math.type(1)                -> EX ScriptRuntimeException: attempt to call a nil value
  return utf8.len('hello')           -> EX ScriptRuntimeException: attempt to index a nil value
  local t <const> = 1                -> EX SyntaxErrorException: unexpected symbol near '<'
  goto x ::x:: return 'goto-ok'      -> goto-ok
  return bit32.band(12, 10)          -> 8
  return select('#', 1, 2, 3)        -> 3

### J. throughput, runaway, concurrency
  1e6 iterations: 223 ms (result 2999998)
  100k C# callbacks: 27 ms (100000)
  400 KB by concatenation (3 runs): 3579, 2864, 3125 ms
  runaway 'while true do end': state=ForceSuspended yields=7190 elapsed=300 ms
  runaway 'while true do local t = {} end': state=ForceSuspended yields=3415 elapsed=300 ms
  8 scripts on 8 threads: 234 ms, values=1800006
```
