---
id: B-008
title: Idempotency-Key has no project and no payload dimension, so a reused key answers with another project's run
type: bug
priority: P1
size: S
status: todo
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

- [ ] The idempotency record carries the project: a key reused against a **different** project
      answers `409 idempotency-key-reused` (or is scoped per project — decide and record which), and
      never returns another project's run id or `Location`.
- [ ] A key reused with a **different request payload** (branch, parameters, tags, timeout) is
      refused the same way instead of silently replaying, by storing and comparing a request hash.
- [ ] An **unknown or foreign project id** answers a bare 404 again, replay or not: the replay
      short-circuit moves after the project lookup, or the project id becomes part of the lookup key.
- [ ] Records expire: a bounded retention (a timestamp plus a sweeper, or a documented TTL enforced
      at lookup) — a fixed `nightly` key cannot pin the first run forever.
- [ ] The replay/disabled-gate precedence is decided, implemented as chosen, and written into
      `docs/api-v1.md` and F-028/F-029's cards.
- [ ] Docs state the three semantics the audit had to measure: a replay **still spends budget**, a
      409 body carries `fieldErrors` (the doc lists only `error`/`message`), and `projectDisabled` on
      a run resource is the project's **live** state, not a run snapshot.

## Subtasks

- [ ] Migration: `ProjectId` (or a scoped key) + `RequestHash` + `CreatedAt`/expiry, with the unique
      index rebuilt accordingly.
- [ ] Lookup: after the project lookup, compare project and hash, refuse on mismatch.
- [ ] Retention: a sweeper plus a bounded window, and a test that an expired key triggers normally.
- [ ] Docs and both cards' notes.

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
