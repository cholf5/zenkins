---
id: F-027
title: API v1 — organization API tokens, trigger/cancel/read runs without a browser
type: feature
priority: P1
size: M
status: review
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

- [x] An Owner can mint and revoke API tokens with scopes (`trigger`, `read`,
      `cancel`); the plaintext is shown exactly once, the stored form is a hash, and
      "last used at" is recorded.
- [ ] `POST /api/v1/projects/{id}/runs` triggers a run and answers 201 with the run
      id and number; parameters, branch, required tags and timeout are accepted with
      the form's validation and the form's error codes.

      **Partial — everything but the number.** The 201 carries the run resource (its
      `id` is the run id) and a `Location` header; parameters/branch/tags/timeout are
      validated by the form's own routine, and the 400/409 codes are the form's
      (`parameter-unknown`, `branch-invalid`, `timeout-invalid`, `project-disabled`,
      `queue-full`), proven side by side in
      `ApiV1RunApiTests.The_api_answers_the_form_s_parameter_codes` and
      `A_disabled_project_answers_409_project_disabled_and_queues_nothing`. There is
      no run **number**: F-036 has not landed, and the card that owns numbering must
      define it rather than the API inventing one (the response deliberately has no
      `number` field).
- [ ] `GET /api/v1/runs/{id}` returns status, steps, failure reason, numbers and
      chain links — never secret values, never a credential, never a parameter value
      the definition marks as password (F-041).

      **Partial — chain links do not exist.** The resource carries status, the frozen
      step list, failure reason, times, exit code, timeout, deadline and the worker
      name, and it carries **no** parameter values at all (F-041's password-typed
      parameters do not exist yet; omitting every value is the safe answer and is
      documented in `docs/api-v1.md`). This repository has no run-chain concept, so
      there is nothing to link to; a later card that introduces chains adds the links
      here.
- [x] `GET /api/v1/runs/{id}/log` returns text/plain with `?from=<offset>` tailing,
      applying the same redaction as the UI's download.
- [x] `POST /api/v1/runs/{id}/cancel` cancels with the same semantics as the UI
      button (queued race handled the way F-009 does).
- [x] `GET /api/v1/projects` and `GET /api/v1/projects/{id}/runs` list with paging
      and the same tenant filter as every other read.
- [x] Every refusal keeps the repo's contract: a foreign or unknown id is a bare
      404 with no existence oracle, a missing scope is 403, an invalid token is 401
      and a rate-limited token is 429 before any lookup.
- [x] An optional `Idempotency-Key` header makes a retried trigger queue one run,
      not two.
- [x] Docs: a short "API v1" page under `docs/` with the token lifecycle, the four
      verbs and a curl example; linked from the README.

## Subtasks

- [x] `ApiToken` entity (org, hash, scopes, created by, expires at, last used at,
      revoked at) + `ApiTokenAuthenticationHandler` (bearer, same shape as the
      worker token handler but resolving an organization + scopes, not a worker).
- [x] Endpoints under one `MapGroup("/api/v1")` with a scope requirement per route;
      JSON via the existing source-generated serializers.

      **Deviation:** the responses use `Results.Json`/`WriteAsJsonAsync`, the same path
      every existing API endpoint (`/api/workers/*`) uses. There is no source-generated
      context for *responses* in this repository - the existing `JsonSerializerContext`
      types cover persisted payloads (parameters, specs, artifacts) - so introducing
      one here would have created a second JSON path rather than reusing the existing
      one.
- [x] Idempotency: an `ApiIdempotencyRecord` row keyed by (token, key) with the
      resulting run id; a replay answers 200 with the same run.
- [x] Rate limiting per token using the existing limiter infrastructure (the
      webhook's per-id budget is the model).
- [x] UI: an API tokens page (mint, list, revoke, last used) next to Secrets, Owner
      only; the scope checkboxes.
- [x] Q-006 inventory: add the new routes to
      `docs/audits/q-006-surface-inventory.md` — the inventory test fails by
      construction otherwise.
- [x] PRD §3.14 (new): the API contract, token scopes and the refusal shapes.

      **Numbering:** the PRD's last section was §3.11, so the new section is **§3.12**
      (nothing exists at §3.13/§3.14 to put it after). The content is the card's.

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

### Implementation notes (2026-11-16)

- **One trigger path, enforced by refactoring, not by agreement.**
  `RunService.TriggerAsync` (the form) and the new `RunService.TriggerForApiAsync` now
  both delegate to one private `CreateQueuedRunAsync`; the API entry differs only in
  the tenant it hands over and in `BuildRunTriggerType.Api` (no acting user, so
  `TriggerUserId` is null). Branch/tag/timeout/parameter validation, the F-028
  disabled gate, F-029's queue cap, the project row lock and the insert are the same
  code. Cancel got the same treatment: one `CancelRunCoreAsync` behind the page's
  button and the API's route, so F-009's queued race has one implementation.
- **The 409 mapping is where F-028/F-029 said it would be.** `RunOperationStatus.Refused`
  maps to `409` with the refusal code (`project-disabled`, `queue-full`) in the body's
  `error` and in `fieldErrors[0].code`; `InvalidInput` stays `400` with the form's
  field-error code. This is the API answer those two cards listed as missing.
- **Refusals.** `401` is one bodyless answer for missing/unknown/revoked/expired tokens.
  `403` comes from a per-scope authorization policy (`RequireClaim` on the API scheme
  only), so a cookie cannot authenticate an API call and a missing scope never reaches
  a handler. `404` is `Results.NotFound()` (empty) for foreign and unknown ids alike.
  `429` is an endpoint filter after authentication/authorization and before any
  project/run lookup, with `Retry-After`; the bucket is keyed by the token row's id.
- **The log tail is the UI's own stream.** `RunService.StreamRunLogAsync(runId, fromSeq)`
  is the same query the download uses with a `Seq > from` predicate, so the two bodies
  are byte-identical (a test compares them); the worker already redacted every line
  before ingest, and the server has no second redaction step that could drift.
  `X-Zenkins-Last-Seq` (answered before streaming starts) is the next `from` value.
- **Idempotency.** A row per `(ApiTokenId, Key)` with the run id; the lookup short-circuits
  a replay to `200` + `Idempotency-Replayed: true`, and the unique index is the guard.
  Known limit, documented on the page: two *concurrent* posts carrying the same new key
  can each queue a run before the index decides (the answer names the remembered run);
  the guarantee is the card's - a **retried** post queues one run.
- **Not echoed, on purpose.** No parameter values (F-041 does not exist; the safest
  contract is "never echo one"), no repository URL, no secrets, no credential, no
  artifact URL. Numbers are F-036's.
- **Q-006.** Nine routes (six API + three token-management) plus a token store row and
  an idempotency row entered `docs/audits/q-006-surface-inventory.md`; the page row
  named `The_api_tokens_page_is_owner_only_and_never_prints_token_material`, and the
  `/api-tokens` page was added to the existing page-render probe.
- **Migration** generated with the PostgreSQL provider
  (`20261116063155_AddApiTokens`), per `docs/development.md`'s contract.
- **Verification.** `dotnet build`, the seven-suite gate and the new tests are recorded
  in the review log below when the reviewer runs them.
