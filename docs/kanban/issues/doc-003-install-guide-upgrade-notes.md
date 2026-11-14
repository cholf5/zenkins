---
id: DOC-003
title: Install guide and upgrade notes
type: docs
priority: P2
size: M
status: done
created: 2026-11-14
updated: 2026-11-14
blocked:
  reason: The guide documents I-003's published artifacts and service installs; writing earlier would guess at paths, commands and layouts.
  unblock: I-003 accepted
  owner: none
---

## Goal

An operator with no repository clone and no .NET SDK can install, configure, run as a service, upgrade
and uninstall Zenkins (Server + Worker) on Windows, Linux and macOS using only the guide and the I-003
artifacts — and every command in the guide was actually executed while writing it.

## Background

Roadmap v1.0 ("Install guide and upgrade notes"). DOC-001 deliberately covered developer setup only;
nothing documents deployment. Packaging semantics (service lifetimes, configuration paths, provider
selection, upgrade-with-data) only exist once I-003 produces them, so this card waits for that card
and then documents what is real, in DOC-001's verify-every-command style.

Decisions:

- **Written against the published artifacts** of I-003, not against `dotnet run`: paths, commands and
  layouts come from the publish output.
- **Upgrade story is pinned, not prose**: how EF migrations apply on upgrade (auto-migrate vs explicit
  command — whichever the Server actually does), what must be backed up (database, artifacts,
  credentials/keys) and what a rollback requires. The honest answer to "can I downgrade?" is recorded.
- **Unverified OSes are marked**, following I-003's per-OS honesty: a step not exercised on its OS says
  so instead of claiming success.
- **Worker on build machines** gets its own short path: download the single-file Worker, register it,
  done — no SDK.

## Acceptance Criteria

- [x] Per-OS install guide covers prerequisites, artifact placement, service install/uninstall
      (Windows Service / systemd / launchd as produced by I-003), configuration (database provider,
      paths, ports), and first-run Owner bootstrap.
- [x] A fresh-clone-to-production walkthrough exists: install → configure → bootstrap → first real
      build from a registered Worker, using only documented commands.
- [x] Upgrade notes cover: backup scope, how migrations apply, a stop → replace → start upgrade of the
      published Server with data intact, and rollback limits.
- [x] Uninstall removes services and binaries and states exactly what user data remains and where.
- [x] Every command in the guide was executed on at least one OS during this card; unexercised OS
      steps are labelled rather than implied.

## Subtasks

