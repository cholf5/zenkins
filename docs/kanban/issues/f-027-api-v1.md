---
id: F-027
title: API v1 — organization API tokens, trigger/cancel/read runs without a browser
type: feature
priority: P1
size: M
status: done
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
- [x] `POST /api/v1/projects/{id}/runs` triggers a run and answers 201 with the run
      id (`number` arrives with F-036 — see Dependencies); parameters, branch,
      required tags and timeout are accepted with the form's validation and the
      form's error codes.

      **Satisfied once the AC stopped claiming the number.** The 201 carries the run
      resource (its `id` is the run id) and a `Location` header; parameters/branch/
      tags/timeout are validated by the form's own routine, and the 400/409 codes are
      the form's (`parameter-unknown`, `branch-invalid`, `timeout-invalid`,
      `project-disabled`, `queue-full`), proven side by side in
      `ApiV1RunApiTests.The_api_answers_the_form_s_parameter_codes` and
      `A_disabled_project_answers_409_project_disabled_and_queues_nothing`. The
      response deliberately has no `number` field: F-036 owns numbering, and this
      card must not invent one (a placeholder number would poison every bookmark).
- [x] `GET /api/v1/runs/{id}` returns status, steps and the failure reason, and never
      a secret value, a credential, or any parameter value (`number` arrives with
      F-036; the product has no run-chain concept, so no chain links are claimed).

      **Satisfied once the AC stopped claiming numbers and chain links.** The resource
      carries status, the frozen
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
      only; the scope checkboxes. "Next to Secrets" is literal: the sidebar entry was
      added after Secrets (`NavMenu.razor`), because a page with no link is an Owner
      typing a URL from memory.
- [x] Q-006 inventory: add the new routes to
      `docs/audits/q-006-surface-inventory.md` — the inventory test fails by
      construction otherwise.
- [x] PRD §3.14 (new): the API contract, token scopes and the refusal shapes.

      **Numbering:** the PRD's last section was §3.11, so the new section is **§3.12**
      (nothing exists at §3.13/§3.14 to put it after). The content is the card's.

## Dependencies

- F-036 (numbers in responses) — **out of scope here, not deferred**: F-036's own AC
  already says API responses show `#N`. This card ships without the field and F-036
  adds it; that is why neither AC below claims a number.
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

### Verification (2026-11-16)

- `dotnet build Zenkins.slnx -c Release`: 0 warnings, 0 errors.
- The repository gate (`scripts/verify.sh`'s exact commands and filters, re-run after the review
  round): fast **1142**, Milestone **12**, Recovery **4**, PublishSmoke **2**, PublishAot **2**,
  GitSshd **3**, Postgres **1 skipped** (no `ZENKINS_TEST_POSTGRES` on this machine - the suite's
  designed skip); all seven 1166. `artifacts/logs/f-027-gate.log` holds every suite's output, one
  section each: the tooling's command clock splits a single invocation, so the script's commands were
  run one suite at a time rather than through one `verify.sh` process.
- `dotnet ef migrations has-pending-model-changes` (PostgreSQL provider): **no pending changes** -
  the migration and the snapshot agree with the model.
- This card's own tests: `Category=Api` (13) plus `TenantIsolationApiProbeTests` (5) and the Q-006
  inventory tests (3) - all green. They pin: 201 + `Location` + the run row (attributed `Api`), the
  form's parameter code answered identically by API and form, the F-028 409 `project-disabled` and
  F-029 409 `queue-full` with nothing queued, the bare 404 for unknown/foreign ids, 403 per scope,
  401 for unknown/revoked tokens (bodyless), 429 before any lookup (a nonexistent run id is rate-limited
  rather than 404), the log body byte-identical to the UI download plus `?from=` and
  `X-Zenkins-Last-Seq`, cancel + the terminal no-op, paging defaults and bounds, one run for a
  retried `Idempotency-Key`, a session cookie alone never authenticating `/api/v1`, and a missing
  scope answering 403 **before** the budget is consulted (`A_session_cookie_cannot_authenticate_the_api`,
  `A_missing_scope_is_a_403_before_the_budget_is_spent`).

### Review round 1 (2026-11-16) - the fixes, item by item

The review's verdict was FAIL on two AC wordings only (the Lead rewrote them; the code had no blocking
defect), plus eight smaller corrections. Nothing else was reworked.

1. **`/api-tokens` had no entry point.** `NavMenu.razor` now carries "API tokens" (Lucide
   `key-square`) directly after "Secrets", so the page is reachable the way every other page is; the
   subtask's "next to Secrets" now means the sidebar order, not just a prose intention.
2. **`TouchLastUsedAt` could turn an authenticated request into a 500.** It now catches
   `DbUpdateException` (which covers the concurrency loser) and logs a Warning. The invariant is in the
   doc comment: last-used is best effort and never vetoes a request that already authenticated - a
   revoke that won the row still takes effect on the next request.
3. **`docs/development.md` baseline.** Re-measured with this card's tests and written back
   (fast 1142 / all seven 1166), so the old 1124/1148 figures are gone rather than deferred.
