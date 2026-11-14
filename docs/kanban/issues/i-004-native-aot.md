---
id: I-004
title: NativeAOT: the Worker publishes AOT; the Server gets a recorded verdict
type: feature
priority: P2
size: L
status: done
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

`Zenkins.Worker` ships as a first-class NativeAOT artifact (fast start, no runtime install) that
passes the real end-to-end build scenario, and the Server receives an explicit, documented AOT
verdict — expected: stays JIT — so the decision is recorded rather than folklore.

## Background

User-requested card (2026-11-14). The stack splits cleanly on AOT feasibility:

- **Worker (AOT target)**: console host; process spawning (`ProcessRunner`, git CLI) is AOT-safe;
  MoonSharp 2.0.0 is an interpreter (no runtime codegen), but its CLR surface (`UserData` type
  registration for host functions) is reflection-based and must be proven against a real pipeline;
  the wire uses `System.Text.Json` in `WorkerApiClient` and `GitCredentialClient` via reflection-based
  overloads, which are disabled under AOT.
- **Server (not an AOT target on .NET 10)**: Blazor Server (component reflection), EF Core 10 (AOT
  support — precompiled queries — still experimental) and ASP.NET Identity are all documented
  non-targets; the Server's packaging path is I-003's JIT self-contained publish. The card records
  this verdict with reasons and revisit triggers, in the style of `docs/lua-runtime.md`, instead of
  attempting the impossible.
- **Zenkins.Shared is referenced by both**, so it must become `IsAotCompatible` with zero warnings for
  the Worker to be clean.

Decisions:

- **Warnings are errors.** `<PublishAot>true</PublishAot>` lives in `Zenkins.Worker.csproj` (not only
  on the command line) so analyzers run on every build; no `NoWarn`, no `UnconditionalSuppressMessage`
  without a written justification at the site.
- **Source-generate everything dynamic on the Worker**: a `JsonSerializerContext` for every type that
  crosses the wire (registration, heartbeat, log chunks, report, credential payloads), `JsonTypeInfo`
  overloads everywhere — reflection-based `Serialize`/`ReadFromJson` overloads do not survive;
  `[GeneratedRegex]` for any `RegexOptions.Compiled` in Worker paths.
- **MoonSharp under AOT is proven, not assumed**: interpreter mode needs no codegen, but host
  function registration must keep its types (`[DynamicallyAccessedMembers]` where the surface is
  generic); the proof is the same `pipeline.lua` executing with identical step outcomes under JIT and
  AOT. Findings go into `docs/lua-runtime.md`'s decision record.
- **Test the published binary, never `dotnet run`**: the smoke test is the published AOT Worker
  against a published Server completing Q-002's scenario (register, claim, clone, `pipeline.lua`,
  logs, artifact, cancellation).
- **RID matrix matches I-003** (`win-x64`, `linux-x64`, `osx-arm64`/as measured) and each OS builds on
  itself — AOT needs the native toolchain (VS C++ workload / clang+zlib / Xcode CLT).

## Acceptance Criteria

- [x] `Zenkins.Worker` builds with `<PublishAot>true</PublishAot>` and zero IL2xxx/IL3xxx warnings
      under warnings-as-errors, locally and in CI.
- [x] `Zenkins.Shared` (and any Worker-only library) carries `IsAotCompatible=true` with zero
      warnings.
- [x] Every Worker wire type serializes through the source-generated `JsonSerializerContext`; no
      reflection-based serialization overload remains in Worker code paths.
- [x] The published AOT Worker on all matrix RIDs completes Q-002's real scenario against a published
      Server: registration, claim, clone, `pipeline.lua` with host functions and `secret()`, streamed
      logs, artifact upload, and the cancellation path.
- [x] The same `pipeline.lua` produces identical step outcomes under JIT and AOT (measured once,
      recorded in the card log and `docs/lua-runtime.md`).
- [x] The Server AOT verdict is documented (reasons per subsystem, alternatives, revisit triggers such
      as EF Core AOT leaving experimental) in `docs/`, and the Server's I-003 packaging shows no
      regression.
- [x] Startup-time observation for the Worker (JIT vs AOT) is recorded as evidence for keeping AOT,
      or for dropping it honestly if the gain is nil.

## Subtasks

- [x] Enable `PublishAot` on the Worker and `IsAotCompatible` on `Zenkins.Shared`; fix surfaced
      analyzers warnings at the source.
- [x] Source-generated JSON context(s) for the Worker's API client and credential client; replace
      reflection overloads.
- [x] MoonSharp AOT proof: host-function registration under trimming/AOT, one real pipeline both
      modes, record findings.
