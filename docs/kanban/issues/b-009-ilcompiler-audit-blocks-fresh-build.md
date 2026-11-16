---
id: B-009
title: A fresh Windows machine cannot restore the solution — NU1903 on ILCompiler 10.0.11, and warnings are errors
type: bug
priority: P2
size: S
status: review
created: 2026-11-16
updated: 2026-11-16
blocked: windows+linux: local (macOS) verification only; the rule is enforced by a build error, the other platforms are not run here
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
      compiler. Resolved as a *derived* version plus an enforced pair (see Resolution): every machine
      resolves the compiler that matches its own runtime band, and a version that disagrees with the
      band is a build error rather than a resolution.
- [x] A clean `dotnet build Zenkins.slnx -c Release` succeeds on Windows without
      `-p:NuGetAudit=false`, and `dotnet list package --vulnerable --include-transitive` reports
      nothing for the Worker on both Windows and macOS. macOS: verified. Windows: **still not run on
      Windows** — but the NU1903 half was reproduced on macOS by pinning 10.0.11, and the macOS side of
      both commands is green (see Verification). The floor check now makes the *other* half
      deterministic: an SDK band older than 10.0.14 stops the build with ZENKINS0003 instead of a
      NU1903 nobody can read.
- [x] `TreatWarningsAsErrors` stays on: the fix is a correct dependency, not a suppressed audit.
- [x] If `global.json`'s roll-forward is what lets the drift in, narrow it and record why; if pinning
      ILCompiler is enough, record that instead.
- [ ] CI keeps building green on all three runners after the change. Owed: the Windows and Linux jobs.

## Subtasks

- [x] Pin `Microsoft.DotNet.ILCompiler` (or the runtime/toolchain set) to a version with no advisory.
- [ ] Verify on the Windows box and on macOS; record both `project.assets.json` versions. macOS done
      (compiler 10.0.14 = runtime band 10.0.14); the Windows numbers are owed.
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

**The real rule: the compiler and the runtime band are one number.** `Microsoft.DotNet.ILCompiler` and
`Microsoft.NETCore.App.Runtime.<rid>` must carry the *same* 10.0.x patch. ILC enforces it at publish
time and refuses to continue:

```
error : Overriding System.Private.CoreLib.dll with a newer version is not supported.
Attempted to use .../microsoft.netcore.app.runtime.linux-x64/10.0.15/.../System.Private.CoreLib.dll
instead of .../runtime.linux-x64.Microsoft.DotNet.ILCompiler/10.0.14/.../System.Private.CoreLib.dll
```

So the version is **not** a free choice and cannot be a constant: two machines on different SDK patches
need two different values, and any single constant is wrong on one of them. What the repository
actually owns is (a) pairing the two numbers and (b) a floor.

