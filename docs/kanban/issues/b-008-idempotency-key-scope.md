---
id: B-008
title: Idempotency-Key has no project and no payload dimension, so a reused key answers with another project's run
type: bug
priority: P1
size: S
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A reused `Idempotency-Key` can no longer tell a client "200, here is your run" about a run that
belongs to a different project, a different body, or a project that does not exist.

## Background

Found by the F-027/F-028/F-029 integration audit (2026-11-16), reproduced against a real host. The
replay lookup is `(ApiTokenId, Key)` only (`ApiTokenService.cs:336-347`, unique index
`ApiModelConfiguration.cs:61`) and the replay short-circuit sits **before** the project lookup and
before F-028/F-029's gates (`ApiEndpoints.cs:111-118`). Measured consequences:

- Same key, **different project**: 200 with `projectId` of the first project and a `Location`
  pointing at its run; the requested project queues **nothing**. A script that walks one commit sha
  across several projects silently builds only the first.
- Same key, **unknown project id**: 200, where `docs/api-v1.md` promises a bare 404.
- Same key, **different body** (branch changed to `hotfix`): 200 replaying the old run; the new
  branch is discarded with no error code.
- Disable → fix → **re-enable** → retry the same key: 200 replaying the *cancelled* run, so a nightly
  job with a fixed key would never build again.
- The record has no retention at all: no sweeper, no expiry column
  (`ApiModelConfiguration.cs:53-74`). Only deleting the run (cascade) removes it.

Separately, the replay path bypasses F-028's disabled gate and F-029's queue cap entirely — the three
cards never decided whether replay wins over the gates. Today replay wins; that is acceptable but it
must be written down rather than discovered.

## Acceptance Criteria

- [x] The idempotency record carries the project: a key reused against a **different** project
      answers `409 idempotency-key-reused` (or is scoped per project — decide and record which), and
      never returns another project's run id or `Location`.

      **Decided: a refusal, not a per-project scope.** The unique index stays `(ApiTokenId, Key)` and
      the record gains `ProjectId`; a key reused against another project is `409 idempotency-key-reused`
      with `fieldErrors[0].field = idempotency-key`. Per-project scoping was rejected because it would
      let one token's key mean two different runs with no signal - the audit's "a script walking one
      sha across projects silently builds only the first" becomes "builds one per project, silently".
- [x] A key reused with a **different request payload** (branch, parameters, tags, timeout) is
      refused the same way instead of silently replaying, by storing and comparing a request hash.
      `ApiIdempotencyRecord.RequestHash` is a lowercase-hex SHA-256 over the canonical payload
      (`RunTriggerFingerprint`: trimmed branch, tag set, trimmed timeout, parameters by name).
- [x] An **unknown or foreign project id** answers a bare 404 again, replay or not: the replay
      short-circuit moves after the project lookup, or the project id becomes part of the lookup key.
      Done: the key's decision moved into `RunService.CreateQueuedRunAsync`, immediately after the
      project read and before the project's gates.
- [x] Records expire: a bounded retention (a timestamp plus a sweeper, or a documented TTL enforced
      at lookup) — a fixed `nightly` key cannot pin the first run forever. Chosen: a stored `ExpiresAt`
      (`CreatedAt + 24 h`, `ApiIdempotencyRecord.Retention`), **enforced at lookup** — an expired
      binding is deleted and forgotten, so the same key queues a fresh run; no sweeper is needed for
      correctness (the column is indexed for one if it is ever added).
- [x] The replay/disabled-gate precedence is decided, implemented as chosen, and written into
      `docs/api-v1.md` and F-028/F-029's cards. **Decided: a replay wins** over F-028's disabled gate
      and F-029's queue cap (it is the idempotency semantic itself; a new key reads the gates).
- [x] Docs state the three semantics the audit had to measure: a replay **still spends budget**, a
      409 body carries `fieldErrors` (the doc lists only `error`/`message`), and `projectDisabled` on
      a run resource is the project's **live** state, not a run snapshot. All three are in
      `docs/api-v1.md` (the budget paragraph, the refusal-shapes table, the run-resource paragraph).

## Subtasks

- [x] Migration: `ProjectId` (or a scoped key) + `RequestHash` + `CreatedAt`/expiry, with the unique
      index rebuilt accordingly. `20261116094220_AddIdempotencyKeyScope` (the index stays
      `(ApiTokenId, Key)`; an `ExpiresAt` index was added; pre-existing rows are backfilled as expired,
      because a binding with no project and no hash must not replay anything).
- [x] Lookup: after the project lookup, compare project and hash, refuse on mismatch.
- [x] Retention: a sweeper plus a bounded window, and a test that an expired key triggers normally.
      The window plus lookup enforcement; the test ages the record past the window
      (`ApiV1TestHost.AgeIdempotencyRecordAsync`) instead of waiting a day.
