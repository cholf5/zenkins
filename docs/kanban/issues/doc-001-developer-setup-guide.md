---
id: DOC-001
title: Developer setup guide
type: docs
priority: P2
size: S
status: review
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

- [x] `docs/development.md` exists and is linked from `README.md`.
- [x] A clean machine following only the document reaches a logged-in dashboard.
- [x] The document states the .NET SDK version actually verified, not a guess.
- [ ] PostgreSQL switch is documented and was actually executed once. **DEFERRED** — no PostgreSQL
      server, `psql`, `pg_isready` or Docker exists on the verification host (`/tmp/zenkins-doc-001/15-postgres-probe.log`); the switch is documented and was executed up to the connection attempt
      (`/tmp/zenkins-doc-001/18-ef-pg-update.log`: `Failed to connect to 127.0.0.1:5432`). Installing a
      server or Docker was explicitly out of scope for this card. Proof command on a machine with
      PostgreSQL: `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef database update --project src/Server`
      exiting 0, then a first-run registration against that database.
- [x] Every command in the document was copy-pasted and run during writing (POSIX forms; the
      PowerShell variants are shell-syntax only and could not be executed — no PowerShell on the
      verification host, noted in the document's header).

## Subtasks

- [x] Write `docs/development.md`.
- [x] Link from `README.md`.
- [x] Fresh-clone verification run.

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

### 2026-11-12 — implemented

- Wrote `docs/development.md`: prerequisites (.NET 10 SDK only, verified 10.0.403, `global.json`
  floor 10.0.100/`latestFeature`), clone/restore/build/test with observed counts (build 0/0, tests
  88/0/0), first run (launch profile => Development on 5067, SQLite at
  `src/Server/App_Data/zenkins.db`, startup migration, asset serving, first-run bootstrap vs the
  no-organization state, sign-out, DB reset), configuration (`Database:Provider`,
  `ConnectionStrings:Default`, PostgreSQL switch, production migration path, `ForwardedHeaders`
  allowlists, cookie `Secure`), repository layout and the one-card-per-change workflow, the
  PostgreSQL-generated migration contract, and troubleshooting.
- README drift fixed: status line no longer says "nothing is runnable yet", the empty Getting
  started section is now a real quick start, and the guide is linked from Documentation and the
  layout block.
- Facts taken from source, not guessed: placement of the launch profile above `ASPNETCORE_ENVIRONMENT`
  / `ASPNETCORE_URLS`, the PG-only `has-pending-model-changes` gate (SQLite reports a false
  "pending changes"), the Production-from-source 500 on `_framework`/`_content` (published output
  serves them with 200), and the antiforgery bridge (`400` without the token).
- Deliberately does not describe F-003 (project CRUD) or any feature that does not exist yet:
  pipelines, workers, runs, logs, secrets and webhooks are called out as not implemented.
- Fresh-clone walkthrough executed from the committed cards' HEAD (`walkthrough.log`). It surfaced one
  hygiene bug, now fixed in the document: the optional curl smoke test left `cookies.txt` /
  `register.html` untracked in the clone, so the block now ends with `rm -f cookies.txt register.html`
  (verified: `git status --short` empty afterwards).

### 2026-11-12 — review round 1 fixes

Review verdict was FAIL with three blocking defects, all fixed here:

1. **F-003 drift.** The closing paragraph now describes what projects actually do at HEAD
   (`/projects`, `/projects/new`, `/projects/{id}`, `/{id}/edit`, `/{id}/delete/confirm`; Member
   create/edit, Owner-only delete, repository/default-branch/pipeline-path validation, unique name per
   organization from `20261112084601_AddProjectUniqueName`) and keeps only pipelines, workers, runs,
   logs, secrets and webhooks in the not-implemented list. README's status line matches, and the
   `## Documentation` heading restored (it had been swallowed by `## Architecture`).
2. **PostgreSQL run command started in Production.** Both variants of the switch now prefix
   `ASPNETCORE_ENVIRONMENT=Development` (`$env:` form for PowerShell), with a paragraph explaining that
   `--no-launch-profile` also drops the profile's Development default and would reproduce the asset 500
   from §6. The same fix was needed in the §6 `ASPNETCORE_URLS` example.
3. **Published-run database path.** The example now `cd`s into the publish directory and states the
   rule plainly: the content root is the current working directory, so running the DLL from the repo
   root writes `<repo root>/App_Data/zenkins.db` and never `artifacts/publish/App_Data`. The §3 reset
   note points at that directory too.

Also fixed: test count refreshed to the real HEAD total (152); layout lists `src/Server/Projects/` and
`Properties/`; antiforgery note covers the project create/edit/delete forms
(`ProjectFormCard.razor`, `DeleteProject.razor`); PostgreSQL type names now quote the strings that are
actually in the migration files (`uuid`, `timestamp with time zone`, `character varying(450)`), with a
`grep` to check; Docker rule cited as PRD §2.2/§8.9 instead of §7.2; PowerShell variants added for the
production-migration one-liner and both §5 PG-prefixed commands; `launchBrowser: true` mentioned for
the first run.

Re-verified at HEAD (transcripts `27`–`32`, then `walkthrough2.log`, a fresh clone of the fix commit):
`--no-launch-profile` without the environment variable really is Production and 500s the assets
(`27`), with `ASPNETCORE_ENVIRONMENT=Development` it serves them with 200 (`31`); the published DLL
started outside its directory puts `App_Data/` in the cwd (`28`), started from the publish directory it
puts it there (`32`); PG model check still exits 0 with F-003's migration (`30`). The clone walkthrough
also created a project over the real form endpoints (`POST /projects/create` → 302 `/projects/{id}`,
list shows it) and confirmed the published run's content root and `App_Data` under the publish
directory.

## Verification

- Related files: `docs/development.md`, `README.md`
- How to run/verify: fresh clone (`git clone <repo>`) into a temp directory, then the document
  verbatim; register the first account over HTTP, reach the dashboard, tear down.
- Environment: macOS 26.5 (arm64), .NET SDK 10.0.403, no PostgreSQL / `psql` / `pg_isready` / Docker /
  PowerShell. Transcripts are on the verification host under `/tmp/zenkins-doc-001/`.
- Results: PASS on every executed step; the one deferred AC is the PostgreSQL switch (above).

Gates run in the repository at HEAD (transcripts `gate-build.log`, `gate-test.log`):
`dotnet build -c Release` → `Build succeeded. 0 Warning(s) 0 Error(s)`;
`dotnet test` → `total: 152, failed: 0, succeeded: 152, skipped: 0` in a clean checkout of this commit
(`walkthrough2.log`, which cloned it). The same gate on the working tree — which at the time of writing
also carried uncommitted F-003 test edits — reported `total: 153, failed: 0, skipped: 0`, so the exact
number moves with the next card; 152 is the count of the revision this guide describes.
Markdown link check: every relative `](...)` target in `README.md` and `docs/development.md` resolves
(`grep`-driven loop over both files, exit 0); `README.md` links `docs/development.md` twice and has a
`## Documentation` heading again.

Per-section transcripts:

| Section | Transcript | Observed |
|---|---|---|
| Prereqs (§1) | `01-restore.log`, `25-tool-install.log` | `dotnet --version` 10.0.403; `dotnet --list-sdks` 10.0.101 / 10.0.403; `dotnet-ef` 10.0.15 installs and reports its version |
| Clone/restore/build/test (§2) | `01-restore.log`, `02-build.log`, `03-test.log`, `gate-build.log`, `gate-test.log` | restore 4 projects; build `0 Warning(s) 0 Error(s)`; tests green: `88/0/0` before F-003, `152/0/0` at the revision this card now targets |
| First run (§3) | `04-first-run.log`, `05-http-probe.log`, `06-register.log`, `07-second-user.log`, `08-logout.log`, `19-reset-db.log`, `24-dev-assets.log` | migrations applied on startup; listening on 5067/Development; `/` 302 → login; register POST 302 → `/` + `Zenkins.Auth` cookie; dashboard shows `Starter Organization`; second account → `/no-organization`; logout clears the cookie; deleting `App_Data` recreates and re-migrates the DB; `_framework`/`_content` 200 in Development |
| Configuration (§4) | `16-ef-sqlite.log`, `17-ef-pg-modelcheck.log`, `12-production-assets.log`, `13-publish.log`, `14-published-prod.log`, `15-postgres-probe.log`, `18-ef-pg-update.log` | SQLite `database update` no-op exit 0; PG model check passes (exit 0) while SQLite reports pending changes (exit 1); Production-from-source 500 on `_framework`/`_content` with `FileNotFoundException`, published output 200; PostgreSQL switch fails only at `Failed to connect to 127.0.0.1:5432` (AC deferred) |
| Layout/migrations (§5) | `23-ef-migrations-add.log`, `30-recheck.log` | `migrations add` with the PG provider succeeds and is undone cleanly (`migrations remove` under SQLite rewrites the snapshot — documented); `has-pending-model-changes` exits 0 with F-003's `AddProjectUniqueName`; `grep -rl "character varying(450)" src/Server/Data/Migrations` lists the generated files |
| Troubleshooting (§6) | `09-port-in-use.log`, `10-urls.log`, `11-env-urls.log`, `11b-env-urls.log`, `20-returnurl.log`, `21-antiforgery.log`, `22-pre-f001-fk.log`, `27-nolaunch-env.log`, `28-published-cwd.log`, `32-published-dbpath.log` | `address already in use` text; `--urls` and `ASPNETCORE_URLS --no-launch-profile` both bind the requested port; `ReturnUrl` sanitising and the `/account/login` bounce; tokenless POST → 400; pre-F-001 DB reproduces `SQLite Error 19: FOREIGN KEY constraint failed`, delete-and-recreate recovers; `--no-launch-profile` alone = Production + asset 500 (`27`), with `ASPNETCORE_ENVIRONMENT=Development` = 200 (`31`); published run writes `App_Data/` into the cwd unless you `cd` into the publish directory (`28`, `32`) |
| End-to-end | `walkthrough.log`, `26-docblock.log`, `walkthrough2.log` | `walkthrough.log` / `26-docblock.log`: the pre-F-003 flow (302 → login, register 302 → `/`, `Starter Organization` + `Dashboard`, second account → `/no-organization`, sign-out, clean clone). `walkthrough2.log`: the same flow from a fresh clone of this fix commit plus the re-verified changed commands — 152/0/0, project create 302 → detail, Development run with `--no-launch-profile` 200 on both asset prefixes, Production equivalent 500, published run from the publish directory writing `zenkins.db` next to the DLL, clone clean at the end |

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

