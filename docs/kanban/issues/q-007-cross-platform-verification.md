---
id: Q-007
title: Cross-platform verification on real Windows and Linux machines
type: test
priority: P1
size: L
status: done
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
- Linux — either the `linux-cloud-host` SSH host (203.0.113.10, ubuntu, x86_64) or, if that is not
  reachable from the Worker host, the WSL2 distribution on `WINDOWS-DEV-BOX` with systemd enabled (see the
  WSL2 decision below). The card records which one produced the evidence.

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
- **WSL2 counts as a Linux target only when labelled as such** (PM ruling, 2026-11-14). The Windows
  machine also carries Docker and WSL. A WSL2 distribution with systemd enabled is a legitimate way to
  exercise the unit for real — but it is Microsoft's kernel, not bare metal and not a cloud VM, so
  every claim it produces is written as "WSL2 (<distro>, systemd enabled)" and `docs/install.md` §4
  keeps its "not executed" label until a real distribution has run it. A bare-metal or cloud Linux
  host, if reachable, is still preferred and outranks WSL evidence for the same step.

## Acceptance Criteria

- [x] **Windows Service**: the published Server installs via `deploy/windows/install-server-service.ps1`
      into a real directory, the service is registered with automatic start, it starts and serves
      `/account/login`, the first-run Owner bootstrap works, and all data lands under
      `<InstallDir>\App_Data` (nothing in `System32`, nothing in the repository). Killing the process
      makes the service manager restart it (the `sc.exe failure` actions work), and `-Uninstall`
      removes the service registration cleanly.
- [x] **systemd unit**: with the `zenkins` service user created, the published Server runs as
      `zenkins-server.service` under `ProtectSystem=strict` with `ReadWritePaths` covering the install
      directory (the startup write probes pass), `systemctl is-enabled` reports enabled, it serves
      login and bootstrap, `systemctl restart` preserves the database, and uninstall leaves only the
      documented residue.
- [x] **A Worker from each OS completes a real build**: the published Worker for that platform
      registers against that Server, heartbeats, clones a real repository, executes a real
      `pipeline.lua`, streams logs and uploads an artifact that downloads byte-identically; one run is
      cancelled mid-step and ends honestly `Canceled`. No SDK and no repository checkout on the target.
