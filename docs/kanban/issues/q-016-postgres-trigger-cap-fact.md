---
id: Q-016
title: The trigger-side queue cap has no provider-discriminating fact — SQLite cannot see its lock either
type: test
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

`Category=Postgres` covers the trigger's atomic queue cap the way it already covers the claim's
concurrency cap, so deleting `StageQueuedRunAsync`'s project row lock turns a committed test red.

## Background

F-029's review established the standard for the claim path: a single-writer SQLite database
serializes the transaction, so a fast-suite test can only show that a limit exists, never that the
project row lock enforces it. A `Category=Postgres` fact was added for exactly that reason, and it
does discriminate.

The integration audit on 2026-11-16 found the **trigger** path was never held to the same standard.
Deleting the `LockProjectForTriggerAsync` check in `RunService.StageQueuedRunAsync` (keeping the
count) leaves every committed `queue-full` fact green on SQLite, while a real PostgreSQL probe puts
**six queued runs into a `MaxConcurrentRuns`-style cap of one** in round 1. The property is real; its
only evidence today is an uncommitted probe.

## Acceptance Criteria

- [ ] `PostgresProjectCapTests` (same xunit collection — every Postgres fact drops `public`) carries
      the trigger-side fact: six concurrent API/form triggers against a full queue admit exactly one
      row per round, three rounds.
- [ ] It carries the negative control the claim fact has: a hand-written count-then-insert through
      the product's own methods admits more than one, so the count is shown to be load-bearing.
- [ ] Deleting `StageQueuedRunAsync`'s project row lock turns that fact red on PostgreSQL and leaves
      the SQLite fact green; both runs are recorded on F-029 with the mutation recipe.
- [ ] The fast filter and `scripts/verify.sh` still exclude `Category=Postgres`, and
      `docs/development.md` §2's counts are re-recorded.

## Subtasks

- [ ] Port the audit's trigger probe into the committed suite.
- [ ] Record the two mutation runs on F-029.

## Dependencies

- none (F-029 is accepted; this closes the asymmetry its review created)

## Notes and Decisions

- The audit's probe lived in an untracked file in its own checkout and is deliberately not pasted
  here; the production line it exercises is `RunService.StageQueuedRunAsync`'s
  `LockProjectForTriggerAsync` call.
