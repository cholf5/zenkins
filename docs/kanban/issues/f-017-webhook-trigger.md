---
id: F-017
title: Webhook trigger: signature, branch filter, replay protection
type: feature
priority: P1
size: M
status: done
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

A `git push` to a configured branch starts a run for the pushed commit, with the request
authenticated by a per-webhook secret, replayed deliveries ignored, and pushes to other branches
accepted-but-ignored. Manual triggering keeps working unchanged.

## Background

PRD §3.9: support push and branch updates, branch filtering, webhook secret verification, and
duplicate-trigger prevention; generic format first, compatible with common Git services, without
per-provider integration modules. PRD §3.5 lists the run's Trigger Type and Git Commit as first-class
run facts; F-014 made `GitBranch`/`GitCommit` the run's pinned inputs.

Decisions:

- **The URL carries only an opaque webhook id** (`POST /webhooks/{webhookId}`), never an
  organization, project or name. An unknown id, a disabled webhook and another tenant's id are the
  same plain 404 with an empty body — the endpoint must not become a project-existence oracle.
- **Signature verification is GitHub-compatible HMAC-SHA256 over the raw request body**
  (`X-Hub-Signature-256: sha256=<hex>`), which GitHub and Gitea both send; GitLab's
  `X-Gitlab-Token` shared secret is also accepted for compatibility. Comparison is constant-time in
  both cases. A bad or missing signature is a 401 with no detail (no "expected vs actual", no
  hint about which scheme was tried).
- **The body is read once, raw, before model binding** — signature verification over a re-serialized
  body is a class of bug this avoids by construction — with a 1 MB cap and a 400 for malformed JSON.
- **Payload normalisation is one small parser, not three integrations**: `ref`, `after`, `before`,
  plus `repository.clone_url` when present (GitHub/Gitea/GitLab all use `ref`/`after`/`before` for
  pushes). A payload without a usable `ref`/`after` is a 400. A delete event (`after` all zeroes) is
  202-ignored, never a run.
- **Replay protection has two layers**: the delivery id (`X-GitHub-Delivery`, `X-Gitea-Delivery`,
  `X-Gitlab-Event-UUID`) is unique per webhook in a dedupe table with a TTL (default 7 days, pruned by
  the existing sweeper pattern); when no delivery id is present, the dedupe key is the body hash
  within a short window (default 10 minutes), which also absorbs a client retry storm. A duplicate is
  a 202 with the original outcome, not an error — git hosts retry, and a retry is not an attack.
- **Branch filter** supports exact names and a trailing `*` wildcard (`release/*`), defaulting to the
  project's default branch. A non-matching branch is 202 with `ignored: branch` and creates nothing.
- **The run is created exactly like a manual one** (`TriggerType = Webhook`, no `TriggerUserId`,
  `GitBranch` from the ref, `GitCommit` = `after`, `PipelinePath` snapshot, tags from the webhook
  config, run timeout from the server default). The webhook does not execute anything itself, so all
  of F-014/F-015's failure modes apply unchanged.
- **The secret is shown once**: unlike organization secrets (write-only, never readable), a webhook
  secret must be copied into the git host, so create and rotate return the plaintext exactly once and
  the page offers "copy" at that moment; afterwards the API returns the configured state only. This is
  the documented asymmetry, not an oversight.
- **Rate limiting is per webhook** (default 60 requests/minute, configurable) to keep an
  authenticated-but-compromised secret from queuing unbounded runs; 429 with `Retry-After`.

## Acceptance Criteria

- [x] `Webhook` entity (`OrganizationId`, `ProjectId`, `Name`, `SecretCiphertext`, `BranchFilter`,
      `RequiredTags`, `Enabled`, `CreatedAt/UpdatedAt`, `CreatedByUserId`) + migration, unique
      `(ProjectId, Name)`; secret encrypted with the F-012 protector (purpose
      `Zenkins.Webhooks.v1`); PostgreSQL contract gate clean.