4. **`docs/api-v1.md`'s code list** now includes `tag-invalid` (the form really returns it) and names
   the API-only codes next to it.
5. **A body whose JSON does not parse** is documented as the framework binder's existing, global
   empty-`400` behaviour (the worker API does the same); every 400 a handler produces carries the
   `error` body. Chosen over a second parsing path in the endpoint, which would duplicate the binder.
6. **API cancels no longer borrow the user vocabulary.** `RunCancelReasons.ForApi(tokenName)` writes
   `canceled-by-api <label>` into the run's `FailureReason`, and `CancelForApiAsync` uses it - the
   record now says an organization token did it, not a person.
7. **`GetTokensAsync` takes the acting user id and checks the Owner role itself** (empty list for a
   Member), so the Owner gate is a service rule like Create/Revoke, not only a page rendering choice.
8. **The trigger answer no longer fabricates a run resource.** When the row is gone between the insert
   and the read, the body is `{id, url}` only (`ApiRunRefResource`) instead of a well-shaped resource
   with `status: ""` and `ProjectId = Guid.Empty`.
9. **Two more tests:** a session cookie alone cannot authenticate `/api/v1` (bare 401), and a missing
   scope answers 403 *before* the budget is consulted (three refused reads that never spend the token's
   one slot). This card's total is now 13 contract facts + 5 tenancy probes.
10. **In-memory paging** stays (the UI's own pattern) and is recorded as an explicit follow-up with
    file and line numbers, below.

The gate baseline in `docs/development.md` was re-measured with this card's tests and written back
(fast 1142, all seven 1166); there is no "stale baseline" follow-up left.

Follow-ups this card leaves, deliberately:

- **Paging is in memory.** `ApiEndpoints.ListProjectRunsAsync`
  (`src/Server/Api/ApiEndpoints.cs:155`, `:173`) reads every run of the project through
  `RunService.GetProjectRunsAsync` (`src/Server/Runs/RunService.cs:878`), which materialises the whole
  list and builds every run's step views (`ToViewsAsync`, `RunService.cs:2465`) before
  `ApiPageResource` takes the requested slice (`ApiEndpoints.cs:428`). That is the UI's own existing
  pattern - the run list page reads the same way - so this card does not change it, but a project with
  thousands of runs would pay for the full read on every `page=1`. A later card should push
  `Skip/Take` plus a `COUNT` into the service (and skip the step views for runs it pages past).
- F-036 (run numbers) is out of scope per Dependencies - the response has no `number` field and gains
  one when F-036 lands. **CLOSED by F-036 (2026-11-16):** `ApiRunResource.Number` now carries the
  project-scoped `#N` in the trigger answer and every read route; `id` stayed the reference, so nothing
  this card promised changed shape (`docs/api-v1.md`'s run resource documents the field; it is nullable,
  because `0` is not a build number - only a pre-migration read has no number).
- F-041 (password-typed parameters) does not exist yet; when it lands, `ApiRunResource` stays
  value-free (it exposes no parameter value at all), so nothing here has to change - a future card
  would only decide whether a *non-secret* value is ever worth exposing.

### 2026-11-16 — independent review: PASS (second round)

Two rounds in an isolated copy of the uncommitted batch. The first ended FAIL for a reason that was
the card's fault, not the code's: AC2 demanded a run `number` and AC3 demanded `numbers` and chain
links, while Dependencies called F-036 "nice to have". Both clauses were removed — F-036's own AC
already promises API responses show `#N`, and the product has no run-chain concept at all, so the
clause was vacuous from the day it was written. Under the corrected wording both criteria are
satisfied.

What the review actually proved, rather than read:

- **No second trigger path.** Read side by side, `TriggerAsync` and `TriggerForApiAsync` differ only
  in the tenant/membership check and the trigger type; everything after them is
  `CreateQueuedRunAsync` — project lookup, the F-028 disabled gate, freestyle/steps, branch, tags,
  timeout, parameters, the queue cap, the transaction. No fork in order, codes, kind branch or
  refusal mapping.
- **The refusal shapes are real, not asserted into existence.** A spent budget answers 429 to an
  *unknown* run id, which is what proves no lookup happened first. A session cookie is rejected with
  a bare 401 (verified with a real login). A missing scope is a 403 that does **not** spend budget:
  the reviewer confirmed a trigger-only token survives three 403s and still gets 201 on its next
  scoped call, with a read-only control token proving the bucket really holds one.
- **The stamp never 500s.** Holding `BEGIN IMMEDIATE` on the SQLite file, an authenticated request
  still answered 200 with one warning in the log.
- The migration is pending-clean under PostgreSQL, and the SQLite provider reports pending on HEAD as
  well — the repo's pre-existing provider asymmetry, not this card's.
- Deviating from the subtask's "source-generated serializers" was accepted: the repo has no
  response-side source-gen context and `/api/workers/*` uses the same `Results.Json` path; the AOT
  promise belongs to the Worker, which this does not touch.

Leftovers recorded as Q-015 rather than reopening the card (a broader stamp catch and its timeout,
the untested `GetTokensAsync` Owner check, one unqualified sentence in the API doc).
