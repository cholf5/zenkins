---
id: DOC-003
title: Install guide and upgrade notes
type: docs
priority: P2
size: M
status: todo
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

- [ ] Per-OS install guide covers prerequisites, artifact placement, service install/uninstall
      (Windows Service / systemd / launchd as produced by I-003), configuration (database provider,
      paths, ports), and first-run Owner bootstrap.
- [ ] A fresh-clone-to-production walkthrough exists: install → configure → bootstrap → first real
      build from a registered Worker, using only documented commands.
- [ ] Upgrade notes cover: backup scope, how migrations apply, a stop → replace → start upgrade of the
      published Server with data intact, and rollback limits.
- [ ] Uninstall removes services and binaries and states exactly what user data remains and where.
- [ ] Every command in the guide was executed on at least one OS during this card; unexercised OS
      steps are labelled rather than implied.

## Subtasks

- [ ] Install guide skeleton per OS (mirroring I-003's matrix), then fill from real artifacts.
- [ ] Worker quick-start for build machines.
- [ ] Upgrade/rollback notes with a performed upgrade run.
- [ ] Link from README and `docs/development.md`.

## Dependencies

- I-003 (blocked until its packaging is accepted)

## Test Cases

### TC-001: The guide works verbatim

Steps:
1. On a machine (or clean container) without the repo and without the .NET SDK, follow the guide
   exactly: install, configure, bootstrap, register a Worker, run one real build.

Expected:
- Every step succeeds with no undocumented manual fix; deviations found during writing are fixed in
  the product or the guide, not waved through.

### TC-002: The upgrade path preserves data

Steps:
1. Install version A per the guide; run a build; upgrade to version B per the upgrade notes.

Expected:
- Database rows, artifacts and credentials survive; the guide's claims match observed behaviour.

## Development Log

### 2026-11-14

- Planned from roadmap v1.0. Blocked on I-003 by design: the guide documents I-003's packaging, so
  writing it earlier would produce fiction.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `docs/install.md` (or the structure DOC-001 chose), `docs/development.md` (link),
  `README.md` (link), I-003's publish scripts and service definitions
- How to run/verify: execute the guide verbatim per TC-001; no automated suite beyond that by design.
- Results: not run (card not started).
