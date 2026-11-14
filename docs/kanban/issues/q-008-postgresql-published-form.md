---
id: Q-008
title: PostgreSQL in published form: real instance, migrations, full build
type: test
priority: P1
size: M
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

A published Server runs against a real PostgreSQL instance and does everything it does on SQLite:
applies its migrations from empty at startup, serves login and the first-run Owner bootstrap, runs a
real pipeline to completion with logs and an artifact, survives a stop → start and one schema upgrade,
and fails loudly when the provider is selected without a connection string. Then the "contract
verification only" annotations in I-003 and D-006 can be retired.

## Background

PostgreSQL is an MVP item (`docs/prd.md` §7.1 "PostgreSQL 支持") and the code supports it —
`Database__Provider=Postgres`, an Npgsql provider package, and migrations generated with the PostgreSQL
provider, which is why the model snapshot carries Npgsql shapes and the contract gate
(`has-pending-model-changes` with `Database__Provider=Postgres`) has been green throughout. What has
never happened is a **real PostgreSQL instance**: every claim so far is one of three things — provider
selection tested at the configuration layer, migrations executed on SQLite, or the model diffed against
the Npgsql provider. I-003 and D-006 both record the gap explicitly rather than claiming otherwise.

Decisions:

- **A real instance, named in the card.** Docker (or a native install) is fine; the card records the
  server version and where it runs, so the claim can be reproduced.
- **Environment-only configuration.** The same published artifact, configured through environment
  variables, with no rebuild and no source tree.
- **Migrations are the subject, not a background step.** Apply from empty with
  `Database__ApplyMigrationsOnStartup=true`; then take a database created at the previous migration and
  let the new Server migrate it forward, proving the upgrade path on PostgreSQL rather than on SQLite.
- **The failure path is part of the contract.** `Database__Provider=Postgres` without a usable
  connection string must fail fast with a clear message — never silently fall back to SQLite.
- **Honest about what the suite cannot do.** The test suite runs on SQLite and there is no live
  PostgreSQL harness; this card's evidence is a scripted manual run recorded in the card, not a new
  test suite pretending to cover it.
- **Leave the instance clean**: test database dropped, or the container removed, with the decision
  recorded.

## Acceptance Criteria

- [ ] A published Server, configured only through environment variables, boots against a real
      PostgreSQL instance, applies every migration from an empty database at startup, serves
      `/account/login` and completes the first-run Owner bootstrap. Data is verifiably in PostgreSQL
      (queried from `psql`), and no SQLite database file is created.
- [ ] A real pipeline run completes end to end against that instance: the run, its steps and its logs
      are readable from the database with `psql`, status transitions match, and the artifact downloads
      byte-identically.
- [ ] Stop → start preserves everything, and one **upgrade** is exercised on PostgreSQL: a database
      created at the previous migration is migrated forward by the new Server (the D-006 attribution
      column appears), and rows written before the upgrade are still readable with the old values and
      a null attribution.
- [ ] The failure path is honest: provider `Postgres` with no usable connection string fails fast with
      a named error and creates no SQLite file.
- [ ] The instance (product and version, host, how it was started), the exact commands, and everything
      not exercised are recorded in the card; the instance is left clean.

## Subtasks

- [ ] Provision a real PostgreSQL instance and record its version.
- [ ] Publish → run the Server against it → bootstrap → full build run → verify rows with `psql`.
- [ ] Upgrade path: create at the previous migration → start the new Server → verify the forward
      migration and old rows.
- [ ] Failure-path check (provider without connection string).
- [ ] Record evidence and clean up.

## Dependencies

- I-003 (published Server), F-014/F-015 (run and step records), D-006 (the migration this card's
  upgrade case exercises), Q-007 (optional: the Linux machine from that card is a natural host for a
  container if the reference machine has no Docker).

## Test Cases

### TC-001: First boot on an empty PostgreSQL database

Steps:
1. Start a real PostgreSQL instance; publish the Server; run it with
   `Database__Provider=Postgres`, `ConnectionStrings__Default=Host=…`, and
   `Database__ApplyMigrationsOnStartup=true`.
2. Open the login page and bootstrap the Owner; inspect the database with `psql`.

Expected:
- The Server starts, migrations apply from empty, login and bootstrap work, the tables are populated in
  PostgreSQL, and no `App_Data/zenkins.db` exists.

### TC-002: A real run against PostgreSQL

Steps:
1. Register a Worker against that Server and trigger a build of a real repository.
2. Inspect `BuildRuns`, `BuildSteps` and `BuildLogs` through `psql`; download the artifact.

Expected:
- The run completes with the expected status transitions, steps and logs are present and ordered, the
  artifact bytes match, and nothing about the run depends on SQLite.

### TC-003: Upgrade on PostgreSQL

Steps:
1. Create a database at the migration immediately preceding the latest one, and populate a pre-upgrade
   row (a rotated secret, per the newest migration).
2. Start the new Server against it with the migration flag on.

Expected:
- The new migration applies, the new column exists, the pre-upgrade row still holds its old values, and
  the page shows the old row's attribution as `not recorded` (the same semantic the SQLite test pins).

## Development Log

### 2026-11-14

- Opened alongside Q-007 when the v1.0 batch closed: the board was empty, but every PostgreSQL claim in
  the repository is contract-level (provider selection, Npgsql-generated migrations, a green model
  gate) with no live instance behind it. I-003's record and D-006's development log both say so
  explicitly; this card exists to retire those annotations or to find out why they cannot be retired.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Data/ZenkinsDataOptions.cs`, `src/Server/Data/DataServiceCollectionExtensions.cs`,
  `src/Server/Data/Migrations/*`, `docs/development.md` §Switching to PostgreSQL, `docs/install.md` §2.
- How to run/verify: publish with `scripts/publish.sh`, run the published Server against a real
  instance with environment configuration, and follow the three test cases; the card is the evidence
  record. No automated suite change is expected (the suite runs on SQLite).
- Results: not run (card not started).
