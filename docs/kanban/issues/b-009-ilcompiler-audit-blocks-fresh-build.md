---
id: B-009
title: A fresh Windows machine cannot restore the solution — NU1903 on ILCompiler 10.0.11, and warnings are errors
type: bug
priority: P2
size: S
status: review
created: 2026-11-16
updated: 2026-11-16
blocked: windows: the pin is verified on macOS only (the 10.0.11 failure itself was reproduced there)
---

## Goal

`dotnet build Zenkins.slnx -c Release` succeeds on any machine the repository claims to support,
without a caller having to disable NuGet audit — and the AOT worker is compiled by an ILCompiler
version that has no open advisory against it.

## Background

Found on 2026-11-16 on a real Windows box (Windows 10 19045, x64) while standing up platform
verification:

- `dotnet build Zenkins.slnx -c Release` fails at restore with **NU1903**: `Microsoft.DotNet.ILCompiler`
  **10.0.11** carries a known high-severity advisory (GHSA-p3jp-9v83-76w9), and
  `Directory.Build.props` sets `TreatWarningsAsErrors=true`, so the audit warning is fatal.
- Adding `-p:NuGetAudit=false` builds cleanly in 4.9 s (0 warnings, 0 errors), so nothing else on that
  machine is wrong.

The same solution builds on the project's macOS machine with **no** vulnerable package reported, and
its assets resolve `Microsoft.DotNet.ILCompiler/10.0.14`. `global.json` asks for SDK `10.0.100` with
`rollForward: latestFeature`, so the toolchain version — and therefore the ILCompiler version — is a
function of **which SDK the machine happens to have**, not of the repository. That drift is the real
defect: the Windows box picked 10.0.400, macOS has 10.0.403, and they disagree about the AOT compiler
they compile the Worker with.

`src/Worker/Zenkins.Worker.csproj` sets `PublishAot`, so this is not a test-only package: it is the
compiler that produces the shipped worker binary.

## Acceptance Criteria

- [x] The ILCompiler version is pinned by the repository (central package management) or by an
      explicit SDK pin, so two machines with different installed feature bands resolve the same
      compiler.
- [ ] A clean `dotnet build Zenkins.slnx -c Release` succeeds on Windows without
      `-p:NuGetAudit=false`, and `dotnet list package --vulnerable --include-transitive` reports
      nothing for the Worker on both Windows and macOS. macOS: verified. Windows: **not verified here**
      (no Windows machine) — but the failing half of it was reproduced on macOS by pinning 10.0.11,
      see Verification.
- [x] `TreatWarningsAsErrors` stays on: the fix is a correct dependency, not a suppressed audit.
- [x] If `global.json`'s roll-forward is what lets the drift in, narrow it and record why; if pinning
      ILCompiler is enough, record that instead.
- [ ] CI keeps building green on all three runners after the change. Owed: the Windows and Linux jobs.

## Subtasks

- [x] Pin `Microsoft.DotNet.ILCompiler` (or the runtime/toolchain set) to a version with no advisory.
- [ ] Verify on the Windows box and on macOS; record both `project.assets.json` versions. macOS done
      (10.0.14); the Windows number is owed.
- [x] Decide and document the `global.json` roll-forward policy.

## Dependencies

- none

## Notes and Decisions

- Not filed as a security emergency: the advisory is against the AOT compiler, and the exposure path
  (what an attacker must control) has not been established. Recorded here so the version is chosen
  deliberately rather than by whichever machine built the artifact.
- The workaround is deliberately **not** written into `docs/` as the supported way to build: a build
  that needs audit disabled is a build that would ship the vulnerable compiler.

## Resolution (2026-11-16)

**The version: 10.0.14.** GHSA-p3jp-9v83-76w9 (CVE-2026-111497, the Brotli decoder, high) covers
`Microsoft.DotNet.ILCompiler >= 10.0.0, < 10.0.14`; 10.0.14 is the first patched release of the 10.0
line and the version the current SDK band ships, so the pin is a no-op on a machine already there and
lifts a 10.0.11 machine onto the patched compiler. Deliberately **not** 10.0.15 (the band EF Core and
the hosting packages are on): the AOT compiler is paired with the SDK's own runtime band, and pinning
it ahead of the SDK would be the same drift pointing the other way. The advisory is the only one
published against this package (`/advisories?affects=Microsoft.DotNet.ILCompiler` returns one entry).

**How it is pinned — not where you would look first.** `Directory.Packages.props` cannot hold it: the
reference is *SDK-implicit* under `PublishAot`, and central package management refuses a
`PackageVersion` for an implicit reference (**NU1009**, measured). An explicit `PackageReference` with
a version is refused too (**NU1008**). The SDK builds that reference from its own
`KnownILCompilerPack` item (`ILCompilerPackVersion` in the installed SDK's
`Microsoft.NETCoreSdk.BundledVersions.props`), so the repository rewrites that item instead:

- `Directory.Build.props` — `<ZenkinsILCompilerVersion>10.0.14</ZenkinsILCompilerVersion>`, next to the
  `TreatWarningsAsErrors` that made the missing pin fatal, with the reasoning above written out.
- `Directory.Build.targets` — `KnownILCompilerPack Update="Microsoft.DotNet.ILCompiler"
  ILCompilerPackVersion="$(ZenkinsILCompilerVersion)"`, at evaluation time (restore is what reads it;
  a `Target` would be too late).

