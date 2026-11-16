# API v1

Zenkins' machine-facing API (card F-027): an organization mints a scoped token, and a script
triggers, reads, tails and cancels runs without a browser. The API drives the **same** run service the
pages use - the same branch/tag/parameter validation, the same project gate (`project-disabled`,
`queue-full`), the same queue - so the API and the UI cannot diverge.

The only other machine-facing surfaces are the worker API (`/api/workers/*`, worker tokens) and the
signed webhook. This page documents `/api/v1` only.

## Tokens

- Mint on the **API tokens** page (`/api-tokens`), Owner-only: a label, and any of the scopes
  `trigger`, `read`, `cancel`.
- The plaintext (`zapi_…`) is shown **exactly once** at creation. Only its SHA-256 hash is stored, so
  a database leak cannot impersonate the organization and a lost token cannot be re-read - mint a new
  one and revoke the old one.
- The page lists each token's scopes, creation time and last use (updated at most once a minute), and
  revokes it. Revoking is immediate and permanent; requests with a revoked token get `401`.
- A token has no expiry unless a row says so (`ExpiresAt`); minting from the UI creates one without an
  expiry.
- The token is the standing authority: the Owner role is checked when minting, not on every request.

## Authentication

Every `/api/v1` request carries the token as a bearer credential:

```
Authorization: Bearer <token>
```

A query-string token is deliberately **not** supported: a `?token=` URL ends up in access logs,
proxy logs and shell history. A session cookie never authorizes an API call, and an API token never
authorizes a page.

## Refusal shapes

| Status | When | Body |
|---|---|---|
| `401` | no credential, unknown token, revoked token, expired token | empty, no tenant detail |
| `403` | the token lacks the route's scope | empty (authorization policy, before any lookup) |
| `404` | the project or run does not exist **in the token's organization** (a foreign id is the same answer) | empty, no existence oracle |
| `429` | the token's per-minute budget is spent | `{"error":"rate-limited"}`, `Retry-After: <seconds>` |
| `400` | a malformed request | `{"error":"<code>","message":…,"fieldErrors":[{"field":…,"code":…}]}` |
| `409` | the project refused the trigger (`project-disabled`, `queue-full`) | `{"error":"<code>","message":…}` |

The `400` and `409` codes are the **form's own codes** (`branch-invalid`, `tag-invalid`, `tag-unknown`,
`timeout-invalid`, `parameter-unknown`, `parameter-invalid`, `steps-missing`, `project-disabled`,
`queue-full`), because both surfaces call the same validation. The remaining API-only codes are
`request-invalid`, `idempotency-key-invalid`, `from-invalid`, `page-invalid`, `rate-limited`, plus the
token-management codes (`name-required`, `name-invalid`, `name-taken`, `scope-required`,
`scope-invalid`, `token-already-revoked`) on the forms.

One documented exception to "every 400 carries a body": a request whose JSON **does not parse at all**
never reaches a handler - the framework's body binder answers `400` with an empty body, which is the
existing, global behaviour of every JSON endpoint in this server (the worker API included), not a
choice of this route. Every 400 a handler produces carries the body shape above.

The budget defaults to 120 requests per minute per token (`Api:RateLimitPerMinute`). It is spent before
any project or run is looked up, so a `429` says nothing about whether an id exists.

## Routes

| Method | Route | Scope | Answer |
|---|---|---|---|
| `POST` | `/api/v1/projects/{projectId}/runs` | `trigger` | `201` + the run resource, `Location: /api/v1/runs/{id}` |
| `GET` | `/api/v1/projects` | `read` | `200` + a page of projects |
| `GET` | `/api/v1/projects/{projectId}/runs` | `read` | `200` + a page of runs, newest first |
| `GET` | `/api/v1/runs/{runId}` | `read` | `200` + the run resource |
| `GET` | `/api/v1/runs/{runId}/log?from=<seq>` | `read` | `200` `text/plain` + `X-Zenkins-Last-Seq` |
| `POST` | `/api/v1/runs/{runId}/cancel` | `cancel` | `200` + `{"runId":…,"outcome":…,"status":…}` |

