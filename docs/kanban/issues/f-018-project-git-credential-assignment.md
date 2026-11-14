---
id: F-018
title: Project git credential assignment, and a real clone served by the server
type: feature
priority: P1
size: M
status: done
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

An Owner can attach an organization git credential to a project in the UI, the Server hands that
credential to the Worker for that project's runs, and a private repository clones **from that
credential** rather than from whatever key happens to sit on the worker host — with a missing or
rejected credential visible instead of silent.

## Background

Q-007 verified the cross-platform story on real machines and found, along the way, that the
credential path never participates in it. F-013 built the storage and the delivery endpoint, but
`Project.GitCredentialId` has **no writer anywhere in the product**:

- `GitCredentialService.CreateAsync` takes no project and only inserts an organization-level row; the
  `ProjectId` the settings form posts is used solely to build the post-redirect
  (`GitCredentialEndpoints` passes `form?.ProjectId` to `SuccessOutcome`).
- `ProjectInput` carries name/repository URL/branch/pipeline path only, so the create/edit endpoints
  cannot accept a credential even if a form offered one, and no project page has an attach control.
- `FetchMaterialForRunAsync` returns `NotFound` unless `project.GitCredentialId` is set — reading a
  column nothing writes.

D-003 recorded this as a deliberate v0.9 scope cut ("the assignment has no UI … so the tests seed it"),
which is why the tests have always seeded the column and why nothing failed. Q-007 made the cost
visible: **all six runs in that card cloned with the worker host's ambient key**, and the server logged
`Git credential request resolved no material` for every one of them.

Decisions:

- **Wire the existing pieces, don't rebuild them.** The credential row, encryption, the worker-side
  fetch endpoint and the client already exist and are tested; what is missing is the assignment.
- **Owner-only, tenant-safe, like every other credential operation.** The attach/detach surface follows
  F-012's authorization and Q-001's isolation rules, and the new queries go through the tenant filter.
- **A missing credential is not silent.** Today a project with no credential degrades to ambient keys
  with no operator-visible signal. After this card, the run record (or the run page) must say that no
  credential was used because none is assigned, and a rejected credential must surface as a failure
  with a reason, not as an opaque `git-auth-rejected`.
- **Prove it with a credential, not with ambient keys.** The acceptance evidence is a private
  repository cloned on a machine that has **no** ambient key for it, using only what the server
  delivered.

## Acceptance Criteria

- [x] An Owner can attach one of the organization's git credentials to a project and detach it again,
      from the UI, with the change auditable on the page (which credential, by whom, when).
- [x] The assignment is persisted (`Project.GitCredentialId` written), tenant-scoped, Owner-only, and
      survives a restart; a credential belonging to another organization, or a credential id that does
      not exist, is refused.
- [x] A run of that project fetches its credential from the server and the Worker clones the private
      repository **without any ambient key or SSH agent on the worker host** — proven by a real build
      on a machine whose `~/.ssh` holds nothing for that host, recorded in the card.
- [x] A project with no credential assigned says so on the run (visible to the owner) instead of
      silently falling back to ambient keys; a credential the remote rejects fails the run with a
      reason that names the rejection rather than a bare `git-auth-rejected`.
- [x] Deleting a credential (F-012 semantics) that is still assigned is refused or confirmed with the
      projects that use it, the same way D-006 handles referenced secrets.
- [x] Owner-only and cross-tenant probes for the new surface are permanent tests, and the existing
      secrets/credential suites stay green.

## Subtasks

