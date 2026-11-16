---
id: F-029
title: Queue controls — per-project concurrency, quiet period and a bounded queue
type: feature
priority: P2
size: M
status: done
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

- [x] `Project.MaxConcurrentRuns` (null = unlimited) is enforced at claim time:
      with a limit of 1, a second run of the project is never claimed while the
      first is `Running`.
- [x] `Project.QuietPeriodSeconds` (default 0): a queued run is not claimable until
      the period has passed; a trigger during the period is a normal queued run (no
      coalescing in this card).
- [x] `Project.MaxQueuedRuns` (null = unlimited): a trigger beyond the cap is
      refused before any insert, with a field error on the form, 409 on the API and
      `queue-full` on a webhook delivery row.

      **Complete (2026-11-16).** The refusal happens before any insert, the form shows
      the field-level code (`?runError=queue-full`, rendered with the observed
      numbers), a webhook delivery records `queue-full` and answers 2xx, and F-027's
      `POST /api/v1/projects/{id}/runs` now answers `409` with `error: queue-full` -
      proven by
      `ApiV1RunApiTests.A_full_queue_answers_409_queue_full_and_queues_nothing`
      (one queued run, cap 1, the API refuses, the run count does not move).
- [x] The claims stay correct under concurrency: two workers claiming at the same
      instant cannot both take the second slot of a `MaxConcurrentRuns = 1` project
      (this is the property B-002's report says is missing).
- [x] All three limits are shown on the project settings tab and echoed in every
      refusal message ("queue full: 20 queued runs, cap 20").
- [x] Existing projects behave exactly as today (all limits null/0) and the
      defaults are documented.

## Subtasks

- [x] Entity + migration: the three columns with their defaults; validation (a
      minimum of 1 for concurrency, a non-negative quiet period, a cap ≥ 1).
- [x] Claim path: move the slot decision into one transaction that takes the
      project row lock (or a counter row), the same pattern as
      `LockWorkerForClaimAsync`; record the observed-vs-allowed behavior in a test
      that runs two claims in parallel.
- [x] Create path: the queue-cap check inside the trigger transaction, so two
      concurrent triggers cannot both pass it.
- [x] UI: a Limits card; the quiet period rendered as "seconds after trigger".
- [x] PRD §3.5: record the three controls and the claim ordering (quiet period →
      concurrency → queue cap).
- [x] B-002: **kept, this card depends on it** (not absorbed) — the decision and its
      reasons are below and in this card's Development Log.

## Dependencies

- B-002 (the check-then-act fix) — **kept as a dependency, not absorbed**: B-002 landed on
  2026-11-15 (the worker-slot decision inside the claim's transaction, with its own regression
  test); this card lays policy on top of it instead of duplicating it.

## Test Cases

### TC-001: A concurrency limit of 1 serializes claims

Steps:
1. Set `MaxConcurrentRuns = 1`; queue two runs; hand two claims to two workers at
   the same instant.

Expected:
- Exactly one claim succeeds; the other answers 204.
- After the first run ends, the second is claimed.

Result (observed, 2026-11-16): PASS, in two tiers — and the tiers say different things.
Sequential half: `QueueControlTests.A_concurrency_limit_of_one_serializes_the_project_until_the_run_ends`
(worker A claims, worker B answers 204 with its own slot free — the *claimed* run
already holds the project's slot — and after A's run completes, B claims the other run).
**SQLite (fast suite):** `ConcurrencyProjectCapTests` — 3 rounds, 2 workers with
tag-disjoint queues so each walks a different first candidate, 4 concurrent claims each
released through one barrier, verdict read from the rows — exactly one admission per
round. This proves the limit *exists and is enforced* on every run; it does **not**
prove the lock is what enforces it, because SQLite's single writer serializes the
statements anyway (mutation evidence in the Development Log).
**PostgreSQL (`Category=Postgres`, `PostgresProjectCapTests`):** the same shape, 5
rounds, against a real server — exactly one admission per round — plus a negative
control: the two statements a check-then-act implementation would run (count the active
runs, then lease), executed as a sequence through the product's own compare-and-set on
the same project, **admit two**. Read that control for exactly what it shows: **the count
is load-bearing** — the compare-and-set alone cannot hold the cap — and nothing more. It
is a sequence, not the eight claims overlapping (it produces the same two active runs on
SQLite, so it has no provider discrimination), and the two paths differ in more than the
lock (worker row lock, candidate snapshot, quiet-period filter). The evidence that the
*lock* is what makes the count trustworthy is the mutation record in the Development Log
— the claim's project lock deleted, the count kept — which turns this fact red in round 1
on PostgreSQL.

### TC-002: A quiet period delays claimability

Steps:
1. Set a 5-second quiet period; trigger a run; claim immediately, then after 6
   seconds.

Expected:
- The first claim answers 204; the second claims the run.

Result (observed, 2026-11-16): PASS.
`QueueControlTests.A_quiet_period_delays_claimability_and_zero_means_immediately`: with
a 5-second quiet period the claim answers 204 while the run is inside the period; the
run is then aged past its period (the deterministic stand-in for waiting, asserted on
the row itself) and the next claim leases it; a project with the default 0 is claimable
immediately, which is what proves the period — not the harness — blocked the first
claim.

### TC-003: A full queue refuses without inserting

Steps:
1. Set `MaxQueuedRuns = 2`; trigger three times.

Expected:
- Two runs are queued; the third is refused with the field error / 409 / delivery
  outcome `queue-full`.
- The refusal happens before the run row exists (no orphan queued row).

Result (observed, 2026-11-16): PASS.
`QueueControlTests.A_full_queue_refuses_the_next_trigger_before_any_row_exists` (two
queued, the third redirects to `?runError=queue-full`, the table holds exactly two rows,
the page renders "Queue full: 2 queued runs, cap 2", and draining one row lets exactly
one more trigger through) and
`WebhookProjectGateTests.A_full_queue_records_queue_full_and_queues_nothing` (the second
push records `queue-full` with no run id and the git host still sees 202). The API's 409
is F-027's — see the AC note above.

## Notes and Decisions

- Rejected: coalescing triggers inside the quiet period ("the last push wins"). It
  is a separate feature with its own semantics (which commit wins, what the UI
  shows) and it would quietly discard runs people asked for.
- **B-002 is kept, not absorbed.** B-002 (the `maxConcurrency` check-then-act) was
  fixed on 2026-11-15 and is in main: `RunService.ClaimAsync` decides the worker's
  slots inside one transaction that begins by locking the caller's own worker row
  (`LockWorkerForClaimAsync`), and the lease compare-and-set is unchanged. Re-doing
  that work here would duplicate a fix that is already proven by its own regression
  test, so this card *depends* on it and adds a second, different serialization to the
  same transaction: the project row lock (`LockProjectForTriggerAsync`) plus the
  project's active-run count, before the same CAS. Two locks, two scopes — the worker
  lock bounds one worker's slots, the project lock bounds one project's slots — and the
  claim takes them in that order (worker row → project row → run rows), with candidate
  order following the queue's own FIFO, so no cycle is possible.
- **Why not one statement.** Folding the project cap into the lease CAS as a subquery
  would repeat exactly the mistake B-002 analysed: on PostgreSQL READ COMMITTED each
  statement snapshots independently, and two claims lease *different* runs, so no row
  lock would save them. The project row lock is the same reasoning B-002 used for the
  worker row.
- **Ordering: quiet period → concurrency → queue cap.** The quiet period and the
  concurrency limit decide whether a queued run may be claimed; the queue cap decides
  whether a trigger becomes a queued run at all. The create path takes the same project
  lock, so the count and the insert cannot interleave with another trigger.
- **What "active" means for the concurrency limit.** `Running` plus
  claimed-but-not-yet-accepted runs (`Queued` with a `WorkerId`) — the identical
  predicate the worker's own slot count uses, so a claim cannot outrun the limit through
  a lease, and an expired lease keeps occupying the slot until the lease sweeper clears
  it (the documented worker-cap behaviour).
- **Defaults.** `MaxConcurrentRuns` and `MaxQueuedRuns` are nullable (null = unlimited)
  and `QuietPeriodSeconds` defaults to 0, so every project that never opens the Limits
  card behaves exactly as it did before this card. A project with no concurrency limit
  pays neither the project lock nor the count on the claim path.
- **Validation bounds.** Concurrency 1..100, quiet period 0..86400 s, queue cap 1..1000;
  the same ranges are restated as database check constraints so a row written out of
  band cannot declare a limit no claim could satisfy.

## Development Log

### 2026-11-16

- **Decision first (the card asks for it): B-002 stays, this card depends on it.** See
  "Notes and Decisions" — the claim keeps B-002's transaction and worker-row lock, and
  this card adds the project-row lock and the project's active-run count inside the same
  transaction, immediately before the unchanged lease CAS. Nothing here re-implements a
  non-atomic check-then-act.
- **Entity + migration.** `Project.MaxConcurrentRuns` (int?, null = unlimited),
  `Project.QuietPeriodSeconds` (int, default 0), `Project.MaxQueuedRuns` (int?, null =
  unlimited), plus three check constraints. One migration
  (`20261116044928_AddProjectEnableAndQueueControls`, shared with F-028's columns;
  PostgreSQL-generated like the rest of the history, applied cleanly to SQLite and to a
  throwaway PostgreSQL 16.15 cluster).
- **Claim path.** Per candidate, `RunService.ProjectAllowsClaimAsync` answers the three
  questions in the card's order: a disabled project's run is never claimed (F-028's
  defence in depth), a run inside its quiet period is skipped in memory (SQLite cannot
  compare `DateTimeOffset` in SQL, and time only moves forward inside one transaction),
  and a project with a limit takes the project row lock, counts its active runs, and
  refuses when the count is at the limit. A project without a limit takes neither.
- **Create path.** `RunService.StageQueuedRunAsync` (shared with F-028) takes the same
  project row lock, reads the flag, counts the project's queued runs and refuses at the
  cap *before* `context.BuildRuns.Add`, so a refusal can never leave an orphan row and
  two concurrent triggers cannot both pass. The refusal message quotes the observed
  numbers; the page re-reads the count for the redirect because the PRG can only carry
  the code.
- **Endpoints + UI.** `POST /projects/{id}/limits` (form-bound, antiforgery, the same
  404/403 shapes) and the Limits card on the settings tab, with the quiet period
  labelled as seconds after the trigger and blanks meaning the default. PRD §3.5 records
  the three controls and the ordering.
- **Verification.** `dotnet build Zenkins.slnx -c Release` clean. Fast suite 1124 passed
  / 0 failed / 0 skipped; Milestone 12, Recovery 4, PublishSmoke 2, PublishAot 2,
  GitSshd 3, Postgres 1 — all seven green (the Postgres suite with
  `ZENKINS_TEST_POSTGRES` set to a disposable PostgreSQL 16.15 database; without it the
  same suite skips with the recorded reason, re-run to prove that path). This card's
  facts: 8 in `tests/Zenkins.Tests/Runs/QueueControlTests.cs` (including the four
  illegal-limit cases), 1 in
  `tests/Zenkins.Tests/Concurrency/ConcurrencyProjectCapTests.cs` (SQLite), 1 in
  `tests/Zenkins.Tests/Concurrency/PostgresProjectCapTests.cs` (`Category=Postgres`,
  which also carries the negative control), 1 in
  `tests/Zenkins.Tests/Webhooks/WebhookProjectGateTests.cs`.
- **The race, honestly — what each provider proves.** On SQLite the race cannot be
  observed at all: every claim begins with a write (the worker-row no-op `UPDATE`), so
  the single writer serializes the statements even when the *lock's* purpose is bypassed.
  Review measured exactly that with mutants: **M1** (only the claim's
  `LockProjectForTriggerAsync` deleted, the count kept) stays **green** on SQLite, **M2**
  (the whole limit deleted) goes red, and on PostgreSQL M1 goes red. So the SQLite
  regression proves the limit exists; it cannot prove the limit is *the lock's* doing. The
  card's Notes now say so, and the lock's necessity has its own committed evidence:
  - **`tests/Zenkins.Tests/Concurrency/PostgresProjectCapTests.cs`**, trait
    `Category=Postgres`, wired into `scripts/verify.sh` and development.md §2 as the
    seventh suite. It needs a disposable PostgreSQL named by `ZENKINS_TEST_POSTGRES` (its
    `public` schema is dropped and recreated before each fact) and **skips with that
    reason recorded** where the variable is absent — the GitSshd discipline — so it is
    excluded from the fast filter and costs a machine without PostgreSQL nothing.
  - It carries (a) the AC's fact — cap 1, two tag-disjoint workers, 8 concurrent claims,
    5 rounds: exactly one admission per round, read from the rows — and (b) a
    **negative control built from the product's own primitive**: two contexts count the
    project's active runs (both read 0) and then both call `TryLeaseBuildRunAsync` on two
    different runs, leaving **2** active. That is the check-then-act sequence a non-atomic
    implementation would run, reproduced deterministically on the same provider, so (a) is
    evidence about the lock rather than about a race that never happened.
  - **Mutation check against the committed suite** (local edit, reverted immediately; not
    a committed switch, because a test-only branch inside the claim path would be product
    code changing to satisfy a test): with the claim's project lock deleted and the count
    kept (M1), `Category=Postgres` went **red in round 1** — "the MaxConcurrentRuns=1
    project holds 2 active runs (2 of 8 concurrent claims admitted) on PostgreSQL". With
    the code restored, the same suite is green (5/5 rounds). Logs:
    `artifacts/logs/p1-postgres-run.log` (green) and
    `artifacts/logs/p1-postgres-m1-mutant.log` (M1 red). The cluster was a throwaway
    PostgreSQL 16.15 (`initdb` in a temp directory) and was stopped and deleted afterwards.
  - **Limitation, stated plainly.** The negative control exercises the two statements, not
    `RunService` with its lock removed; the M1 result above is the direct measurement, and
    it is a local mutation run, not a repeatable CI switch. Anyone re-running it has to
    delete one call in `ProjectAllowsClaimAsync` — which is exactly what the card records
    so the next reviewer does not have to rediscover it.
- **Not covered, and why.** The API's 409 for a refused trigger is F-027's (no JSON API
  exists yet); the service already returns the dedicated `Refused` status and the
  `queue-full` code an API maps to 409.

### 2026-11-16 — independent review: PASS

Two review rounds in an isolated copy of the uncommitted batch. The verdict turned on one question:
does the committed regression actually pin the project row lock?

- **First round said no.** Mutating the tree to delete only the claim's `LockProjectForTriggerAsync`
  (keeping the count) left `ConcurrencyProjectCapTests` green on SQLite: a single-writer database
  serializes the whole claim transaction, so the row lock cannot be observed there. The card had
  claimed the committed test pinned "the same verdict" — an over-claim, now removed.
- **Second round said yes, after the fix.** The claim shape was rebuilt as a committed
  `Category=Postgres` suite against a real server, and the reviewer independently reproduced it:
  five rounds of eight concurrent claims admit exactly one on PostgreSQL, and deleting only the
  claim's project-row lock turns round 1 red ("holds 2 active runs (2 of 8 concurrent claims
  admitted) on PostgreSQL"). Skipping without `ZENKINS_TEST_POSTGRES`, and the destructive schema
  reset's mechanical guard (the database name must contain `test`), were both verified.
- The negative control was cut back to what it proves: a hand-written check-then-act through the
  product's own compare-and-set admits two, so **the count is load-bearing** — that is all. It is a
  sequence, it reproduces on SQLite, and the two paths differ in more than the lock; the card now
  says so instead of over-claiming.
- AC3 was the API's 409, now closed by F-027. Existing projects keep their unlimited defaults;
  the trigger form, the webhook delivery row and the settings page all echo the observed values.
