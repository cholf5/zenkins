---
id: F-027
title: API v1 — organization API tokens, trigger/cancel/read runs without a browser
type: feature
priority: P1
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

An organization can mint scoped API tokens and drive Zenkins from scripts and other
systems: trigger a run (with parameters), read a run's status, tail its log, cancel
it — with the same validation, tenant isolation and redaction the UI has.

## Background

The only machine-facing surfaces today are the worker API (`/api/workers/*`, worker
tokens) and the inbound signed webhook. Everything else is a Blazor SSR page posting
an antiforgery-protected form. Consequences: a deploy script cannot trigger
anything, another CI cannot wait for a build, and a `curl` in a pipeline has nothing
to call. Jenkins' equivalents are `/job/x/build?token=`, `/buildWithParameters`,
`/api/json` and `/consoleText`.

The card must not create a second trigger path: it calls the same
`RunService.CreateRunAsync`, so branch/tag/parameter validation, kind guards,
concurrency policy and dedupe cannot diverge from the form.

## Acceptance Criteria

- [ ] An Owner can mint and revoke API tokens with scopes (`trigger`, `read`,
      `cancel`); the plaintext is shown exactly once, the stored form is a hash, and
      "last used at" is recorded.
- [ ] `POST /api/v1/projects/{id}/runs` triggers a run and answers 201 with the run
      id and number; parameters, branch, required tags and timeout are accepted with
      the form's validation and the form's error codes.
- [ ] `GET /api/v1/runs/{id}` returns status, steps, failure reason, numbers and
      chain links — never secret values, never a credential, never a parameter value
      the definition marks as password (F-041).
- [ ] `GET /api/v1/runs/{id}/log` returns text/plain with `?from=<offset>` tailing,
      applying the same redaction as the UI's download.
- [ ] `POST /api/v1/runs/{id}/cancel` cancels with the same semantics as the UI
      button (queued race handled the way F-009 does).
- [ ] `GET /api/v1/projects` and `GET /api/v1/projects/{id}/runs` list with paging
      and the same tenant filter as every other read.
- [ ] Every refusal keeps the repo's contract: a foreign or unknown id is a bare
      404 with no existence oracle, a missing scope is 403, an invalid token is 401
      and a rate-limited token is 429 before any lookup.
- [ ] An optional `Idempotency-Key` header makes a retried trigger queue one run,
      not two.
- [ ] Docs: a short "API v1" page under `docs/` with the token lifecycle, the four
      verbs and a curl example; linked from the README.

## Subtasks

- [ ] `ApiToken` entity (org, hash, scopes, created by, expires at, last used at,
      revoked at) + `ApiTokenAuthenticationHandler` (bearer, same shape as the
      worker token handler but resolving an organization + scopes, not a worker).
- [ ] Endpoints under one `MapGroup("/api/v1")` with a scope requirement per route;
      JSON via the existing source-generated serializers.
- [ ] Idempotency: a `TriggerIdempotency` row keyed by (token, key) with the
      resulting run id; a replay answers 200 with the same run.
- [ ] Rate limiting per token using the existing limiter infrastructure (the
      webhook's per-id budget is the model).
- [ ] UI: an API tokens page (mint, list, revoke, last used) next to Secrets, Owner
      only; the scope checkboxes.
- [ ] Q-006 inventory: add the new routes to
      `docs/audits/q-006-surface-inventory.md` — the inventory test fails by
      construction otherwise.
- [ ] PRD §3.14 (new): the API contract, token scopes and the refusal shapes.

## Dependencies

- F-036 (numbers in responses) — nice to have, not blocking
- F-017 (the rate limiter and refusal shapes it established) — accepted

## Test Cases

### TC-001: A token triggers a parameterized run

Steps:
1. Mint a token with `trigger` scope.
2. `POST /api/v1/projects/{id}/runs` with a parameter value.

Expected:
- 201 with the run id and number; the run's page shows the submitted value.
- The token's `last used at` moved.

### TC-002: Scopes and tenants are enforced without an oracle

Steps:
1. Call the trigger route with a `read`-only token.
2. Call every route with a token of organization A against B's ids.

Expected:
- Step 1 is 403.
- Step 2 is a bare 404 on all of them; no route leaks existence or a field.

### TC-003: A retried trigger with an idempotency key queues one run

Steps:
1. Send the same POST twice with the same `Idempotency-Key`.

Expected:
- One run exists; the second answer is 200 carrying the same run id.

## Notes and Decisions

- Bearer token, never a query-string token: Jenkins' `/build?token=` style puts the
  secret in every access log and proxy log. A query-string form is not offered.
- No GraphQL, no SDK in this card. A CLI (a thin wrapper) is a separate, later card
  if the API proves useful.