List routes take `?page=<n>` (default 1) and `?pageSize=<n>` (default 20, maximum 100) and answer
`{"items":[…],"page":1,"pageSize":20,"total":42}`.

### Trigger

```bash
curl -sS -X POST https://zenkins.example.com/api/v1/projects/<project-id>/runs \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: deploy-2026-11-16" \
  -d '{"branch":"main","requiredTags":"linux","timeoutMinutes":"30","parameters":{"GREETING":"hi"}}'
```

- `branch` blank or absent means the project's default branch.
- `timeoutMinutes` is the same **string** the form posts (so a bad value is `timeout-invalid`, not a
  JSON binder error).
- An optional `Idempotency-Key` (≤200 characters) makes a retried post answer the run the first one
  queued: the retry gets `200` with `Idempotency-Replayed: true` and the same `id`, instead of queueing
  a second run. Keys are scoped to the token.
- The `201`/`200` body is the run resource (below); `id` is the run's reference. Run **numbers** are
  not part of this release (card F-036 owns them); do not expect a `number` field.

### The run resource

```json
{
  "id": "…",
  "projectId": "…",
  "status": "Running",
  "triggerType": "Api",
  "branch": "main",
  "commit": "…",
  "pipelinePath": "pipeline.lua",
  "pipelineName": "build",
  "workerName": "linux-agent-1",
  "createdAt": "2026-11-16T09:00:00+00:00",
  "startedAt": "2026-11-16T09:00:01+00:00",
  "endedAt": null,
  "exitCode": null,
  "failureReason": null,
  "timeoutSeconds": 1800,
  "deadlineAt": "2026-11-16T09:30:01+00:00",
  "projectDisabled": false,
  "steps": [
    {"id": "…", "index": 1, "name": "Restore", "status": "Success", "startedAt": "…", "endedAt": "…", "exitCode": 0, "failureReason": null}
  ],
  "url": "/api/v1/runs/…"
}
```

Deliberately absent: **build-parameter values** (F-041's password-typed parameters do not exist yet,
and the API never echoes a parameter value), secrets, credentials, and any artifact URL. The status,
steps, failure reason and numbers are the record.

### Log tailing

```bash
curl -sS https://zenkins.example.com/api/v1/runs/<run-id>/log?from=1720 \
  -H "Authorization: Bearer <token>"
```

The body is `text/plain`, the same lines in the same order the run page's log download serves (the
server stores lines the worker already redacted; there is no second redaction step, so the two surfaces
are byte-identical). `from=<seq>` means "I already have every line up to this sequence number" - the
response carries only newer lines. The response's `X-Zenkins-Last-Seq` header is the offset to ask for
next: poll with `?from=<that value>` to tail a running build without re-reading the log. A non-numeric
or negative `from` is `400 from-invalid`.

### Cancel

```bash
curl -sS -X POST https://zenkins.example.com/api/v1/runs/<run-id>/cancel \
  -H "Authorization: Bearer <token>"
```

Same semantics as the run page's button (card F-009): a `Queued` run becomes `Canceled` immediately; a
`Running` run is marked cancel-requested and its worker is signaled, and it stays `Running` until the
worker reports the kill; a terminal run is a friendly no-op. `outcome` is `canceled`,
`cancel-requested` or `already-finished`, and `status` is the run's state after the call.

The actor recorded on the run is the **token**, not a person: an API-initiated cancel writes
`canceled-by-api <token label>` where a page cancel writes `canceled-by-user <email>` (it becomes the
run's `FailureReason` for a queued cancel). The token's label is therefore worth choosing well - it is
what the run's history will say about who stopped the build.

## Not in this version

- No run numbers (F-036), no GraphQL, no SDK, no CLI wrapper.
- No token expiry UI (the column exists), no per-project scoping (a token covers its organization).
- No `?token=` query-string credential, ever.
- Two concurrent posts carrying the same new `Idempotency-Key` can both queue a run before the unique
  index decides the winner; the answer names the remembered one. The guarantee this release makes is
  the one the card names: a **retried** post (a lost response) queues one run.
