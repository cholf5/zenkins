---
id: Q-007
title: Cross-platform verification on real Windows and Linux machines
type: test
priority: P1
size: L
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

PRD §10 scenarios 2 and 7 are proven on real hardware rather than on one laptop: a published Server
installs and runs as a Windows Service, and as a systemd unit; a Worker from each of those operating
systems registers against its Server, executes a real `pipeline.lua`, and tag-restricted work lands on
the right platform. Only then may "跨平台构建 / 跨平台部署" be called verified.

## Background

I-003 shipped the packaging and verified exactly one OS: macOS (launchd, install / `kill -9` restart /
clean bootout). `win-x64` and `linux-x64` were cross-compiled publish successes only, and
`deploy/windows/install-server-service.ps1` and `deploy/systemd/zenkins-server.service` were delivered
and recorded as **not executed** — DOC-003 documents that honestly, which is why the gap is visible
instead of assumed. Every acceptance harness so far ran on macOS.

Two machines make closing this cheap, both already on this account (see `list_machines`):

- Windows — `WINDOWS-DEV-BOX`, Windows 10.0.19045 (Intel Ultra 7 265), available.
- Linux — the `linux-cloud-host` SSH host (203.0.113.10, ubuntu, x86_64), reachable.

The card records what these machines actually are and what was actually run on them; it does not
assume either is usable until the environment gate below passes.

Decisions:

- **Environment gate first, before any implementation claim.** Windows Service creation needs
  elevation (`sc.exe create` / `New-Service` fail without it) and the systemd path needs SSH access
  plus sudo. The first deliverable is a one-line answer per machine: can we install a service here,
  yes or no, with evidence. If a machine cannot be used, record precisely what is missing (elevation,
  credentials, network) and stop — a static walkthrough is not a substitute and must not be dressed
  as one.
- **Real installs on real paths.** The published artifacts from `scripts/publish.sh` are transferred
  and installed into a normal directory on each machine; the target has no repository and no SDK, so
  this simultaneously re-proves I-003's "no SDK needed" claim on an OS that never ran it.
- **The service manager owns the process.** Start-on-boot is proven by the service's own
  configuration (`StartType auto` / `systemctl is-enabled`), restart-on-crash by killing the process
  and watching the service manager bring it back — not by a script that restarts it.
- **Clean up and leave the machines as found.** Services uninstalled, test directories removed, no
  processes left running, and a short note of what was deliberately left (nothing, or why).
- **Record, don't round up.** Each machine's OS build, the exact commands, and every deviation go in
  the card. Anything not exercised stays labelled.

## Acceptance Criteria

- [ ] **Windows Service**: the published Server installs via `deploy/windows/install-server-service.ps1`
      into a real directory, the service is registered with automatic start, it starts and serves
      `/account/login`, the first-run Owner bootstrap works, and all data lands under
      `<InstallDir>\App_Data` (nothing in `System32`, nothing in the repository). Killing the process
      makes the service manager restart it (the `sc.exe failure` actions work), and `-Uninstall`
      removes the service registration cleanly.
- [ ] **systemd unit**: with the `zenkins` service user created, the published Server runs as
      `zenkins-server.service` under `ProtectSystem=strict` with `ReadWritePaths` covering the install
      directory (the startup write probes pass), `systemctl is-enabled` reports enabled, it serves
      login and bootstrap, `systemctl restart` preserves the database, and uninstall leaves only the
      documented residue.
- [ ] **A Worker from each OS completes a real build**: the published Worker for that platform
      registers against that Server, heartbeats, clones a real repository, executes a real
      `pipeline.lua`, streams logs and uploads an artifact that downloads byte-identically; one run is
      cancelled mid-step and ends honestly `Canceled`. No SDK and no repository checkout on the target.
- [ ] **Tag placement across platforms**: with a Worker on each OS declaring its platform tag, a run
      whose required tags name one platform is claimed only by that platform's Worker; the other
      Worker polls the same queue and never receives it. (PRD §10 scenario 2's actual promise.)
- [ ] Both machines' OS versions, the executed commands, the artifacts' provenance and every deviation
      are recorded in this card, and anything still unexercised is labelled as such.
- [ ] Machines left clean: both services uninstalled, test directories removed, no leftover processes
      or scheduled tasks.

## Subtasks

- [ ] Environment gate: prove (or disprove) service-install capability on each machine; record the
      answer and the missing piece if blocked.
- [ ] Windows: publish → transfer → install as a service → bootstrap → worker → real build → kill and
      observe restart → uninstall.
- [ ] Linux: publish → transfer → service user → systemd unit → bootstrap → worker → real build →
      restart and observe data → uninstall.
- [ ] Cross-platform tag placement run with both Workers online.
- [ ] Record the evidence (commands, versions, outputs) and clean both machines.

## Dependencies

- I-003 (the packaging and the two unexecuted service definitions), F-004/F-005 (registration,
  heartbeat, reconnect), DOC-003 (the guide whose commands this card executes for the first time).

## Test Cases

### TC-001: Windows Service lifecycle with a real build

Steps:
1. Publish `win-x64`; transfer the Server folder to the Windows machine; run
   `install-server-service.ps1 -InstallDir <dir> -Port <port>` from an elevated shell.
2. Open the login page, bootstrap the Owner, register a `win-x64` Worker, trigger a build of a real
   repository, cancel a second run mid-step.
3. `Get-Process` the server, kill it, watch the service manager restart it, then `-Uninstall`.

Expected:
- The service starts automatically and serves login; data lives in `<dir>\App_Data`; the worker runs
  the pipeline, logs stream, the artifact downloads; the cancelled run ends `Canceled`; the killed
  process comes back without manual intervention; uninstall removes the service and leaves only the
  documented residue.

### TC-002: systemd lifecycle with a real build

Steps:
1. Publish `linux-x64`; transfer; create the `zenkins` user; install the unit with the install-directory
   substitution; `systemctl enable --now zenkins-server`.
2. Bootstrap, register a `linux-x64` Worker, run a build, then `systemctl restart` and re-check data.

Expected:
- `is-enabled` reports enabled; startup write probes pass under `ProtectSystem=strict`; the build
  completes with logs and artifact; restart preserves the database and the session; uninstall removes
  the unit and leaves the documented residue.

### TC-003: Tag placement picks the platform

Steps:
1. With both Workers online (one per OS, each declaring its platform tag), trigger a run whose
   required tags name the Windows worker, and one whose tags name the Linux worker.

Expected:
- Each run is claimed by the matching Worker only; the other Worker polls the same queue throughout
  and never receives it; both runs reach the terminal state their Worker reported.

## Development Log

### 2026-11-14

- Opened after the v1.0 batch closed. The board was empty but PRD §10 scenarios 2 and 7 were only
  ever exercised on macOS; I-003's own record says the Windows and systemd service definitions were
  delivered and not executed, and DOC-003 repeats that in the guide. Two machines on this account make
  closing it a matter of running the guide rather than writing code.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `scripts/publish.sh`, `deploy/windows/install-server-service.ps1`,
  `deploy/systemd/zenkins-server.service`, `docs/install.md` §4/§5, `src/Worker/WorkerConfiguration.cs`
  (worker environment for the target machines).
- How to run/verify: execute DOC-003's §4 (Linux) and §5 (Windows) sections verbatim on the machines
  named above; the card is the record of that execution. No automated suite is expected beyond the
  repository's own five suites on the reference machine.
- Results: not run (card not started).
