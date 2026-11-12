# Development setup

Everything needed to go from a clean machine to a signed-in dashboard: prerequisites, build, first
run, configuration, repository layout, and troubleshooting.

> **Verified on:** macOS 26.5 (arm64) with .NET SDK **10.0.403**, on 2026-11-12; re-verified after
> project CRUD landed. Every command in the POSIX (macOS/Linux) form below was run in that order from
> a fresh `git clone`; the expected outputs quoted here are the ones those runs produced. PowerShell
> equivalents differ only in shell syntax (`$env:`, `Remove-Item`, `Tee-Object`) and are given
> wherever they differ.

## 1. Prerequisites

**.NET 10 SDK — that is the only prerequisite.**

| Needed | Not needed |
|---|---|
| .NET 10 SDK (`10.0.100` or newer) | Java / JVM |
| Git (to clone) | Docker or any container runtime |
| | Node.js / npm / Tailwind CLI |
| | PostgreSQL, MySQL or any database server (SQLite is the default) |

Not needed, and not merely optional: the UI ships pre-built CSS in the `BlazorBlueprint` NuGet
packages (`Directory.Packages.props`), so there is no Node/Tailwind build step, and the default
database is a single SQLite file created on first run.

### Which SDK version

- Verified on this machine: `10.0.403` (also `10.0.101` is installed; either works).
- Floor enforced by [`global.json`](../global.json): `10.0.100` with `rollForward: latestFeature`, so
  any 10.0.x SDK in feature band `1xx` or newer is accepted (a 9.x SDK is not).

```bash
dotnet --version     # 10.0.403
dotnet --list-sdks   # lists the installed SDKs and their feature bands
```

If `dotnet --version` fails, install the SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>
(macOS: `brew install --cask dotnet-sdk`; Linux: the Microsoft package feed or `dotnet-install.sh`;
Windows: the installer or `winget install Microsoft.DotNet.SDK.10`) and open a new terminal.

## 2. Clone, restore, build, test

```bash
git clone <repository-url> zenkins
cd zenkins

dotnet restore
dotnet build -c Release
dotnet test
```

Expected results, as observed:

| Command | Observed |
|---|---|
| `dotnet restore` | restores 4 projects (`Zenkins.Shared`, `Zenkins.Worker`, `Zenkins.Server`, `Zenkins.Tests`) |
| `dotnet build -c Release` | `Build succeeded.` / `0 Warning(s)` / `0 Error(s)` — warnings are errors (`Directory.Build.props`) |
| `dotnet test` | `Passed!` / `failed: 0` / `skipped: 0` — 152 tests at the revision this guide was last verified against (a snapshot: the count grows as cards land). No test may fail and none may be skipped |

`dotnet test` builds the Debug configuration unless you pass `-c Release`; that is why its log says
`bin/Debug/net10.0/Zenkins.Tests.dll` even after a Release build. Tests use the Microsoft Testing
Platform runner configured in `global.json`; `dotnet test` is the supported entry point.

The first build downloads NuGet packages and takes noticeably longer than later ones.

## 3. First run

```bash
dotnet run --project src/Server
```

Expected log, as observed:

```text
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20261112051428_Initial'.
...
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5067
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Development
info: Microsoft.Hosting.Lifetime[0]
      Content root path: /path/to/zenkins/src/Server
```

Open <http://localhost:5067>. `dotnet run` picks up `src/Server/Properties/launchSettings.json`,
whose `http` profile sets the port *and* `ASPNETCORE_ENVIRONMENT=Development`. Because the launch
profile wins, you do **not** need to set the environment variable for this command — and setting
`ASPNETCORE_ENVIRONMENT` or `ASPNETCORE_URLS` in the shell has no effect while the profile is used
(see §6).

The profile also sets `launchBrowser: true`. Editor launch configurations (Visual Studio, Rider, VS
Code) honour it and open the page for you, so a tab may appear on its own; from a terminal just use
the URL printed in the log.

### Where the database lives

`src/Server/App_Data/zenkins.db` — a single SQLite file, created on demand, next to the running
project's content root. While the app runs you will also see `zenkins.db-wal` and `zenkins.db-shm`.
The whole `App_Data` directory and all `*.db*` files are git-ignored; nothing database-related is
ever committed.

### What Development changes

Two things, both of which matter locally:

1. **Startup migration.** `Database:ApplyMigrationsOnStartup` defaults to
   `app.Environment.IsDevelopment()`, so a Development run applies pending EF Core migrations before
   the first request and you get a ready database. Production does not do this (see §4).
