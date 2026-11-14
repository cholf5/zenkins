---
id: Q-008
title: PostgreSQL in published form: real instance, migrations, full build
type: test
priority: P1
size: M
status: done
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
(`has-pending-model-changes` with `Database__Provider=Postgres`) has been green throughout. What had
never happened was a **real PostgreSQL instance**.

Decisions:

- **A real instance, named in the card.** The machine at hand has no Docker client at all (no
  `docker`/`podman`/`colima`/`lima`, no virtualisation backend), so this card used the native route the
  card allows: Homebrew **PostgreSQL 16.15** in a throwaway cluster. The Docker route stays unverified
  and is recorded as such.
- **Environment-only configuration.** The same published artifact, configured through environment
  variables, from an unrelated working directory.
- **Migrations are the subject, not a background step.** Apply from empty with
  `Database__ApplyMigrationsOnStartup=true`; then take a database created at the previous migration and
  let the new Server migrate it forward — with the pre-upgrade row written **while the schema is still
  the old one**, so "old row" means genuinely old.
- **The failure path is part of the contract**, and it has two shapes (§TC-004): no connection string at
  all, and a connection string that cannot be reached — plus the honest note that an unreachable
  database with startup migration **off** (the production default) does not fail at startup at all.
- **Honest about what the suite cannot do.** The test suite runs on SQLite and there is no live
  PostgreSQL harness; this card's evidence is a scripted run recorded here.
- **Leave the instance clean**, with the decision recorded (see §Cleanup).

## Acceptance Criteria

- [x] A published Server, configured only through environment variables, boots against a real
      PostgreSQL instance, applies every migration from an empty database at startup, serves
      `/account/login` and completes the first-run Owner bootstrap. Data is verifiably in PostgreSQL
      (queried from `psql`), and no SQLite database file is created.
- [x] A real pipeline run completes end to end against that instance: the run, its steps and its logs
      are readable from the database with `psql`, status transitions match, and the artifact downloads
      byte-identically.
- [x] Stop → start preserves everything, and one **upgrade** is exercised on PostgreSQL: a database
      created at the previous migration is migrated forward by the new Server (the D-006 attribution
      column appears), and rows written before the upgrade are still readable with the old values and
      a null attribution.
- [x] The failure path is honest: provider `Postgres` with no usable connection string fails fast with
      a named error and creates no SQLite file.
- [x] The instance (product and version, host, how it was started), the exact commands, and everything
      not exercised are recorded in the card; the instance is left clean.

## Subtasks

- [x] Provision a real PostgreSQL instance and record its version.
- [x] Publish → run the Server against it → bootstrap → full build run → verify rows with `psql`.
- [x] Upgrade path: create at the previous migration → start the new Server → verify the forward
      migration and old rows.
- [x] Failure-path check (provider without connection string).
- [x] Record evidence and clean up.

## Dependencies

- I-003 (published Server), F-014/F-015 (run and step records), D-006 (the migration this card's
  upgrade case exercises), Q-007 (the machine and the publish conventions).

## Test Cases

### TC-001: First boot on an empty PostgreSQL database — PASS

Steps:
1. Start a real PostgreSQL instance; publish the Server; run it with `Database__Provider=Postgres`,
   `ConnectionStrings__Default=…`, and `Database__ApplyMigrationsOnStartup=true`.
2. Open the login page and bootstrap the Owner; inspect the database with `psql`.

Expected:
- The Server starts, migrations apply from empty, login and bootstrap work, the tables are populated in
  PostgreSQL, and no `App_Data/zenkins.db` exists.

