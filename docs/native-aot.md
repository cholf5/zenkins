# Native AOT: the Worker publishes AOT, the Server stays JIT

What card I-004 decided about NativeAOT, what it measured, and what would reopen the decision.
The Worker ships as a first-class NativeAOT artifact; the Server is recorded as a JIT-only target
instead of fighting an impossible port. This document is the decision record the card's issue file
refers to; `docs/lua-runtime.md` §8.1 carries the MoonSharp-specific findings.

> **Verified on:** macOS 26.5 (arm64) with .NET SDK **10.0.403**, 2026-11-14, by running the card's
> gates: `dotnet publish -c Release -r osx-arm64` (AOT, 0 warnings), the five test suites of
> `docs/development.md` §2, and the startup benchmark described in §3. Every number in §3 and §4 is
> an observation from those runs. The win-x64 and linux-x64 rows are **analyzer-build evidence
> only** — see §7 for what was and was not verified per RID.

## 1. Decision

**`Zenkins.Worker` publishes NativeAOT (osx-arm64 verified); `Zenkins.Server` stays JIT;
`Zenkins.Shared` is `IsAotCompatible`.**

| Subject | Decision | Where it lives |
|---|---|---|
| Worker | `<PublishAot>true</PublishAot>` — in the csproj, not only on the publish command line, so the AOT/trim/single-file analyzers run on every `dotnet build` and warnings-as-errors fails at the analyzer level | `src/Worker/Zenkins.Worker.csproj` |
| Shared | `<IsAotCompatible>true</IsAotCompatible>` — the same analyzer surface for the assembly the Worker and the Server both consume | `src/Shared/Zenkins.Shared.csproj` |
| Server | **No AOT attempt.** Verdict recorded in §6; its packaging stays I-003's JIT self-contained publish | — |
| JIT matrix | Unchanged behaviour: the JIT rows pass `-p:PublishAot=false` explicitly so they cannot drift with the Worker's csproj default; `scripts/publish.py --aot` (or the `--worker-aot` spelling it still accepts) adds the AOT variant for the host RID | `scripts/publish.py`, `scripts/zt/dotnet.py` |

The one `NoWarn` in the solution (`IL2104;IL3053`, MoonSharp's trim/AOT aggregates) lives in the
Worker csproj with its justification written at the site; `WorkerAotGuardTests` pins it to exactly
that pair so it cannot widen silently. MoonSharp itself is reflection-rooted (§4.2).

**Switch triggers** (any one re-opens this decision):

1. The Server's blockers move (§6's revisit triggers) — the verdict is re-evaluated, not auto-applied.
2. MoonSharp ships a release with trim/AOT annotations — the `TrimmerRootAssembly` root and the
   `NoWarn` pair are re-examined, and the §4 probe is re-run.
3. A card needs a Worker dependency that cannot survive AOT (reflection-based JSON, runtime
   codegen) and cannot be source-generated — the Worker's AOT status is renegotiated.

## 2. What keeps the Worker AOT-clean

- **Source-generated JSON everywhere.** `WorkerJsonContext` carries every type that crosses the
  wire (registration, heartbeat, claim, definition, complete, cancel, steps, secrets, artifacts,
  the artifact error body, the cancel contract, and the SignalR `LogLineMessage` payload in every
  shape the hub protocol can name at runtime, plus the `Guid`/`string` primitives). Calls use
  `JsonTypeInfo` overloads only. `GitCredentialClient` reads its one body through
  `JsonDocument.Parse` — a reflection-free DOM read, AOT-safe by construction.
- **The SignalR log stream routes through the context.** The JSON hub protocol serializes
  hub-method arguments via `PayloadSerializerOptions`, whose default resolver is reflection-based
  and whose package suppresses the resulting warnings internally — the gap would surface only as a
  runtime failure in a published binary, on the first `AppendLinesAsync`/`cancelRun` exchange.
  `LogStreamClient` replaces that options instance with the protocol's own defaults (camelCase,
  case-insensitive reads, MaxDepth 64) resolved through `WorkerJsonContext`, so the bytes on the
  wire are exactly what the JIT worker sent.
- **`[GeneratedRegex]` instead of `RegexOptions.Compiled`.** `GitUrl`'s scp-like pattern was the
  only JIT-codegen regex in the worker; the source-generated scanner has identical matching
  behaviour and no codegen dependency.
- **No reflection in the Lua surface.** Host functions are delegate `CallbackFunction`s — there is
  no `UserData.RegisterType`, no `RegisterAssembly`, no CLR type registration anywhere in
  production (`docs/lua-runtime.md` §3.2). A renamed host-function type is a compile error, never
  a runtime `MissingMethodException`.