That the update really drives the graph was proved by moving it: 10.0.15 in the property turned
`project.assets.json` into `Microsoft.DotNet.ILCompiler/10.0.15` plus
`runtime.osx-arm64.Microsoft.DotNet.ILCompiler/10.0.15`, and back.

**`global.json`: keep `latestFeature`, and say why in the file.** Narrowing it would not make machines
agree — it would only choose a *different* SDK per machine and, at `latestPatch`, refuse a fresh
Windows box that ships 10.0.4xx, which is this card's defect inverted. With the compiler pinned by the
repository, the field no longer decides what compiles the worker, so it stays at the value that makes
the guide's single prerequisite (".NET 10 SDK, 10.0.100 or newer") true. The reasoning is written as
comments in `global.json` itself (the SDK's reader accepts them) and summarised in
`docs/development.md` §1.

**Residual risk, stated rather than hidden.** On a machine whose SDK band is *older* than 10.0.14, the
pin makes restore and `dotnet build` correct immediately (build runs the AOT analyzers, not `ilc`), but
an AOT *publish* there would pair a 10.0.14 compiler with that SDK's older runtime pack. The fix for
that is to be on a current SDK band, which is what "10.0.100 or newer" already asks for; the Windows
box in the report (10.0.400) would want a 10.0.4xx SDK that ships 10.0.14 or newer. Not verified here
— there is no Windows machine.

## Verification

- **The Windows failure reproduced on macOS, by pinning the version it failed on.** With
  `ZenkinsILCompilerVersion` set to `10.0.11`, `dotnet build src/Worker/Zenkins.Worker.csproj -c Release`
  fails exactly as reported: `error NU1903: Warning As Error: Package 'Microsoft.DotNet.ILCompiler'
  10.0.11 has a known high severity vulnerability, https://github.com/advisories/GHSA-p3jp-9v83-76w9`
  (two occurrences, one per restore/build pass). With 10.0.14 the same command is clean. The audit is a
  function of the resolved version, not of the machine, so this is the same verdict the Windows box
  will report. Log: `artifacts/logs/b009-mutant-ilcompiler-10.0.11.log`.
- **macOS `project.assets.json`** (`src/Worker/obj/project.assets.json`):
  `Microsoft.DotNet.ILCompiler/10.0.14` and `runtime.osx-arm64.Microsoft.DotNet.ILCompiler/10.0.14`
  (SDK 10.0.403). The Windows number is **owed** — the expectation is 10.0.14, because the pin no longer
  consults the installed SDK.
- `dotnet build Zenkins.slnx -c Release` (macOS): 0 warnings, 0 errors —
  `artifacts/logs/b009-build-clean-10.0.14.log`. `TreatWarningsAsErrors` and NuGet audit untouched;
  no `NuGetAudit=false` anywhere.
- `dotnet list package --vulnerable --include-transitive`: `Zenkins.Worker` → "no vulnerable packages";
  solution-wide → no vulnerable packages in any of the six projects
  (`artifacts/logs/b009-vulnerable-worker.log`, `artifacts/logs/b009-vulnerable-solution.log`).
  Windows still owed.
- The full gate (`scripts/verify.sh`), including the two AOT suites that actually run `ilc` with the
  pinned compiler: `artifacts/logs/b004-q020-b009-gate.log`.
- Not verified: the Windows box itself (restore without `NuGetAudit=false`, its assets version, its
  `--vulnerable` output, and an AOT publish there), and the Linux runner.

## Progress 2026-11-16 — fixed and verified on Windows, awaiting the CI witness

`Directory.Packages.props` cannot carry the pin: the compiler arrives as an implicit
`PublishAot` reference, and central package management rejects both a `PackageVersion` for it
(NU1009) and an explicit versioned reference (NU1008). The version is therefore set through the item
the SDK itself reads — `KnownILCompilerPack`'s `ILCompilerPackVersion` in `Directory.Build.targets`,
fed by `ZenkinsILCompilerVersion` in `Directory.Build.props` — and that it takes effect was proven by
moving the number and watching `project.assets.json` follow.

10.0.14 is the choice: GHSA-p3jp-9v83-76w9 covers `>= 10.0.0, < 10.0.14`, and 10.0.14 is both the
first patched release on the line and the version this SDK band already ships, so machines that built
fine keep their exact compiler while the affected one moves to the fix. `global.json` keeps
`latestFeature` deliberately — narrowing it would reject the machines this fixes — because the
compiler no longer follows the installed SDK.

On the Windows box that could not build at all (`10.0.400` SDK, whose stock `ILCompilerPackVersion`
is the affected `10.0.11`):

- Plain `dotnet build Zenkins.slnx -c Release` → **0 warnings, 0 errors, no NU1903**, no
  `NuGetAudit=false`, `TreatWarningsAsErrors` untouched.
- `project.assets.json` → `Microsoft.DotNet.ILCompiler/10.0.14` plus the matching
  `Microsoft.NETCore.App.Runtime.NativeAOT.win-x64/10.0.14`.
- `dotnet list package --vulnerable --include-transitive` → nothing on any of the six projects.
- `dotnet publish src/Worker -r win-x64 -p:PublishAot=true` → a 13.7 MB native `PE32+` that prints
  `Zenkins Worker 1.0.0`. This was the residual risk the card carried — a new compiler against an old
  runtime pack — and it does not materialise: both sides are 10.0.14.

Environment note for anyone publishing AOT on Windows: the first attempt failed with `MSB3073` because
`findvcvarsall.bat` captured `'vswhere.exe' is not recognized` into the linker path; adding the Visual
Studio Installer directory to `PATH` fixed it. Nothing to do with this repository.