- [x] `POST /webhooks/{webhookId}` accepts a GitHub-shaped push, a Gitea-shaped push and a
      GitLab-shaped push for the same commit, creating one run each with the right branch and commit.
- [x] Invalid signature, missing signature, malformed body, oversized body and a payload with no
      `ref`/`after` are each rejected with the specified status and no run; no response leaks whether
      the id exists, which signature scheme was tried, or any tenant detail.
- [x] Replay: the same delivery replayed ten times creates exactly one run; a body without a delivery
      id replayed within the window creates one run, and after the window a new run is allowed.
- [x] Branch filter: matching branch creates a run; non-matching is 202 `ignored: branch` with no
      run and no error; `release/*` matches `release/1.2` and not `main`.
- [x] Delete events (`after` all zeros) create nothing and are 202.
- [x] The created run reports `TriggerType = Webhook`, the exact commit, the branch and no trigger
      user; it flows through claim/execute/log/artifacts with no webhook-specific path.
- [x] A webhook whose project has no eligible worker leaves the run `Queued` (normal behavior) and
      the delivery is still de-duplicated.
- [x] Secret lifecycle: create/rotate returns the plaintext once; a later read returns only
      "configured / rotated at"; the plaintext never appears in logs or the run page.
- [x] Rate limit returns 429 with `Retry-After` and creates no run.
- [x] Project settings page: create, rotate, enable/disable, delete a webhook, showing the endpoint
      URL and the branch filter; the plaintext appears exactly once (creation/rotation response).
- [x] Tenant isolation: a member of another organization cannot create, list, rotate or delete
      webhooks for a project; the Q-001 harness gains webhook probes.

## Subtasks

- [x] `Webhook` entity + migration + Owner/Member permission rules (project edit rights).
- [x] Endpoint + raw-body signature verification + normaliser + dedupe store + rate limiter +
      run creation through the existing `RunService` path.
- [x] Webhook secret lifecycle endpoints/UI (show-once) and per-webhook delivery log (last delivery,
      last outcome) for the settings page.
- [x] Tests (`Category=Webhooks`): three payload shapes, signature matrix, replay (id and hash),
      branch filter (exact and wildcard), delete event, run creation facts, rate limit, secret
      lifecycle, access control, tenancy probes.

## Dependencies

- F-014 (a run is a pipeline run with a branch/commit)
- F-012 (encryption for the webhook secret)
- F-013 (the commit the webhook names is what the worker checks out)

## Test Cases

### TC-001: A push starts a run for that commit

Steps:
1. Configure a webhook for `main` with a secret; sign a GitHub-shaped push for commit X on `main`.
2. POST it; wait for the run to reach a terminal state.

