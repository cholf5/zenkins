---
id: I-003
title: Self-contained publish and OS service packaging (Windows Service / systemd / launchd)
type: release
priority: P1
size: L
status: done
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

Server and Worker publish as self-contained, framework-free artifacts for Windows, Linux and macOS,
installable and runnable as Windows Service, systemd unit and launchd agent, with every published
artifact smoke-tested by running the real thing (PRD §10 scenario 7: no Docker dependency).

## Background

Roadmap v1.0 ("Self-contained publish + Windows Service / systemd / launchd packaging"). Nothing
publishes today beyond build output and `dotnet run`; every acceptance harness so far ran executables
from the build tree. Packaging changes the product: data paths (`App_Data`, `Artifacts`,
`work/{run-id}`), configuration (appsettings under a service's working directory) and lifecycle
(start on boot, restart on crash) all move. JIT self-contained is deliberately this card's packaging —
NativeAOT is a separate card (I-004) so AOT feasibility never blocks a v1.0 release path.

Decisions:

- **Per-RID publish matrix**: `win-x64`, `linux-x64`, `osx-arm64` (or `osx-x64` as measured — record
  the choice). One RID per publish; no RID guessing at runtime.
- **Server publishes as a self-contained folder** (Blazor Server ships `wwwroot` static web assets —
  a single-file Server is not a goal); **the Worker may additionally publish single-file**
  (`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`) for a one-exe drop on build machines.
- **Service hosting via the hosting layer** (`UseWindowsService()` / `UseSystemd()`, launchd plist for
  macOS), not hand-written service wrappers, so lifetimes and logging stay the framework's.
- **All persistent paths are configurable and resolve from `AppContext.BaseDirectory`** — never
  `Assembly.Location` (IL3000 under single-file, and I-004 will inherit the discipline).
- **Smoke test the published artifact, not `dotnet run`**: the published Server and the published
  Worker must complete Q-002's real scenario together (register, clone, `pipeline.lua`, logs,
  artifact).
- **Per-OS honesty**: service install/uninstall is verified on the OS at hand; OSes that could not be
  exercised are recorded explicitly (machine availability, not silence) and carried into DOC-003.

## Acceptance Criteria

- [x] `dotnet publish -c Release -r <rid>` succeeds for Server and Worker on all three RIDs with no
      trim/AOT warnings introduced (trimming stays off in this card) and no `IL3000` path usage.
- [x] The published Server boots with no .NET runtime installed on the machine, serves login and
      first-run Owner bootstrap, and all persistent paths (database, artifacts, work dirs) follow
      documented configuration rather than the source tree.
- [x] The published Worker registers against the published Server, heartbeats, executes a real
      `pipeline.lua` from a real repository, streams logs and produces a downloadable artifact.
- [x] Server runs as a service on each OS via the documented mechanism (Windows Service, systemd
      unit, launchd agent); start-on-boot and restart-on-crash behave as configured wherever that OS
      could be exercised, and unexercised OSes are recorded in the card, not assumed.
- [x] Configuration (connection strings, provider selection SQLite/PostgreSQL, ports, paths) is
      overridable per environment without rebuilding — the same artifact runs dev and production.
- [x] An upgrade of the published Server preserves data (database and artifacts survive a
      stop → replace → start cycle).

## Subtasks

- [x] Publish properties per project (RIDs, self-contained, Worker single-file variant) and a publish
      script or docs entry per OS.
- [x] Path/configuration audit: every persisted path resolves from configuration, `AppContext.BaseDirectory`
      based; provider selection (SQLite/PostgreSQL) works in published form.
- [x] Service packaging: Windows Service, systemd unit, launchd plist; install/uninstall commands.
- [x] Smoke harness: published Server + published Worker complete the Q-002 scenario from publish
      output alone.
- [x] Upgrade cycle test (stop → replace binaries → start → data intact).

## Dependencies

- none (Q-002 and Q-004 provide the harnesses this card reuses)

## Test Cases

### TC-001: Published pair completes a real build — PASS

