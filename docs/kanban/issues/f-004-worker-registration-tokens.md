---
id: F-004
title: Worker registration, tokens and heartbeat (Server)
type: feature
priority: P1
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

An Organization Owner can mint single-use registration tokens, and a Worker process can register
itself with the Server (name, OS, architecture, version, tags, CPU count, max concurrency) and prove
its identity on every later call with its own worker token; heartbeats keep the worker Online.

## Background

PRD §3.4.2/§3.4.3 and §6 (Worker, WorkerToken entities). Roadmap constraint: communication must be
authenticated from the first call — the first call authenticates with a registration token an Owner
issued; every later call authenticates with the per-worker token returned at registration. Workers
are tenant-owned (`ITenantOwned`, `OrganizationId`): the registration token is minted inside an
active Organization and the worker joins that Organization.

Decisions (review may challenge, but implement these unless the reviewer objects):

- Registration tokens and worker tokens are stored **hashed** (SHA-256) with a readable prefix
  (`zreg_…`, `zwrk_…`); plaintext is shown exactly once at creation. Single-use registration tokens.
- Registering with a name that already exists in the Organization updates that Worker row
  (capabilities, version), revokes its previous active worker tokens and returns a fresh worker
  token — container restarts must not duplicate workers.
- Worker API lives under `/api/workers/*` and is exempt from cookie auth; the token resolves the
  tenant, never a cookie. All auth failures are `401` with no tenant detail.
- Minimal UI: `/workers` page — all Organization Members can view workers; only the Owner creates,
  rotates and revokes registration tokens.

## Acceptance Criteria

- [ ] `Worker` and `WorkerToken` entities, tenant-owned, with a migration generated under the
      PostgreSQL contract (`has-pending-model-changes` clean).
- [ ] Owner can create a registration token (shown once, stored hashed), list tokens
      (created / last-used / revoked, no secrets) and revoke; revoke immediately invalidates.
- [ ] `POST /api/workers/register` with a valid, unused registration token creates (or updates by
      name, per the decision above) the Worker row and returns a worker token; the registration
      token is burned on first use.
- [ ] Registration payload records name, OS, architecture, version, tags, CPU count, max
      concurrency; heartbeat can refresh capability fields and `LastHeartbeatAt`.
- [ ] `POST /api/workers/heartbeat` with a valid worker token succeeds; with a missing, unknown or
      revoked token returns `401` and changes nothing.
- [ ] `/workers` page: list workers of the active Organization only (name, OS, arch, version, tags,
      status, CPU count, last heartbeat); foreign-org workers are invisible (tenant filter).
- [ ] Worker-API requests are authenticated from the first call; no worker endpoint accepts a
      session cookie as authorisation for a worker action.

## Subtasks

- [ ] Entities + PostgreSQL-contract migration + `DbSet`s in `ZenkinsDbContext`.
- [ ] Token hashing/verification helpers + service (issue/burn/revoke/register/heartbeat).
- [ ] `/api/workers/register` + `/api/workers/heartbeat` endpoints with token auth middleware/handler.
- [ ] `/workers` page + token management form endpoints (antiforgery, Owner-only, PRG errors).
- [ ] Tests (`Category=Workers`): registration happy path, burned token, revoked token, bad worker
      token 401, cross-tenant invisibility, name-collision update path.

## Dependencies

- none (data layer, Identity and Owner/Member roles already exist)

## Test Cases

### TC-001: Registration with a valid token

Steps:
1. Owner mints a registration token in Organization A.
2. `POST /api/workers/register` with that token and capability fields.

Expected:
- `200` with a worker token; the worker appears on `/workers` for A with its capabilities;
  the registration token can no longer be used (second register → `401`).

### TC-002: Bad credentials are 401 and change nothing

Steps:
1. Register with an unknown, revoked or already-burned registration token.
2. Heartbeat with an unknown worker token.

Expected:
- `401` every time; no Worker row created or modified; plaintext tokens never logged.

### TC-003: Cross-tenant invisibility

Steps:
1. Register a worker into Organization A. Authenticate as a user whose active Organization is B.

Expected:
- `/workers` shows no row for A's worker; worker rows carry `OrganizationId` under the global
  query filter (no `ExecuteUpdate`/raw SQL bypasses on these entities outside a system scope).

### TC-004: Heartbeat refreshes state

Steps:
1. Register, then heartbeat twice with updated CPU count/tags.

Expected:
- `200`; `LastHeartbeatAt` and capability fields updated; status remains Online.

## Development Log

### 2026-11-13

- (pending implementation)

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: (pending)
- How to run/verify: `dotnet build -c Release` && `dotnet test -c Release --filter "Category=Workers"`
- Results: (pending)