- [x] Install guide skeleton per OS (mirroring I-003's matrix), then fill from real artifacts.
- [x] Worker quick-start for build machines.
- [x] Upgrade/rollback notes with a performed upgrade run.
- [x] Link from README and `docs/development.md`.

## Dependencies

- I-003 (blocked until its packaging is accepted)

## Test Cases

### TC-001: The guide works verbatim — PASS on macOS, labelled elsewhere

Steps:
1. On a machine (or clean container) without the repo and without the .NET SDK, follow the guide
   exactly: install, configure, bootstrap, register a Worker, run one real build.

Expected:
- Every step succeeds with no undocumented manual fix; deviations found during writing are fixed in
  the product or the guide, not waved through.

Result (observed 2026-11-14): PASS for the OS that could be exercised, with every other step labeled
rather than implied. `docs/install.md` §12 carries the command log as delivered evidence: fourteen
executed steps on macOS 26.5 (publish → tar transfer → launchd install → `status` → `curl` → App_Data
inspection → log location → worker registration → worker first start → worker restart → first build →
`kill -9` self-heal → upgrade drill → uninstall) plus a per-OS table of what was not executed. The
same appendix pins the honest gaps: macOS boot-time start (`RunAtLoad`), cross-version upgrade, a
PostgreSQL deployment, all of Linux, and all of Windows.

### TC-002: The upgrade path preserves data — PASS (same-revision; cross-version recorded unexercised)

Steps:
1. Install version A per the guide; run a build; upgrade to version B per the upgrade notes.

Expected:
- Database rows, artifacts and credentials survive; the guide's claims match observed behaviour.

Result (observed 2026-11-14): PASS within the evidence that exists, and the guide now says exactly
that much. The performed drill is stop → replace binaries (keeping `App_Data/`) → start on the same
publish output — the same shape `Category=PublishSmoke` asserts — so what is proven is a same-revision
rebuild preserving the database, the key ring and the artifact store. No vA → vB migration was ever
exercised, and the guide states that in the upgrade section and the appendix instead of implying it;
the mechanism that would carry it (startup migration via `Database__ApplyMigrationsOnStartup`) is
documented with its multi-node caveat.

## Development Log

### 2026-11-14

- Planned from roadmap v1.0. Blocked on I-003 by design: the guide documents I-003's packaging, so
  writing it earlier would produce fiction.
- Implemented as `docs/install.md` (853 lines, 12 sections): the artifact matrix (including the three
  things that do not exist — no `zenkins` CLI, no application `--port`, no Worker service template, no
  single-file Server, no AOT Server); shared facts (paths resolve from `AppContext.BaseDirectory`,
  never the content root or CWD; the key ring matters as much as the database); per-OS install with
  macOS marked verified and Linux/Windows marked not executed; Worker quick-start (environment
  variables only — the Worker takes zero command-line arguments); reverse-proxy configuration;
  upgrades with backup scope, migration mechanics and a rollback boundary; uninstall with an explicit
  per-OS residue list; troubleshooting; a one-page path; and the TC-001 command log appendix.
- One product fix belongs to this card by PM ruling: `deploy/windows/install-server-service.ps1` now
  writes `Database__ApplyMigrationsOnStartup=true` into the service's `Environment` alongside
  `ASPNETCORE_URLS`. Without it a fresh Windows Service deployment (CWD `System32`, so the bundled
  `appsettings.json` is never read, and `Production` defaults migrations off) fails its first
  `/account/register` with `no such table: BootstrapMarkers`. The launchd plist and the systemd unit
  already set the flag; the Windows script was the outlier.
- Review round 1 (reconciliation) found three must-fix items, all documentation: uninstall never told
  the operator to delete the binaries (the scripts only remove the service registration) nor listed
  residue at per-file granularity; `ForwardedHeaders__KnownProxies` was documented as a JSON array,
  which cannot work through the environment provider (the binder reads child keys —
  `ForwardedHeaders__KnownProxies__0=...`, the shape `HostConfigurationTests` pins, or the proxy is
  untrusted, `X-Forwarded-Proto` is dropped and the login cookie is not `Secure`); and the Windows
  side had no runnable example at all — the only pipeline snippet was the POSIX `sh -c`, while steps
  are argv vectors with no shell. Round 2 found one self-inflicted error: the Windows artifact step
  claimed 17 bytes while `-NoNewline` writes 16 (the POSIX `echo` adds one LF). Final verification:
  PASS.
- Honest limits recorded in the card and the guide: Linux systemd and Windows Service are static
  walkthroughs, not executions; PostgreSQL in published form is untested; cross-version upgrade is
  unproven; macOS boot-time start is configured but not observed.
- Verification (dev run + independent review runs): `dotnet build -c Release` → 0 warnings, run three
  times across the rounds; fast suite `--filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot"`
  → 1001 passed / 0 failed / 0 skipped (twice, the second after all edits). No code, test or script
  behavior changed beyond the one approved ps1 line pair.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | No product defect was found by this card. One packaging gap had already been found by the DOC-003 preread and is fixed here: the Windows service script did not apply migrations on startup, so a fresh deployment's first registration failed with `no such table: BootstrapMarkers`. | Closed | `install-server-service.ps1` now writes `Database__ApplyMigrationsOnStartup=true` into the service `Environment` (PM-approved, one-line scope). |

## Verification

- Related files: `docs/install.md` (the guide, 853 lines); `README.md` and `docs/development.md`
  (links and the deploy pointer); `deploy/windows/install-server-service.ps1` (the migration flag);
  evidence sources: `scripts/publish.sh`, `deploy/macos/com.zenkins.server.plist` and
  `install-launchd.sh`, `deploy/systemd/zenkins-server.service`, `docs/development.md` §2/§3/§6,
  `docs/native-aot.md` §3/§7.
- How to run/verify: execute the guide verbatim per TC-001 on the OS at hand; the appendix §12 is the
  command log and the per-OS "not executed" table. No automated suite beyond `dotnet build -c Release`
  and the fast suite (a documentation card changes no behavior).
- Results (observed 2026-11-14): `dotnet build -c Release` 0 warnings / 0 errors (dev, and re-run by
  the reviewer twice); fast suite 1001 passed / 0 failed / 0 skipped; macOS launchd steps as recorded
  in §12 (install, `status`, `curl`, App_Data placement, `kill -9` self-heal, clean uninstall, upgrade
  drill); Linux systemd, Windows Service and PostgreSQL-in-published-form remain recorded as
  unexercised.
- Known documentation debt left open (not this card's scope): `docs/development.md` §4 still describes
  `DataProtection:KeysDirectory` and `Artifacts:RootPath` as relative to the content root, while the
  code resolves them from `AppContext.BaseDirectory`; the guide states the correct behaviour inline,
  and the stale table rows should be corrected in a future docs card.