Result (observed 2026-11-14): PASS, reproduced independently by the reviewer on its own 16.15 cluster.
Instance: `PostgreSQL 16.15 (Homebrew) on aarch64-apple-darwin25.6.0`, cluster in a throwaway directory,
`pg_ctl … -o '-p 5433 -k … -c listen_addresses=127.0.0.1'`, no `brew services` registration. The published
Server (`osx-arm64`, binary provably containing the D-006 migration — `grep -a -c
AddSecretRotationAttribution Zenkins.Server.dll` = 2, dll newer than the last `src/` commit) started from
an **unrelated working directory** with only environment variables: 16 × `Applying migration` ending at
`20261114102208_AddSecretRotationAttribution`; `/account/login` 200, `/` → 302, headless registration
landed `Starter Organization`. `psql`: `users 1 / orgs 1 / members 1 (Owner) / BootstrapMarkers 1 /
__EFMigrationsHistory 16`; 19 tables; `Secrets.RotatedByUserId` = `character varying(450)` nullable,
`Workers.IsDisabled` = `boolean not null default false`. No SQLite file anywhere under the install:
`find … -name '*zenkins.db*' | wc -l` = 0, `App_Data` holds only `artifacts` and `keys`. After a stop →
start the log reads `No migrations were applied. The database is already up to date.` and login still
302 → `/` 200.

**Same-run provenance.** The proof that the running process was really configured for PostgreSQL is a
three-piece set from one run (the commands ran in sequence, not simultaneously — what has to be excluded
is "a different process or a different database", and PID/port/database name/login/data counts do that):
`ps eww` on the restarted process shows exactly `ASPNETCORE_URLS`, `Database__Provider`,
`ConnectionStrings__Default`, `Database__ApplyMigrationsOnStartup` (started under `env -i HOME PATH`, so
nothing leaked from a parent shell); `psql` on the same database reports the same rows the UI shows; and
the restart-time check of the very same install directory found no SQLite file. That check ran **against
the 45402 install directory after the restart** (the path does not change across a restart), and the
first boot's narrower `*zenkins.db*` count of `0` is a second observation of the same directory with the
same result. Both are quoted in the appendix.

One more reason the environment variables are not being silently overridden by the bundled
`appsettings.json` (which names SQLite): that run started from an unrelated, empty working directory, and
application-settings discovery follows the working directory — there was no `appsettings.json` to load at
all, and environment variables are also the last source in the default order. The conclusion does not rest
on that argument, though: it rests on the `psql` rows, the `ps eww` variables and the absent SQLite file.

### TC-002: A real run against PostgreSQL — PASS

Steps:
1. Register a Worker against that Server and trigger a build of a real repository.
2. Inspect `BuildRuns`, `BuildSteps` and `BuildLogs` through `psql`; download the artifact.

Expected:
- The run completes with the expected status transitions, steps and logs are present and ordered, the
  artifact bytes match, and nothing about the run depends on SQLite.

Result (observed 2026-11-14): PASS. The repository had to be reachable over an allowed scheme
(`ProjectValidation` accepts only `https`/`ssh`, so `file://` is refused by the product): a local
git-over-HTTPS repository with a self-signed certificate, with `GIT_SSL_CAINFO` injected into the Worker
process (no global git configuration touched), `pipeline.lua` = Restore / Build / Publish producing
`report.txt`. The `WorkerSingleFile` artifact registered with a one-time `zreg_` token minted on
`/workers`, and the run came from `POST /projects/{id}/runs/start`.

Execution-order note for anyone replaying this section: those `/account/login` and `psql -U zenkins …`
commands ran **before** `pg_hba.conf` was switched to `scram-sha-256`; at that point the host line was
still `trust`, so they need no password. After the switch, every connection in this card carries one.

Two runs exist, and the card names them so the row counts are unambiguous (an unfiltered aggregation of
`BuildSteps`/`Artifacts` shows 6 rows / 2 rows, which is `2 runs × 3 steps` and `1 artifact per run` — not
a duplicated run):

