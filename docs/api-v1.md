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
| `409` | the project refused the trigger (`project-disabled`, `queue-full`), or the `Idempotency-Key` already names a different request (`idempotency-key-reused`) | `{"error":"<code>","message":…,"fieldErrors":[{"field":…,"code":…}]}` |

The `400` and `409` codes are the **form's own codes** (`branch-invalid`, `tag-invalid`, `tag-unknown`,
`timeout-invalid`, `parameter-unknown`, `parameter-invalid`, `steps-missing`, `project-disabled`,
`queue-full`), because both surfaces call the same validation. A `409` carries `fieldErrors` exactly as a
`400` does - `queue-full` and `project-disabled` are reported as a form-level `queue` field, a reused
`Idempotency-Key` as an `idempotency-key` field - and its `message` quotes the observed numbers where a refusal has any. The
remaining API-only codes are `request-invalid`, `idempotency-key-invalid`, `idempotency-key-reused`,
`from-invalid`, `page-invalid`, `rate-limited`, plus the token-management codes (`name-required`,
`name-invalid`, `name-taken`, `scope-required`, `scope-invalid`, `token-already-revoked`) on the forms.

One documented exception to "every 400 carries a body": a request whose JSON **does not parse at all**
never reaches a handler - the framework's body binder refuses it. In **Production** that answer is `400`
with an empty body; in **Development** the same request answers `400` with the developer exception page's
HTML instead. Both are the existing, global behaviour of every JSON endpoint in this server (the worker
API included), not a choice of this route. Every 400 a handler produces carries the body shape above.

The budget defaults to 120 requests per minute per token (`Api:RateLimitPerMinute`). It is spent before
any project or run is looked up, so a `429` says nothing about whether an id exists, and a **replay spends
a slot like any other request** (a retried post is still a request).

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
  a second run. The header's full contract - its scope, its refusals, its retention - is the next
  section.
- The `201`/`200` body is the run resource (below); `id` is the run's reference. `number` is the
  project-scoped build number (card F-036), the same `#N` the UI prints - additive, so `id` remains the
  value every route takes.

#### The `Idempotency-Key`, in full

The header is optional. Presented, it names **one request**, and these are the rules a client can rely on
(card B-008):

- **Scope: the token, then one project and one payload.** Two tokens may use the same key without
  colliding. One token's key is bound to the project it first triggered and to that request's payload -
  the branch, `requiredTags`, `timeoutMinutes` and the parameter values (tags compared as a set, the rest
  trimmed, parameters by name).
- **A retry is a replay.** The identical request again answers `200`, `Idempotency-Replayed: true`, the
  same `id`, and a `Location` pointing at that run. Nothing is queued and nothing is spent but the
  rate-limit slot.
- **A different request is a refusal.** The same key against **another project**, or with a changed
  branch/tags/timeout/parameter value, answers `409 idempotency-key-reused` (with `fieldErrors` on
  `idempotency-key`) and queues nothing. It never answers another project's run id.
- **An unknown or foreign project is still a bare `404`.** The project is looked up first, so a key can
  never turn a `404` into a `200`.
- **A replay wins over the project's gate.** If the project was disabled, or its queue filled up, after
  the first post, the retry still answers the run it queued - a replay is the idempotency semantic itself,
  not a new trigger. A **new** key reads those gates and gets `409 project-disabled` / `409 queue-full`.
  (Decided with cards F-028 and F-029 and recorded on both.) One consequence, stated because it is the
  one an operator will meet: a run the **disable** cancelled is still the answer a retry of its key gets
  (status `Canceled`, and the resource's `projectDisabled` shows the live state) until that key expires -
  to build again the same day, send a new key.
- **A key expires.** The binding lives 24 hours (`ApiIdempotencyRecord.Retention`); after that it is
  forgotten and the same key queues a fresh run - a nightly job with a fixed key cannot pin its first run
  forever.
- **Concurrent posts with one new key queue one run.** Two requests carrying the same new key race inside
  one transaction: the database's unique `(token, key)` index picks the winner, the loser's transaction is
  rolled back (its run is never queued), and it answers the winner's run as a replay.

### The run resource

```json
{
  "id": "…",
  "number": 42,
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

Deliberately absent: **build-parameter values**, secrets, credentials, and any artifact URL. The API
never echoes a parameter value, and since F-041 that is a security rule as well as a tidiness one: a
`password` parameter is a per-run input that must never be readable back through an API. A `gitRef` or
`run` value is not sensitive, but it is frozen onto the run and read from the run page - the API's
contract does not change with the type.

`projectDisabled` is the project's **live** state, read when the resource was rendered - not a snapshot
of the run's own moment. A run that was queued while the project was enabled reads `true` after the
project is disabled, which is exactly the question an operator asks ("did this stop because the project
is disabled?").

`number` is the run's build number inside its project (card F-036): assigned once, at trigger time, and
never reused - the value the UI shows as `#N` and the permalinks carry. It is not unique across projects,
so keep using `id` as the reference; `/api/v1/runs/{id}` is the resource's own address. It is `null`, never
`0`, for a row the product did not number (only a database read before the F-036 migrations ran): `0` is not
a build number, and the server refuses one.

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

- No GraphQL, no SDK, no CLI wrapper.
- No rebuild/replay routes (card F-037 ships them on the run page only); a script re-triggers with the
  same body and its own `Idempotency-Key`.
- No token expiry UI (the column exists), no per-project scoping (a token covers its organization).
- No `?token=` query-string credential, ever.
- The guarantee an `Idempotency-Key` makes is the one the paragraph above spells out: a **retried** post
  queues one run, and two posts carrying the same new key still queue exactly one (the loser's
  transaction is rolled back and answers the winner's run). A key is not a lock on the project: two
  *different* keys queue two runs, which is what a client asking twice means.
