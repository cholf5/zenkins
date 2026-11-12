---
id: F-005
title: Worker console host (register, heartbeat, reconnect)
type: feature
priority: P1
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

`src/Zenkins.Worker` becomes a real console host: it registers with the Server using a registration
token, persists its worker token locally, keeps heartbeating while online, reconnects with backoff
after failures, and shuts down gracefully — with no ASP.NET Core dependency.

## Background

PRD §3.4.1 (Local and Remote Workers share one implementation), §3.4.3 (worker connects outbound;
no inbound port), Phase 2 checklist. The current `Program.cs` is a stub that prints a hint and
exits. `WorkerRuntimeConfigTests` guards that the Worker keeps running on the base runtime (no
ASP.NET shared framework) — that guard must keep passing, which is why this card uses plain
`HttpClient` only.

Configuration (environment variables; documented in the card and `docs/development.md` is updated
in a later card if needed):

| Variable | Meaning | Default |
|---|---|---|
| `ZENKINS_SERVER_URL` | Server base URL | required |
| `ZENKINS_REGISTRATION_TOKEN` | one-time token from an Owner | required only on first registration |
| `ZENKINS_WORKER_NAME` | worker name | machine name |
| `ZENKINS_WORKER_TAGS` | comma-separated tags | empty |
| `ZENKINS_HEARTBEAT_SECONDS` | heartbeat interval | `15` |

## Acceptance Criteria

- [ ] On first start with a registration token the worker registers (F-004 endpoint), persists the
      returned worker token under the platform user-profile directory with restrictive permissions
      (`0700` dir / `0600` file on POSIX), and prints what it is doing to the console.
- [ ] On later starts it authenticates with the persisted worker token (heartbeat path), refreshing
      capabilities — no new registration token needed.
- [ ] Heartbeat loop at the configured interval; on transport failure it retries with capped
      exponential backoff plus jitter and logs state transitions, instead of crashing or spamming.
- [ ] SIGINT/SIGTERM (Ctrl+C) triggers a graceful stop: in-flight HTTP call settled, a best-effort
      final heartbeat with a short timeout, exit code 0.
- [ ] `Zenkins.Worker.csproj` references only `Zenkins.Shared` and BCL packages; the existing
      Worker runtime-config guard test still passes.
- [ ] Invalid configuration (missing server URL, unparseable interval) produces a clear one-line
      error and a non-zero exit code — not a stack trace.

## Subtasks

- [ ] Config parsing + validation (unit-tested).
- [ ] Token file store: path scheme per server host + worker name, atomic write, restrictive
      permissions (unit-tested on POSIX; Windows best-effort ACL noted).
- [ ] Registration + heartbeat client (`HttpClient`, JSON), backoff scheduler, console logging.
- [ ] Graceful shutdown wiring (`Console.SignCancelling`/`PosixSignalRegistration`).
- [ ] Process-level E2E: spawn the worker against a test-host Server, assert registration and ≥1
      heartbeat, then graceful stop.

## Dependencies

- F-004

## Test Cases

### TC-001: First registration consumes the token once

Steps:
1. Start the worker against a test server with a valid registration token.

Expected:
- Worker registers, token file created with restrictive permissions, worker visible on the server;
  a second fresh start uses the persisted worker token (no registration call).

### TC-002: Server unreachable → backoff, no crash

Steps:
1. Start the worker pointed at a closed port; then start the real server a few seconds later.

Expected:
- Bounded-backoff retries in the log, process stays alive, registers once the server appears.

### TC-003: Graceful shutdown

Steps:
1. Registered worker receives SIGINT/SIGTERM.

Expected:
- Exit code 0 within a few seconds; best-effort offline heartbeat sent (server may mark it Offline
  by staleness later — that is F-010's concern, not a failure here).

## Development Log

### 2026-11-13

- (pending implementation)

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: (pending)
- How to run/verify: `dotnet build -c Release` && `dotnet test -c Release`
- Results: (pending)