- [x] Docs and both cards' notes.

## Dependencies

- none (F-027 is accepted; this is the contract hole it left)

## Test Cases

### TC-001: A key cannot straddle projects or bodies

Steps:
1. Trigger project A with key `k`; repeat key `k` against project B; repeat against an unknown id;
   repeat against A with a different branch.

Expected:
- B and the unknown id are refused (409 / 404 respectively) and queue nothing; the changed branch is
  refused rather than silently replaying.

### TC-002: A key does not outlive its retention

Steps:
1. Trigger with key `k`, age the record past retention, trigger again with `k`.

Expected:
- A new run is queued, and the old record is gone or overwritten.

Result (observed, 2026-11-16): PASS both cases, in the fast suite.
- **TC-001.** `ApiV1RunApiTests.A_reused_key_cannot_straddle_projects_or_payloads_and_an_unknown_project_stays_a_bare_404`:
  project A with key `deploy-1` → `201`; project B with the same key → `409 idempotency-key-reused`
  (with the `idempotency-key` field error, no `Idempotency-Replayed`) and **zero** rows for B; the same
  project with `branch: hotfix` and with `timeoutMinutes: 30` → `409` each, still one run; the identical
  retry → `200 Idempotency-Replayed: true` with A's run id and `Location`; an unknown project id with
  the same key → bare `404` with an empty body. The stored record names project A and a 64-char hash.
- **TC-001, concurrency.** `ApiV1RunApiTests.Concurrent_posts_with_the_same_new_key_queue_exactly_one_run`:
  four concurrent posts with one new key → exactly one `201`, three `200` replays of the same run id,
  one run row, one record. The loser's transaction is rolled back, so the key cannot queue two runs.
- **TC-002.** `ApiV1RunApiTests.An_expired_key_queues_a_new_run_instead_of_replaying_forever`: after the
  record is aged past 24 h the same key answers `201` with a **new** run id, the project holds two runs
  and the single record now names the new one.
- **Precedence.** `ApiV1RunApiTests.A_replay_wins_over_the_disabled_gate_and_a_new_key_reads_the_refusal`:
  the retried post answers `200` replaying the run it queued while the project is disabled; a fresh key
  on the same project answers `409 project-disabled` and queues nothing.
- **Budget.** `ApiV1RunApiTests.A_replay_still_spends_the_token_s_budget`: with a budget of two, the first
  trigger and its replay spend it and the third request is `429 rate-limited`.

## Notes and Decisions

- **A key names one request (decided above), inside one token.** The identity of a key is the token row;
  the project and the payload are the two dimensions added here. A second token can use the same word.
- **The hash is taken from the request as sent, canonicalized** - tags as a set, parameters by name,
  branch and timeout trimmed - because the decision is made *before* the project's gates and field
  validation: a replay must answer a formerly valid request even if today's parameter definitions or tag
  roster changed. Resolving first would let a definition edit turn a retry into a `400` instead of the
  run it already queued. Consequence, stated plainly: a client that retries `{}` as `{"branch":"main"}`
  on a project whose default branch is `main` is refused as "a different request"; the doc says the hash
  covers the payload as sent.
- **The record is written in the run's own transaction.** `RunService.QueueStagedRunAsync` inserts the
  binding inside the transaction that already staged the run, so a crash between two units of work can
  never leave "run queued, key unknown" (which would let the retry queue a second run) or "key bound,
  run rolled back". The loser of a concurrent race has its whole transaction rolled back and answers the
  winner's run.
- **Where the decision lives, and why not in the endpoint.** The endpoint cannot know the project before
  the 404 question is answered, and it must not re-implement the fingerprint (the trigger path is the one
  place a request is interpreted). So `ApiEndpoints` hands `RunTriggerIdempotency(token, key)` to
  `RunService.TriggerForApiAsync`, and `ApiTokenService` no longer has the two idempotency methods.
- **Retention: 24 hours, enforced where it is read.** No background sweeper was added: a sweeper that
  has not run yet must not let an expired key replay, so the lookup has to check anyway; deleting on
  read makes the bound true even on a server that never sweeps.
- **What this card deliberately does not do.** It does not turn a key into a per-project lock (two
  different keys queue two runs), and it does not replay a run whose record was cascaded away (deleting
  the run deletes its binding: a key is only ever as durable as the run it points at).
- **The "disable → fix → re-enable → retry the same key" case, honestly.** With the decided precedence
  (a replay wins), that retry answers the run the **disable cancelled** instead of queueing a new one -
  the caller sees `Canceled` on the resource plus `projectDisabled: true`, which is the truthful answer,
  and the remedy is a new key. What this card changes about the audit's finding is the bound: the
  binding lives 24 h, so a nightly job with a fixed key replays at most within one day and then builds
  again on its own; before this card it replayed for the token's lifetime. Making a cancelled run
  "consume" its key was considered and rejected: it would let one key queue two runs, which is the
  guarantee this card exists to keep. Documented in `docs/api-v1.md`.