- [x] Write path: endpoint or form field to attach/detach a credential on a project (Owner-only,
      validated against the organization's credentials).
- [x] Project edit / settings page control, showing the current assignment and its provenance.
- [x] Run-side messaging: "no credential assigned" and credential-rejection reasons reach the run page
      and the log without leaking material.
- [x] Delete-and-referenced flow for credentials, mirroring D-006's reference awareness.
- [x] Real proof: private repository, keyless worker host, real build.

## Dependencies

- F-012 (storage and encryption), F-013 (delivery endpoint and worker client), D-003 (project tabs and
  the settings surface this extends), D-006 (the reference-awareness pattern to mirror), Q-007 (the
  evidence that the path is unused today).

## Test Cases

### TC-001: Attach, run, clone — without an ambient key — PASS

Steps:
1. Create a project pointing at a private repository; on a worker host whose `~/.ssh` has no key for
   that host, start a Worker.
2. Attach the organization's credential to the project in the UI; trigger a run.

Expected:
- The clone succeeds from the delivered credential; the worker host's ambient keys are provably absent
  and unused; the run log shows the credential source without revealing material.

Result (observed, 2026-11-14): PASS, in a purely synthetic environment. The private repository is a
local **TLS + Basic-auth** dumb-HTTP server (new fixture `AuthenticatedHttpsGitServer`, a self-signed
certificate with a `127.0.0.1` SAN injected through `GIT_SSL_CAINFO`), and the "ambient" side is a
credential helper the test writes itself plus a self-generated key — no real credential store, keychain
or `~/.ssh` was read at any point.

`MilestoneGitCredentialTests` runs a **real Server process and a real Worker process** (it asserts the
Worker PID differs from the test process). The fixture passes the worker only `HOME`, `GIT_SSL_CAINFO`
and `SSH_AUTH_SOCK=`; every hardening variable comes from the product's own
`GitEnvironment.Base(hardeningDirectory)`. Run 1 (nothing attached) → **Failed** with
`FailureReason=git-credential-not-assigned`, the run log saying no credential is assigned and that a
credential stored on the worker host is not used, and — the decisive part — the synthetic helper's call
count **did not change** (1 → 1) while that same helper succeeds when the product's hardening is not in
play (control: helper calls 1, server accepted, clone exit 0). Attach through the settings form, run
again → **Success**, `GitCommit == repository.MainTip == OriginMainTip`, the `Clone` step `Success`, the
run log recording that it cloned with the project's assigned `HttpsToken` credential and the server
logging that material was delivered (never the value).

Independently, the reviewer re-proved this **without the test fixture at all**: a throwaway project
referencing `src/Worker`, passing empty `EnvironmentOverrides`, so every hardening key had to come from
the product. Control clone with the synthetic ambient helper: exit 0, helper calls 1. Product, no
credential: `AuthenticationRejected`, helper calls 1 → 1, server accepted 5 → 5, repository empty, and
the child process environment showed `ASKPASS:(empty)`, `AUTHSOCK:(empty)`,
`GLOBAL:<run>/hardening/gitconfig.hardened`, `NOSYSTEM:1`, `CONFIGCOUNT:1`, `KEY0:credential.helper`,
`VALUE0:(empty)`. A second channel was also closed: an inherited `GIT_ASKPASS` alone can authenticate
(control: askpass called twice, clone exit 0), and the product clears it (askpass calls stay 2, clone
`AuthenticationRejected`).

### TC-002: No credential is visible, not silent — PASS

Steps:
1. Trigger a run of a project with no credential assigned against a private repository.

Expected:
- The run fails (or is refused) with a reason naming the missing assignment, and the run page states
  that no credential is assigned — not an opaque clone failure, and not a silent ambient-key success.

Result (observed, 2026-11-14): PASS. `PipelineFailureReasons.GitCredentialNotAssigned`
(`git-credential-not-assigned`) is in the shared whitelist, in `WorkerReportable` (so the server accepts
it), raised by the worker only when no material was delivered *and* authentication failed, and rendered
by the run page: the credential line is always visible and a new explanation line turns the code into
English, including the sentence that the worker will not borrow a credential stored on its host. The
runner's log line states the same thing and never names the credential (the run page is readable by
members).

### TC-003: Boundary cases — PASS

Steps:
1. Attempt to attach another organization's credential; delete an assigned credential; detach and
   re-attach.

Expected:
- Cross-tenant attach is refused without leaking existence; the delete warns with the referencing
  projects; detach and re-attach behave idempotently.

Result (observed, 2026-11-14): PASS. Attach/detach resolve **both** ids through the tenant filter, so a
foreign or missing project or credential is a plain **404** (never a 302 to another tenant's page, never
a 500); a Member gets **403**; the permanent probes assert both databases are unchanged. Deletion reads
the referencing projects, refuses with them named (409 JSON without a project id, or a PRG
`credential-in-use` from the settings page), and requires both a visible confirmation checkbox **and**
the credential id in the request — a checkbox alone is not enough, and a hidden default cannot delete.
Confirmed deletion removes the row; the referencing rows are nulled **by the database** (see
§Assignment invariant below), and each unassignment writes an audit line. Detach is idempotent, and
re-attach preserves nothing stale.

### Assignment invariant: the database, not the code, clears the reference — PASS

The interesting failure this card exists to prevent is a *dangling* `Project.GitCredentialId`, because
that degrades silently back into an anonymous clone. A foreign key now carries the invariant:
`Projects.GitCredentialId → GitCredentials.Id` with `ON DELETE SET NULL` (migration
`20261114134917_AddProjectGitCredentialForeignKey`, index included, `Down` symmetric), and the delete
path no longer writes NULL itself (referencing projects are read `AsNoTracking()`, for naming and audit
only). Proven at the SQL level: an assignment emits `UPDATE "Projects"`; a confirmed delete emits
`DELETE "GitCredentials"` and **no** `UPDATE "Projects"`, while the column ends up NULL. A written
competition (the credential row deleted while an attach is in flight) returns **404** and leaves no
dangling pointer and no dirty row; the FK violation is mapped from SQLite extended code 787 and
PostgreSQL 23503. `PRAGMA foreign_keys` is on in both the product and test paths, and writing a
non-existent credential id raises `DbUpdateException` with the row unchanged.

