---
id: I-001
title: Solution scaffold — .NET 10, .slnx, central package management, Server/Worker/Shared
type: chore
priority: P1
size: M
status: review
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

- [x] `Zenkins.slnx` exists and `dotnet build` succeeds at the repo root on .NET 10 SDK.
- [x] Three projects under `src/` plus one test project under `tests/`, wired with the references above.
- [x] Central package management: every `PackageReference` has no `Version`; all versions live in `Directory.Packages.props`.
- [x] `Directory.Build.props` sets `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`, `TargetFramework=net10.0`.
- [x] `Zenkins.Server` runs and serves a page; `Zenkins.Worker` runs and exits cleanly with a "not configured" message.
- [x] `git status` is clean immediately after `dotnet build` (gitignore covers `bin/`, `obj/`, runtime data).
- [x] `Shared` has no reference to `Microsoft.AspNetCore.*`.

## Subtasks

- [x] Create `Directory.Build.props` and `Directory.Packages.props`.
- [x] `dotnet new sln -n Zenkins -f slnx`; create `web`-less `classlib` for Shared, `web` for Server, `console` for Worker.
- [x] Add `xunit` (v3) test project; verify `dotnet test` runs zero-but-green.
- [x] Add project references and confirm the solution builds warning-free.
- [x] Smoke-run both hosts, then revert any runtime state so the tree stays clean.

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

Scaffolded the solution from templates, then trimmed them back.

- `dotnet new sln -n Zenkins -f slnx`, then `classlib` (`src/Shared`), `blazor --interactivity Server --empty` (`src/Server`), `console` (`src/Worker`), and a hand-written `xunit.v3` project (`tests/Zenkins.Tests`). `dotnet sln add` groups per project folder in `.slnx`, so the file was rewritten to two folders: `/src/` and `/tests/`.
- `Directory.Build.props`: `TargetFramework=net10.0`, `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `Deterministic=true`, plus `ContinuousIntegrationBuild` when `CI` is set. Target frameworks were removed from every `.csproj` so this file is the only source.
- `Directory.Packages.props`: `ManagePackageVersionsCentrally=true`, one pinned version (`xunit.v3` 4.0.3). The only `PackageReference` in the tree is versionless; `Shared` and `Worker` have no packages at all.
- References: `Server → Shared`, `Worker → Shared`, `Tests → Shared`. The Worker reference is what makes the "Shared must not drag ASP.NET Core in" rule meaningful, so it exists from day one even though Shared is still empty.
- `Shared` is deliberately empty (template `Class1.cs` deleted). No contract type exists yet; inventing one would be exactly the speculation §8.10 forbids. It still builds and is referenced by both hosts.
- Server kept the Blazor Server wiring (`AddRazorComponents().AddInteractiveServerComponents()`, `MapRazorComponents<App>().AddInteractiveServerRenderMode()`, `UseAntiforgery`, `MapStaticAssets`) with a single `Pages/Home.razor`. Everything else the template generated — `Layout/MainLayout`, `ReconnectModal`, `Pages/Error`, `Pages/NotFound`, scoped `.razor.css`, `_Imports` extras — was deleted, because D-001 owns the shell and those files would be rewritten on day one. `Program.cs` also dropped the template's `UseExceptionHandler("/Error")`, `UseStatusCodePagesWithReExecute("/not-found")` (both pointed at the deleted pages) and `UseHttpsRedirection` (TLS termination is a deployment concern; no https listener is configured in this card).
- `wwwroot/app.css` reduced to a four-line placeholder so `MapStaticAssets` has something to serve; D-001 replaces it with compiled Tailwind.
- Worker is a plain console process: zero package references, no `Microsoft.Extensions.Hosting`, `Main` returns 0. When `ZENKINS_SERVER_URL` is unset it prints `Zenkins Worker is not configured: set ZENKINS_SERVER_URL to the Zenkins Server base URL.` and exits. Exit code 0 is deliberate while there is no real host — nothing failed, there was simply nothing to do; revisit when the v0.2 worker host lands and a supervisor needs a failure signal.
- Tests carry one real assertion rather than a `Assert.True(true)` placeholder: `Shared_assembly_does_not_reference_aspnetcore` loads `Zenkins.Shared` by name and fails if any referenced assembly starts with `Microsoft.AspNetCore`. That turns acceptance criterion 7 into a permanent regression guard, and `dotnet test` is green with one test.
- Test runner: `xunit.v3` 4.0.3 ships a Microsoft.Testing.Platform (MTP) runner. On the .NET 10 SDK, `dotnet test` refuses to route MTP apps through the retired VSTest bridge (`Testing with VSTest target is no longer supported...`), and the `<TestingPlatformDotnetTestSupport>` property makes it worse (it imports the VSTest override). The supported opt-in is `global.json` with `"test": { "runner": "Microsoft.Testing.Platform" }`, so `global.json` was added. It also pins SDK `10.0.403` with `rollForward: latestFeature`, which is the version the card was verified on. `xunit.runner.visualstudio` was therefore not needed and is not referenced.
- `src/Server/appsettings.Development.json` generated by the template is ignored by the existing root `.gitignore` (local-override rule), so it was deleted instead of force-added: only `appsettings.json` is committed.
- No entities, no EF Core, no auth, no extra folders. Nothing was added to `.gitignore`; the existing rules already cover `bin/`, `obj/`, `App_Data/`, `*.db`, `logs/` (verified with `git status --short` after a build).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `Zenkins.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `src/**`, `tests/Zenkins.Tests/**`
- How to run/verify: `dotnet build -c Release && dotnet test`
- Results (2026-11-12, SDK 10.0.403, osx-arm64). Logs in `/tmp/zenkins-logs/I-001/`:
  - TC-001 clean tree: copied the tree without `bin/`, `obj/` or `.git` to `/tmp/zenkins-clean`, then `dotnet build -c Release` → `Build succeeded. 0 Warning(s) 0 Error(s)`; `dotnet test` → `total: 1, failed: 0, succeeded: 1`, exit 0 (`clean-build.log`).
  - TC-002 in place: `dotnet test` → exit 0, one test passed (`test-4.log`).
  - TC-003: `dotnet list src/Worker/ package --include-transitive | grep -i aspnet` → no output, grep exit 1; Worker reports `[net10.0]: No packages were found for this framework` (`worker-packages-clean.log`). Same for `src/Shared`.
  - Server smoke: `ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet run --project src/Server -c Release --no-launch-profile` → `HTTP 200`, body contains `<h1>Zenkins</h1>`, then `Application is shutting down` (`server-smoke.log`, `server-page.html`).
  - Worker smoke: `dotnet run --project src/Worker -c Release --no-launch-profile` with `ZENKINS_SERVER_URL` unset → exit 0, prints the "not configured" message (`worker-smoke.log`).
  - `git status --short` after `dotnet build` → no output once the scaffold is committed; build output lands only in gitignored `bin/`/`obj/`.
