---
id: I-004
title: NativeAOT: the Worker publishes AOT; the Server gets a recorded verdict
type: feature
priority: P2
size: L
status: todo
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

- [ ] `Zenkins.Worker` builds with `<PublishAot>true</PublishAot>` and zero IL2xxx/IL3xxx warnings
      under warnings-as-errors, locally and in CI.
- [ ] `Zenkins.Shared` (and any Worker-only library) carries `IsAotCompatible=true` with zero
      warnings.
- [ ] Every Worker wire type serializes through the source-generated `JsonSerializerContext`; no
      reflection-based serialization overload remains in Worker code paths.
- [ ] The published AOT Worker on all matrix RIDs completes Q-002's real scenario against a published
      Server: registration, claim, clone, `pipeline.lua` with host functions and `secret()`, streamed
      logs, artifact upload, and the cancellation path.
- [ ] The same `pipeline.lua` produces identical step outcomes under JIT and AOT (measured once,
      recorded in the card log and `docs/lua-runtime.md`).
- [ ] The Server AOT verdict is documented (reasons per subsystem, alternatives, revisit triggers such
      as EF Core AOT leaving experimental) in `docs/`, and the Server's I-003 packaging shows no
      regression.
- [ ] Startup-time observation for the Worker (JIT vs AOT) is recorded as evidence for keeping AOT,
      or for dropping it honestly if the gain is nil.

## Subtasks

- [ ] Enable `PublishAot` on the Worker and `IsAotCompatible` on `Zenkins.Shared`; fix surfaced
      analyzers warnings at the source.
- [ ] Source-generated JSON context(s) for the Worker's API client and credential client; replace
      reflection overloads.
- [ ] MoonSharp AOT proof: host-function registration under trimming/AOT, one real pipeline both
      modes, record findings.
- [ ] Publish + smoke harness per RID (reuses I-003's publish conventions and Q-002's scenario).
- [ ] Server verdict document with revisit triggers.
- [ ] CI matrix entry (each OS builds its own RID).

## Dependencies

- none (coordinates with I-003 on publish conventions; landing I-003 first avoids churn, but the AOT
  Worker can be smoke-tested against a JIT-built Server, so nothing blocks)

## Test Cases

### TC-001: The AOT binary runs the real scenario

Steps:
1. `dotnet publish -c Release -r <rid>` the Worker with AOT; point it at a published Server; trigger a
   real repository build; cancel one run mid-step.

Expected:
- Registration, execution, logs, artifact and the honest `Canceled` path all behave as under JIT; the
  binary runs with no .NET runtime installed.

### TC-002: Trim sensitivity is caught at build, not at runtime

Steps:
1. Remove or rename a host-function type the Lua surface registers; build with AOT analyzers on.

Expected:
- The build fails at the analyzer level (or the proof test fails deterministically), never a silent
  runtime `MissingMethodException` in a published binary.

### TC-003: The wire stays source-generated

Steps:
1. Grep the Worker for reflection-based `JsonSerializer` overloads; run the wire-focused tests against
   the AOT publish output.

Expected:
- Zero reflection overloads; all wire tests green against the published binary.

## Development Log

### 2026-11-14

- Planned per user request. Scope pinned by the .NET 10 AOT decision guide: Worker is a genuine AOT
  target; Blazor Server + EF Core + Identity on the Server are not — the card records that verdict
  rather than fighting it. MoonSharp's reflection-based registration is the one real unknown and gets
  a dedicated proof task.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Worker/Zenkins.Worker.csproj`, `src/Shared/Zenkins.Shared.csproj`,
  `src/Worker/WorkerApiClient.cs`, `src/Worker/Git/GitCredentialClient.cs`, `src/Worker/Lua/LuaRuntime.cs`,
  `docs/lua-runtime.md` (decision record), `Directory.Build.props`/`Directory.Build.targets`
- How to run/verify: `dotnet build -c Release` (analyzers on); `dotnet publish -c Release -r <rid>`
  per RID matrix; smoke harness against published Server; `dotnet test -c Release` full suite.
- Results: not run (card not started).
