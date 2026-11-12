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
- `wwwroot/app.css` reduced to a 10-line placeholder so `MapStaticAssets` has something to serve; D-001 replaces it with compiled Tailwind.
- Worker is a plain console process: zero package references, no `Microsoft.Extensions.Hosting`, `Main` returns 0. When `ZENKINS_SERVER_URL` is unset it prints `Zenkins Worker is not configured: set ZENKINS_SERVER_URL to the Zenkins Server base URL.` and exits. Exit code 0 is deliberate while there is no real host — nothing failed, there was simply nothing to do; revisit when the v0.2 worker host lands and a supervisor needs a failure signal.
- Tests are not a `Assert.True(true)` placeholder: `Shared_assembly_does_not_reference_aspnetcore` loads `Zenkins.Shared` by name and fails if any referenced assembly starts with `Microsoft.AspNetCore`. That check is secondary, not the guard: `Assembly.GetReferencedAssemblies()` only lists assemblies reached through used types, so it stays green if a `FrameworkReference` is added (see the review fix below).
- Test runner: `xunit.v3` 4.0.3 ships a Microsoft.Testing.Platform (MTP) runner. On the .NET 10 SDK, `dotnet test` refuses to route MTP apps through the retired VSTest bridge (`Testing with VSTest target is no longer supported...`), and the `<TestingPlatformDotnetTestSupport>` property makes it worse (it imports the VSTest override). The supported opt-in is `global.json` with `"test": { "runner": "Microsoft.Testing.Platform" }`, so `global.json` was added. It also pins the SDK floor at `10.0.100` with `rollForward: latestFeature`, so any installed 10.0.x feature band is accepted. `xunit.runner.visualstudio` was therefore not needed and is not referenced.
- `src/Server/appsettings.Development.json` generated by the template is ignored by the existing root `.gitignore` (local-override rule), so it was deleted instead of force-added: only `appsettings.json` is committed.
- No entities, no EF Core, no auth, no extra folders. Nothing was added to `.gitignore`; the existing rules already cover `bin/`, `obj/`, `App_Data/`, `*.db`, `logs/` (verified with `git status --short` after a build).

#### Review fix (2026-11-12, after FAIL verdict)

Two blocking findings were verified and fixed; nothing else changed.

- **SDK floor was too high.** `global.json` pinned `10.0.403`, which makes the repo unbuildable for anyone on an older feature band (`A compatible .NET SDK was not found`). It is now `"version": "10.0.100"` with `"rollForward": "latestFeature"`. Verified on the installed `10.0.403` (builds, `dotnet --version` resolves to 10.0.403) and, in a copy of the tree pinned to `10.0.101` / `latestPatch` — the other SDK installed on this machine — build is 0 warnings / 0 errors and both tests pass.
- **AC7 guard was blind to the realistic failure.** Adding `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `Zenkins.Shared.csproj` leaves both `Assembly.GetReferencedAssemblies()` and `dotnet list package --include-transitive` unchanged, so the previous single test stayed green and the log overclaimed it as a "permanent guard". Now there are two checks that do see it:
  - `Directory.Build.targets` → target `Zenkins_RejectAspNetCoreFrameworkReference`, `BeforeTargets="CoreCompile"`, active only for `Zenkins.Shared`. It batches over `@(FrameworkReference)` and errors on any `Microsoft.AspNetCore*` identity, naming the project and the offending framework. Because it is evaluation-time MSBuild, it fails the build instead of reporting after the fact.
  - `tests/Zenkins.Tests/WorkerRuntimeConfigTests.cs` → `Worker_runtimeconfig_does_not_include_aspnetcore_frameworks` reads `src/Worker/bin/<Configuration>/<tfm>/Zenkins.Worker.runtimeconfig.json` (located from `AppContext.BaseDirectory` upward to the `Zenkins.slnx` root, never a hardcoded path) and asserts no `Microsoft.AspNetCore*` framework. It reads all three SDK shapes: `framework` (single), `frameworks` (plural) and `includedFrameworks` (self-contained publish) — the singular form was found during review and would otherwise have made the test vacuous in the one-framework case. If the Worker output is missing, the test skips with an explanatory message rather than passing silently.
  - `tests/Zenkins.Tests/Zenkins.Tests.csproj` gained a build-order-only `ProjectReference` to `Zenkins.Worker` (`ReferenceOutputAssembly="false"`) so plain `dotnet test` always builds the Worker and exercises the runtimeconfig check instead of skipping it.
  - Non-vacuity was proven, not assumed: with the `FrameworkReference` present, `dotnet build -c Release` fails with the guard error; with the guard target temporarily disabled (so the Worker can be produced) `dotnet test -c Release` fails the runtimeconfig test. Both edits were reverted and both are green again.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `Zenkins.slnx`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json`, `src/**`, `tests/Zenkins.Tests/**`
