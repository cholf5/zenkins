---
id: B-012
title: A restarted Worker whose credential the Server refuses stays dead behind a 401 loop — even with a registration token in the environment
type: bug
priority: P1
size: M
status: review
created: 2026-11-19
updated: 2026-11-19
blocked: none
---

## Goal

A Worker that restarts always comes back. If the Server still accepts its persisted credential, the
restart is a no-op; if the Server no longer does, and the operator supplied a registration token, the
Worker registers again and keeps its name (and therefore its row, history and queue) instead of
looping on 401 forever. With no registration token, the one stderr line names the exact file and the
exact fix.

## Background

Reported from a live trial: after a restart the Worker would not reconnect — "even setting
`ZENKINS_WORKER_NAME` does not help, the Server does not recognise it".

The name was never the credential. The Server authenticates a Worker by the SHA-256 of a `zwrk_…`
token (`WorkerService.AuthenticateWorkerAsync`); the name only selects a row on the Server and a
directory on the Worker (`WorkerTokenStore`). So the report's two symptoms point at the credential:

1. **The persisted token is not found.** The store is keyed by `serverUrl.Authority` **and** the worker
   name, so a different host, a different port, a different name, or a different user account (a
   service runner's `HOME`) silently changes the path. The Worker printed a configuration error naming
   the path it looked at — which the report's operator read as "the Server rejects my name".
2. **The persisted token is found but refused.** The Server's data was replaced or restored, or the
   credential was revoked. Every loop then logged the rejection and retried with the same dead token,
   forever. The guidance said "mint a new registration token and re-register if this persists" — but
   re-registering was impossible: `RunAsync` only registered when the token file was **absent**, so a
   registration token in the environment was ignored while the file existed. Recovery required knowing
   about the file and deleting it.

Neither is survivable by waiting, and neither should need file surgery. The Server already supports
same-name re-registration (it updates the row in place and revokes the previous tokens), so the
Worker has a correct recovery move available the moment the Server gives a definitive 401/403.

## Acceptance Criteria

- [x] A persisted credential the Server answers 401/403 to, together with a registration token in the
      environment, is replaced automatically: the Worker registers again, persists the replacement, and
      carries on. The Worker row (id, history, queue) is unchanged — same name, same row.
- [x] The replacement is used by every loop without a restart: the heartbeat loop rotates one shared
      `WorkerCredential`, and the run/probe/validation loops and the log-stream reconnect read it per
      request.
- [x] A healthy persisted credential is never replaced, and a staged registration token is not burned:
      the startup probe only re-registers on a definitive 401/403.
- [x] A transport failure, a request timeout, or a Server that is down says nothing about the
      credential: the Worker keeps the persisted one and the ordinary retry loops stay in charge.
- [x] Without a registration token, nothing is registered, the token file is untouched, and the stderr
      line names `tokenStore.StorePath` and the variable to set.
- [x] A registration token the Server has already burned is reported once and not retried: at startup
      that is exit code 3 with one line; during a running process it is one line and the process stays
      up (a supervised service must not crash-loop).
- [x] No token is ever written to a log: only paths, status codes and the Server-assigned worker id.
- [x] The behaviour is pinned by tests that assert the server-side effect (which token authenticates,
      which rows exist), not by a log line alone.

## Subtasks

- [x] `WorkerCredential`: the one place the token lives, replaceable at runtime (`Volatile` reference).
- [x] `WorkerRunner`: establish the identity up front (`EstablishIdentityAsync`), probe the persisted
      token once when a registration token is configured, and heal on 401/403 in the heartbeat loop.
- [x] Thread the credential through `LogStreamClient`, `WorkerRunLoop`, `WorkerProbeLoop`,
      `WorkerDefinitionValidationLoop` instead of a captured `string`.
- [x] Sharpen the "no persisted token" and "credential refused" messages (path, variable, what it
      means).
- [x] Tests: `WorkerIdentityRecoveryTests` (four cases: restart heal, no-token guidance, no needless
      replacement, running heal).
- [x] Docs: `docs/install.md` §6 + §10, `CHANGELOG.md`.

## Dependencies

- F-004 (token model), F-005 (worker console host), B-011 (no overlap).

## Test Cases

### TC-001: a refused persisted token is replaced by a configured registration token

Steps: register a worker; stop it; revoke its worker token server-side (the effect of a replaced
database); restart it with a fresh registration token and the same name.

Expected: the Worker logs that the Server no longer accepts the persisted token, registers again,
persists the replacement, comes back online; one row for the name, same id; the new token answers 200,
the old one 401.

### TC-002: no registration token means guidance, never a new identity

Steps: as TC-001, restart with no registration token.

Expected: no registration attempt, no new token rows, the file unchanged; the stderr line contains
`Delete '<store path>'` and `ZENKINS_REGISTRATION_TOKEN`.

### TC-003: a healthy credential and a staged token are left alone

Steps: register; stop; mint a fresh registration token; restart with it while the persisted token is
still valid.

Expected: no re-registration, the store byte-identical, the staged registration token still unused,
exactly one active worker token.

### TC-004: a credential revoked while running is replaced without a restart

Steps: register; stop; restart with a staged (unused) registration token; revoke the running token.

Expected: within one heartbeat the Worker re-registers, persists the new token, keeps its row; the new
token answers 200; the staged registration token is burned.

## Verification

| Check | Command | Result |
|---|---|---|
| Build (Debug) | `dotnet build Zenkins.slnx -c Debug` | Build succeeded, 0 warnings |
| New tests | `dotnet test --filter FullyQualifiedName~WorkerIdentityRecoveryTests` | 4/4 passed, three consecutive runs |
| Affected suites | `dotnet test --filter "Category=Workers\|Category=WorkerRuntime\|Category=Pipelines\|Category=LogStreaming\|Category=Probes\|Category=Artifacts\|Category=Steps\|Category=RunLifecycle\|Category=Concurrency"` | 304/304 passed |
| Real worker processes | `dotnet test --filter "FullyQualifiedName~RecoveryWorkerCrashTests\|FullyQualifiedName~RecoveryRestartTests"` | 2/2 passed (persisted-token reconnect line still emitted) |

Not exercised: the milestone harness (needs its own publish), the PostgreSQL row, and a real
service-managed Worker under launchd/systemd/Task Scheduler — the identity logic is
process-environment-independent, but the *directory* a service runner resolves is not, which is why
`ZENKINS_DATA_DIR` is documented as the thing to pin.
