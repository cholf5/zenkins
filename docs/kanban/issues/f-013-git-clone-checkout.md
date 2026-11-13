---
id: F-013
title: Git: clone, fetch, checkout with HTTPS/SSH credentials
type: feature
priority: P1
size: L
status: done
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

A Worker can materialise a project's repository into `work/{run-id}/repository` at a given branch,
authenticating with an HTTPS token or an SSH key that never appears in argv, in the environment of a
logged command, or in a log line — and it can report the exact commit it checked out.

## Background

PRD §3.2 (project stores repository URL, default branch, pipeline path, credential reference),
PRD §3.3 (pipeline file lives in the repository), PRD MVP checklist ("Git Clone / Fetch / Checkout",
"HTTPS / SSH 认证"), PRD §8 constraint 6 (no shell string building anywhere). F-007 created
`repository/` empty specifically so this card lands without moving anything.

Decisions:

- **`git` CLI, not LibGit2Sharp.** It matches what a user's machine already trusts (credential
  helpers, LFS, submodules, `.gitattributes` conversions), keeps the dependency surface at zero, and
  is what PRD §3.3 assumes. Every invocation is `ProcessStartInfo` with `ArgumentList` — never a
  shell string.
- **Fresh clone per run, no shared object cache.** Isolation is the F-007 property we already sell;
  a cache is a later optimisation with its own corruption story. Full clone by default;
  `ZENKINS_GIT_DEPTH` (default `0` = full) opts into a shallow clone when a project needs the speed.
- **Credentials never travel in argv.** An argv vector (`git clone https://user:token@host/...`) is
  visible in `ps` and in error messages. Instead:
  - HTTPS: `GIT_TERMINAL_PROMPT=0` plus a generated `GIT_ASKPASS` helper in `work/{run}/temp`
    (mode 0700 on POSIX) that answers with the token; the file is deleted when the clone ends,
    success or failure.
  - SSH: the key is written to `work/{run}/temp/id` (mode 0600), `GIT_SSH_COMMAND` pins
    `-i <key> -o IdentitiesOnly=yes -o StrictHostKeyChecking=<policy> -o UserKnownHostsFile=<file>`,
    with `accept-new` as the default policy and `ZENKINS_GIT_KNOWN_HOSTS` for pinning. The
    `accept-new` default is a documented MITM trade-off, not an accident.
  - The token/key is registered with the run's redaction filter (F-015) the moment it is decrypted,
    so even an unexpected echo through git's output cannot reach the log verbatim.
- **Credential delivery is a per-run, worker-authenticated fetch**, not a field in the claim payload:
  the claim response is queued and logged in more places than a secret should be. The worker asks
  for the run's credential at clone time (`GET` with its worker token + run id); the Server resolves
  the project's `GitCredentialId`, decrypts it with the F-012 protector, and answers with the
  material for that one run. A foreign run id or a run not assigned to the calling worker is the
  same generic 404 as everywhere else.
- **`GitCredential` is its own entity** (PRD §6) with `Kind` = `HttpsToken | SshKey`, reuse of the
  F-012 `SecretProtector` (purpose `Zenkins.GitCredentials.v1`), Owner-only management, and the same
  name rules as secrets so it can be referenced from project settings unambiguously.
- **Commit resolution is explicit**: after checkout, `git rev-parse HEAD` (run twice, with a
  `git status --porcelain` sanity check) supplies the SHA that the run reports. `BuildRun.GitCommit`
  is written by the Server from the worker's accepted report, never derived from a branch name.
- **LFS and submodules**: `git lfs` is used when the repository declares LFS and the binary exists
  (otherwise the run logs an honest warning naming the missing tool, rather than silently fetching
  pointer files); submodules are cloned with `--recurse-submodules` and the same credential
  environment. Both are pinned by tests against local fixtures.
- **Scope of this card**: the git layer, the credential plumbing, `BuildRun.GitCommit` and the
  accepted-report extension. **Wiring the clone into the run loop is F-014's first acceptance
  criterion** — until the pipeline run contract exists there is no branch to check out, so this
  card tests the layer directly against real local repositories.

## Acceptance Criteria

- [x] `GitWorkspace` clones a repository into `work/{run-id}/repository` at a branch, resolves the
      SHA, and fails with a clear, credential-free message for: unknown branch, unreachable host,
      auth rejected, non-empty target directory, and a path that escapes the work root.