Steps:
1. Publish Server and Worker for the current OS; run only the published binaries; bootstrap, create a
   project, trigger a run of a real repository's `pipeline.lua`.

Expected:
- Login, run, live logs and artifact download all work; no SDK or runtime on PATH is used.

Result (observed, 2026-11-14): PASS. The published pair (osx-arm64, apphost binaries run directly, no
`dotnet exec`, no `ASPNETCORE_ENVIRONMENT`) completes the full Q-002 scenario from publish output
alone: bootstrap, project, secret, a real repository's `pipeline.lua` (pure POSIX sh steps), per-step
logs, byte-identical artifact download, run page and log download. The suite lives in
`tests/Zenkins.Tests/Milestone/PublishedSmokeTests.cs` (Category=PublishSmoke) and also runs TC-003's
second-environment round with the single-file Worker. Data lands in `<publish>/App_Data` with no
CWD leakage. The independent review re-ran the publish matrix (3 RIDs × Server/Worker/Worker-single-
file: 9 artifacts, 0 warnings) and the smoke suite itself.

### TC-002: Service lifecycle on the current OS — PASS (macOS; Windows/Linux recorded unexercised)

Steps:
1. Install the Server as a service (Windows Service / systemd / launchd per OS); reboot or restart
   the service; kill the process and let the service manager restart it.

Expected:
- The service starts on boot, restarts after a crash, and the database/artifacts survive the cycle.

Result (observed, 2026-11-14): PASS on macOS, the OS at hand (scope ruling below). The launchd agent
installs via `deploy/macos/install-launchd.sh`: running + HTTP 200 with data under the home directory
(BaseDirectory discipline holds at CWD=/); kill -9 → launchd restarts with a new pid, HTTP 200, DB
files untouched; bootout → process gone, port closed, plist removed, definition gone, data kept per
design. Boot-time start (RunAtLoad) is configured but not exercised (no reboot); system-level
LaunchDaemon is left to DOC-003. Windows Service (`deploy/windows/install-server-service.ps1`) and
systemd (`deploy/systemd/zenkins-server.service`) are delivered and recorded as not executed on this
machine — carried to DOC-003 per the card's per-OS honesty rule.

### TC-003: Configuration without rebuild — PASS

Steps:
1. Publish once; run twice with different environment configuration (port, data paths, provider).

Expected:
- Both runs use their own configuration; the binaries are identical; no source-tree paths leak into
  the published run.

Result (observed, 2026-11-14): PASS. The same publish output ran twice with different environment
configuration (fixed port + three explicit path overrides); binaries hash-identical to the publish
artifacts, the configured database/store/keys land exactly where configured, no App_Data appears in
the install directory, and a full run completes with the single-file Worker. The review additionally
proved the appsettings contract experimentally: with the launchd plist pinning WorkingDirectory,
appsettings.json applies; without it, it is silently ignored — which is why every deploy script pins
WorkingDirectory or routes configuration through environment variables (the Windows script sets
ASPNETCORE_URLS through SCM service Environment).

## Development Log

### 2026-11-14

- Planned from roadmap v1.0. JIT self-contained only; the AOT variant is I-004 and must not couple to
  this card's acceptance. Smoke tests reuse Q-002's real-repo scenario against publish output.
- Implemented. Publish matrix via `scripts/publish.sh` (win-x64 / linux-x64 / osx-arm64 × Server
  folder, Worker folder, Worker single-file) — all succeed with zero warnings, trimming/AOT off; the
  RID is recorded as `osx-arm64` (the machine at hand). Path/configuration audit: every persistent
  path (default SQLite database, artifacts root, Data Protection keys, worker work root) resolves
  relative values from `AppContext.BaseDirectory` instead of the content root or CWD, pinned by
  `ReleasePackagingTests` (default/relative/absolute branches); the design-time EF factory
  deliberately keeps the source-tree base. Service hosting goes through the hosting layer
  (`UseWindowsService()`/`UseSystemd()`); macOS is a launchd agent.
