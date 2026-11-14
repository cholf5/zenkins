---
id: I-003
title: Self-contained publish and OS service packaging (Windows Service / systemd / launchd)
type: release
priority: P1
size: L
status: todo
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

- [ ] `dotnet publish -c Release -r <rid>` succeeds for Server and Worker on all three RIDs with no
      trim/AOT warnings introduced (trimming stays off in this card) and no `IL3000` path usage.
- [ ] The published Server boots with no .NET runtime installed on the machine, serves login and
      first-run Owner bootstrap, and all persistent paths (database, artifacts, work dirs) follow
      documented configuration rather than the source tree.
- [ ] The published Worker registers against the published Server, heartbeats, executes a real
      `pipeline.lua` from a real repository, streams logs and produces a downloadable artifact.
- [ ] Server runs as a service on each OS via the documented mechanism (Windows Service, systemd
      unit, launchd agent); start-on-boot and restart-on-crash behave as configured wherever that OS
      could be exercised, and unexercised OSes are recorded in the card, not assumed.
- [ ] Configuration (connection strings, provider selection SQLite/PostgreSQL, ports, paths) is
      overridable per environment without rebuilding — the same artifact runs dev and production.
- [ ] An upgrade of the published Server preserves data (database and artifacts survive a
      stop → replace → start cycle).

## Subtasks

- [ ] Publish properties per project (RIDs, self-contained, Worker single-file variant) and a publish
      script or docs entry per OS.
- [ ] Path/configuration audit: every persisted path resolves from configuration, `AppContext.BaseDirectory`
      based; provider selection (SQLite/PostgreSQL) works in published form.
- [ ] Service packaging: Windows Service, systemd unit, launchd plist; install/uninstall commands.
- [ ] Smoke harness: published Server + published Worker complete the Q-002 scenario from publish
      output alone.
- [ ] Upgrade cycle test (stop → replace binaries → start → data intact).

## Dependencies

- none (Q-002 and Q-004 provide the harnesses this card reuses)

## Test Cases

### TC-001: Published pair completes a real build

Steps:
1. Publish Server and Worker for the current OS; run only the published binaries; bootstrap, create a
   project, trigger a run of a real repository's `pipeline.lua`.

Expected:
- Login, run, live logs and artifact download all work; no SDK or runtime on PATH is used.

### TC-002: Service lifecycle on the current OS

Steps:
1. Install the Server as a service (Windows Service / systemd / launchd per OS); reboot or restart
   the service; kill the process and let the service manager restart it.

Expected:
- The service starts on boot, restarts after a crash, and the database/artifacts survive the cycle.

### TC-003: Configuration without rebuild

Steps:
1. Publish once; run twice with different environment configuration (port, data paths, provider).

Expected:
- Both runs use their own configuration; the binaries are identical; no source-tree paths leak into
  the published run.

## Development Log

### 2026-11-14

- Planned from roadmap v1.0. JIT self-contained only; the AOT variant is I-004 and must not couple to
  this card's acceptance. Smoke tests reuse Q-002's real-repo scenario against publish output.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Zenkins.Server.csproj`, `src/Worker/Zenkins.Worker.csproj`,
  `Directory.Build.props`/`Directory.Build.targets`, `src/Server/Program.cs` (hosting lifetimes),
  path/configuration resolution sites across `src/**`
- How to run/verify: `dotnet publish -c Release -r <rid>` per matrix; service install per OS docs;
  smoke harness; `dotnet test -c Release` full suite.
- Results: not run (card not started).