- [x] HTTPS-token authentication works end to end against a local HTTP git server fixture, with the
      token supplied only through `GIT_ASKPASS`: a test asserts the token appears in neither the
      child's argv, nor `GIT_ASKPASS`'s path/content after cleanup, nor any captured log line.
- [x] SSH-key authentication is constructed correctly (unit-pinned command line and file
      permissions), and the key file is deleted after the operation.
- [x] `GitCredential` entity + migration + Owner-only create/rotate/delete; the worker fetch endpoint
      returns material only for a run assigned to the calling worker; cross-tenant and foreign-run
      attempts are indistinguishable 404s.
- [x] `BuildRun.GitCommit` exists, is set from the accepted report, and is immutable afterwards (a
      second accepted report for the same run cannot rewrite it).
- [x] `checkout()` is available to the runtime as a host primitive (registered through F-011's
      `RegisterHostFunction`, yielding while it works): it is idempotent — already at the run's
      commit is a no-op, otherwise it fetches and re-checks-out the pinned commit.
- [x] LFS behaviour is honest: with `git-lfs` present the fixture's pointer is materialised; without
      it the run logs one warning naming `git-lfs` and continues (no silent pointer files).
- [x] Submodule fixture checks out recursively with the credential environment applied.
- [x] A clone that exceeds the run's remaining budget is killed with the process tree and reported as
      `timeout` (F-009 semantics), leaving no partial git process behind.
- [x] Config knobs (`ZENKINS_GIT_DEPTH`, `ZENKINS_GIT_KNOWN_HOSTS`, per-operation timeout) are
      parsed, validated, defaulted and pinned in `WorkerConfigurationTests`.

## Subtasks

- [x] `src/Worker/Git/{GitWorkspace,GitCredentialMaterial,GitCommand,AskPassScript}.cs` (new):
      argv-only invocation, environment construction, askpass/key materialisation and cleanup,
      commit resolution, LFS/submodule handling, timeout and kill.
- [x] `src/Worker/Git/GitLogFilter.cs`: registers credential values with the run's redaction filter
      before any git command runs (shares the filter F-015 extends; a minimal filter lands here).
- [x] Server: `GitCredential` entity + migration; `Secrets`-style protector purpose; Owner-only CRUD;
      worker credential-fetch endpoint with run/worker validation.
- [x] `BuildRun.GitCommit` + accepted-report extension (Server writes it once; conflict on rewrite).
- [x] Runtime primitive `checkout()` + registration seam (F-011 contract §6).
- [x] Fixtures: local bare repository builder, an HTTP git server (e.g. `git http-backend` behind the
      test host, or a Kestrel static handler with dumb HTTP) for the token path, an SSH-key case as
      a construction pin, an LFS repo and a submodule repo.
- [x] Tests (`Category=Git`): clone/branch/SHA, credential secrecy, SSH construction, idempotent
      `checkout()`, LFS with/without the binary, submodules, failure messages, budget kill,
      config pins.

## Dependencies

- F-012 (protector + Owner-only secret management pattern)
- F-011 (the runtime registration seam `checkout()` uses)

## Test Cases

### TC-001: Clone at a branch, exact commit

Steps:
1. Build a fixture repository with three commits on `main` and one on `feature`.
2. Clone at `feature`; then clone at a non-existent branch.

Expected:
- (1) `HEAD` equals the `feature` tip; the reported SHA matches `git rev-parse HEAD`.
- (2) fails with a message naming the branch and no credential/path leakage.

### TC-002: The token is never observable outside the askpass helper

Steps:
1. Clone an HTTPS fixture with a token credential.
2. Inspect: the child process argv, the work directory tree after the run, the captured log lines.

Expected:
- The token appears in none of them; the askpass file is gone; the clone succeeded.

### TC-003: Credential fetch is scoped to the run and the worker

Steps:
1. Worker A fetches the credential for its own run.
2. A different worker of the same organization fetches the same run.
3. A worker of another organization fetches a run id from organization A.

Expected:
- (1) returns material. (2) and (3) return the same generic 404 body, and the Server logs neither
  attempt as an authorization failure with distinguishable text.

### TC-004: checkout() is idempotent and pinned

Steps:
1. Call `checkout()` on a workspace already at the run's commit.
2. Move `HEAD` to an unrelated commit, then call `checkout()` again.