- PM scope ruling (2026-11-14): macOS is verified in full (publish, smoke, launchd lifecycle,
  upgrade); win-x64/linux-x64 are verified as cross-compiled publish successes only; Windows Service
  / systemd install scripts are delivered but not executed here, and PostgreSQL in published form is
  untested — all recorded and carried into DOC-003.
- Gate semantics change: the smoke suite drives real publishes and processes, which made combined
  runs load-flaky once (two unrelated tests failed under contention, green in isolation; confirmed by
  the reviewer). The acceptance gate is now the per-suite runs — Fast 997, PublishSmoke 2, Milestone
  6, Recovery 4 (= 1009) — documented in `docs/development.md` §2; a no-filter combined run is
  explicitly not the gate.
- Upgrade semantics (AC-6): data lives beside the binaries (`<publish>/App_Data`); the upgrade cycle
  is stop → replace binaries keeping App_Data → start; deploy scripts state it, the smoke asserts it
  (Owner session survives, historical artifacts byte-identical, worker reconnects, new run succeeds).
- Independent review: PASS on all six ACs with its own re-runs (publish matrix, smoke, launchd
  lifecycle including kill -9 restart and clean bootout, plus an appsettings-discovery experiment),
  and seven findings; the fix round closed F1–F6 (suite split, ps1 -Port implemented via SCM service
  Environment + corrected header, docs §3/§6 rewritten for the new path semantics, four stale
  content-root comment claims corrected) and the re-verify confirmed fast 997/997 deterministic,
  PublishSmoke 2/2, no product behavior change. Two reviewer citations (plist header,
  ZenkinsDataOptions doc) were re-checked and withdrawn as over-attribution; dev's fix list matched
  the actual diff exactly.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | No product defect found. All findings were packaging/docs-level and fixed in-card: a load-flaky combined-suite gate (suite split), a Windows ps1 whose `-Port` was inert and whose header over-claimed appsettings (implemented via SCM service Environment, header corrected), docs/development.md sections contradicting the new path semantics (rewritten), and four stale content-root comment claims (reworded). | Closed | Windows/Linux service install and PostgreSQL-in-published-form remain honestly recorded as unexercised here, carried to DOC-003. |

## Verification

- Related files: `scripts/publish.sh`; `deploy/macos/com.zenkins.server.plist`,
  `deploy/macos/install-launchd.sh`; `deploy/windows/install-server-service.ps1`;
  `deploy/systemd/zenkins-server.service`; `src/Server/Program.cs`, `src/Server/Data/ZenkinsDataOptions.cs`,
  `src/Server/Data/DataServiceCollectionExtensions.cs`, `src/Server/Artifacts/ArtifactOptions.cs`,
  `src/Server/Artifacts/ArtifactsServiceCollectionExtensions.cs`, `src/Server/Secrets/SecretDataProtection.cs`,
  `src/Server/Zenkins.Server.csproj`; `src/Worker/Program.cs`, `src/Worker/WorkerConfiguration.cs`;
  `Directory.Packages.props`; `tests/Zenkins.Tests/Milestone/PublishedSmokeTests.cs`,
  `tests/Zenkins.Tests/ReleasePackagingTests.cs`, `tests/Zenkins.Tests/Milestone/MilestoneHost.cs`;
  `docs/development.md` §2/§3/§6.
- How to run/verify (per-suite gate, see docs/development.md §2): fast suite
  `dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke"`;
  `--filter "Category=PublishSmoke"`; `--filter "Category=Milestone"`; `--filter "Category=Recovery"`;
  publish matrix `scripts/publish.sh`; launchd install/restart/uninstall per `deploy/macos/`.
- Results (observed 2026-11-14; dev run + review re-runs + fix-round re-verify): fast 997/997 twice
  (deterministic after the split), PublishSmoke 2/2 twice, Milestone 6, Recovery 4, combined 1009;
  publish matrix 9 artifacts, 0 warnings; launchd install / kill -9 restart / clean uninstall
  verified twice (dev + review); upgrade cycle preserves data; Windows/Linux service install and
  PostgreSQL-in-published-form recorded unexercised (DOC-003).
