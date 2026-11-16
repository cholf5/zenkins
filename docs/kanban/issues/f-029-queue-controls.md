---
id: F-029
title: Queue controls — per-project concurrency, quiet period and a bounded queue
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A project cannot swamp the queue or the workers: it declares at most N concurrent
runs, an optional quiet period before a queued run becomes claimable, and a queue
cap whose overflow is refused with an actionable message.

## Background

Today any number of runs can be queued for one project; only the worker fleet bounds
concurrency (workers × `maxConcurrency`), and B-002 records that even that bound is
check-then-act — a token-holding client can exceed the cap. Jenkins has "Do not
allow concurrent builds" and a quiet period, and a busy repository with a webhook
can queue dozens of runs in a minute, each competing for the same worker slots.

This card is also where the atomic-slot question lands: either it absorbs B-002's
fix, or it waits for it and lays policy on top. Decide at implementation and record
which, because duplicating a non-atomic check would make B-002 worse.

## Acceptance Criteria

- [ ] `Project.MaxConcurrentRuns` (null = unlimited) is enforced at claim time:
      with a limit of 1, a second run of the project is never claimed while the
      first is `Running`.
- [ ] `Project.QuietPeriodSeconds` (default 0): a queued run is not claimable until
      the period has passed; a trigger during the period is a normal queued run (no
      coalescing in this card).
- [ ] `Project.MaxQueuedRuns` (null = unlimited): a trigger beyond the cap is
      refused before any insert, with a field error on the form, 409 on the API and
      `queue-full` on a webhook delivery row.
- [ ] The claims stay correct under concurrency: two workers claiming at the same
      instant cannot both take the second slot of a `MaxConcurrentRuns = 1` project
      (this is the property B-002's report says is missing).
- [ ] All three limits are shown on the project settings tab and echoed in every
      refusal message ("queue full: 20 queued runs, cap 20").
- [ ] Existing projects behave exactly as today (all limits null/0) and the
      defaults are documented.

## Subtasks

- [ ] Entity + migration: the three columns with their defaults; validation (a
      minimum of 1 for concurrency, a non-negative quiet period, a cap ≥ 1).
- [ ] Claim path: move the slot decision into one transaction that takes the
      project row lock (or a counter row), the same pattern as
      `LockWorkerForClaimAsync`; record the observed-vs-allowed behavior in a test
      that runs two claims in parallel.
- [ ] Create path: the queue-cap check inside the trigger transaction, so two
      concurrent triggers cannot both pass it.
- [ ] UI: a Limits card; the quiet period rendered as "seconds after trigger".
- [ ] PRD §3.5: record the three controls and the claim ordering (quiet period →
      concurrency → queue cap).
- [ ] B-002: close it as absorbed (or keep it and make this card depend on it) —
      write the decision into both cards' development logs.

## Dependencies

- B-002 (the check-then-act fix) — decide whether this card absorbs it

## Test Cases

### TC-001: A concurrency limit of 1 serializes claims

Steps:
1. Set `MaxConcurrentRuns = 1`; queue two runs; hand two claims to two workers at
   the same instant.

Expected:
- Exactly one claim succeeds; the other answers 204.
- After the first run ends, the second is claimed.

### TC-002: A quiet period delays claimability

Steps:
1. Set a 5-second quiet period; trigger a run; claim immediately, then after 6
   seconds.

Expected:
- The first claim answers 204; the second claims the run.

### TC-003: A full queue refuses without inserting

Steps:
1. Set `MaxQueuedRuns = 2`; trigger three times.

Expected:
- Two runs are queued; the third is refused with the field error / 409 / delivery
  outcome `queue-full`.
- The refusal happens before the run row exists (no orphan queued row).

## Notes and Decisions

- Rejected: coalescing triggers inside the quiet period ("the last push wins"). It
  is a separate feature with its own semantics (which commit wins, what the UI
  shows) and it would quietly discard runs people asked for.