## Development Log

### 2026-11-16

- **Entity + migration.** `ApiIdempotencyRecord` gains `ProjectId`, `RequestHash` (64) and `ExpiresAt`,
  with `Retention = 24 h`; `ApiModelConfiguration` keeps the unique `(ApiTokenId, Key)` index, adds an
  `ExpiresAt` index and a 64-char bound on the hash. Migration
  `20261116094220_AddIdempotencyKeyScope` was generated with the PostgreSQL provider (the repository's
  migration contract - `grep -r "character varying" src/Server/Data/Migrations` still finds the history),
  `dotnet ef migrations has-pending-model-changes` is clean, and the defaults backfill pre-existing rows
  as **expired** on purpose (see the migration's doc comment).
- **Trigger path.** `RunService.CreateQueuedRunAsync` takes a `RunTriggerIdempotency?`, resolves
  `RunTriggerFingerprint.Of(input)` and the remembered binding after the project read, and answers
  `ReplayedRun`/`IdempotencyKeyReused` before the disabled gate, the field validation and the queue cap.
  `QueueStagedRunAsync` writes the binding in the run's transaction and turns a unique-index refusal into
  a rollback plus the winner's run; `StageQueuedRunAsync` gained the optional binding (the webhook and
  rebuild/replay paths pass `null` and are unchanged). `RunTriggerResult` gained `Replayed`.
- **API layer.** `ApiEndpoints.TriggerRunAsync` passes the scope, maps `Replayed` to `200` +
  `Idempotency-Replayed: true` and otherwise `201`; the two `ApiTokenService` idempotency methods are
  gone. `RunFields.IdempotencyKey` and `RunErrorCodes.IdempotencyKeyReused` carry the refusal vocabulary.
- **Docs.** `docs/api-v1.md` grew a "The `Idempotency-Key`, in full" section (scope, replay, refusal,
  404, precedence, retention, concurrency), the 409 row of the refusal table (now with `fieldErrors`),
  the replay-spends-budget sentence, the `projectDisabled`-is-live sentence, and the removed
  "two concurrent posts may both queue" caveat (they no longer can). F-028 and F-029 each gained a Notes
  bullet recording the replay-over-gate precedence.
- **Verification.** `dotnet build Zenkins.slnx -c Release` clean. `scripts/verify.sh` green in 500 s:
  build, fast 1172, Milestone 12, Recovery 4, PublishSmoke 2, PublishAot 2, GitSshd 3, Postgres 3 -
  **all seven 1198** (baseline 1165/1190; this card adds 5 fast facts, Q-015 adds 2, Q-016 adds the
  Postgres one). Logs: `artifacts/logs/b008-q015-q016-gate.log`, `...-fast-suite.log`,
  `artifacts/logs/q016-postgres-green.log`. Nothing in this card needed PostgreSQL: it is exercised
  through the real HTTP surface on SQLite (including the concurrent same-key fact).
- **Not covered, and why.** (a) No background sweeper: the retention is enforced at lookup, which is what
  makes the bound true on a server that never sweeps; a sweeper would be a second, slower enforcement of
  the same fact and has no test that could fail while the lookup check holds. (b) A cross-*organization*
  reuse of the same key is not a special case: the record is read through the tenant filter, so another
  organization's key is simply unknown to this token (and the token id differs anyway).

## Review

2026-11-16 — independent review: **PASS**, six of six criteria, in an isolated copy of the batch with
a temporary PostgreSQL 16.15 cluster.

The question worth proving here was not "does it return 409" but "does a lost race leave a run
behind". It does not: eight concurrent posts on one new key, five rounds, every answer carrying the
same run id, and bare Npgsql reads of both tables after each round — one run, one record, the record
naming that run, and every round adding exactly one run. Thirty-five of the forty posts really took
the unique-violation path (counted from the log line the loser writes), so the rollback branch is
exercised rather than bypassed by an early return.

Also verified: a different project answers `409 idempotency-key-reused` with a `fieldErrors` entry on
`idempotency-key` and no `Location`; an unknown project id stays a bare 404 because the project read
comes first; an aged record queues a new run and deletes the old row rather than sweeping it; a
replay still spends budget; and `projectDisabled` on a run resource is read live from the project.

Two lows are Q-017: the fingerprint hashes the *parsed* tags, so a difference made only of an
invalid tag replays instead of refusing, and the "replay wins over the queue cap" rule was claimed
as pinned by facts that never fill a queue.
