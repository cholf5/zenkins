---
id: F-005
title: Worker console host (register, heartbeat, reconnect)
type: feature
priority: P1
size: M
status: done
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

- [x] On first start with a registration token the worker registers (F-004 endpoint), persists the
      returned worker token under the platform user-profile directory with restrictive permissions
      (`0700` dir / `0600` file on POSIX), and prints what it is doing to the console.
- [x] On later starts it authenticates with the persisted worker token (heartbeat path), refreshing
      capabilities — no new registration token needed.
- [x] Heartbeat loop at the configured interval; on transport failure it retries with capped
      exponential backoff plus jitter and logs state transitions, instead of crashing or spamming.
- [x] SIGINT/SIGTERM (Ctrl+C) triggers a graceful stop: in-flight HTTP call settled, a best-effort
      final heartbeat with a short timeout, exit code 0.
- [x] `Zenkins.Worker.csproj` references only `Zenkins.Shared` and BCL packages; the existing
      Worker runtime-config guard test still passes.
- [x] Invalid configuration (missing server URL, unparseable interval) produces a clear one-line
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

**Implementation decisions.**

- **Host shape.** `src/Zenkins.Worker` is a plain console host (`OutputType=Exe`, no ASP.NET
  dependency): `HttpClient` + source-generated `System.Text.Json` (`WorkerJsonContext`, trim-safe),
  the only project reference is `Zenkins.Shared`. The `WorkerRuntimeConfigTests` guard (no
  `Microsoft.AspNetCore.App` in the runtimeconfig) still passes.
- **Configuration.** Parsed once at startup from the card's env-var table; one extra knob
  `ZENKINS_DATA_DIR` overrides the token directory (so the E2E never touches the real user profile
  and operators can mount a secret volume). Invalid config (missing URL, unparseable URL/interval,
  over-long name/tags) is a single one-line stderr message + exit code 2 — never a stack trace.
- **Token store.** `WorkerTokenStore` persists to
  `<dataDir>/worker-tokens/<server-host+port>/<worker-name>/worker.token` (host/name segments are
  sanitized + hash-disambiguated; hostile names cannot escape the store root). Write is atomic
  (temp file, mode set to `0600` **before** the bytes land, then move over target); the directory
  chain is created `0700`. On POSIX both modes are asserted by unit tests and the E2E; on Windows
  Unix modes are skipped and ACL hardening is deferred (the profile dir is already user-private).
  A corrupt/empty token file is a clear one-line error, not a silent 401 loop.
- **Runner (state machine).** Load persisted token → if absent, register (retrying transport
  failures) → heartbeat loop at `ZENKINS_HEARTBEAT_SECONDS`. Transport failures retry on capped
  exponential backoff + 25% jitter (`BackoffScheduler`, unit-tested for growth, cap, jitter and
  reset); a 401/403 is `WorkerApiRejectedException` — a definitive answer that keeps backing off
  with an operator-facing line instead of a crash. State transitions are logged (registering,
  registered, token persisted, online, reconnected, retry delays); individual successful
  heartbeats are silent so a healthy worker produces a bounded log. Plaintext tokens never reach
  the console.
- **Graceful shutdown.** `PosixSignalRegistration` for SIGINT/SIGTERM plus `Console.CancelKeyPress`
  as belt-and-braces (on .NET 10/macOS the registered SIGINT callback does not fire when stdin is
  redirected, and SIGTERM re-raises after the handler — the platform quirks and the bounded-wait
  exit-0 path are documented in `Program.cs`). Shutdown cancels the loop token, sends one
  best-effort final heartbeat with a 2 s timeout, logs `stopped cleanly`, exit code 0.
- **E2E (process level).** `WorkerProcessE2ETests` spawns the **real Server binary** (SQLite,
  random port, migrations on startup) and the **real Worker binary**; bootstraps the Owner and
  mints the registration token through the real forms; asserts registration, ≥1 accepted heartbeat
  (worker log line + server DB row + `/workers` page), `0600` token file, and SIGTERM (`kill -s
  TERM`) → exit code 0 with `stopped cleanly`. A second fact asserts bad configuration fails fast
  with exactly one `configuration error` line and a non-zero exit.
- **Manual second-start verification (wrap-up, 2026-11-13).** The automated E2E covers a single
  start, so AC-2 (later starts use the persisted token) was verified by hand with real processes:
  server + worker started, worker registered and persisted its token, SIGTERM → exit 0; the
  worker was then restarted **without `ZENKINS_REGISTRATION_TOKEN`** and logged `using the
  persisted worker token from …` and `online as worker '<same id>'` with **no** `registering`
  call, heartbeats accepted, exit 0.
- **Backoff observed live.** During the same manual run the worker was pointed at a rejecting
  endpoint and logged `Retrying in 1.1s / 2.4s / 4.8s / 6.8s / 12.3s` while staying alive —
  capped exponential growth + jitter behaving as TC-002 expects.

