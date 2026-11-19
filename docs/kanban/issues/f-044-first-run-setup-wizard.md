---
id: F-044
title: First-run setup wizard — welcome, deployment facts, Owner account, organization name, first Worker
type: feature
priority: P1
size: L
status: todo
created: 2026-11-19
updated: 2026-11-19
blocked: none
---

## Goal

A deployment with no accounts answers its first browser with a guided setup instead of a bare
registration form: a welcome page that shows what this deployment is (version, database, the
directories that must be backed up) and whether its schema is ready, then one page that creates the
Owner account and names the organization, then an optional page that mints the first Worker's
registration token and shows the command to start it.

## Background

Today the first-run story is a hint above the ordinary registration form, and the organization is
always named `Starter Organization`. The person standing up a CI server for the first time has to
know, from the docs, that the first account is privileged, where the SQLite file and the key ring
live, and that a Worker is a separate process with an environment-variable contract. Jenkins's setup
wizard answers exactly that, at the moment it matters.

Constraints this card inherits:

- **One transaction, one Owner.** The account, the organization (with the chosen name), the Owner
  membership and the bootstrap marker still commit together; the marker is still the race guard, so
  two browsers finishing setup cannot create two organizations.
- **The wizard is the only first-run door.** While the marker is absent, `/account/login` and
  `/account/register` send the browser to `/setup`, and once it is present `/setup` and
  `/setup/owner` are gone. `/setup/worker` stays reachable for an Owner — it is the same capability
  as the Workers page.
- **Facts, not theatre.** The welcome page shows the version, the provider, the database target,
  the key-ring and artifact directories, and the real schema state (`GetPendingMigrationsAsync`).
  It does not print "key ring writable ✓" — the process would not be running if it were not.
- **The Worker step is optional** and says so: an organization with no Worker can be set up and
  browsed; it just cannot build.
- **Every new string is in the catalogue.** F-043's coverage test scans the source, so the wizard's
  copy is translated in the same commit.

## Acceptance Criteria

- [ ] With no accounts, `GET /` still redirects to sign-in, and `/account/login` and
      `/account/register` redirect to `/setup` (302). `GET /setup` renders the welcome page: version,
      provider, database target, key-ring and artifact directories, the schema state, and a "Start
      setup" link.
- [ ] A schema with pending migrations is reported as a problem with the `dotnet ef database update`
      remedy, and the wizard does not offer the Owner form. An unreachable database renders the
      error instead of a 500 page.
- [ ] `GET /setup` and the welcome page create no rows.
- [ ] `POST /setup/owner` with a name, email and password creates the account, the organization with
      **that** name, the Owner membership and the marker in one transaction, signs the browser in,
      and redirects to `/setup/worker`.
- [ ] A blank or over-long organization name, a weak password and a duplicate email each come back
      as a readable validation message on the same page, with no rows written.
- [ ] The password policy is Identity's own (12+ chars, mixed case, digit, symbol); the wizard does
      not restate it in code.
- [ ] After the marker exists, `/setup` and `/setup/owner` redirect (to `/` when signed in, to
      `/account/login` when not), and `/account/register` works again for later accounts.
- [ ] `/setup/worker` requires authentication and organization-Owner rights; a Member sees the
      Owner-required notice, not a token form.
- [ ] Minting on `/setup/worker` shows the plaintext registration token exactly once, with the
      server URL and the two environment variables to start a Worker, and two ways out: skip to the
      dashboard, or finish.
- [ ] The welcome, Owner and Worker pages are static SSR (the Owner form has to be able to set the
      auth cookie) and are available in English and Simplified Chinese.
- [ ] Every new `L10n` key has a `Translations.zh-Hans.json` entry (the catalogue test proves both
      directions).
- [ ] The concurrent-setup race still produces exactly one organization and one marker.

## Subtasks

- [ ] `BootstrapService`: the organization name is an argument (`DefaultOrganizationName` remains the
      fallback for the direct-registration path).
- [ ] `RegistrationService.RegisterOwnerAsync`: the wizard's path — refuses when the marker exists,
      validates the name, and reports losing the race instead of creating an account without an
      organization.
- [ ] `Setup/SetupReadiness.cs`: provider, database target, key-ring directory, artifact root,
      version, and a real schema check with the pending-migration list.
- [ ] `Setup/SetupGateMiddleware.cs`: the four redirects above, registered before authentication.
- [ ] `SetupLayout` (wide, brand header, language switcher) and a step indicator shared by the three
      pages.