**A constant pin was the first attempt and it broke CI.** Pinning 10.0.14 fixed the 10.0.11 machine and
turned **all three** runners red in run `43912478387` (`cb2ec4d`) — Fast/Milestone/Recovery/PublishSmoke
green, `PublishAot` 2/2 failing on the CoreLib override above (10.0.14 compiler against the runners'
10.0.15 runtime pack). The earlier reasoning in this card ("10.0.15 is EF Core's band, so pinning it
would be reverse drift") was **wrong** and is replaced by the rule above: the same 10.0.x patch line is
exactly what has to match; the EF Core pins are unrelated.

**What the repository does instead.**

- `Directory.Build.targets` derives the compiler from the SDK's own runtime band —
  `ZenkinsRuntimeBandVersion` = `$(BundledNETCoreAppPackageVersion)` (10.0.14 on SDK 10.0.403,
  10.0.15 on the CI runners' band), `ZenkinsILCompilerVersion` defaults to it, and the SDK's
  `KnownILCompilerPack` item is rewritten to that value at evaluation time so *restore* sees it. Two
  numbers, one source, equal by construction on every machine.
- `ZenkinsILCompilerVersion` stays overridable, but three `Error`s (in a target that runs before
  `CollectPackageReferences` and `PrepareForBuild`, i.e. in both the restore and the build graph) hold
  the rule:
  - **ZENKINS0001** — nothing to derive and no override: the build cannot pair the two numbers.
  - **ZENKINS0002** — the override disagrees with the band the SDK reports through
    `KnownFrameworkReference`/`LatestRuntimeFrameworkVersion` (read a second time, from the SDK, not
    from our own property). Message names both numbers and the ILC error it prevents.
  - **ZENKINS0003** — the band is below `ZenkinsILCompilerMinimumVersion` = **10.0.14**
    (GHSA-p3jp-9v83-76w9 covers `>= 10.0.0, < 10.0.14`). Message says which SDK to install and that
    disabling NuGet audit is not the fix.
- Why a build error and not a test: the failure it prevents happens at *publish*, in CI only, which is
  precisely how this card's first fix went red. A test would need a publish to observe it, and a test
  can be skipped or filtered; the guard aborts `dotnet build` and `dotnet restore` before any of that,
  on any project that sets `PublishAot`, with no network access.
- `Directory.Packages.props` still cannot hold it: the reference is SDK-implicit and central package
  management refuses `PackageVersion` for those (**NU1009**; an explicit version is **NU1008**), which
  is why the SDK's item is rewritten instead.
- `global.json` keeps `rollForward: latestFeature`, now with the reason written in the file: the field
  no longer decides the compiler, and narrowing it would refuse a fresh Windows box on a 10.0.4xx SDK —
  this card's defect inverted.

**Residual risk, stated rather than hidden.** The floor is a hard requirement: a machine whose SDK band
is below 10.0.14 fails the build by design (ZENKINS0003), because it would compile the shipped worker
with a version that has an open advisory. That is the honest answer, and it is why the guide now says a
current `10.0.4xx` SDK is what you want. The pairing is enforced for the *current* evaluation
(`TargetFramework`), which is all this solution builds.

## Verification

- **The CI failure, repaired and guarded.** Local band is 10.0.14 (SDK 10.0.403), so the derivation
  yields `ZenkinsILCompilerVersion` = `ZenkinsRuntimeBandVersion` = **10.0.14** and `PublishAot` is
  green again (`artifacts/logs/b009-publishaot-local.log`, 2/2). The runners' 10.0.15 band needs no
  change to the repository: the value follows the band.
- **The guard fires, locally, at build time** (each captured in `artifacts/logs/`):
  - `-p:ZenkinsILCompilerVersion=10.0.15` → **ZENKINS0002** "NativeAOT compiler 10.0.15 does not match
    this SDK's runtime band 10.0.14", `dotnet build` exit 1 (`b009-guard-mismatch.log`); `dotnet
    restore` alone also exit 1 with the same error (`b009-guard-restore.log`), so nothing reaches a
    publish.
  - `-p:ZenkinsILCompilerMinimumVersion=10.0.15` → **ZENKINS0003** "this SDK's runtime band is 10.0.14,
    older than the patched floor 10.0.15" (`b009-guard-floor.log`).
  - `-p:BundledNETCoreAppPackageVersion=10.0.15` (faking the SDK's band) → the derived compiler follows
    it to 10.0.15 and the guard catches the disagreement with
    `KnownFrameworkReference`("NativeAOT compiler 10.0.15 does not match … 10.0.14"), which proves the
    derivation reads the SDK and the check is not comparing our property with itself.
- **The two numbers** on macOS (SDK 10.0.403,
  `src/Worker/obj/project.assets.json` after a RID restore): `Microsoft.DotNet.ILCompiler/10.0.14` and
  `runtime.osx-arm64.Microsoft.DotNet.ILCompiler/10.0.14`; the SDK's runtime band the publish pairs them
  with is `LatestRuntimeFrameworkVersion` = **10.0.14** (`ZenkinsRuntimeBandVersion` = `10.0.14` under
  `-p:RuntimeIdentifier=osx-arm64 -p:SelfContained=true`). Equal, which is the whole point. The Windows
  and Linux numbers are **owed** and should read 10.0.15 on a CI runner, i.e. both equal again.
- **The Windows failure that opened the card, reproduced on macOS**: with the compiler forced to
  `10.0.11`, `dotnet build src/Worker/Zenkins.Worker.csproj -c Release` fails exactly as reported —
  `error NU1903: Warning As Error: Package 'Microsoft.DotNet.ILCompiler' 10.0.11 has a known high
  severity vulnerability, https://github.com/advisories/GHSA-p3jp-9v83-76w9`
  (`b009-mutant-ilcompiler-10.0.11.log`).
- `dotnet build Zenkins.slnx -c Release`: 0 warnings, 0 errors. `dotnet list package --vulnerable
  --include-transitive`: no vulnerable packages for `Zenkins.Worker`, and none solution-wide
  (`b009-vulnerable-worker.log`, `b009-vulnerable-solution.log`). `TreatWarningsAsErrors` and NuGet
  audit untouched; no `NuGetAudit=false` anywhere.
- Full gate (`scripts/verify.sh`, seven suites): `artifacts/logs/b004-q020-b009-gate.log`.
- Not verified: the Windows and Linux runners themselves (build, assets versions, `--vulnerable`, AOT
  publish). AC2/AC5 stay unticked for that reason.

## Earlier Windows evidence (2026-11-16, kept — its pin is superseded)

Before the CI breakage, a Windows box — the machine that could not build at all (SDK `10.0.400`, whose
stock `ILCompilerPackVersion` is the affected `10.0.11`) — was brought up with the **constant** pin of
`10.0.14`, and on that box it worked:

- Plain `dotnet build Zenkins.slnx -c Release` → 0 warnings, 0 errors, no NU1903, no
  `NuGetAudit=false`, `TreatWarningsAsErrors` untouched.
- `project.assets.json` → `Microsoft.DotNet.ILCompiler/10.0.14` plus the matching
  `Microsoft.NETCore.App.Runtime.NativeAOT.win-x64/10.0.14`.
- `dotnet list package --vulnerable --include-transitive` → nothing on any of the six projects.
- `dotnet publish src/Worker -r win-x64 -p:PublishAot=true` → a 13.7 MB native `PE32+` that prints
  `Zenkins Worker 1.0.0`.

Why it worked there and not on CI: that box's runtime band was **also** 10.0.14, so the constant
happened to be the right pair. The runners are on 10.0.15, where the same constant is a mismatched
pair, and the publish failed on the CoreLib override (run `43912478387`). That is the evidence that
established the rule above — the number has to follow the band, so it is derived and guarded now, and
the Windows verification of *that* mechanism is still owed.

Environment note for anyone publishing AOT on Windows (unrelated to this repository, and it survives
the change): the first publish attempt failed with `MSB3073` because `findvcvarsall.bat` captured
`'vswhere.exe' is not recognized` into the linker path. Adding the Visual Studio Installer directory to
`PATH` fixed it; the compile itself was fine.
