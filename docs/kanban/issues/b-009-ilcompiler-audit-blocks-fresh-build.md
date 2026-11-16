---
id: B-009
title: A fresh Windows machine cannot restore the solution — NU1903 on ILCompiler 10.0.11, and warnings are errors
type: bug
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: windows: reproducible there; not on macOS
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

- [ ] The ILCompiler version is pinned by the repository (central package management) or by an
      explicit SDK pin, so two machines with different installed feature bands resolve the same
      compiler.
- [ ] A clean `dotnet build Zenkins.slnx -c Release` succeeds on Windows without
      `-p:NuGetAudit=false`, and `dotnet list package --vulnerable --include-transitive` reports
      nothing for the Worker on both Windows and macOS.
- [ ] `TreatWarningsAsErrors` stays on: the fix is a correct dependency, not a suppressed audit.
- [ ] If `global.json`'s roll-forward is what lets the drift in, narrow it and record why; if pinning
      ILCompiler is enough, record that instead.
- [ ] CI keeps building green on all three runners after the change.

## Subtasks

- [ ] Pin `Microsoft.DotNet.ILCompiler` (or the runtime/toolchain set) to a version with no advisory.
- [ ] Verify on the Windows box and on macOS; record both `project.assets.json` versions.
- [ ] Decide and document the `global.json` roll-forward policy.

## Dependencies

- none

## Notes and Decisions

- Not filed as a security emergency: the advisory is against the AOT compiler, and the exposure path
  (what an attacker must control) has not been established. Recorded here so the version is chosen
  deliberately rather than by whichever machine built the artifact.
- The workaround is deliberately **not** written into `docs/` as the supported way to build: a build
  that needs audit disabled is a build that would ship the vulnerable compiler.