2. **Static assets from source.** In Development, `dotnet run` serves `_framework/*` and
   `_content/*` assets from the build output (HTTP 200). In Production, a run *from source* returns
   **500** for those two prefixes — the static web asset manifest is a publish artifact. Use
   Development for local work, or run the published output (§6).

### First-run registration

1. `GET http://localhost:5067/` redirects to `/account/login` (302, `ReturnUrl=%2F`).
2. Go to **Register** (`/account/register`). Because the deployment has no users yet, the page says
   the first account becomes the **Owner** of the starter organization.
3. Register with any email and a password of at least 12 characters containing upper and lower case
   letters, a digit and a symbol (Identity policy in `src/Server/Auth/IdentitySetup.cs`).
4. The account is signed in automatically and lands on the dashboard, which shows a
   `Starter Organization` badge and an empty *Build activity* card.

What registration writes, in **one** transaction: the `User` row (ASP.NET Identity), the
`Organization` row named `Starter Organization`, an `OrganizationMember` row with role `Owner`, and
the single-row bootstrap marker that makes the first-run state permanent. Later sign-ups get an
account **without** an organization; they are redirected to `/no-organization` and stay there until
an Owner adds them as a member (Organizations → Members).

### Sign out

`/account/logout` is a POST form (deliberate: a GET sign-out is CSRF-able). It clears the
`Zenkins.Auth` cookie. The cookie is `HttpOnly`, `SameSite=Lax`, 8-hour sliding expiry, and `Secure`
whenever the request is HTTPS.

### Reset the local database

Stop the app (Ctrl+C), then delete the data directory; the next run recreates and migrates it:

```bash
rm -rf src/Server/App_Data
dotnet run --project src/Server
```

```powershell
Remove-Item -Recurse -Force src/Server/App_Data
dotnet run --project src/Server
```

The register page says "first account becomes the Owner" again afterwards. This is the standard
recovery for a dev database you no longer trust — see §6. A *published* run keeps its database in
`App_Data/` under the directory you started it from (§6), not in the repository: if you ran the
published DLL elsewhere, delete that directory instead.

### Headless smoke test (optional, macOS/Linux)

The browser path above is the normal one. To check the same flow from a terminal:

```bash
curl -s -c cookies.txt http://localhost:5067/account/register -o register.html
TOKEN=$(grep -o 'name="__RequestVerificationToken" value="[^"]*"' register.html | head -1 | sed 's/.*value="//; s/"$//')

curl -si -b cookies.txt -c cookies.txt -X POST http://localhost:5067/account/register \
  --data-urlencode "_handler=register" \
  --data-urlencode "__RequestVerificationToken=$TOKEN" \
  --data-urlencode "Input.Email=owner@example.com" \
  --data-urlencode "Input.Password=CorrectHorse9!" \
  --data-urlencode "Input.ConfirmPassword=CorrectHorse9!" | head -1   # HTTP/1.1 302 Found

curl -s -b cookies.txt http://localhost:5067/ | grep -o 'Starter Organization' | head -1

rm -f cookies.txt register.html   # scratch files from this smoke test
```

The POST answers `302` with `Location: http://localhost:5067/` and sets the `Zenkins.Auth` cookie; the
last command prints `Starter Organization`. Antiforgery is required: the token comes from the
rendered form (`__RequestVerificationToken`) together with the antiforgery cookie in `cookies.txt`.
A second registration with a different email redirects to `/no-organization` instead.

## 4. Configuration

Configuration is ordinary ASP.NET Core configuration: `appsettings.json`, then
`appsettings.{Environment}.json`, then user secrets, then environment variables. Environment variables
use double underscores for nesting (`Database__Provider`, `ConnectionStrings__Default`). Note that
`appsettings.Development.json` is git-ignored, so put machine-specific local overrides in environment
variables or user secrets, never in a committed file.

### Database provider

[`src/Server/appsettings.json`](../src/Server/appsettings.json):

```json
{
  "Database": {
    "Provider": "Sqlite"
  }
}
```

| Key | Values | Default |
|---|---|---|
| `Database:Provider` | `Sqlite`, `Postgres` | `Sqlite` |
| `ConnectionStrings:Default` | provider-specific connection string | `Data Source=<content root>/App_Data/zenkins.db` |
| `Database:ApplyMigrationsOnStartup` | `true`, `false` | `true` in Development, `false` otherwise |

A missing or blank connection string is only allowed for SQLite (it falls back to the file above);
`Postgres` without `ConnectionStrings:Default` fails at startup with an explicit error.