- **The wire guard tests.** `WorkerAotGuardTests` (fast suite) runs TC-003's grep as assertions:
  no reflection-based `JsonSerializer` overload, no reflection `ReadFromJsonAsync`, no
  `RegexOptions.Compiled` in worker source; the missing-registration assertion proves every wire
  type resolves source-generated metadata; and the csproj pins (PublishAot / IsAotCompatible /
  the exact NoWarn pair) fail closed if the analyzer surface is ever removed.

## 3. Measured evidence (osx-arm64)

**Publish:** `dotnet publish -c Release -r osx-arm64` — succeeded, **0 warnings**. The output is one
native executable (`Mach-O 64-bit executable arm64`, ~12.9 MB) and debug symbols: no
`Zenkins.Worker.dll`, no `runtimeconfig.json`, no CoreCLR files (asserted by the PublishAot suite's
fixture). The JIT Worker folder publish for comparison is 88.7 MB on disk.

**Startup (why AOT is worth keeping):** two anchors, both spawn-to-observation, measured with
`python3`/`time.perf_counter`, one warm-up run discarded, N=7, median and min reported. Baseline is
the self-contained JIT folder publish executed through its own apphost (never `dotnet run`; the
single-file JIT variant pays a self-extract cost and was not benchmarked).

| Anchor | JIT (folder publish) | AOT | Ratio |
|---|---|---|---|
| (a) config-error: no `ZENKINS_SERVER_URL`, spawn → exit 2 (zero network) | 24.9 / 24.4 ms | 7.3 / 6.3 ms | ~3.4× |
| (b) spawn → the startup banner line (`Zenkins Worker:` — printed to **stderr** through the no-token startup-error path, which then exits 2; still zero network) | 34.3 / 34.1 ms | 5.2 / 5.0 ms | ~6.6× |
| (b′) spawn → **registration accepted** against a live published Server (fresh one-time token + name per run) | 69.0 / 68.3 ms | 9.1 / 8.9 ms | ~7.6× |

The gain is real and worth keeping. Anchor (b′) includes the server's registration round trip for
both modes; anchors (a)/(b) isolate process start plus the worker's startup path.

**The real scenario under AOT** (`Category=PublishAot`, `PublishedAotSmokeTests`): the published
AOT Worker against the published JIT Server completes Q-002's full story — registration, claim,
clone, `pipeline.lua` with host functions and `secret()`, streamed logs with the `***` redaction,
artifact upload and download — and the cancellation path ends honestly (`Canceled`,
`canceled-by-user`, step 1 `Failed`/`canceled`, steps 2–3 `Skipped`/`run-canceled`, the step-2
marker file absent).

**The dual-mode proof:** the same `pipeline.lua` (the PublishedHappy shape: three shell steps, a
pipeline-level env var, a `secret()` claim, an artifact) executed by the JIT Worker and the AOT
Worker against identical published Servers produced **identical recorded outcomes** — run status,
exit code, commit, every step tuple, every log line, the artifact bytes, the redaction count. The
only masked differences are timing, not semantics: the wall-clock duration inside the one synthetic
"exited with code N after Xs" line, and each host's own temporary root/run id inside the clone's
stderr line.

## 4. MoonSharp under NativeAOT

The interpreter needs no runtime codegen, but the standard library is registered by reflection, and
that surface does not survive an unrooted AOT compile. Full findings and the probe live in
[`lua-runtime.md` §8.1](lua-runtime.md#81-moonsharp-under-nativeaot-i-004); the short form:

1. **Without a root, AOT is silently broken at runtime.** ilc drops the member metadata of
   MoonSharp's `CoreLib` module types (measured: `GetMethods` returns empty for every module type),
   the registration loop registers nothing, every stdlib function (`type`, `pcall`, `pairs`,
   `string.*`, `math.*`, `os.*`) is `nil` at runtime, and the prelude dies at its first `type()`
   call — every run fails `pipeline-invalid`. No build warning points at it.
2. **The fix is one line with a written reason:** `<TrimmerRootAssembly Include="MoonSharp.Interpreter" />`
   in the Worker csproj. Measured after rooting: every stdlib function present, the interpreter's
   behaviour identical to JIT, and the dual-mode proof above passes.
3. **What still warns:** 22 trim/AOT warnings inside MoonSharp's CLR-bridge interop layer and its
   compatibility reflection helpers — code the Worker's composition provably never reaches (no
   `CoreModules.Dynamic`, no `UserData` registration). They aggregate as `IL2104`/`IL3053`, are
   suppressed with justification at the csproj site, and are covered by the switch triggers in §1.

## 5. TC-002: trim sensitivity is caught at build, not at runtime

The card's literal step ("remove or rename a host-function type the Lua surface registers") is not
expressible on the real surface — the Lua host functions are closures, not registered CLR types —
so the pin is a composition of four gates, two of them demonstrated during the card:

- **Rename → compile failure, never a runtime break.** Renaming the `WorkerSecretResponse` record
  (its registration and call sites untouched) fails `dotnet build` with `CS0246` at both the
  `WorkerJsonContext` reference and the call site.