Expected:
- One run exists for branch `main` and commit X, `TriggerType = Webhook`, and the worker checks out
  exactly X (F-013's reported commit matches).

### TC-002: Signature and existence are indistinguishable failures

Steps:
1. POST with a wrong signature, then with no signature, to a valid id.
2. POST a validly signed body to an unknown id, then to a disabled webhook.

Expected:
- (1) 401 with an empty body both times.
- (2) 404 with an empty body both times, identical to an unknown route's shape; no run created in
  any case.

### TC-003: Replay creates one run

Steps:
1. Send the same delivery (same delivery id) ten times.
2. Send a delivery without a delivery id ten times within the window; then once after the window.

Expected:
- Ten replays → one run, nine 202 duplicates.
- No-id repeats → one run within the window, a second run after it.

### TC-004: Branch filter and delete events

Steps:
1. Push to `feature/x` (filter `main`, `release/*`); push to `release/1.2`.
2. Send a delete event for `main`.

Expected:
- `feature/x`: 202 `ignored: branch`, no run. `release/1.2`: a run. Delete: 202, no run.

## Development Log

### 2026-11-13

- Planned from the v0.3 plan; no work started. "Cancel in progress on a new push" is deliberately
  absent (parking lot) — v1 supports overlapping runs per branch, which is what the PRD's branch
  filter describes and what the queue already handles.

### 2026-11-13 — implemented, reviewed, reworked, accepted

- **The card's own dependency exposed a real defect, and the claim contract grew to fix it.** The
  review found that a webhook run recorded the pushed commit while the worker checked out the branch
  tip: two pushes seconds apart (which this card explicitly allows to overlap) could leave run A
  building commit B's tree and reporting A. `BuildRun.GitCommit` already said it is produced by the
  worker's `rev-parse` and never inferred by the server, and that sentence had quietly stopped being
  true for webhook runs. The claim now carries the commit, the worker checks that commit out and
  re-reads HEAD, and a report that disagrees is refused with `git-commit-mismatch` before any write.
  A test against a real repository moves the branch tip forward and proves the run still builds the
  pinned commit with the later file absent.
- **Verification is over raw bytes, by construction.** The endpoint reads the body in chunks and hands
  a byte array to the signature check; there is no model to re-serialize. Two tests pin the
  consequence: a compact signature over a re-formatted body is refused, and the same body signed
  verbatim is accepted.
- **Replay is a unique index, not a check.** Ten concurrent deliveries create one run; the losers read
  the winner's row and answer 202. The first implementation sampled its freshness clock before
  inserting, so a loser could judge the winner's row stale, delete it and queue a second run - a
  failure that appeared once in a full run. Sampling moved after the insert and the freshness check
  lost its negative-age short circuit, and there is now a clock-injection test that does not depend
  on a race.
- **The rate limiter's key is the id in the URL, not the delivery id.** Keying by delivery would make
  a new id a fresh budget, and limiting only known ids would turn 404-versus-429 into an existence
  oracle. A bucket exists per raw id with a bound and an expiry; the consequence - a known webhook out
  of budget answers 429 to duplicates too - is the intended trade.
- **Both sides of the money path are tested rather than asserted.** A chunked body without a length
  is refused without being buffered (with production counters, not a test hook, and a mutation that
  makes it red), and the rate limiter now runs before the body is read, so an oversized flood cannot
  buy the work of reading it. A delivery whose run cannot be queued leaves no row, and the same
  delivery id still builds afterwards - the failure mode that would otherwise silence a push forever.
- Documented: the shallow-clone trade. A pinned commit must exist in the clone's history, so
  `ZENKINS_GIT_DEPTH > 0` can make a checkout fail honestly when a pushed commit falls outside the
  shallow window; it fails rather than silently building the branch tip.
- Gate: `dotnet build -c Release` 0 warnings 0 errors; `Category=Webhooks` 69/0/0; full suite
  883/0/0, zero skipped; `Category=Git` 48 unchanged; PostgreSQL contract gate exits 0 without a
  running database.
- Registered, not changed: the rate limiter has properties documented but no unit tests of its own
  (bound, eviction, clock rollback failing open, and the fixed window's double burst at a boundary);
  and the artifact temp-snapshot test is sensitive to a second `dotnet test` process on the same
  machine, because the snapshot deliberately scans the whole temp tree instead of a private prefix.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Data/Entities/Webhook.cs`, `src/Server/Data/Migrations/*`,
  `src/Server/Webhooks/*`, `src/Server/Runs/*`, `src/Server/Components/Pages/Projects/*`,
  `src/Server/Program.cs` (route outside the cookie-auth fallback, like the worker API),
  `src/Server/appsettings.json`, `tests/Zenkins.Tests/Webhooks/*`, `tests/Zenkins.Tests/Tenancy/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Webhooks"`;
  full suite; PostgreSQL contract gate.
- Results: accepted 2026-11-13 after two review rounds and one rework round. `dotnet build -c Release`
  0 warnings 0 errors; `Category=Webhooks` 69/0/0; full suite 883/0/0, zero skipped; `Category=Git` 48
  and `Category=Artifacts` 89 unchanged; PostgreSQL contract gate exits 0 without a running database.