- [ ] `SetupWelcome.razor` (`/setup`), `SetupOwner.razor` (`/setup/owner`), `SetupWorker.razor`
      (`/setup/worker`).
- [ ] `Translations.zh-Hans.json` entries.
- [ ] Tests: the gate (four redirects), the facts page, the owner form (happy path, name/password/
      duplicate failures), the post-setup lockout, the worker step (auth, Owner-only, token shown
      once), and the HTTP-level race.
- [ ] `AuthTestHost.RegisterAsync` seeds an Owner through the wizard, so the existing suites keep
      seeding the same state.
- [ ] Docs: PRD §3.18, `docs/development.md` §3, `docs/install.md` first-run, `CHANGELOG.md`,
      `README.md` status line.

## Dependencies

- F-001 (registration, bootstrap marker), F-002 (organization role checks), F-004 (registration
  tokens), F-043 (localization catalogue).

## Test Cases

### TC-001: The gate

Steps:
1. Boot a host with an empty database.
2. `GET /account/login`, `GET /account/register`, `GET /setup`.
3. Complete setup; `GET /setup`, `GET /setup/owner` signed in and signed out.

Expected:
- The first two are 302 to `/setup`; `/setup` is 200 and contains the version and "Start setup".
- After setup: `/setup` and `/setup/owner` are 302 to `/` (signed in) and `/account/login` (not).

### TC-002: Owner and organization

Steps:
1. `GET /setup/owner`, then POST the form with `Contoso CI`, an email and a valid password.

Expected:
- 302 to `/setup/worker` with the auth cookie; one user, one organization named `Contoso CI`, one
  Owner membership, one marker; the dashboard badge reads `Contoso CI`.

### TC-003: Validation

Steps:
1. POST `/setup/owner` with a blank name; then with a 201-character name; then a weak password; then
   an email that already exists.

Expected:
- Each answers 200 with the field's message, no organization and no marker.

### TC-004: The worker step

Steps:
1. Anonymous `GET /setup/worker`; a Member's `GET /setup/worker`; the Owner's POST to mint.

Expected:
- 302 to sign-in; the Owner-required notice; the token, the server URL and both environment
  variables on the POST response.

### TC-005: The race

Steps:
1. Two clients POST `/setup/owner` concurrently.

Expected:
- Exactly one organization and one marker; at most one client is signed in as its Owner.

## Development Log

### 2026-11-19

- Card written, then implemented in one pass: `SetupGateMiddleware` (case-insensitive, trailing-slash
  tolerant, and the one place that sends an unreadable first-run state to `/setup` instead of an
  exception page), `SetupReadiness` (version, provider, credential-free database target, key-ring and
  artifact paths, and a real `GetPendingMigrationsAsync` answer with the two exception families a
  database surface can raise for "unreachable"), `SetupLayout` + the shared step indicator, and the
  three static-SSR pages.
- `BootstrapService` now takes the organization name (argument, with `StarterOrganizationName` still
  the fallback for the direct path); `RegistrationService.RegisterOwnerAsync` is the wizard's entry
  and refuses a completed setup by name instead of creating an account without an organization.
- The old first-run hint on `/account/register` is gone (the gate makes it unreachable); the page is
  a plain "later accounts join as Members" form now.
- Local smoke on the workstation (build only, no test run): the four redirects, the welcome page's
  real SQLite path and schema state, the Owner POST's 302 to `/setup/worker` with the auth cookie,
  the typed name on the dashboard, the minted `zreg_…` token with both start commands, and the
  Production pending-migration warning were each observed by hand; a catalogue scan of the source
  showed no missing or orphaned keys.
- Tests are written (`SetupWizardTests`, 8 facts) but run on a Windows machine against the pushed
  branch, not on the macOS workstation. Results go here when they arrive.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Setup/`, `src/Server/Components/Pages/Setup/`,
  `src/Server/Components/Layout/SetupLayout.razor`, `src/Server/Auth/BootstrapService.cs`,
  `src/Server/Auth/RegistrationService.cs`, `src/Server/Program.cs`,
  `tests/Zenkins.Tests/Setup/SetupWizardTests.cs`.
- How to run/verify: `dotnet build Zenkins.slnx -c Debug`; `dotnet test --filter "Category=Auth"`
  and `Category=Localization` on a Windows machine (the build is not run on the macOS workstation).
- Results: pending the Windows run (build + the fast suite on `feat/f-044-setup-wizard`).
