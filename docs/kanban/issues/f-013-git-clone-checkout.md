---
id: F-013
title: Git: clone, fetch, checkout with HTTPS/SSH credentials
type: feature
priority: P1
size: L
status: todo
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

- [ ] `GitWorkspace` clones a repository into `work/{run-id}/repository` at a branch, resolves the
      SHA, and fails with a clear, credential-free message for: unknown branch, unreachable host,
      auth rejected, non-empty target directory, and a path that escapes the work root.
- [ ] HTTPS-token authentication works end to end against a local HTTP git server fixture, with the
      token supplied only through `GIT_ASKPASS`: a test asserts the token appears in neither the
      child's argv, nor `GIT_ASKPASS`'s path/content after cleanup, nor any captured log line.
- [ ] SSH-key authentication is constructed correctly (unit-pinned command line and file
      permissions), and the key file is deleted after the operation.
- [ ] `GitCredential` entity + migration + Owner-only create/rotate/delete; the worker fetch endpoint
      returns material only for a run assigned to the calling worker; cross-tenant and foreign-run
      attempts are indistinguishable 404s.
- [ ] `BuildRun.GitCommit` exists, is set from the accepted report, and is immutable afterwards (a
      second accepted report for the same run cannot rewrite it).
- [ ] `checkout()` is available to the runtime as a host primitive (registered through F-011's
      `RegisterHostFunction`, yielding while it works): it is idempotent — already at the run's
      commit is a no-op, otherwise it fetches and re-checks-out the pinned commit.
- [ ] LFS behaviour is honest: with `git-lfs` present the fixture's pointer is materialised; without
      it the run logs one warning naming `git-lfs` and continues (no silent pointer files).
- [ ] Submodule fixture checks out recursively with the credential environment applied.
- [ ] A clone that exceeds the run's remaining budget is killed with the process tree and reported as
      `timeout` (F-009 semantics), leaving no partial git process behind.
- [ ] Config knobs (`ZENKINS_GIT_DEPTH`, `ZENKINS_GIT_KNOWN_HOSTS`, per-operation timeout) are
      parsed, validated, defaulted and pinned in `WorkerConfigurationTests`.

## Subtasks

- [ ] `src/Worker/Git/{GitWorkspace,GitCredentialMaterial,GitCommand,AskPassScript}.cs` (new):
      argv-only invocation, environment construction, askpass/key materialisation and cleanup,
      commit resolution, LFS/submodule handling, timeout and kill.
- [ ] `src/Worker/Git/GitLogFilter.cs`: registers credential values with the run's redaction filter
      before any git command runs (shares the filter F-015 extends; a minimal filter lands here).
- [ ] Server: `GitCredential` entity + migration; `Secrets`-style protector purpose; Owner-only CRUD;
      worker credential-fetch endpoint with run/worker validation.
- [ ] `BuildRun.GitCommit` + accepted-report extension (Server writes it once; conflict on rewrite).
- [ ] Runtime primitive `checkout()` + registration seam (F-011 contract §6).
- [ ] Fixtures: local bare repository builder, an HTTP git server (e.g. `git http-backend` behind the
      test host, or a Kestrel static handler with dumb HTTP) for the token path, an SSH-key case as
      a construction pin, an LFS repo and a submodule repo.
- [ ] Tests (`Category=Git`): clone/branch/SHA, credential secrecy, SSH construction, idempotent
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
- Results: not run (card not started).