- **evidence run** — the one whose artifact was downloaded, hashed, re-downloaded after a restart and
  compared; its `BuildRuns` row is `Success`, `TriggerType=Manual`, `GitBranch=main`, non-null
  `GitCommit`, `ExitCode=0`, `WorkerId` equal to the registered worker; its three `BuildSteps` are
  `Success`/`ExitCode 0` with contiguous indexes; its `BuildLogs` are 13 rows with `Seq 1…13` for that
  run, containing `step 1/3 "Restore" started`, the pipeline's own output and
  `publishing artifact 'report.txt' (File, 1 file(s), 19 bytes)`.
- **polling run** — created only to sample the `Queued → Running → Success` transitions at 0.15 s; its
  artifact was never downloaded. It carries the same shape (3 steps, 13 log rows, one artifact).

`Artifacts` holds one row per run, both `report.txt`, 19 bytes, `Status=Ready`, manifest sha256
`e0760183…1eb70`; the downloaded bytes hash to the same value (`printf 'artifact-body-q008\n'` is
exactly 19 bytes and that hash), and after stop → start a second download of the same URL compares clean.

Provenance note (recorded because it was a real gap in the first report): the original instance was
already destroyed when this was questioned, so the run/steps/logs/artifact rows above were re-produced by
replaying TC-002 on a fresh 16.15 cluster with the same published artifact and the same two-run shape;
the replay's ids are `35073d5b…` (evidence run, artifact `1ff95f3d…`) and `0f17b9e0…` (polling run,
artifact `b2c6f64f…`), both with the same 19-byte hash. The original evidence run was `6634e0cc…`
(artifact `a55fa165…`) and the original polling run `57a8a66e…`.

**Credential source, stated plainly.** `Projects.GitCredentialId` is NULL here because nothing in the
product writes it (F-018), so this clone was **anonymous HTTPS against a local repository**; no
server-issued credential and no ambient key was involved. F-013's credential delivery is therefore still
not verified by a real build — that is F-018's job.

### TC-003: Upgrade on PostgreSQL — PASS

Steps:
1. Create a database at the migration immediately preceding the latest one, and populate a pre-upgrade
   row (a rotated secret, per the newest migration).
2. Start the new Server against it with the migration flag on.

Expected:
- The new migration applies, the new column exists, the pre-upgrade row still holds its old values, and
  the page shows the old row's attribution as `not recorded`.

Result (observed 2026-11-14): PASS, on PostgreSQL rather than SQLite. `dotnet ef database update
20261113203304_AddWorkerDisabled` (over TCP with a password) produced 15 migrations, and
`information_schema` confirmed `Secrets` had **no** `RotatedByUserId` column — the baseline is the
genuine previous schema. The new binary then started with `ApplyMigrationsOnStartup=false`, applied **0**
migrations, and completed the Owner bootstrap on that old schema. Only then was the pre-upgrade row
written, with `psql`, on the 8-column table: `Name=q008_pre_upgrade_rotated`, a real 155-byte ciphertext
taken from a run of the app with its key ring copied along, `RotatedAt` and `CreatedByUserId` set (the
timestamps are back-filled to look like an older rotation, which the card states rather than implying
they are literal insert times). Restarting with the flag applied
`20261114102208_AddSecretRotationAttribution`: history 15 → 16, `RotatedByUserId | character varying |
YES`, the old row unchanged field by field (same id, name, ciphertext length 155, `RotatedAt`,
`CreatedByUserId`, `CreatedAt`) and `RotatedByUserId` NULL. `Secrets` page as the Owner: that row renders
`… by not recorded`, and that string occurs exactly once on the page.

### TC-004: The failure path — PASS (two shapes, plus one honest caveat)

Steps:
1. Run the published Server with `Database__Provider=Postgres` and no connection string.
2. Run it with a connection string that cannot be reached.

Expected:
- Fails fast with a named error and creates no SQLite file.