- **Missing registration → fast-suite failure.** Stripping the four `LogLineMessage`
  registrations from `WorkerJsonContext` makes `Every_wire_type_has_source_generated_metadata` fail
  deterministically, naming the missing shapes (`LogLineMessage`, `List`1`, `LogLineMessage[]`,
  `IReadOnlyList`1`) before any publish.
- **The analyzer surface is pinned.** `PublishAot` / `IsAotCompatible` / the exact `NoWarn` pair are
  asserted by the fast suite; removing them fails the gate instead of silently disabling AOT
  analysis.
- **The PublishAot smoke is the deterministic runtime proof.** If the reachable MoonSharp surface,
  the SignalR payload wire or the JSON contexts ever break under AOT, `PublishedAotSmokeTests`
  fails on a real pipeline instead of letting a published binary fail in production.

## 6. The Server verdict: stays JIT

The Server is not an AOT target on .NET 10. This is a recorded verdict, not an omission:

| Subsystem | Why it blocks AOT |
|---|---|
| Blazor Server (interactive SSR) | Component rendering, navigation and the render-tree depend on runtime reflection and dynamic component activation; Blazor Server has no supported AOT hosting story on .NET 10 (AOT in Blazor exists for WebAssembly, a different runtime). |
| EF Core 10 | The AOT path (precompiled queries) is explicitly experimental; the Server's data layer is the product's core and does not ride an experimental compiler surface. |
| ASP.NET Identity + Data Protection | Identity's stores and the Data Protection key ring lean on the same reflection-heavy hosting stack; no AOT-ready path is documented. |
| SignalR server hub + static web assets | The hub side and the Blazor circuit share the ASP.NET Core hosting pipeline above; the static web asset manifest is a publish artifact tied to the JIT build. |

**Alternatives considered:**

- *AOT anyway* — blocked by all four rows above; there is no partial AOT for an ASP.NET Core app.
- *Trim-only publish (no AOT)* — possible in principle, but the Server's payload is the framework
  itself (Blazor, EF, Identity); trimming it is a size project with a correctness surface, and
  neither size nor startup was this card's goal for the Server. Recorded as a possible future card,
  not a decision made here.
- *Split the Server* — no: the Server is one app (I-003's packaging), and the card's goal is a
  recorded verdict, not a redesign.

**Revisit triggers** (any one re-opens the verdict):

1. EF Core's AOT support (precompiled queries) ships as stable, non-experimental.
2. Blazor Server gains a supported AOT hosting model on the server runtime.
3. Identity / Data Protection document AOT-ready paths, or those subsystems are extracted from the
   Server's hosting stack.
4. CI exists in this repository — then the per-OS CI matrix entry (each OS builds its own RID) is
   added; this card recorded it as a trigger because there is no CI to extend today.

**No regression:** I-003's PublishSmoke suite (2 tests, JIT folder + single-file, `App_Data`
discipline, upgrade cycle) passes unchanged with the Worker's AOT default in place — the JIT rows
pin `-p:PublishAot=false` explicitly.

## 7. RID matrix status

| RID | What was verified on 2026-11-14 | Not verified (recorded honestly) |
|---|---|---|
| `osx-arm64` | Full: AOT publish 0 warnings; packaging asserts; Q-002 scenario incl. cancel; dual-mode proof; startup numbers | — |
| `win-x64` | `dotnet build -c Release -r win-x64` with AOT analyzers on: 0 warnings, 0 errors | A real AOT publish — it needs the Visual Studio C++ workload on a Windows machine, which this card's machine does not have |
| `linux-x64` | `dotnet build -c Release -r linux-x64` with AOT analyzers on: 0 warnings, 0 errors | A real AOT publish — it needs clang plus zlib development headers on a Linux machine, which this card's machine does not have |

.NET does not cross-compile AOT for another OS: each OS publishes its own RID (the CI matrix entry
of §6's trigger 4, when CI exists). The analyzer builds are the guarantee that travels across
machines — every IL2xxx/IL3050 hit is a build failure everywhere, before any publish.

## 8. How to verify

```bash
dotnet build -c Release                                                                          # analyzers on, 0 warnings
dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres&Category!=Load"  # fast suite
dotnet test -c Release --filter "Category=Milestone"                                             # acceptance harness
dotnet test -c Release --filter "Category=Recovery"                                              # acceptance harness
dotnet test -c Release --filter "Category=PublishSmoke"                                          # I-003 JIT packaging
dotnet test -c Release --filter "Category=PublishAot"                                            # I-004 AOT suite (needs the native toolchain)
dotnet publish src/Worker/Zenkins.Worker.csproj -c Release -r osx-arm64                          # the AOT artifact, 0 warnings
scripts/publish.py --aot osx-arm64                                                               # the matrix with the AOT variant
```
