---
id: Q-016
title: The trigger-side queue cap has no provider-discriminating fact — SQLite cannot see its lock either
type: test
priority: P2
size: S
status: done
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

- [x] `PostgresProjectCapTests` (same xunit collection — every Postgres fact drops `public`) carries
      the trigger-side fact: six concurrent API/form triggers against a full queue admit exactly one
      row per round, three rounds.
      `Concurrent_triggers_never_overfill_the_queue_and_the_naive_sequence_would`: `MaxQueuedRuns = 1`,
      six concurrent real start-run form posts per round, three rounds; the queued count is read from the
      rows and asserted equal to the number of triggers that reported success (so "everything refused"
      cannot look green), with every other answer asserted to be `?runError=queue-full`.
- [x] It carries the negative control the claim fact has: a hand-written count-then-insert through
      the product's own methods admits more than one, so the count is shown to be load-bearing.
      Two contexts both count the project's queued runs (both read 0, cap 1 not reached) and both then
      insert a run through the product's own `ZenkinsDbContext` and `BuildRun` entity: **2 queued runs
      in a one-run queue**. The insert numbers are supplied by the test so card F-036's unique
      `(ProjectId, Number)` index is not what the control trips over.
- [x] Deleting `StageQueuedRunAsync`'s project row lock turns that fact red on PostgreSQL and leaves
      the SQLite fact green; both runs are recorded on F-029 with the mutation recipe.
      Red in round 1: "the MaxQueuedRuns=1 project holds 2 queued runs (2 of 6 concurrent triggers
      admitted) on PostgreSQL". Same tree, SQLite: `QueueControlTests` + `ConcurrencyProjectCapTests`,
      9/9 green. Recipe and logs are on F-029's Development Log.
- [x] The fast filter and `scripts/verify.sh` still exclude `Category=Postgres`, and
      `docs/development.md` §2's counts are re-recorded. The fact carries `Trait("Category", "Postgres")`
      and joins `PostgresCollection`; the filter and the script are unchanged; §2 now says the Postgres
      suite is 3 facts (and the batch's totals).

## Subtasks

- [x] Port the audit's trigger probe into the committed suite.
- [x] Record the two mutation runs on F-029.

## Dependencies

- none (F-029 is accepted; this closes the asymmetry its review created)

## Notes and Decisions

- The audit's probe lived in an untracked file in its own checkout and is deliberately not pasted
  here; the production line it exercises is `RunService.StageQueuedRunAsync`'s
  `LockProjectForTriggerAsync` call.
- **Why a form trigger and not a JSON API call.** The card allows either; the form is the path the
  existing SQLite `queue-full` facts use, so the two providers are exercised by the *same* request
  surface - the comparison the mutation rests on is provider-to-provider, not route-to-route.
- **Why the admitted count is asserted against the queued count.** A provider that serialized six
  triggers into six refusals would leave `queued == 0`, which satisfies "never more than the cap" while
  proving nothing. The equality assertion (plus `worstQueued == 1` and one admission per round) is what
  makes the fact bite in both directions.
- **What the negative control does and does not prove.** It shows the count is load-bearing: a
  count-then-insert sequence through the product's own context leaves the queue over capacity. It is
  deliberately *not* a race - it is deterministic on any provider, including SQLite, just like the claim
  fact's control. The evidence that the *lock* is what makes the count trustworthy is the mutation run
  recorded on F-029, not this control.

## Development Log

### 2026-11-16

- **One fact, three rounds, on a real server.** Added to
  `tests/Zenkins.Tests/Concurrency/PostgresProjectCapTests.cs` (the existing `Category=Postgres` class,
  same `PostgresCollection`, same disposable-database prerequisite and skip reason). Setup per round: a
  fresh project with `MaxQueuedRuns = 1` saved through the real settings form, then six concurrent
  `POST /projects/{id}/runs/start` posts released through one barrier, then the verdict read from the
  rows. Three rounds, one admission each. `Assert.Equal(queued, admitted)` and
  `Assert.Equal(triggers - admitted, refused)` are what stop a "nothing worked" run from looking green.
- **The control.** Two tenant-scoped contexts count the project's queued runs (both `0`) and then insert
  a run each (numbers 1 and 2, so F-036's unique index stays out of the way): `2` queued runs in a
  `MaxQueuedRuns = 1` queue.
- **The mutation, both providers.** Local edit, reverted immediately: delete
  `StageQueuedRunAsync`'s `LockProjectForTriggerAsync` block, keep the count.
  `Category=Postgres` → **red in round 1** ("holds 2 queued runs (2 of 6 concurrent triggers
  admitted)"). Same tree, `QueueControlTests` + `ConcurrencyProjectCapTests` → **9/9 green**. Lock
  restored → `Category=Postgres` 3/3 green. Logs:
  `artifacts/logs/q016-postgres-m1-mutant-trigger-lock-deleted.log`,
  `artifacts/logs/q016-sqlite-same-mutant-green.log`,
  `artifacts/logs/q016-postgres-restored-green.log`.
- **Evidence discipline.** The cluster was a throwaway PostgreSQL 16.15 (`initdb` in a temp directory,
  `LC_ALL=C`, loopback only) and was stopped and deleted afterwards; the connection string used on this
  machine is not written into any tracked file (the suite's own docs use `pg.example.test`).
- **Counts.** Fast suite unchanged (this fact is excluded); Postgres 2 → 3; `docs/development.md` §2
  re-recorded from the real runs: **fast 1172, all seven 1198** (Milestone 12, Recovery 4, PublishSmoke 2,
  PublishAot 2, GitSshd 3, Postgres 3). Whole gate green in 500 s -
  `artifacts/logs/b008-q015-q016-gate.log`.
- **Not covered, and why.** The probe does not measure how *many* triggers would slip through without the
  lock on every possible interleaving - it only requires that at most the cap is admitted, which is the
  property. The exact number the mutant produces (2 of 6 in round 1) is an observation, not a contract.


## Review

2026-11-16 — independent review: **PASS**, four of four, with both directions reproduced by the
reviewer rather than taken from the implementer's log.

The fact carries the four assertions that keep it honest: `queued <= cap`, `queued == admitted`,
`triggers - admitted == refused`, and `worstQueued == 1` — without the second one, "every trigger
failed" would look green. Deleting only `StageQueuedRunAsync`'s `LockProjectForTriggerAsync` (count
kept) turns it red on PostgreSQL in round 1 — the reviewer measured three of six admitted where the
implementer measured two, which the card already declares as an observation rather than a contract —
while the same tree's SQLite `QueueControlTests` and `ConcurrencyProjectCapTests` stay 9/9 green.
That contrast is the whole point: it is the same standard F-029's review applied to the claim path,
now applied to the insert path.
