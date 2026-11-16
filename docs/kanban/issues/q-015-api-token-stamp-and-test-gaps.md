---
id: Q-015
title: F-027's review leftovers — the last-used stamp still fails loud and wide, and two fixes have no regression net
type: test
priority: P3
size: S
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

The API token stamp can never turn an authenticated request into a 500 or into a 30-second wait, and
the two fixes F-027's second review round asked for are pinned by tests.

## Background

F-027 passed on its second review round (2026-11-16). Four leftovers were recorded rather than
reopening the card; none of them change a passing criterion, and one of them was demonstrated rather
than argued:

- `TouchLastUsedAt` catches `DbUpdateException` only. The write failure is proven harmless — the
  reviewer held `BEGIN IMMEDIATE` on the SQLite file and an authenticated `GET /api/v1/projects`
  still answered 200 with one warning — but the **read** before it is outside the catch, so a
  `SqliteException` there (`no such table: ApiTokens` reproduced it) still becomes a 500.
- The same swallowed failure waits out the full command timeout: the reviewer measured **30 s** for
  one request. A locked database would turn every polled trigger into a 30-second stall.
- `GetTokensAsync` grew an Owner check when the API landed, but `ApiTokens.razor` short-circuits on
  `_canManage == false` before calling it, so no test ever exercises it — deleting the check leaves
  the whole suite green.
- `docs/api-v1.md` says a malformed JSON body answers an empty 400 as "global behaviour". True in
  Production (0 bytes, verified against the worker API too); in Development the same request answers
  400 with a 4,313-byte developer exception page.

The fifth leftover — the artifact zip fact snapshotting the whole OS temp directory — is not here; it
is Q-014.

## Acceptance Criteria

- [x] A stamp failure of any kind (`DbUpdateException`, `SqliteException`, `InvalidOperationException`)
      is caught, logged once as a warning, and never fails the authenticated request;
      `OperationCanceledException` is not swallowed.
      The catch is now `catch (Exception failure) when (failure is not OperationCanceledException &&
      failure is DbUpdateException or DbException or InvalidOperationException)` - `DbException` is the
      provider base (`SqliteException`, `PostgresException`), and it covers the **read** before the write
      as well as the write itself. Proven by
      `ApiTokenStampTests.A_locked_database_costs_the_stamp_seconds_and_never_fails_the_request`: with
      another connection holding SQLite's write lock the authenticated `GET /api/v1/projects` still
      answers `200`, exactly one `Warning` is logged for the stamp, the token's `LastUsedAt` stays null,
      and the next (unlocked) request writes it. The test is not vacuous: removing the catch turns it red
      with `Expected: OK / Actual: InternalServerError`.
- [x] The stamp's own command timeout is short and independent of the request's (a locked database
      costs seconds, not the full command timeout); the measured before/after is recorded.
      `context.Database.SetCommandTimeout(ApiTokenService.StampCommandTimeout)` (3 s) on the stamp's own
      context only - the request's contexts are untouched. **Measured on this machine, same test, same
      held lock: 30.1 s before, 3.1 s after** (logs `artifacts/logs/q015-stamp-before-measure.log` and
      `artifacts/logs/b008-q015-stamp-after-measure.log`; the bound asserted in the fact is 15 s).