## Development Log

### 2026-11-14

- Opened from Q-007's finding. Deliberately not fixed inside that verification card: the work is a
  feature (UI surface + write path + run-side messaging), and mixing it into an acceptance card would
  have made both impossible to judge independently.
- Implemented. **Write path**: `GitCredentialService.AssignToProjectAsync` is the only place that writes
  `Project.GitCredentialId`; it checks Owner, resolves *both* ids through the tenant filter, is
  idempotent, records `GitCredentialAssignedAt`/`AssignedByUserId`, and audits with the credential id and
  name, the project, the actor and whatever it replaced. `DetachFromProjectAsync` clears all three
  columns. New routes `POST /projects/{projectId}/git-credential/attach|detach`; two nullable columns via
  migration `20261114123330_AddProjectCredentialAssignment` (PostgreSQL-generated shapes, `Down`
  symmetric). The settings page shows the current assignment (name, kind, attached by, attached at) or a
  `#credential-assignment-none` block that says plainly that nothing is assigned and that the worker host's
  credentials will not be used; a reference pointing at a deleted row renders `#credential-assignment-missing`.
  Members still never see a credential name (D-003's asymmetry kept).
- **Run side**: the chain was already there except its first link — the page attach writes the column the
  fetcher reads. Tests no longer seed it anywhere on the product path.
- **Hardening** — the part that makes "no ambient credential" true instead of decorative. Every git
  invocation starts from `GitEnvironment.Base(hardeningDirectory)`: `GIT_TERMINAL_PROMPT=0`,
  **`GIT_ASKPASS` cleared**, `SSH_AUTH_SOCK` cleared, `GIT_CONFIG_GLOBAL` pointing at an empty file,
  `GIT_CONFIG_NOSYSTEM=1`, and `GIT_CONFIG_COUNT=1`/`GIT_CONFIG_KEY_0=credential.helper`/`VALUE_0=` so even
  a credential helper written into the cloned repository's own `.git/config` is neutralised. SSH commands
  always carry `-F <run>/hardening/ssh_config.hardened` (on macOS ssh takes `~` from the passwd entry, not
  `$HOME`, so overriding HOME does not hide the user's `~/.ssh/config`; `-F` does) plus
  `-o IdentityAgent=none`, the existing `-i` + `IdentitiesOnly=yes`, and a per-run `UserKnownHostsFile`.
  A project with **no** credential but an SSH URL gets a command with no `-i` at all and `BatchMode=yes`.
  The hardened empty files live under `work/{run}/hardening/`, deliberately not in `temp/` (whose
  emptiness after a run is an existing credential-secrecy property).
- **Why the tests are believable**: the criterion is a *count*, not a message — a synthetic helper appends
  a line per invocation, and hardening must leave that count unchanged; the server's accepted-request
  count must not move either. The reviewer removed the test fixture entirely (a throwaway project with an
  empty `EnvironmentOverrides`) and reproduced the same numbers from the product alone, and separately
  showed that an inherited `GIT_ASKPASS` is a real authentication channel that the product now closes.
- **Reference awareness**: deletion reads the referencing projects and refuses with them named; the
  confirmation needs both a visible checkbox and the credential id in the request; a confirmed delete
  unassigns through the FK and writes an audit line per project.
- **Incident, recorded because it affected the operator**: while designing the ambient probes, the
  developer ran `git credential fill` without disabling the system configuration, and two early
  control-group clones inherited the system helper chain — on macOS that chain includes
  `git-credential-osxkeychain`, so the operator was prompted by the keychain several times. No real
  credential was read (the probes targeted synthetic hosts and synthetic helpers) and the practice was
  stopped: every subsequent probe runs under `env -i` with a private HOME, a self-written helper and
  self-generated keys, and `GitCli.Run` now injects the hardening by default so no test can reach the host
  helper by omission.
- Review rounds: PASS with three P2 findings, all closed — a restart test that actually restarts (kill the
  server process, verify the old PID left the process table, assert a new PID, then read the assignment
  from the new process and from the database), the FK above, and a rotation test that used to seed the
  column now exercising the real attach route. The reviewer's SQL-level probe is what makes the FK claim
  trustworthy.
- Limits recorded: the SSH side is shape-pinned (`-F`, `IdentityAgent=none`, `BatchMode=yes`, no `-i`
  without a credential) but there is **no real SSH clone** in the suite — a keyless sshd is the follow-up;
  PostgreSQL was checked at the model-gate level only (Q-008 built the live path; this card added columns
  and an FK, and `has-pending-model-changes` with the PostgreSQL provider is green); and `ON DELETE SET
  NULL` clears `GitCredentialId` while leaving `GitCredentialAssignedAt`/`AssignedByUserId` at their old
  values — invisible in the UI (the block only renders when the id is set) and deliberately accepted, since
  a trigger or a service-side write-back would reintroduce the read/write window the FK was added to close.
- Verification (developer runs and independent review runs): `dotnet build -c Release` 0 warnings;
  `Category=Git` 58, `Category=ProjectUi` 12, `Category=TenantIsolation` 115, fast suite 1030 passed with
  0 skipped, `Category=Milestone` 8 (three consecutive green runs after the restart test was added),
  `Category=Recovery` 4; PostgreSQL model gate `No changes have been made to the model since the last
  migration.` Two load-dependent flakes were observed and are **not** this card's: the long-standing
  `RunLogReconnectTests` duplicate test, and `DashboardOrganizationSwitchPageTests` — the latter is the
  previously unidentified D-002-B2 flake, now captured with a concrete hypothesis (its host sets
  `OfflineAfterSeconds=1` with a one-hour monitor interval, so a seeded `Running` run can be swept to
  `Lost` before the page reads it).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| 1 | Medium | `Project.GitCredentialId` had no writer, so the delivery path was unreachable and every run silently fell back to the worker host's ambient credentials. | Fixed | Attach/detach surface + service write path + FK-backed invariant; a real build now clones from the delivered credential while a synthetic ambient credential is provably ignored. |
| 2 | Medium | An inherited `GIT_ASKPASS` (or `credential.helper` from the cloned repo's own config, or `IdentityAgent`/`ProxyCommand` from the user's `~/.ssh/config`) could authenticate even with no credential assigned. | Fixed | `GIT_ASKPASS`/`SSH_AUTH_SOCK` cleared, `GIT_CONFIG_NOSYSTEM=1`, empty `GIT_CONFIG_GLOBAL`, `GIT_CONFIG_COUNT` neutralising `credential.helper`, `-F` hardening ssh config, `IdentityAgent=none`. |
| 3 | Low | `ON DELETE SET NULL` leaves the attribution columns populated after the credential is deleted. | Accepted | Invisible in the UI; a trigger or service-side write-back would reintroduce the read/write window the FK closed. Asserted explicitly so it cannot change silently. |
| 4 | Low | The SSH hardening is shape-pinned only; no real SSH clone is exercised. | Open (follow-up) | Recorded as a limit; a keyless sshd fixture is the follow-up card. |

## Verification

- Related files: `src/Server/Git/*` (`GitCredentialService`, endpoints, model configuration, validation),
  `src/Server/Data/Entities/Project.cs`, both migrations (`…AddProjectCredentialAssignment`,
  `…AddProjectGitCredentialForeignKey`), `src/Server/Components/Pages/Projects/ProjectSettings.razor`,
  `src/Server/Components/Pages/Runs/RunDetail.razor`, `src/Server/Components/Shared/RunDisplay.cs`,
  `src/Server/Runs/RunService.cs`, `src/Shared/Pipelines/PipelineFailureReasons.cs`,
  `src/Worker/Git/{GitEnvironment,GitWorkspace}.cs`, `src/Worker/Pipelines/PipelineRunner.cs`,
  `src/Worker/WorkDirectory.cs`, `docs/audits/q-006-surface-inventory.md` (two new routes),
  tests: `Git/GitCredentialAssignmentTests`, `Git/GitAmbientCredentialHardeningTests`,
  `Git/GitCredentialAssignmentMigrationTests`, `Git/AuthenticatedHttpsGitServer`,
  `Milestone/MilestoneGitCredentialTests`, `RunUi/RunDetailCredentialMessageTests`,
  `Tenancy/TenantIsolationHttpSurfaceProbeTests`, `Tenancy/TenantIsolationAuditProbes`.
- How to run/verify: `dotnet build -c Release`; `--filter "Category=Git"`; `Category=ProjectUi`;
  `Category=TenantIsolation`; the fast suite; `Category=Milestone` (real processes); the PostgreSQL model
  gate with the provider prefix; `Category=Recovery` when the milestone/recovery hosts change.
- Results (observed 2026-11-14; developer runs plus independent review runs and a re-verification): build
  0 warnings; Git 58/58; ProjectUi 12/12; TenantIsolation 115/115; fast 1030/1030 with 0 skipped;
  Milestone 8/8 (three consecutive green); Recovery 4/4; PostgreSQL model gate green. The reviewer's
  fixture-free reproduction and SQL-level FK probe are described in TC-001 and §Assignment invariant.
  Limits: no real SSH clone; PostgreSQL verified at the model gate only; stale attribution after a
  deletion (accepted, asserted).