Result (observed 2026-11-14): PASS, with the shape recorded exactly (this is an **uncaught exception that
aborts the process, not a friendly error page** — the card accepts that as "fails fast with a named
error"; polishing it into a page is out of scope here).

- **A — no connection string at all**: `rc=134`, first line `Unhandled exception.
  System.InvalidOperationException: ConnectionStrings:Default is required when Database:Provider is
  'Postgres'.`, stack `ZenkinsDataOptions.cs:54` ← `AddZenkinsData` ← `Program.cs:37`. It creates **no
  `App_Data` at all**, no SQLite file, and the port never opens.
- **B — a connection string that cannot be reached** (`Port=5999`): `rc=134`,
  `Npgsql.NpgsqlException (0x80004005): Failed to connect to 127.0.0.1:5999`, still no SQLite file, and
  the port never opens. Difference worth recording: the writable-directory pre-checks for the key ring
  and the artifact store run **before** the database connection, so `App_Data/{keys,artifacts}` does
  exist here — "creates no App_Data" holds for A only.
- **Caveat found during reconciliation**: with startup migration **off** (the production default) an
  unreachable database does **not** fail at startup — the process comes up and serves, and the first
  request that touches the database fails. "Fails fast" is a property of the migration-enabled startup
  and of the first database access, not of every boot.

### Credential authentication was actually exercised (not `trust`) — PASS

The Homebrew default is `trust`, under which a connection string's password is never checked, so `trust`
alone would have proven nothing. `pg_hba.conf` line 119 (`host all all 127.0.0.1/32`) was changed from
`trust` to `scram-sha-256` and reloaded (115/117/121/124–126 untouched — verified against the stock
Homebrew file during reconciliation). `show password_encryption` = `scram-sha-256`; `pg_authid` shows
`zenkins | SCRAM-SHA-256$…`. Negative: `PGPASSWORD= psql …` → `fe_sendauth: no password supplied`,
exit 2. Positive: with the password, `select current_user, current_database()` → `zenkins | zenkins`.
Then the published Server was restarted with the password in its connection string (new pid, `env -i`
with four explicit variables, 0 migrations applied, login 200) and the data was intact:
`users 1 / orgs 1 / projects 1 / runs 1(+1 replay) / steps 3(+3) / logs 13(+13) / artifacts 1(+1) /
secrets 1`.

## Development Log

### 2026-11-14

- Opened alongside Q-007 when the v1.0 batch closed: every PostgreSQL claim in the repository was
  contract-level (provider selection, Npgsql-generated migrations, a green model gate) with no live
  instance behind it.
- Executed on the machine at hand: **macOS 26.5.2, Homebrew PostgreSQL 16.15**, throwaway cluster on
  port 5433, no service registered. The card's decisions allowed a native install; **Docker was not
  exercised** (this host has no Docker client or virtualisation backend at all) and the **`win-x64`
  published artifact was not run** (its apphost cannot execute here), both recorded rather than implied.
- One card-worthy nuance the reconciliation surfaced: an unreachable connection string with startup
  migration off does not fail at startup. Recorded in §TC-004 so nobody later pins the wrong contract.
- A product defect was found and **not** fixed here: a form POST with no bindable fields makes
  `SecretEndpoints.CreateAsync` dereference a null `form` → 500 (`NullReferenceException`), reproduced
  independently by the reviewer, whose probe showed a completely empty form triggers it too (the stack
  stays server-side; the client gets the generic error page). Opened as **F-019**.
- Documentation: `docs/development.md` §Switching to PostgreSQL (≈ lines 363–371, "Status of this
  section") now carries a superseded-by-Q-008 paragraph naming the instance, what was verified and what
  was not. The `contract-only` annotations inside the accepted cards `d-006` (≈ line 131) and `i-003`
  (≈ lines 149, 193) are **not** edited — they recorded the state at the time — and are superseded by
  this card.
- Cleanup decision: the throwaway cluster was **deleted** rather than `DROP DATABASE`-ed. That was a
  choice, not a limitation (the local socket line was still `trust`, so a socket `DROP DATABASE` would
  have worked); deleting removes roles and `pg_hba` edits with it. Consequences recorded: the cluster
  directories are gone, so row-level evidence cannot be re-queried and this card carries the original
  output instead. The `postgres@16` keg itself is left installed with **no** registered service and
  **no** running instance; Homebrew's own post-install `initdb` cluster under
  `/opt/homebrew/var/postgresql@16` exists but is idle and is **not** Q-008 residue. To remove the
  package entirely: `brew uninstall postgresql@16 && brew autoremove` (the latter also drops the
  dependencies this install pulled in).
- Verification: `dotnet build -c Release` → 0 warnings / 0 errors; fast suite → 1017 passed / 0 failed /
  0 skipped; the PostgreSQL model gate with both variables →
  `No changes have been made to the model since the last migration.`; no repository files changed by this
  card (it is a verification card); the only repository files touched were this card, `docs/development.md`
  (the superseded-status paragraph) and the new `F-019` card.

## Appendix: the exact commands and their echoes

Everything below is what was actually run and printed during this card (raw echoes where they were kept;
where only a summary survived, the line says so). Credentials, certificates and database names here are
throwaway values from a deleted cluster.

**Host and instance.** `Noria.local`, macOS 26.5.2, arm64; `PostgreSQL 16.15 (Homebrew) on
aarch64-apple-darwin25.6.0`, cluster created with
`initdb -D /tmp/zenkins-q008-pg/data -U postgres --locale=en_US.UTF-8`, started with
`pg_ctl -D /tmp/zenkins-q008-pg/data -o '-p 5433 -k /tmp/zenkins-q008-pg -c listen_addresses=127.0.0.1' start`;
no `brew services` entry.

**Publish freshness.**

```
$ grep -a -c AddSecretRotationAttribution /tmp/zenkins-q008/server/Zenkins.Server.dll
2
$ stat -f '%Sm' /tmp/zenkins-q008/server/Zenkins.Server.dll
Nov 14 20:07:39 2026      # last commit touching src/ was 2026-11-14 18:54:55 (119dc5b)
```

**TC-001 — first boot, publish output, environment only, unrelated working directory.**

```
$ cd /tmp/q008-cwd && env Database__Provider=Postgres \
    ConnectionStrings__Default='Host=127.0.0.1;Port=5433;Database=zenkins;Username=zenkins;Password=zenkins' \
    Database__ApplyMigrationsOnStartup=true \
    ASPNETCORE_URLS=http://127.0.0.1:5068 \
    /tmp/zenkins-q008/server/Zenkins.Server
… Applying migration '20261114102208_AddSecretRotationAttribution'   (16 such lines, Initial → this one)
Hosting environment: Production
Data Protection key ring: /tmp/zenkins-q008/server/App_Data/keys
Artifact store: /tmp/zenkins-q008/server/App_Data/artifacts
Now listening on: http://127.0.0.1:5068
```

`/account/login` → 200; `/` → `302 /account/login?ReturnUrl=%2F`; registration through
`/account/register` produced `Starter Organization`.

```
$ psql -h 127.0.0.1 -p 5433 -U zenkins -d zenkins -Atc '
    select (select count(*) from "__EFMigrationsHistory"),
           (select max("MigrationId") from "__EFMigrationsHistory"),
           (select count(*) from "AspNetUsers"),
           (select count(*) from "Organizations"),
           (select "Role" from "OrganizationMembers"),
           (select count(*) from "BootstrapMarkers");'
16|20261114102208_AddSecretRotationAttribution|1|1|Owner|1

$ psql … -Atc "select column_name, data_type, is_nullable, character_maximum_length
                from information_schema.columns
                where table_name='Secrets' and column_name='RotatedByUserId';"
RotatedByUserId|character varying|YES|450
```

(19 tables in `public`; `Workers.IsDisabled` = `boolean not null default false`; the row and column
queries above are transcribed from the recorded output.)

**scram-sha-256, exercised rather than assumed.** `pg_hba.conf` line 119 before and after
(`pg_ctl -D … reload` → `server signaled`); the neighbouring `local` line was left alone, which is why a
socket `DROP DATABASE` would still have worked:

```
117:local   all             all                                     trust
119:host    all             all             127.0.0.1/32            trust          <- before
119:host    all             all             127.0.0.1/32            scram-sha-256  <- after
121:host    all             all             ::1/128                 trust
```

```
$ psql -U postgres -tc 'show password_encryption;'
 scram-sha-256
$ psql -U postgres -tc "select rolname, left(rolpassword,13) from pg_authid where rolname='zenkins';"
 zenkins | SCRAM-SHA-256

$ PGPASSWORD= psql -h 127.0.0.1 -p 5433 -U zenkins -d zenkins -tc 'select 1;'
psql: error: connection to server at "127.0.0.1", port 5433 failed: fe_sendauth: no password supplied
exit=2

$ PGPASSWORD=zenkins psql -h 127.0.0.1 -p 5433 -U zenkins -d zenkins -tc 'select current_user, current_database();'
 zenkins      | zenkins
exit=0
```

**Restart under `env -i` (nothing inherited) with the password in the connection string.**

```
$ env -i HOME=/Users/<you> PATH=/usr/bin:/bin \
    ASPNETCORE_URLS=http://127.0.0.1:5068 \
    Database__Provider=Postgres \
    ConnectionStrings__Default='Host=127.0.0.1;Port=5433;Database=zenkins;Username=zenkins;Password=zenkins' \
    Database__ApplyMigrationsOnStartup=true /tmp/zenkins-q008/server/Zenkins.Server
old pid=44634 / stopped / new pid=45402 / login 200 after restart (300ms)
migrations applied on this start: 0     # No migrations were applied. The database is already up to date.

$ ps eww 45402 | tr ' ' '\n' | grep -E '^(ASPNETCORE_URLS|Database__Provider|ConnectionStrings__Default|Database__ApplyMigrationsOnStartup)='
ASPNETCORE_URLS=http://127.0.0.1:5068
Database__Provider=Postgres
ConnectionStrings__Default=Host=127.0.0.1;Port=5433;Database=zenkins;Username=zenkins;Password=zenkins
Database__ApplyMigrationsOnStartup=true

$ find /tmp/zenkins-q008/server -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm'
   (no output — no SQLite file; the first boot's narrower check `-name '*zenkins.db*' | wc -l` printed 0)
$ ls /tmp/zenkins-q008/server/App_Data
artifacts
keys

$ psql … -Atc '…count of users/orgs/projects/runs/steps/logs/artifacts/secrets…'
1 | 1 | 1 | 1 | 3 | 13 | 1 | 1
```

**TC-002 — the artifact, byte for byte.**

```
$ curl -s -b cookies.txt -o /tmp/a.bin 'http://127.0.0.1:5068/runs/<runId>/artifacts/<artifactId>/download'
download 200 bytes=19
$ shasum -a 256 /tmp/a.bin
 e07601837e03a5f8b931159588bd435f821cdfb223c60efe10cbb5a4b251eb70  /tmp/a.bin
$ printf 'artifact-body-q008\n' | shasum -a 256
 e07601837e03a5f8b931159588bd435f821cdfb223c60efe10cbb5a4b251eb70  -
$ cmp /tmp/a.bin /tmp/a-after-restart.bin && echo 'cmp clean: pre-restart == post-restart bytes'
cmp clean: pre-restart == post-restart bytes
```

**TC-003 — the upgrade, at SQL level.** `dotnet ef database update 20261113203304_AddWorkerDisabled`
(over TCP with the password) → `15` rows in `__EFMigrationsHistory`, and
`select count(*) from information_schema.columns where table_name='Secrets' and
column_name='RotatedByUserId'` → `0`. The new binary then ran with the flag off (0 migrations applied),
the Owner was bootstrapped, and only then was the pre-upgrade row inserted on the 8-column table. A
restart with the flag on logged `Applying migration '20261114102208_AddSecretRotationAttribution'`, and:

```
$ psql … -Atc 'select count(*) from "__EFMigrationsHistory";'
16
$ psql … -c '\d "Secrets"'
 … RotatedByUserId | character varying(450) |           |          |
$ psql … -Atc 'select "Name", "RotatedAt", "RotatedByUserId" from "Secrets" where "Name"=$$q008_pre_upgrade_rotated$$;'
q008_pre_upgrade_rotated | 2026-11-14 19:13:41+08 | 
```

**TC-004 — both failure shapes.**

```
$ env -u ConnectionStrings__Default Database__Provider=Postgres /tmp/zenkins-q008/server/Zenkins.Server; echo rc=$?
Unhandled exception. System.InvalidOperationException: ConnectionStrings:Default is required when Database:Provider is 'Postgres'.
   at Zenkins.Server.Data.ZenkinsDataOptions…:line 54
rc=134          # and no App_Data directory was created at all

$ env Database__Provider=Postgres ConnectionStrings__Default='Host=127.0.0.1;Port=5999;…' Database__ApplyMigrationsOnStartup=true …; echo rc=$?
Npgsql.NpgsqlException (0x80004005): Failed to connect to 127.0.0.1:5999
rc=134          # App_Data/{keys,artifacts} did exist: the writable-directory probes run first
```

**Model gate.**

```
$ env Database__Provider=Postgres ConnectionStrings__Default='Host=127.0.0.1;Port=5433;Database=zenkins;Username=zenkins;Password=zenkins' \
    dotnet ef migrations has-pending-model-changes --project src/Server
No changes have been made to the model since the last migration.    (exit 0)
```

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | Low | Not a PostgreSQL defect: `SecretEndpoints.CreateAsync` dereferences a null `[FromForm]` binding, so an empty or field-less POST returns 500 instead of a validation error. The correct shape still works (`Name`/`Value` → 302 `/secrets?notice=created`), so the UI path is unaffected; the stack stays server-side. Found while probing the published Server. | Open — F-019 | Out of this card's scope; F-019 guards every form handler and asserts no client-visible stack. |

## Verification

- Related files: `src/Server/Data/ZenkinsDataOptions.cs`, `src/Server/Data/DataServiceCollectionExtensions.cs`,
  `src/Server/Data/ZenkinsDbContextOptionsExtensions.cs`, `src/Server/Data/Migrations/*`,
  `docs/development.md` §Switching to PostgreSQL, `docs/install.md` §2.
- How to run/verify: publish with `scripts/publish.sh`, start the published Server against a real instance
  with environment configuration only, and follow the four test cases; the card is the evidence record.
  No automated suite change is expected (the suite runs on SQLite).
- Results (observed 2026-11-14; developer runs, independent reviewer runs on its own instance, and a
  replay for row-level provenance): real PostgreSQL **16.15**, 16 migrations from empty
  (`__EFMigrationsHistory` = 16, 19 tables), login 200 + Owner bootstrap, real pipeline run `Success` with
  three `Success` steps and 13 ordered log rows, one 19-byte artifact per run with matching sha256 on
  download, stop → start preserving everything (`No migrations were applied…`), PostgreSQL upgrade
  15 → 16 with the pre-upgrade row intact and a null attribution rendering as `not recorded`,
  `scram-sha-256` password authentication over TCP exercising a real password, and both failure shapes
  (no connection string → 134, no `App_Data`; unreachable → 134 with `App_Data/{keys,artifacts}` created
  first). Postgres-only model gate green. Not exercised, labelled: Docker route, `win-x64` published
  artifact, upgrade **across product versions**, `pg_hba`'s `local` / `replication` lines and its
  `::1/128` host line (all still `trust`), and
  decryption of the pre-upgrade ciphertext after the upgrade (the worker-side path exists —
  `RunSecretService` / `/api/workers/runs/{id}/secrets` — but was not run).