- [x] Publish + smoke harness per RID (reuses I-003's publish conventions and Q-002's scenario).
- [x] Server verdict document with revisit triggers.
- [x] CI matrix entry (each OS builds its own RID).

## Dependencies

- none (coordinates with I-003 on publish conventions; landing I-003 first avoids churn, but the AOT
  Worker can be smoke-tested against a JIT-built Server, so nothing blocks)

## Test Cases

### TC-001: The AOT binary runs the real scenario — PASS

Steps:
1. `dotnet publish -c Release -r <rid>` the Worker with AOT; point it at a published Server; trigger a
   real repository build; cancel one run mid-step.

Expected:
- Registration, execution, logs, artifact and the honest `Canceled` path all behave as under JIT; the
  binary runs with no .NET runtime installed.

Result (observed, 2026-11-14): PASS. The published AOT Worker (`osx-arm64`; the publish output carries
no `.dll`, no `runtimeconfig.json`, no CoreCLR files — asserted by the suite's fixture) against the
published JIT Server completes Q-002's full story — registration, claim, clone, `pipeline.lua` with
host functions and `secret()`, streamed logs with redaction, artifact upload/download — and the
cancellation path ends honestly (`Canceled` / `canceled-by-user`, step 1 `Failed`/`canceled`, steps
2–3 `Skipped`/`run-canceled`, the step-2 marker file absent). The independent reviewer re-ran the
suite itself.

### TC-002: Trim sensitivity is caught at build, not at runtime — PASS (restated form, PM ruling)

Steps:
1. Remove or rename a host-function type the Lua surface registers; build with AOT analyzers on.

Expected:
- The build fails at the analyzer level (or the proof test fails deterministically), never a silent
  runtime `MissingMethodException` in a published binary.

Result (observed, 2026-11-14): PASS, via a restated form — the card's literal "remove or rename a
host-function type" is not expressible on this surface because the host functions are closures with
no type registration to remove (found in preread; PM ruling accepted the restatement). The proof is a
three-part combination, none of it a runtime-only check: a fast-suite guard test asserts the JSON
context covers all 31 wire shapes (stripping one registration fails deterministically and names the
missing shapes), the same guard pins `PublishAot`/`IsAotCompatible`/the single justified `NoWarn`
pair in the csproj, and `[GeneratedRegex]` fails at build time on a missing pattern. Demos recorded:
renaming a shape → `CS0246` at compile; stripped registration → guard failure listing the four
missing shapes.

### TC-003: The wire stays source-generated — PASS

Steps:
1. Grep the Worker for reflection-based `JsonSerializer` overloads; run the wire-focused tests against
   the AOT publish output.

Expected:
- Zero reflection overloads; all wire tests green against the published binary.

Result (observed, 2026-11-14): PASS. Worker grep: zero non-typeinfo `JsonSerializer` calls. The REST
context (`WorkerJsonContext`, 21 types) pre-dated this card and was verified complete against every
payload; the one real gap was SignalR — the hub protocol's payload serialization is reflection-based
inside `SignalR.Protocols.Json` with `IL2026`/`IL3050` suppressed in the package, so AOT built clean
and would have failed at the first payload. Closed with `AddJsonProtocol` mirroring the protocol
defaults (camelCase, case-insensitive, `MaxDepth 64`) + `TypeInfoResolver=WorkerJsonContext` covering
`LogLineMessage`, its list shapes, `Guid` and `string`; both failure points (`AppendLinesAsync`
payload, `cancelRun` inbound) are type-closed and exercised by the AOT smoke, including the
cancellation path.

## Development Log

### 2026-11-14

- Planned per user request. Scope pinned by the .NET 10 AOT decision guide: Worker is a genuine AOT
  target; Blazor Server + EF Core + Identity on the Server are not — the card records that verdict
  rather than fighting it. MoonSharp's reflection-based registration is the one real unknown and gets
  a dedicated proof task.
- Implemented per the PM rulings of 2026-11-14: scope is osx-arm64 in full; win-x64/linux-x64 get
  analyzer-level builds only (real AOT publish needs each OS's native toolchain — recorded unverified,
  carried to DOC-003); the Server verdict is stays-JIT, recorded in `docs/native-aot.md` with
  per-subsystem reasons, alternatives and four revisit triggers; CI is honestly recorded as a revisit
  trigger (the repository has none; analyzers are enforced on every local build via
  warnings-as-errors).
- Two stale assumptions corrected on the way. First, the REST wire was already source-generated
  (`WorkerJsonContext`, 21 types, all `JsonTypeInfo` overloads) — the issue's background was stale
  there. The real gap was SignalR (see TC-003). Second — found during the AOT proof, predicted by
  nobody: ilc drops the reflection metadata of MoonSharp's CoreLib module types (all 12 module types
  answered `GetMethods` with an empty array → stdlib globals nil → every run failed
  `pipeline-invalid`, with zero build warnings). Fixed with
  `<TrimmerRootAssembly Include="MoonSharp.Interpreter" />` (justified at the site) plus one
  `NoWarn` pair (`IL2104;IL3053`) for the 22 member warnings in the `Interop`/`Compatibility` bridge
  the composition provably never reaches. Probe, table and findings recorded in
  `docs/lua-runtime.md` §8.1.