### Switching to PostgreSQL

Start a PostgreSQL server you can reach, create a database and a user (for example
`createdb zenkins` and a `zenkins` role), then point the app at it and apply the migrations:

```bash
export Database__Provider=Postgres
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins"
dotnet ef database update --project src/Server
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server --no-launch-profile
```

```powershell
$env:Database__Provider = 'Postgres'
$env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins'
dotnet ef database update --project src/Server
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Server --no-launch-profile
```

`dotnet ef` comes from the `dotnet-ef` global tool, pinned to the EF Core version in
`Directory.Packages.props`:

```bash
dotnet tool install --global dotnet-ef --version 10.0.15   # only if `dotnet ef --version` fails
```

`--no-launch-profile` is what lets the exported variables take effect, but it also drops the profile's
`ASPNETCORE_ENVIRONMENT=Development` — and without an environment variable the host runs in
**Production**, where `_framework`/`_content` assets return 500 from a source run (§6). That is why
the run command sets `ASPNETCORE_ENVIRONMENT=Development` explicitly. Use the profile
(`dotnet run --project src/Server`) if you only want the local SQLite development setup.

> **Status of this section:** on the machine where this guide was verified there is no PostgreSQL
> server and no Docker, so the switch could not be completed end to end. The provider selection,
> connection-string binding and migration path were exercised up to the connection attempt:
> `dotnet ef database update --project src/Server` under `Database__Provider=Postgres` stops with
> `Failed to connect to 127.0.0.1:5432`. The first command in a real environment that proves the
> switch is that same `dotnet ef database update` exiting 0, followed by a successful first-run
> registration against PostgreSQL. Installing a database server or Docker was deliberately avoided
> (PRD §2.2 and §8.9: no Docker as an infrastructure dependency). The server-side setup commands
> (`createdb`, role creation) were not run either.

### Production database path

Startup migration is Development-only. For anything else, run the migration once per deployment and
leave `Database:ApplyMigrationsOnStartup` unset (or `false`):

```bash
Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" \
  dotnet ef database update --project src/Server
```

```powershell
$env:Database__Provider = 'Postgres'
$env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins'
dotnet ef database update --project src/Server
```

The same two variables (or `ConnectionStrings__Default` alone for SQLite) are what the deployed
process needs; nothing else about the production run differs from §6's published-run example.

### Reverse proxy (TLS termination)

`X-Forwarded-Proto` is what makes `Request.IsHttps` true behind a proxy, and therefore what makes the
auth cookie `Secure` (`CookieSecurePolicy.SameAsRequest`). Loopback proxies are trusted by default;
a proxy on another host must be listed, otherwise its headers are ignored:

```json
{
  "ForwardedHeaders": {
    "KnownProxies": [ "203.0.113.7" ],
    "KnownNetworks": [ "10.0.0.0/8" ]
  }
}
```

Both keys are bound in `src/Server/Program.cs` and covered by
`tests/Zenkins.Tests/Auth/HostConfigurationTests.cs`. A remote client cannot make the app trust its
own `X-Forwarded-Proto`.

## 5. Repository layout and where work belongs

```text
global.json                  SDK floor (10.0.100, latestFeature)
Directory.Build.props        shared build settings (net10.0, nullable, warnings as errors)
Directory.Packages.props     central package versions — add packages here only
Zenkins.slnx                 solution (slnx format)
src/Server/                  ASP.NET Core host, Blazor Server UI, Identity, EF Core
  Auth/                      Identity setup, registration, first-run bootstrap, tenant resolution
  Components/                Razor components: layout, account, organizations, projects pages
  Data/                      DbContext, entities, tenancy, migrations
  Organizations/             membership management + POST endpoints
  Projects/                  project service, validation and CRUD endpoints (POST forms)
  Properties/                launchSettings.json — local port and Development environment
  wwwroot/                   project CSS and the two small JS files (theme, antiforgery bridge)
src/Shared/                  code shared by Server and Worker (must not reference ASP.NET Core)
src/Worker/                  console worker (placeholder in v0.1)
tests/Zenkins.Tests/         xUnit v3 tests (Microsoft Testing Platform runner)
docs/                        PRD, development guide, roadmap, kanban
docs/kanban/board.md         the board: two columns, Todo and Done
docs/kanban/issues/          one card per change, named <ID>-<slug>.md
docs/kanban/templates/       the card template
```

Where new work belongs:

- **A change starts as a card** in `docs/kanban/issues/`, copied from
  `docs/kanban/templates/issue-template.md`, and is added to the Todo table in
  `docs/kanban/board.md`. One card per change.
- Workflow: a developer implements the card and sets `status: review` (the card stays in Todo); an
  independent reviewer checks it against its acceptance criteria; only a PASS moves it to Done with
  `status: done`. A FAIL goes back to the same developer.
- New projects go into `src/` and `Zenkins.slnx`; new packages go into `Directory.Packages.props`
  (central package management, versionless `PackageReference`s); build-wide settings go into
  `Directory.Build.props`. `src/Shared` must not reference ASP.NET Core — `Directory.Build.targets`
  fails the build if it tries.

### The migration contract

There is **one** migration set, and it is written for PostgreSQL: the generated files contain
`uuid`, `timestamp with time zone` and `character varying(450)` — exactly those strings, so
`grep -r "character varying" src/Server/Data/Migrations` finds them and a SQLite-generated set would
not. SQLite is dynamically typed, so the same files apply there unchanged, which keeps a single
history for both providers. Consequences:

- **Generate migrations with the PostgreSQL provider**, never with SQLite:

  ```bash
  Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" \
    dotnet ef migrations add <MigrationName> --project src/Server
  ```

  ```powershell
  $env:Database__Provider = 'Postgres'
  $env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins'
  dotnet ef migrations add <MigrationName> --project src/Server
  ```

  (Generating a migration needs no running database; only `database update` does.) Generating with
  SQLite rewrites `ZenkinsDbContextModelSnapshot.cs` with SQLite type names and silently breaks the
  contract.
- **Check the model before committing** — also with the PostgreSQL provider:

  ```bash
  Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" \
    dotnet ef migrations has-pending-model-changes --project src/Server
  ```

  ```powershell
  $env:Database__Provider = 'Postgres'
  $env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins'
  dotnet ef migrations has-pending-model-changes --project src/Server
  ```

  It must print `No changes have been made to the model since the last migration.` With the SQLite
  provider the same command reports pending changes (store type names differ), which is why the
  `PendingModelChangesWarning` is suppressed for SQLite only.
- **Apply to SQLite for development**: either let the Development startup migration do it, or run
  `dotnet ef database update --project src/Server` with the default provider.
- If you need to undo a migration without a PostgreSQL server, delete the generated `<timestamp>_<Name>.cs`
  and `...Designer.cs` files and run `git checkout -- src/Server/Data/Migrations/ZenkinsDbContextModelSnapshot.cs`.
  `dotnet ef migrations remove` is not a substitute here: it needs a reachable database, and under the
  SQLite provider it rewrites the snapshot with SQLite type names (verified).

## 6. Troubleshooting

### Port 5067 is already in use

Observed when a second instance starts:

```text
System.IO.IOException: Failed to bind to address http://127.0.0.1:5067: address already in use.
```

Stop the other instance, or choose another port. `--urls` is passed through to the app and overrides
the launch profile:

```bash
dotnet run --project src/Server --urls http://localhost:5098
```

`ASPNETCORE_URLS` only wins when the launch profile is skipped — otherwise `applicationUrl` from
`launchSettings.json` overrides it (verified: without `--no-launch-profile` the app still listened on
5067, with it on the requested port):

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5097 dotnet run --project src/Server --no-launch-profile
```

On Windows: set `$env:ASPNETCORE_ENVIRONMENT='Development'` and `$env:ASPNETCORE_URLS='http://localhost:5097'`
before the same command. Without the environment variable the host would run in Production here too,
with the asset 500s described below.

### Reset the local database

See §3. Use it when the schema or data is suspect; there is no local data worth keeping.

### `_framework` / `_content` return 500 in a local Production run

Symptom: `/` renders unstyled and interactive parts are dead while `/_framework/blazor.web.js` and
`/_content/BlazorBlueprint.Components/blazorblueprint.css` return 500. Observed log entry:

```text
System.IO.FileNotFoundException: Could not find file '.../src/Server/wwwroot/_framework/blazor.web.js'.
```

Cause: running from source in Production, where the static web asset manifest is not available.
Fix: use Development for local work, or publish and run the output, which serves both prefixes with
200:

```bash
dotnet publish src/Server -c Release -o ./artifacts/publish
cd artifacts/publish
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:5095 dotnet Zenkins.Server.dll
```

```powershell
dotnet publish src/Server -c Release -o .\artifacts\publish
Set-Location .\artifacts\publish
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ASPNETCORE_URLS = 'http://localhost:5095'
dotnet .\Zenkins.Server.dll
```

`artifacts/` is git-ignored, so the publish output cannot accidentally be committed. **The published
app's content root is the current working directory**, not the directory containing the DLL: started
from `artifacts/publish`, the SQLite file is `artifacts/publish/App_Data/zenkins.db`; started with
`dotnet ./artifacts/publish/Zenkins.Server.dll` from the repository root, it is
`<repo root>/App_Data/zenkins.db` and `artifacts/publish/App_Data` never exists. `cd` first if you want
the database next to the binaries. Remember that Production does not migrate on startup, so either
set `Database__ApplyMigrationsOnStartup=true` for that run or run `dotnet ef database update` first.

### Sign-in bounces back to the sign-in page

`ReturnUrl` is sanitized: only site-relative paths are accepted, so `https://evil.example`,
`//evil.example` and `/\evil.example` all fall back to `/` (verified). A *valid but wrong* `ReturnUrl`
is echoed as-is — a hand-crafted `/account/login?returnUrl=%2Faccount%2Flogin` lands you back on the
sign-in form after a successful sign-in, which looks like a loop. Nothing is broken: navigate to
`http://localhost:5067/` and continue. If sign-in silently fails instead, check for a lockout (5 bad
passwords lock the account for 15 minutes) or for cookies being blocked or dropped (see §4 on the
`Secure` flag behind a proxy).

