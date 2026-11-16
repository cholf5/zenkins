---
id: Q-017
title: The idempotency fingerprint drops invalid tags before hashing, and replay-vs-queue-cap has no fact
type: test
priority: P3
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A retried trigger whose only difference is an invalid tag is refused the way a new key would be, and
the "a replay wins over a full queue" rule is proven by a fact instead of by statement order.

## Background

Both found by B-008's independent review (2026-11-16), neither affects what gets built:

- `RunTriggerIdempotency.cs:48-54` fingerprints the **output** of `RunValidation.ParseTags(...)`,
  which silently drops over-long or control-character tags (`RunValidation.cs:130-146`) — exactly
  the values `ValidateRequiredTags` refuses with `tag-invalid`. So a resend that differs only in an
  invalid tag hashes identically: it replays `200` where the same request with a new key would be
  `400 tag-invalid`. Nothing wrong is built; a refusal is lost.
- F-029's note says the replay-wins-over-the-cap rule is pinned by two facts, but neither fills a
  queue: one only disables the project, the other never sets `MaxQueuedRuns`. The rule holds because
  of statement order alone (`RunService.CreateQueuedRunAsync` looks up the key before
  `StageQueuedRunAsync` counts). The note has been corrected to say so; this card adds the fact.

## Acceptance Criteria

- [ ] The fingerprint hashes the tags **as sent** (trimmed, de-duplicated, ordered — but keeping the
      values validation would refuse), so an invalid-tag difference produces `409
      idempotency-key-reused` rather than a replay.
- [ ] A test covers the tag dimension and the parameters dimension, not only branch and timeout.
- [ ] A fact fills a `MaxQueuedRuns = 1` queue, then: a **new** key answers `409 queue-full` and
      queues nothing; the **same** key answers `200` with `Idempotency-Replayed` and adds no run.
- [ ] F-029's note can then name that fact instead of describing the ordering.
- [ ] Fast-suite counts re-recorded in `docs/development.md` §2.

## Subtasks

- [ ] Move the hash input to the unparsed (trimmed) tag list.
- [ ] Two test dimensions + one queue-cap replay fact.

## Dependencies

- none (B-008 is accepted)

## Notes and Decisions

- Two more B-008 review notes need no work: when a raced winner's record disappears between the
  unique violation and the read-back, the request answers 500 in a window the reviewer could not
  reproduce in 35 attempts (recorded on B-008); and `ApiTokenService`'s catch deliberately includes
  `InvalidOperationException`, which can swallow a genuine programming error as one warning — a
  trade-off that card already states.