- I-003 protected: the publish script's JIT rows pin `-p:PublishAot=false` explicitly, a
  `--worker-aot` variant was added and exercised (four artifacts, the AOT one carrying no dll/
runtimeconfig/CoreCLR), the `PublishedArtifacts` fixture pins the same, and the new `PublishAot`
  suite joins the per-suite gate (docs/development.md §2 now documents five suites: Fast 1001,
  PublishSmoke 2, Milestone 6, Recovery 4, PublishAot 2).
- Startup evidence (keep AOT): dual-anchor methodology, N=7 median/min, JIT baseline = the
  self-contained JIT folder publish run through its own apphost (never `dotnet run`). Config-error
  24.9/24.4 ms vs 7.3/6.3 ms; startup banner 34.3/34.1 vs 5.2/5.0; registration accepted against a
  live published Server 69.0/68.3 vs 9.1/8.9 (~7.6×). The reviewer re-measured within noise.
- Dual-mode proof (AC-5): the same `pipeline.lua` executed by the JIT Worker and the AOT Worker
  against published Servers produced identical step outcomes, log lines (one wall-clock duration
  masked) and artifact bytes — recorded here and in `docs/lua-runtime.md` §8.1 / `docs/native-aot.md`
  §3.
- Independent review: PASS with its own re-runs (build, AOT publish, publish.sh `--worker-aot`, all
  five suites, win-x64/linux-x64 analyzer builds, startup re-measure, dual-mode parity), a clean
  per-file reconciliation (including no residue from the TC-002 demo's git-checkout incident), and
  three non-blocking notes disposed at acceptance: four duplicate probe rows removed from
  `lua-runtime.md` §8.1; the anchor-(b) row in `native-aot.md` reworded (the banner goes to stderr
  through the startup-error path and exits 2 — zero network either way, numbers unchanged); AC-5's
  card-log record is this entry.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | No product defect found. The AOT work surfaced two silent landmines, both fixed in-card: SignalR hub payloads serialize by reflection inside the dependency with the warnings suppressed (closed with a source-generated payload resolver), and ilc dropped MoonSharp's CoreLib module reflection metadata, nil-ing the stdlib (closed with a justified `TrimmerRootAssembly`). | Closed | Both are pinned by guard tests and the AOT smoke; findings recorded in `docs/lua-runtime.md` §8.1 and `docs/native-aot.md`. |

## Verification

- Related files: `src/Worker/Zenkins.Worker.csproj`, `src/Worker/LogStreamClient.cs`,
  `src/Worker/WorkerApiClient.cs` (WorkerJsonContext), `src/Worker/Git/GitUrl.cs`,
  `src/Shared/Zenkins.Shared.csproj`, `scripts/publish.sh`,
  `tests/Zenkins.Tests/Milestone/PublishedAotSmokeTests.cs`,
  `tests/Zenkins.Tests/WorkerAotGuardTests.cs`, `tests/Zenkins.Tests/Milestone/PublishedSmokeTests.cs`,
  `docs/native-aot.md`, `docs/lua-runtime.md` §8.1, `docs/development.md` §2.
- How to run/verify (per-suite gate, five suites): fast suite
  `dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot"`;
  then `Category=PublishSmoke`, `Category=PublishAot`, `Category=Milestone`, `Category=Recovery`;
  `dotnet publish -c Release -r osx-arm64` (AOT, zero warnings); `scripts/publish.sh [--worker-aot]
  osx-arm64`; startup benchmark per `docs/native-aot.md` §3's method.
- Results (observed 2026-11-14; dev run + independent review re-runs): build 0 warnings; Fast 1001,
  PublishSmoke 2, Milestone 6, Recovery 4, PublishAot 2 — all green, 0 skipped; osx-arm64 AOT publish
  0 warnings; publish.sh exit 0 (JIT rows still JIT, WorkerAot artifact carries no dll/runtimeconfig/
  CoreCLR); win-x64/linux-x64 analyzer builds 0/0 (real AOT publish unexercised — needs per-OS
  native toolchains, DOC-003); dual-mode parity identical; startup ~3.4–7.6× faster (method and
  numbers in `docs/native-aot.md` §3).