- [x] **Tag placement across platforms**: with a Worker on each OS declaring its platform tag, a run
      whose required tags name one platform is claimed only by that platform's Worker; the other
      Worker polls the same queue and never receives it. (PRD §10 scenario 2's actual promise.)
- [x] Both machines' OS versions, the executed commands, the artifacts' provenance and every deviation
      are recorded in this card, and anything still unexercised is labelled as such.
- [x] Machines left clean: both services uninstalled, test directories removed, no leftover processes
      or scheduled tasks.

## Subtasks

- [x] Environment gate: prove (or disprove) service-install capability on each machine; record the
      answer and the missing piece if blocked.
- [x] Windows: publish → transfer → install as a service → bootstrap → worker → real build → kill and
      observe restart → uninstall.
- [x] Linux: publish → transfer → service user → systemd unit → bootstrap → worker → real build →
      restart and observe data → uninstall.
- [x] Cross-platform tag placement run with both Workers online.
- [x] Record the evidence (commands, versions, outputs) and clean both machines.

## Dependencies

- I-003 (the packaging and the two unexecuted service definitions), F-004/F-005 (registration,
  heartbeat, reconnect), DOC-003 (the guide whose commands this card executes for the first time).

## Test Cases

### TC-001: Windows Service lifecycle with a real build — PASS

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

Result (observed 2026-11-14, Windows 10 22H2 build 19045.6466, SDK 10.0.400, git 2.52): PASS, with the
crash recovery proven by the service manager's own records rather than by a script. `sc qc` =
`AUTO_START` + `C:\zenkins\server\Zenkins.Server.exe`; `sc qfailure` = `reset 86400` + 3 ×
`RESTART/5000ms`; the service `Environment` holds `ASPNETCORE_URLS=http://127.0.0.1:5000` and
`Database__ApplyMigrationsOnStartup=true`. Identity is asserted, not just a 200: the page title is
`Sign in · Zenkins`, `/_framework/blazor.web.js` returns 200, and `netstat -ano` attributes
`127.0.0.1:5000` to the service PID (while `0.0.0.0:5000` belongs to an unrelated application that
happens to share the port — the reason every check here targets loopback and asserts identity). Data
lands in `<InstallDir>\App_Data\{zenkins.db, -wal, -shm, keys/, artifacts/}`; `System32` holds no
Zenkins file. A real build (`352f8065`) cloned over SSH, ran three steps and published a 17-byte
artifact whose sha256 matches in four places (worker work directory, server blob, download, manifest).
Cancel (`a969f6cd`) ended `Canceled` with `CancelReason=canceled-by-user …`, `EndedAt` set,
`LeaseExpiresAt` cleared, the running step `Failed`/`canceled` and the rest `Skipped`/`run-canceled`.
For the crash path the process runs as `LocalSystem`, so `Stop-Process` is refused; it was terminated
through WMI (`Win32_Process.Terminate`, `ReturnValue=0`). System log 7031 records "the corrective
action will run in 5000 milliseconds: restart the service" and Application log records
`ZenkinsServer/0 Service started successfully` six seconds later with a new PID — the SCM restarted it,
nobody ran a script. `-Uninstall` reached `1060`, the service key was gone, the port was released.
What the reviewer could not re-verify is recorded honestly: the run rows and blobs were deleted with
the install directory, so the four-way hash and the cancel fields rest on the developer's log plus the
durable event-log records (7045 install, 7031 crash, service start).

### TC-002: systemd lifecycle with a real build — PASS

Steps:
1. Publish `linux-x64`; transfer; create the `zenkins` user; install the unit with the install-directory
   substitution; `systemctl enable --now zenkins-server`.
2. Bootstrap, register a `linux-x64` Worker, run a build, then `systemctl restart` and re-check data.

Expected:
- `is-enabled` reports enabled; startup write probes pass under `ProtectSystem=strict`; the build
  completes with logs and artifact; restart preserves the database and the session; uninstall removes
  the unit and leaves the documented residue.

Result (observed 2026-11-14 on `linux-cloud-host`: Ubuntu 26.04.1 LTS, kernel 7.0.0-43-generic,
systemd 259, 4 CPU, **no .NET SDK installed**): PASS. The unit ran as `zenkins-server.service` with
`User/Group=zenkins`, `WorkingDirectory=/opt/zenkins/server`, `ASPNETCORE_URLS=http://127.0.0.1:5001`
(5000 was taken on that host), `Database__ApplyMigrationsOnStartup=true`, `Restart=on-failure`/`5s`,
`NoNewPrivileges`, `PrivateTmp`, `ProtectSystem=strict` and `ReadWritePaths=/opt/zenkins/server`.
`is-enabled` = enabled, `is-active` = active, and the journal shows the startup write probes passing
(`Data Protection key ring: /opt/zenkins/server/App_Data/keys`, `Artifact store: …/artifacts`) with
migrations applied from empty. The `Content root path: /opt/zenkins/server` line confirms
`WorkingDirectory` took effect. A real build (`48cb5a2b`) completed in 0.6 s with a 19-byte artifact
whose sha256 matches in the same four places; the journal carries the whole queue/lease/artifact/finish
sequence. `systemctl restart` moved the MainPID (1266948 → 1270730) with the journal recording
`No migrations were applied. The database is already up to date.`, the database and blob unchanged and
the same session cookie still valid. Uninstall went `disabled`/`inactive` → unit removed →
`not-found`, port closed. Not exercised, and labelled: **start-on-boot** (the VM was not rebooted; only
`WantedBy=multi-user.target` + `is-enabled` are the configuration evidence). Two failed runs during
setup (`ec4007bd` `git-failed`, `930af7ad` `git-auth-rejected`) are recorded as what they were — the
ambient-key and hairpin-NAT struggles, not passing evidence.

### TC-003: Tag placement picks the platform — PASS (placement here, filter in the follow-up run below)

Steps:
1. With both Workers online (one per OS, each declaring its platform tag), trigger a run whose
   required tags name the Windows worker, and one whose tags name the Linux worker.

Expected:
- Each run is claimed by the matching Worker only; the other Worker polls the same queue throughout
  and never receives it; both runs reach the terminal state their Worker reported.

Result (observed 2026-11-14): PASS for placement, with an honest limit the reviewer found and the
developer then closed.

**Placement.** Server on the VM, Linux Worker local, Windows Worker over
`ssh -N -L 15000:127.0.0.1:5001` (the tunnel identity was checked first: `curl` returns the Zenkins
title, not the unrelated application that owns `0.0.0.0:5000` on the Windows host). Workers were
`build-win-q007` (tags `windows,both`) and `build-linux-q007` (tags `linux,both`), both polling at
0.5 s. `RequiredTags=windows` (`10317669`) produced exactly one `Run leased` with the Windows Worker's
id and the page showed `build-win-q007`; `RequiredTags=linux` (`48cb5a2b`) the same for Linux. The
self-declared platform of each worker is recorded by its name and by the `Platform` column that lived
in the (now deleted) database; the journal alone cannot tell hosts apart, so the OS attribution rests
on those two facts, not on the log.

**The limit, and the follow-up that closed it.** The reviewer pointed out that every placement had a
sub-second queue window, so "the right worker got the right run" could not distinguish a working tag
filter from a fast winner; and the claimed symmetry was an illusion, because the other worker's
concurrency slot was occupied at that moment. A second run was therefore designed to fail loudly:
two workers on the VM (same OS — this proves the **filter**, not cross-OS placement, and is labelled
that way), each declaring `windows,both` / `linux,both` at 0.5 s polling. With only the Linux worker
alive, a `RequiredTags=windows` run (`b19513c6`) stayed `Queued` for 14 s with **zero** `Run leased`
lines while that same worker demonstrated liveness by claiming an untagged run in the same window;
starting the Windows worker claimed it in the same second (`Run leased` = 1, WorkerId = the Windows
worker, DB shows 13.7 s of waiting). Mirrored with the roles swapped (`876a7b83`: 13 s queued, zero
lease, then claimed by the Linux worker the second it started). Four `Run leased` lines, one per run,
all with the expected worker. Had the filter been broken, the live idle worker would have taken the
run within 0.25–0.5 s; the two-orders-of-magnitude gap is the discriminating evidence.

## Development Log

### 2026-11-14

- Opened after the v1.0 batch closed. The board was empty but PRD §10 scenarios 2 and 7 were only
  ever exercised on macOS; I-003's own record says the Windows and systemd service definitions were
  delivered and not executed, and DOC-003 repeats that in the guide. Two machines on this account make
  closing it a matter of running the guide rather than writing code.
- Executed. Environment gate: both YES — the Windows machine runs the installer elevated
  (`IsInRole(Administrator)` true, `sc.exe create/failure/start` all succeed) and the Linux VM is
  reachable by key with `sudo NOPASSWD`. Artifacts were published on the reference machine with
  `scripts/publish.sh win-x64 linux-x64` (exit 0, no warnings) and transferred macOS → VM → Windows;
  neither target had the repository, and the VM has no .NET SDK at all (the Windows host does have
  four SDKs, so its claim is "deployed and run without using them", not "no SDK").
- Packaging defect found and fixed in passing (PM-approved, one file):
  `install-server-service.ps1` checked no `sc.exe` exit code, so a second install answered
  `FAILED 1073` while the script still printed `installed:` and exited 0 — re-installing into a new
  directory or port would silently keep the old configuration. It now routes every `sc.exe` through an
  `Invoke-Sc` wrapper that fails loudly, tolerates only `stop`'s 1062, and was exercised five ways
  (first install, re-install, uninstall, uninstall-without-a-service, missing exe).
- Documentation updated with what is now observed rather than recorded as unobserved:
  `docs/install.md` §5.3/§10 (the Windows service does write framework logs to the Application event
  log — provider `Zenkins.Server`, with the SCM channel under `ZenkinsServer`; read the provider, not a
  fixed event id), §5.5 and §4.5 (uninstall is no longer idempotent: `-Uninstall` without a service now
  exits 1, and on systemd `disable --now` after the unit file is gone exits 1 / `stop` exits 5), §9
  (the event-log source registration survives uninstall).
- **Gap found, opened as a separate card (F-018)**: `Project.GitCredentialId` has no writer anywhere
  in the product — creating a credential stores an organization-level row and ignores the form's
  `ProjectId` (used only for the post-redirect), no project page can attach a credential, and
  `FetchMaterialForRunAsync` reads only that never-written column. Every run in this card therefore
  cloned with the worker host's ambient key, and the server logged `Git credential request resolved no
  material` for **all six runs**. This is a recorded v0.9 deliberate gap (D-003 says the assignment has
  no UI and the tests seed the column), not a regression — and F-013's "credentials delivered to a
  Worker" is consequently **not** verified by a real build. F-018 also carries the observability
  finding: a missing credential currently degrades silently to ambient keys with no operator-visible
  warning.
- Reviewer rounds: the first reconciliation passed TC-001/TC-002 on the facts but withheld TC-003,
  because sub-second queue windows cannot discriminate a working filter from a fast winner, and caught
  a leftover 48-byte `worker.token` under `~/.zenkins` on the VM (credential-class residue that
  contradicted the "machines clean" claim; the Windows side had deleted the equivalent). Both were
  closed and the re-verification reconstructed the whole timeline from the VM journal, including the
  new discriminating run.
- Limits recorded: start-on-boot was not exercised on either OS (no reboot); version-to-version
  upgrade is not part of this card; PostgreSQL is Q-008; nothing about the credential path is proven
  here (see F-018).
- Verification (developer runs + independent review runs + re-verification): `dotnet build -c Release`
  → 0 warnings / 0 errors; fast suite → 1017 passed / 0 failed / 0 skipped. Both machines were left
  clean (services removed to `1060`/`not-found`, directories and `~/.zenkins` deleted, test keys
  removed, `authorized_keys` back to 211 bytes, tunnels closed, no processes, ports free). The reviewer
  left one documented trace of its own: eight journal lines for a temporary `zkproof` unit used to
  verify the systemd exit codes.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| 1 | Medium | `install-server-service.ps1` ignored every `sc.exe` exit code: a second install printed `installed:` and exited 0 while SCM had answered 1073, leaving the old binary path and environment in place. | Fixed | Every `sc.exe` now goes through an `Invoke-Sc` wrapper that fails loudly (only `stop`'s 1062 is tolerated); exercised first-install / re-install / uninstall / uninstall-without-service / missing-exe. |
| 2 | High (pre-existing, recorded) | `Project.GitCredentialId` has no writer: credentials are organization-level only, no page can attach one to a project, and run-time lookup reads only that column — so F-013's credential delivery is unreachable from the UI and real builds silently fall back to the worker host's ambient key (server logged `no material` on all six runs here). | Open — F-018 | Deliberately out of this verification card. F-018 implements the assignment and requires a real SSH clone served by the server, not by ambient keys. |

## Verification

- Related files: `scripts/publish.sh`, `deploy/windows/install-server-service.ps1`,
  `deploy/systemd/zenkins-server.service`, `docs/install.md` §4/§5, `src/Worker/WorkerConfiguration.cs`
  (worker environment for the target machines).
- How to run/verify: execute DOC-003's §4 (Linux) and §5 (Windows) sections verbatim on the machines
  named above; the card is the record of that execution. No automated suite is expected beyond the
  repository's own five suites on the reference machine.
- Results (observed 2026-11-14; developer runs, independent review, and a re-verification that
  rebuilt the timeline from the VM journal): `dotnet build -c Release` 0 warnings / 0 errors; fast
  suite 1017/1017, 0 skipped. Windows: service `AUTO_START`, identity-asserted login, data under the
  install directory, SCM restart after a WMI kill (7031 → start six seconds later), uninstall to 1060.
  Linux: `enabled`/`active` under `ProtectSystem=strict`, startup write probes pass, restart preserves
  the database and session, uninstall to `not-found`. Tag placement: one lease per run with the
  expected worker, plus a discriminating run where a live idle Worker left a tag-restricted run queued
  for 14 s with zero leases and the matching Worker claimed it the second it started. Artifacts:
  four-way sha256 equality on both hosts. Not exercised, labelled: start-on-boot (no reboot), version
  upgrade, PostgreSQL (Q-008), and F-013's credential delivery (F-018).
