---
id: Q-015
title: F-027's review leftovers — the last-used stamp still fails loud and wide, and two fixes have no regression net
type: test
priority: P3
size: S
status: todo
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

- [ ] A stamp failure of any kind (`DbUpdateException`, `SqliteException`, `InvalidOperationException`)
      is caught, logged once as a warning, and never fails the authenticated request;
      `OperationCanceledException` is not swallowed.
- [ ] The stamp's own command timeout is short and independent of the request's (a locked database
      costs seconds, not the full command timeout); the measured before/after is recorded.
- [ ] `docs/api-v1.md`'s malformed-JSON sentence names the environment: an empty 400 in Production, a
      developer exception page in Development.
- [ ] A test calls `GetTokensAsync` directly: a Member gets an empty list, an Owner gets the rows, and
      an unknown tenant gets an empty list.

## Subtasks

- [ ] Widen the catch and give the UPDATE its own timeout.
- [ ] One doc sentence.
- [ ] One service-level test.

## Dependencies

- none (F-027 is accepted)