Expected:
- (1) issues no fetch that changes the tree (asserted by a git command trace).
- (2) returns to the run's commit and the tree matches the pinned SHA.

## Development Log

### 2026-11-13

- Planned from the v0.3 plan; no work started. The run-loop wiring (clone on accept, report the
  commit, then hand control to the pipeline definition) is F-014's first acceptance criterion —
  deliberately separated so this card can be reviewed as a self-contained, testable git layer.

### 2026-11-13 — implemented, independently reviewed twice, accepted

- The first review passed every acceptance criterion and found nine minors; six were closed before
  the commit and three are recorded below. The delta review then checked the six against the source.
- **The accepted-report window is closed, but a neighbouring one is not.** The state transition and
  the commit stamp now share one transaction, so an interruption leaves neither and a redelivery
  still completes. The test proves it with a trigger that aborts between the two writes; removing
  the transaction makes it report `Expected Queued / Actual Running`. What remains is older than this
  card: the transition to `Running` is still check-then-act (its `UPDATE` carries no status
  predicate), so two accepted reports racing can both pass. The commit column stays correct because
  the compare-and-set guards it, but the transition itself is not serialised. Worth closing when
  F-014 drives the run loop.
- Gates that exist because the obvious version would have been green and wrong: git silently ignores
  `--depth` for a bare local path, so the shallow fixture goes through `file://` and asserts
  `--is-shallow-repository`; the `git-lfs` stub asserts its own call log, because being on the PATH is
  not the same as being invoked; the submodule fixture requires the same token for both repositories,
  because the recording shim cannot see a child that execs its own binary - and the comment claiming
  otherwise was removed.
- Token leakage is asserted in seven forms (raw, URL-encoded, base64 with and without padding,
  base64url, and the Basic header payload in both forms) across four places: the child's argv as
  recorded at the process boundary, the shim log, every run-log line, and every file under the work
  tree. The known blind spot: the shim records four credential variables, not the whole child
  environment, so a token encoded into some other variable would go uncaught. No such path exists
  today.
- Gate: `dotnet build -c Release` 0 warnings 0 errors; `Category=Git` 48/0/0; full suite 570/0/0;
  `Category=Lua` 94 and `Category=Secrets` 64 unchanged; `has-pending-model-changes` under the
  PostgreSQL provider exits 0 without a running database; the migration, its designer and the
  snapshot carry Npgsql type shapes with no SQLite type names; no new NuGet package.
- Recorded, not changed: the non-empty-target and path-escape messages include the worker's absolute
  path (the card only requires credential-free, and run logs expose worker paths anyway); `GitCredential`
  has form-POST endpoints but no page, so a user cannot create one until the project-settings page
  lands; and `GitCredentialClient`, `GitWorkspace` and `GitCheckoutHostPrimitive` have no production
  caller yet, which is this card's stated scope.
- A single `Category=Secrets` failure was seen once during development and never reproduced: five
  sequential runs, two more inside the full suite, and a parallel stress probe all came back 64/0/0.
  Structural causes were ruled out - every host gets its own temporary database, no shared fixtures,
  no static or environment state, no fixed ports, no clock bounds in those tests. If it recurs, the
  first suspect is the duplicate-name path in `SecretEndpointsTests`, which is the only place that
  catches a `DbUpdateException` and reuses the context.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Worker/Git/*`, `src/Worker/WorkerRunLoop.cs` (report extension only),
  `src/Worker/WorkerConfiguration.cs`, `src/Server/Data/Entities/{GitCredential,BuildRun}.cs`,
  `src/Server/Git/*`, `src/Server/Data/Migrations/*`, `Directory.Packages.props` (no new package
  expected), `tests/Zenkins.Tests/Git/*`, `tests/Zenkins.Tests/WorkerRuntime/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Git"`;
  full suite; PostgreSQL contract gate.
- Results: accepted 2026-11-13. `dotnet build -c Release` 0 warnings 0 errors; `Category=Git` 48/0/0;
  full suite 570/0/0; `Category=Lua` 94/0/0 and `Category=Secrets` 64/0/0 unchanged. PostgreSQL
  contract gate exits 0 with no running database; migration/designer/snapshot are Npgsql-shaped with
  zero SQLite type names; zero new packages. Two review rounds, no major finding.