### `SQLite Error 19: FOREIGN KEY constraint failed` after upgrading an old dev database

Only affects a dev database created before the Identity migration landed (a schema with membership
rows that reference users which do not exist). Observed on such a database when the migration rebuilds
`OrganizationMembers` to add the user foreign key:

```text
Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 19: 'FOREIGN KEY constraint failed'.
```

Recovery: delete the dev database and let it be recreated (§3) — verified, the subsequent
`dotnet ef database update` applies every migration and exits 0. Nothing in the repository needs to
change.

### Forms do nothing, or return 400, when JavaScript is blocked

The organization switcher, the Owner-only organization management forms and the project forms
(create, edit and delete — `Components/Shared/ProjectFormCard.razor`,
`Components/Pages/Projects/DeleteProject.razor`) are plain HTML POSTs. They carry
`data-zenkins-antiforgery`, and `wwwroot/js/antiforgery.js` copies the request token rendered into
`<meta name="request-verification-token">` into a hidden `__RequestVerificationToken` field on submit.
Without that script the POST has no token and antiforgery rejects it with **400** (verified). If you
see this, enable JavaScript for `localhost:5067` — or use the pages that do carry the token in the
form itself (login, register, logout).

### Styles look wrong, or you are looking for the CSS build step

There is none. `src/Server/wwwroot/app.css` and `src/Server/wwwroot/styles/theme.css` are
hand-written and committed; all component and theme CSS comes from the `BlazorBlueprint.Components`
NuGet package as a static web asset (`_content/BlazorBlueprint.Components/blazorblueprint.css`). No
Node, npm or Tailwind CLI is involved, so there is nothing to regenerate after changing a `.razor`
file — the package's utility classes are `bb:`-prefixed and already compiled. A page with no styling
is almost always the Production-from-source asset problem above.

### Reading the app log

There is no log file: the app logs to standard output through `Microsoft.Extensions.Logging`
(`appsettings.json` sets `Default: Information`, `Microsoft.AspNetCore: Warning`).
`Microsoft.EntityFrameworkCore.Database.Command` is filtered to `Warning` in `Program.cs` so SQL and
parameter values never end up in the log. To keep a log while reproducing something:

```bash
dotnet run --project src/Server 2>&1 | tee run.log
```

```powershell
dotnet run --project src/Server 2>&1 | Tee-Object run.log
```

`logs/` and `*.log` are git-ignored. Startup failures (bad configuration, migration errors) are
printed before `Now listening on:` appears — that is the first place to look.

## Current state (what you can actually do)

Working today: first-run Owner bootstrap, cookie sign-in/out, organizations and members (invite,
role change, remove), server-side organization switching, and projects scoped to the active
organization. The project surface is `/projects`, `/projects/new`, `/projects/{id}`,
`/projects/{id}/edit` and `/projects/{id}/delete/confirm`: Members may create and edit, only an
Owner may delete, the repository URL, default branch and pipeline path are validated, and a project
name is unique per organization (`AddProjectUniqueName`).

Not implemented yet: pipelines (Lua execution), workers, build runs, live logs, secrets and webhooks;
the Workers and Secrets pages are shell previews. Do not expect those to work in this revision.