- How to run/verify: `dotnet build -c Release && dotnet test`
- AC7 specifically — this is the check that actually works:
  - `dotnet build -c Release`: the `Zenkins_RejectAspNetCoreFrameworkReference` target in `Directory.Build.targets` fails the Shared build if `@(FrameworkReference)` contains any `Microsoft.AspNetCore*`, e.g. `error : Zenkins.Shared: FrameworkReference 'Microsoft.AspNetCore.App' is not allowed. ...`.
  - `dotnet test`: `Worker_runtimeconfig_does_not_include_aspnetcore_frameworks` parses `src/Worker/bin/<Configuration>/<tfm>/Zenkins.Worker.runtimeconfig.json` and fails if any listed framework starts with `Microsoft.AspNetCore`.
  - `dotnet list src/Worker/ package --include-transitive | grep -i aspnet` is **not** sufficient on its own: a `FrameworkReference` is not a package, so it reports nothing even when the ASP.NET framework is present. It only proves the Worker has no NuGet-level ASP.NET dependency.
- Reproduce (from the repo root, `zsh`):

  ```zsh
  mkdir -p /tmp/zenkins-logs/I-001
  dotnet build -c Release 2>&1 | tee /tmp/zenkins-logs/I-001/build.log
  dotnet test 2>&1 | tee /tmp/zenkins-logs/I-001/test.log
  dotnet list src/Worker/ package --include-transitive 2>&1 | tee /tmp/zenkins-logs/I-001/worker-packages.log

  # Server smoke: serve, fetch, stop
  ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet run --project src/Server -c Release --no-launch-profile &
  curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5099/   # expect 200
  kill %1

  # Worker smoke: expect exit 0 and the "not configured" message
  env -u ZENKINS_SERVER_URL dotnet run --project src/Worker -c Release --no-launch-profile

  # Clean-tree build (TC-001): copy without bin/obj/.git
  rsync -a --exclude '.git' --exclude 'bin' --exclude 'obj' ./ /tmp/zenkins-clean2/
  cd /tmp/zenkins-clean2 && dotnet build -c Release && dotnet test
  ```

  Red/green proof for the AC7 guards: add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `src/Shared/Zenkins.Shared.csproj` → `dotnet build -c Release` must fail with the guard error; then temporarily disable the guard target (rename its `MSBuildProjectName` condition), build, and `dotnet test -c Release` must fail the runtimeconfig test. Revert both edits (and rebuild) and both commands pass again.
- Results (2026-11-12; SDK 10.0.403 and 10.0.101; **only osx-arm64 was exercised** — Windows and Linux were not run for this card). Logs in `/tmp/zenkins-logs/I-001/`:
  - TC-001 clean tree: copied the tree without `bin/`, `obj/` or `.git` to `/tmp/zenkins-clean2`, then `dotnet build -c Release` → `Build succeeded. 0 Warning(s) 0 Error(s)`; `dotnet test` → `total: 2, failed: 0, succeeded: 2, skipped: 0`, exit 0 (`clean-build2.log`).
  - TC-002 in place: `dotnet test` → exit 0, `total: 2, succeeded: 2, skipped: 0` (`test-gate7.log`).
  - TC-003: `dotnet list src/Worker/ package --include-transitive` → `[net10.0]: No packages were found for this framework`, and `grep -i aspnet` finds nothing (`worker-packages-gate2.log`). Same for `src/Shared`.
  - AC7 guard, red: `FrameworkReference` temporarily added to Shared → `dotnet build -c Release` exit 1 with `error : Zenkins.Shared: FrameworkReference 'Microsoft.AspNetCore.App' is not allowed.` (`guard-red3.log`).
  - AC7 test, red: guard target temporarily disabled so the Worker could be produced with the reference → Worker runtimeconfig lists `Microsoft.NETCore.App` + `Microsoft.AspNetCore.App`, `dotnet test -c Release` exit 2, `failed: 1, succeeded: 1` (`guard-bypass3-build.log`, `test-red3.log`).
  - AC7 guard + test, green after revert: build 0 warnings / 0 errors (`build-gate5.log`), `total: 2, succeeded: 2` (`test-gate7.log`).
  - SDK floor: `global.json` `10.0.100`/`latestFeature` resolves to the installed `10.0.403` and builds (`build-gate5.log`); a copy pinned to `10.0.101`/`latestPatch` (`dotnet --version` → 10.0.101) also builds with 0 warnings / 0 errors and passes both tests (`sdk-101-build.log`, `sdk-101-test.log`).
  - Server smoke: `ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet run --project src/Server -c Release --no-launch-profile` → `HTTP 200`, body contains `<h1>Zenkins</h1>`, then `Application is shutting down` (`server-smoke.log`, `server-page.html`).
  - Worker smoke: `dotnet run --project src/Worker -c Release --no-launch-profile` with `ZENKINS_SERVER_URL` unset → exit 0, prints the "not configured" message (`worker-smoke.log`).
  - `git status --short` after `dotnet build` → no output once the scaffold is committed; build output lands only in gitignored `bin/`/`obj/`.