### 2026-11-13 — review fixes

Red proofs below were produced by temporarily reverting each fix, running the new test against
the reverted state, and restoring the fix (shared run with the F-004 server-side proofs: 6/6 red
before restore, 6/6 green after).

- **B1 (blocking), worker half — every 4xx is a definitive answer, not a transport failure.**
  `WorkerApiClient.EnsureSuccessAsync` turned every status except 401/403 into an
  `HttpRequestException`, so a 400 on register (e.g. the old empty-tags rejection) was retried
  forever by the register loop under the misleading line `cannot reach … the server answered 400`.
  Now every 4xx throws `WorkerApiRejectedException` (401/403 keep their "credential is dead"
  message and exit semantics; other 4xx carry `the server rejected the request with {code} …
  (payload or contract mismatch)`). The register loop treats any rejection as final: exactly one
  clear stderr line (`configuration error: …`, status code included) plus operator guidance and a
  non-zero exit — no retry. 5xx/429 and network-level failures still retry with capped backoff.
  The heartbeat loop keeps its definitive-answer semantics (operator line + backoff), with the
  extra guidance only for 401/403.
  Test: `A_400_on_register_is_reported_once_and_exits_without_retrying` — a raw TCP stub answers
  400 to `/api/workers/register`; the worker exits non-zero with exactly one `the server
  rejected` line (containing `400`) and no `Retrying` line. Red proof: with the 4xx branch
  reverted, the worker retried until the 15 s exit-wait failed ("kept running against a
  definitive 400 rejection").
- **F2 (major) — request timeouts / black holes no longer crash the process.** The 10 s
  per-request ceiling in `WorkerApiClient.SendAsync` fires as `TaskCanceledException`, which
  neither loop caught — the process died with an unhandled-exception stack trace (violating
  F-005 AC-3). Both loops now add `catch (OperationCanceledException) when
  (!shutdown.IsCancellationRequested)` and treat the timeout as a transport failure: capped
  backoff retry with a `request timeout` line (the retry-delay wait is shared in
  `BackoffAfterFailureAsync`).
  Test: `A_server_that_never_answers_times_out_and_retries_instead_of_crashing` — a raw TCP
  black hole accepts the connection and never answers; the worker logs ≥2 timeout retries, stays
  alive throughout, and SIGTERM afterwards exits 0.
  Red proof: with the catch removed, the worker died on the first timeout with an unhandled
  `TaskCanceledException` stack trace (test failed waiting for the timeout log line).
- **Review minor 4 — the E2E binary probe is Release-only.**
  `WorkerProcessE2ETests.FindBuiltBinary` now requires the `Release` configuration segment
  instead of taking the newest write across Debug and Release.
- With the server half fixed (F-004 log), a worker started without `ZENKINS_WORKER_TAGS`
  registers with an empty tag list; the worker itself sends `tags: ""`, which the server
  normalizes like any empty capability field.

### 2026-11-13 — second review fixes

Fixes from the second F-004/F-005 review round, item by item. A/C/D are this card's Worker fixes;
B lives on the Server (F-004) and is logged there.

- **A (major) — transient 4xx answers are retryable again.** The B1 review fix made every 4xx a
  `WorkerApiRejectedException`, which also swallowed 408/409/425/429 — but a 409 name-duplicate is
  exactly what the loser of a same-name first-start race receives (two containers of one worker
  starting together), and its retry succeeds once the winner's row is committed. The pre-fix
  behavior turned that self-healing race into a dead process. `EnsureSuccessAsync` now keeps
  401/403 definitive ("credential is dead"), sends every other non-transient 4xx to the rejection
  branch as before, and routes 408/409/425/429 to the retryable `HttpRequestException` path (the
  capped backoff + jitter, like 5xx and network failures) — the class doc's "5xx/429 retry"
  promise is now actually true. The register and heartbeat loops need no change: both already
  retry `HttpRequestException` with backoff.
  Test: `A_409_on_register_is_retried_and_the_worker_goes_online_once_the_conflict_clears` — a raw
  TCP stub answers 409 name-duplicate twice, then serves the real register/heartbeat contract; the
  worker logs the 409 backoff retries, stays alive, registers on a later attempt, goes online, and
  SIGTERMs to exit 0. The existing 400 E2E still pins the definitive branch (one line, no retry).
  Red proof: with the transient exclusion reverted, the worker exited immediately with
  `configuration error: the server rejected the request with 409 while registering` — no retry,
  no online line; fix restored, green.
- **C (minor) — exit code 3 is now real.** `RegistrationRejectedExitCode = 3` existed but the
  register loop threw a plain `WorkerStartupException`, so a permanently rejected registration
  exited with the configuration code 2. `WorkerStartupException` now carries an `ExitCode`
  (default 2) and `Main` returns it: a definitive `WorkerApiRejectedException` during registration
  exits 3, configuration errors stay 2. Final exit-code semantics, documented in the Program.cs
  header: `0` graceful shutdown, `2` invalid configuration / unusable persisted state, `3`
  registration permanently rejected (definitive 4xx on register); heartbeats never exit the
  process — rejections keep the loop backing off.
  Tests: the 400-on-register E2E now asserts exit code 3 exactly (was non-zero); the
  bad-configuration E2E asserts 2 exactly (was non-zero).
- **D (minor) — the per-request ceiling now bounds the body read too.** `SendAsync`'s 10 s timeout
  CTS was disposed as soon as the response arrived, so `EnsureSuccessAsync` and
  `ReadFromJsonAsync` ran on the shutdown token alone; only the default
  `HttpCompletionOption.ResponseContentRead` (HttpClient buffers the whole response inside the
  send) made that safe, by accident rather than by construction. `SendAsync` now returns an owned
  scope (`InFlightResponse`) whose linked token (shutdown + 10 s ceiling) stays armed through
  status handling and body reads — a server that sends headers but stalls the body also hits the
  declared per-request limit, and shutdown still cancels everywhere (both linked). No behavior
  change intended or observed; the F2 black-hole E2E still pins the end-to-end ceiling behavior.
- **B (major), Server half — see F-004.** The Owner revoke path now maps lost concurrency races
  (revoke vs revoke, revoke vs burn) to the friendly pre-check outcomes — never a 500; red proof
  on F-004. No worker change.

**Known gaps.**

- **Empty `ZENKINS_WORKER_TAGS` cannot register.** The card's config table documents an empty
  default, but the server (F-004 `ValidateRegistration`) requires non-empty `tags`, so a worker
  started without tags gets `400 field-invalid` on register and retries forever. Worse, the
  client maps 400 to the transport-style retry branch (`cannot reach … the server answered 400`),
  not the definitive-rejection branch. Needs an alignment decision (server default vs worker
  warning/definitive handling of 4xx ≠ 401/403); deliberately not changed in this wrap-up.
  — **resolved in the 2026-11-13 review fixes above** (server accepts empty tags per PRD §3.4.4;
  the worker treats every 4xx as a definitive rejection).
- The restart-with-persisted-token path (AC-2) is verified manually (above) plus store unit
  tests, but is not asserted by an automated test — the E2E does one start only. Candidate for a
  follow-up E2E extension.
- TC-002's "start the real server a few seconds later" scenario is covered by the backoff unit
  tests and the runner retry loop, not by a dedicated E2E that starts the server late.
- Windows ACL hardening for the token file is deferred (best-effort), as noted on the card.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Worker/Program.cs`, `WorkerRunner.cs`, `WorkerApiClient.cs`, `WorkerConfiguration.cs`,
    `WorkerTokenStore.cs`, `BackoffScheduler.cs`, `Zenkins.Worker.csproj`
  - `tests/Zenkins.Tests/Workers/WorkerProcessE2ETests.cs`, `WorkerConfigurationTests.cs`,
    `WorkerTokenStoreTests.cs`, `BackoffSchedulerTests.cs`
  - `tests/Zenkins.Tests/WorkerRuntimeConfigTests.cs` (unchanged guard, still passing)
- How to run/verify:
  1. `dotnet build -c Release` (builds the Worker binaries the E2E `dotnet exec`s)
  2. `dotnet test -c Release`
  3. Manual process run (optional, as done for AC-2): start the Server on a port with a throwaway
     SQLite DB, mint a token through the `/workers` form, `ZENKINS_SERVER_URL=…
     ZENKINS_REGISTRATION_TOKEN=… ZENKINS_DATA_DIR=… dotnet exec
     src/Worker/bin/Release/net10.0/Zenkins.Worker.dll`, then restart without the registration
     token.
- Results (2026-11-13, after second review fixes):
  - Build: **PASS** — 0 warnings, 0 errors.
  - Tests: **PASS** — 212/212 across the suite (was 208; +4 second-review tests: the 409-retry
    process E2E on this card, 2 revoke-race mappings and the HTTP double-revoke on F-004's
    server half). All five `WorkerProcessE2ETests` facts pass (real server + real worker,
    SIGTERM exit 0; bad-config fail-fast with exit 2; 400-on-register exits 3 without retrying;
    black-hole timeouts survive; 409-on-register retries and goes online).
  - Red proofs: **PASS** — A reverted → the worker exited on the first 409 with a definitive
    rejection (`configuration error: ... 409`) → restored → green (B's red proof is on F-004).
  - Manual AC-2 verification: **PASS** — see Development Log (second start used the persisted
    token, no registration call, exit 0).
  - PostgreSQL contract gate (F-004 dependency): **PASS** — no pending model changes
    (`20261112170526_AddWorkers` is the latest migration).
