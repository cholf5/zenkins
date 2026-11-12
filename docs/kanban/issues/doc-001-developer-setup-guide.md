---
id: DOC-001
title: Developer setup guide
type: docs
priority: P2
size: S
status: todo
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

Someone who has never seen this repository can clone it, run the Server, and reach a logged-in dashboard in under ten minutes on Windows, Linux, or macOS.

## Background

Small but load-bearing: the project targets three operating systems and claims "no Docker, no Java, no Node". That claim is only true if the setup instructions prove it and the one build-time exception (Tailwind CLI produces committed CSS) is stated plainly.

Content to cover:

- Prerequisites: .NET 10 SDK only (which download, how to verify).
- Clone, restore, build, test.
- First run: connection string, first-run registration/bootstrap, where the SQLite file lands.
- Switching to PostgreSQL.
- Repository layout and where new work belongs (`docs/kanban/board.md`).
- Troubleshooting: port in use, reset local database, CSS regeneration.

## Acceptance Criteria

- [ ] `docs/development.md` exists and is linked from `README.md`.
- [ ] A clean machine following only the document reaches a logged-in dashboard.
- [ ] The document states the .NET SDK version actually verified, not a guess.
- [ ] PostgreSQL switch is documented and was actually executed once.
- [ ] Every command in the document was copy-pasted and run during writing.

## Subtasks

- [ ] Write `docs/development.md`.
- [ ] Link from `README.md`.
- [ ] Fresh-clone verification run.

## Dependencies

- I-001 (documents what exists after scaffold)
- realistically written after F-001 lands

## Test Cases

### TC-001: Fresh clone walkthrough

Steps:
1. `git clone` into a clean directory.
2. Follow `docs/development.md` verbatim, no improvisation.

Expected:
- Dashboard reached; no undocumented step required.

## Development Log

### 2026-11-12

- Card created. Not started.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `docs/development.md`, `README.md`
- How to run/verify: fresh-clone walkthrough
- Results: not run yet