- [x] `docs/api-v1.md`'s malformed-JSON sentence names the environment: an empty 400 in Production, a
      developer exception page in Development. Done (the sentence now says Production answers `400` with
      an empty body and Development answers `400` with the developer exception page's HTML).
- [x] A test calls `GetTokensAsync` directly: a Member gets an empty list, an Owner gets the rows, and
      an unknown tenant gets an empty list.
      `ApiTokenStampTests.The_token_list_is_owner_only_in_the_service_a_member_gets_an_empty_list` calls
      the service in a DI scope whose `TenantContext` carries the tenant: Owner → the minted row, Member
      of the same organization → empty, no organization selected → empty. Deleting the service's
      `IsOwnerAsync` check turns the Member assertion red.

## Subtasks

- [x] Widen the catch and give the UPDATE its own timeout.
- [x] One doc sentence.
- [x] One service-level test.

## Notes and Decisions

- **Where the read really sat.** The card's background said the read was outside the catch. In the code
  the read was *inside* the `try`, but the catch only matched `DbUpdateException`, so a provider
  exception from the read (`no such table: ApiTokens`, a closing connection) still escaped as a 500 -
  the same hole, one type narrower. The widened catch closes it for both statements.
- **Why `DbException` and not a bare `catch (Exception)`.** `DbUpdateException` (EF), `DbException` (the
  provider: `SqliteException`, `PostgresException`, a locked/busy database, a dropped table) and
  `InvalidOperationException` (a disposed or misconfigured context) are the three ways this write can
  fail for a database reason. Anything else - a `NullReferenceException`, an `OutOfMemoryException` -
  is a real defect and must keep surfacing. `OperationCanceledException` (and `TaskCanceledException`)
  are excluded by the filter: a cancelled request must not pretend it succeeded.
- **Why 3 seconds, and why the bound is 15.** Three seconds is longer than any healthy stamp and shorter
  than a client's patience for a polled read; the fact asserts under 15 s so a loaded CI machine cannot
  flake, while the pre-fix behaviour (30 s, the provider's default `CommandTimeout`) still fails it.
- **Not covered, and why.** The `OperationCanceledException` half of the filter and the
  `InvalidOperationException` clause have no direct fact: reaching them needs a seam that makes the
  stamp throw a chosen exception type, and the only honest way to do that from a test would be a
  product-code hook that exists for the test. They are one-line clauses of the same filter the measured
  case exercises, and the card says so rather than implying coverage.

## Dependencies

- none (F-027 is accepted)

## Development Log

### 2026-11-16

- **Catch widened, timeout added.** `ApiTokenService.TouchLastUsedAsync` now catches
  `(DbUpdateException | DbException | InvalidOperationException)` and never `OperationCanceledException`,
  logs one warning and returns; its context gets `SetCommandTimeout(StampCommandTimeout)` (3 s,
  `ApiTokenService.StampCommandTimeout`), independent of every other context in the request.
- **Measured before/after on the real path.** Same fact, same held SQLite write lock: **30.1 s → 3.1 s**
  for one authenticated read. The measurement was taken by running the fact twice with the bound
  temporarily at zero so the elapsed time appears in the failure message; both runs are in
  `artifacts/logs/`.
- **Two tests, one file.** `tests/Zenkins.Tests/Api/ApiTokenStampTests.cs` holds both facts and a small
  `RecordingLogProvider` (nothing in the suite read logs before, and "logged once as a warning" is
  otherwise unassertable). `ApiV1TestHost.CreateAsync` gained the optional `configureTestServices`
  parameter the other hosts already had, so the provider can be registered.
- **Mutation, to prove the fact bites.** Deleting the catch (keeping the widened filter's shape so the
  build stays green) turns `A_locked_database_costs_the_stamp_seconds_and_never_fails_the_request` red
  with `Expected: OK / Actual: InternalServerError` - log
  `artifacts/logs/q015-stamp-catch-removed-mutant.log`. Restored immediately; fast suite green.
- **Docs.** The malformed-JSON sentence in `docs/api-v1.md` now names the environment.
- **Verification.** `dotnet build Zenkins.slnx -c Release` clean. `scripts/verify.sh` green in 500 s with
  a throwaway PostgreSQL: fast **1172** / Milestone 12 / Recovery 4 / PublishSmoke 2 / PublishAot 2 /
  GitSshd 3 / Postgres 3 - **all seven 1198** (`artifacts/logs/b008-q015-q016-gate.log`); the API and
  tenancy subset re-run on the final tree: 144 passed
  (`artifacts/logs/b008-q015-q016-api-and-tenancy.log`).

## Review

2026-11-16 — independent review: **PASS**, four of four, with the timing reproduced exactly.

Both numbers hold under the same held write lock: 30.1 s with the command timeout removed, 3.1 s on
the tree as submitted. The interesting risk was leakage — `ZenkinsDbContextFactory` hands every
context the *same* `DbContextOptions` instance, so a timeout set on one context could plausibly
follow the options everywhere. It does not: the context that set it reports 3, a context created
before it reports null, and one created after it reports null, because EF keeps the timeout on the
context's own connection facade. The "exactly one warning" assertion is tight rather than lucky —
one message in the product contains `last-used`, and the test pins level plus count. Deleting the
Owner check makes the service-level fact red, so that fix has a real net.
