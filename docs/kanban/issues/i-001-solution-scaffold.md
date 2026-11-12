---
id: I-001
title: Solution scaffold — .NET 10, .slnx, central package management, Server/Worker/Shared
type: chore
priority: P1
size: M
status: todo
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

A building, runnable three-project .NET 10 solution that every later card can land on without re-litigating project layout, package versions, or build settings.

## Background

Everything downstream blocks on this card. Keep it boring: no abstractions, no folders for code that does not exist yet. PRD constraint §8.10 — do not pre-build architecture for hypothetical needs.

Project layout:

```text
Zenkins.slnx
Directory.Build.props        # nullable, implicit usings, warnings-as-errors, LangVersion, deterministic
Directory.Packages.props     # central package versions
src/
  Server/    Zenkins.Server   (ASP.NET Core + Blazor Server)
  Worker/    Zenkins.Worker   (console host, no ASP.NET)
  Shared/    Zenkins.Shared   (contracts + tenant/git abstractions shared by both)
tests/
  Zenkins.Tests
```

`Shared` must not reference ASP.NET Core — the Worker stays a plain console app and must be publishable self-contained/trimmed later.

## Acceptance Criteria

- [ ] `Zenkins.slnx` exists and `dotnet build` succeeds at the repo root on .NET 10 SDK.
- [ ] Three projects under `src/` plus one test project under `tests/`, wired with the references above.
- [ ] Central package management: every `PackageReference` has no `Version`; all versions live in `Directory.Packages.props`.
- [ ] `Directory.Build.props` sets `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`, `TargetFramework=net10.0`.
- [ ] `Zenkins.Server` runs and serves a page; `Zenkins.Worker` runs and exits cleanly with a "not configured" message.
- [ ] `git status` is clean immediately after `dotnet build` (gitignore covers `bin/`, `obj/`, runtime data).
- [ ] `Shared` has no reference to `Microsoft.AspNetCore.*`.

## Subtasks

- [ ] Create `Directory.Build.props` and `Directory.Packages.props`.
- [ ] `dotnet new sln -n Zenkins -f slnx`; create `web`-less `classlib` for Shared, `web` for Server, `console` for Worker.
- [ ] Add `xunit` (v3) test project; verify `dotnet test` runs zero-but-green.
- [ ] Add project references and confirm the solution builds warning-free.
- [ ] Smoke-run both hosts, then revert any runtime state so the tree stays clean.

## Dependencies

- none

## Test Cases

### TC-001: Clean build from scratch

Steps:
1. `git clean -xdf` (or fresh clone)
2. `dotnet build -c Release`

Expected:
- Build succeeds with 0 warnings, 0 errors (warnings are errors).

### TC-002: Test project runs

Steps:
1. `dotnet test`

Expected:
- Exits 0. Zero (or placeholder) tests, no infrastructure errors.

### TC-003: Worker is not an ASP.NET app

Steps:
1. `dotnet list src/Worker/ package --include-transitive | grep -i aspnet`

Expected:
- No ASP.NET Core packages in the Worker's graph.

## Development Log

### 2026-11-12

- Card created. Not started.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `Zenkins.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `src/**`
- How to run/verify: `dotnet build -c Release && dotnet test`
- Results: not run yet
