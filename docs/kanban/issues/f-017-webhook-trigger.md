---
id: F-017
title: Webhook trigger: signature, branch filter, replay protection
type: feature
priority: P1
size: M
status: todo
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

- [ ] `Webhook` entity (`OrganizationId`, `ProjectId`, `Name`, `SecretCiphertext`, `BranchFilter`,
      `RequiredTags`, `Enabled`, `CreatedAt/UpdatedAt`, `CreatedByUserId`) + migration, unique
      `(ProjectId, Name)`; secret encrypted with the F-012 protector (purpose
      `Zenkins.Webhooks.v1`); PostgreSQL contract gate clean.
- [ ] `POST /webhooks/{webhookId}` accepts a GitHub-shaped push, a Gitea-shaped push and a
      GitLab-shaped push for the same commit, creating one run each with the right branch and commit.
- [ ] Invalid signature, missing signature, malformed body, oversized body and a payload with no
      `ref`/`after` are each rejected with the specified status and no run; no response leaks whether
      the id exists, which signature scheme was tried, or any tenant detail.
- [ ] Replay: the same delivery replayed ten times creates exactly one run; a body without a delivery
      id replayed within the window creates one run, and after the window a new run is allowed.
- [ ] Branch filter: matching branch creates a run; non-matching is 202 `ignored: branch` with no
      run and no error; `release/*` matches `release/1.2` and not `main`.
- [ ] Delete events (`after` all zeros) create nothing and are 202.
- [ ] The created run reports `TriggerType = Webhook`, the exact commit, the branch and no trigger
      user; it flows through claim/execute/log/artifacts with no webhook-specific path.
- [ ] A webhook whose project has no eligible worker leaves the run `Queued` (normal behavior) and
      the delivery is still de-duplicated.
- [ ] Secret lifecycle: create/rotate returns the plaintext once; a later read returns only
      "configured / rotated at"; the plaintext never appears in logs or the run page.
- [ ] Rate limit returns 429 with `Retry-After` and creates no run.
- [ ] Project settings page: create, rotate, enable/disable, delete a webhook, showing the endpoint
      URL and the branch filter; the plaintext appears exactly once (creation/rotation response).
- [ ] Tenant isolation: a member of another organization cannot create, list, rotate or delete
      webhooks for a project; the Q-001 harness gains webhook probes.

## Subtasks

- [ ] `Webhook` entity + migration + Owner/Member permission rules (project edit rights).
- [ ] Endpoint + raw-body signature verification + normaliser + dedupe store + rate limiter +
      run creation through the existing `RunService` path.
- [ ] Webhook secret lifecycle endpoints/UI (show-once) and per-webhook delivery log (last delivery,
      last outcome) for the settings page.
- [ ] Tests (`Category=Webhooks`): three payload shapes, signature matrix, replay (id and hash),
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
- Results: not run (card not started).
