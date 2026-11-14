---
id: B-002
title: maxConcurrency slot check is check-then-act — a token-holding client can exceed the cap (Q-005 hardening)
type: bug
priority: P2
size: M
status: done
created: 2026-11-14
updated: 2026-11-15
blocked: none
---

## Goal

The `maxConcurrency` a worker reports is enforced by the data layer, not by the client's good
behaviour: even a non-compliant client holding a valid worker token cannot hold more running runs
than its reported cap, no matter how it races the claim endpoint.

## Background

Q-005 recorded the seam verbatim (development log, concurred by the independent reviewer): the
server's `maxConcurrency` slot check is check-then-act, so a non-compliant client holding a valid
token could exceed the cap while real worker processes are structurally bounded (claim loops × at
most one run each). F-006's claim itself is a single-statement compare-and-set with a lease; the cap
check sits outside that atomic step, so two racing claim responses can both pass the check.

The consequence today is over-admission beyond the reported cap — not double execution (Q-005's
executed-once property held), not data corruption. That is why the user's ledger defers this past
the v1.0 tag (v1.0.1), with the known issue recorded in the CHANGELOG by P-001 rather than silently
shipped.

The likely shape of the fix: move the slot reservation into the same atomic step as the claim (the
cap is enforced where the compare-and-set is, or a slot row is reserved in the same transaction).
If the claim query changes, Q-005's rule applies: the PostgreSQL contract gate runs and is recorded.

## Acceptance Criteria

- [x] The slot check and the claim are one race-free step; a hostile-but-authenticated claimer
      cannot exceed its reported `maxConcurrency` even under concurrent claims.
- [x] A permanent regression test proves the race is closed: concurrent claims against one worker
      pinned to `maxConcurrency=1` never admit more than one running run.
- [x] Real-worker behaviour is unchanged: Q-005's concurrency scenarios stay green.
- [x] If the claim query changed, the PostgreSQL contract gate ran and its result is recorded here.
- [x] The CHANGELOG known-issue entry (added by P-001) is updated or removed when this lands.

## Subtasks

- [x] Locate the check-then-act window and pin it with a failing test before fixing.
- [x] Make the cap atomic (inside the claim's compare-and-set / transaction).
- [x] Regression test + Q-005 concurrency regression; PostgreSQL contract gate if the claim query
      moved.

## Dependencies

- none technically; scheduled **after the v1.0 tag** (P-001) per the user's ledger decision

## Test Cases

### TC-001: The race the seam allowed — PASS (red before the fix, green after)

Steps:
1. Register a worker with `maxConcurrency=1`; fire concurrent claim requests from multiple
   authenticated clients inside one window; count running runs attributed to that worker.

Expected:
- Exactly one run is admitted past the cap; the losers receive no work; the test asserts this from
  rows, not from absence of errors.

Result (observed, 2026-11-15): PASS after the fix, and honestly RED before it. The permanent test
is `tests/Zenkins.Tests/Concurrency/ConcurrencyClaimCapTests.cs`: one worker registered with
`maxConcurrency=1` through the real register endpoint, 4 queued runs per round, 8 concurrent
authenticated claims released through one barrier (`TaskCompletionSource`, the F-006 race-harness
shape of `RunClaimConcurrencyTests`), 5 rounds per run; the verdict is read from the rows the
server wrote (system-scope run reads: assigned = `Running` + still-leased `Queued`, the same state
the claim's own slot count reads), never from the responses.

- Red evidence (unfixed code, commit 59ca3ac plus only the new test): 3 consecutive complete runs,
  every one failed in round 1 with "the maxConcurrency=1 worker holds 4 assigned runs (4 of 8
  concurrent claims returned a run)". An earlier exploratory run of the same scenario opened the
  window at round 3 instead (4 leases stamped in one instant, visible in the server log) — SQLite's
  write serialization makes the natural race intermittent, which is why the test drives 5 rounds
  with a barrier release and why the recorded attempts differ per run. Two still-earlier attempts
  failed on bugs in the test itself (a per-round queue-scope assertion that wrongly forbade the
  FIFO drain of a previous round's leftovers, and a count read behind an empty tenant filter, which
  saw zero rows); both were fixed in the test, neither weakens it — the final shape fails only on
  the cap violation.
- Post-fix: green on every run, SQLite and PostgreSQL (see the development log for the PG probe).

### TC-002: Compliant clients unchanged — PASS

Steps:
1. Re-run the Q-005 concurrency filter (`FullyQualifiedName~Zenkins.Tests.Concurrency`).

Expected:
- All green; no behaviour change for real workers.

Result (observed, 2026-11-15): PASS. `dotnet test -c Release --filter
"FullyQualifiedName~Zenkins.Tests.Concurrency"` → 5 passed / 0 failed / 0 skipped — Q-005's four
real-process scenarios plus the new cap test. Real workers' claim loops are structurally at most
one run in flight (Q-005's record), so they never touch the enforcement at all: the count they
exercise is the same predicate as before, now simply inside the claim's transaction.

## Development Log

### 2026-11-14

- Card opened from Q-005's recorded seam (dev + reviewer concurrence) and the user's ledger decision:
  hardening deferred past v1.0, CHANGELOG entry in the meantime, this card schedules the fix for
  v1.0.1. Next step: pin the window with a failing test.

### 2026-11-15

- **Pinned the window first.** `tests/Zenkins.Tests/Concurrency/ConcurrencyClaimCapTests.cs` (new;
  TestServer pipeline over a throwaway SQLite file, worker registered with `maxConcurrency=1`
  through the real register endpoint, 8 concurrent authenticated claims per round released through
  one barrier, 5 rounds, verdict from rows). Against the unfixed code it went red on 3 consecutive
  runs, each in round 1: **4 of 8 racing claims admitted past the cap of 1** (an earlier exploratory
  run needed round 3 to open the window — SQLite's write serialization makes the race intermittent,
  which the test answers with rounds, not with a faked mechanism). Recorded honestly: two still-
  earlier attempts failed on bugs in the test itself (per-round queue scope vs the FIFO drain of
  leftovers; a count read behind an empty tenant filter), both fixed in the test, neither weakens
  the assertion.
- **Fix shape: per-worker serialization inside the claim's transaction.** `ClaimAsync` now opens one
  transaction that begins by a deliberate no-op write on the caller's own worker row
  (`ZenkinsDbContext.LockWorkerForClaimAsync`, new guarded `ExecuteUpdate`, SET fixed
  `UpdatedAt = UpdatedAt`): the provider's row lock (PostgreSQL) / the database's single write lock
  (SQLite) is held to commit, so two claims of one worker serialize instead of interleaving the
  slot count with the lease. Inside the lock the count can only ever be raised by the claim itself
  (requeues, cancels and completions only release slots), so count + one lease cannot cross the
  cap. The lease compare-and-set `TryLeaseBuildRunAsync` is **unchanged** - F-006's executed-once
  property and the lease semantics are intact - and everything is EF/LINQ, provider-agnostic (no
  raw SQL anywhere).
- **Why not the one-statement shape** (cap as a subquery in the lease CAS): on PostgreSQL READ
  COMMITTED each statement snapshots independently, so two lease UPDATEs on *different* queued runs
  can both evaluate the cap subquery before either commits - the race survives there while SQLite's
  single writer would mask it. The worker-row lock closes exactly that provider gap, which is why
  the PG probe below matters.
- **PostgreSQL contract gate.** The EF model is unchanged (no migration added, no entity touched):
  `dotnet ef migrations has-pending-model-changes --project src/Server` with
  `Database__Provider=Postgres` printed "No changes have been made to the model since the last
  migration." Because the claim query changed, the gate was backed with real-PostgreSQL behaviour
  evidence (the Q-008 pattern, no Docker): a throwaway PostgreSQL 16.15 cluster (`initdb` to
  /tmp, started on port 5433 with `pg_ctl`, no `brew services` registration), the single migration
  set applied by the real Server at startup (18 migrations, latest
  `20261114134917_AddProjectGitCredentialForeignKey`), then 3 rounds over HTTP against the real
  claim endpoint - per round a fresh `maxConcurrency=1` worker, 4 queued runs, 8 concurrent
  authenticated claims: **exactly 1 admitted per round (1x200 + 7x204), rows read with psql showing
  exactly 1 assigned run per worker**. Cluster stopped and the /tmp cluster, run directory and
  probe script deleted afterwards; nothing left registered or running.
- **Regression.** `dotnet build -c Release` 0 warnings / 0 errors; new test green on repeated runs
  (SQLite 3 consecutive + the PG probe rounds); `FullyQualifiedName~Zenkins.Tests.Concurrency` 5/5;
  fast suite (`Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd`)
  1046/1046 (1045 at the tag + this card's test); Recovery+Workers namespaces 88/88.
- **Sequencing note (stays honest).** The user directed the board be cleared while the v1.0 tag is
  blocked on their push decision. The tag points at 59ca3ac, whose tree still contains the
  check-then-act claim; this card's work sits on main strictly after it and carries the fix, so the
  "scheduled **after the v1.0 tag**" ordering in Dependencies holds exactly as written - the fix
  will ship with the release that follows the tag.
- Known trade-offs, for the reviewer: a claim now holds a transaction (and the worker-row lock)
  for its whole unit of work - milliseconds, bounded by `MaxClaimCandidates`; a concurrent
  heartbeat of the same worker briefly waits on that lock (row lock on PG, brief write wait on
  SQLite); the no-op lock UPDATE produces one dead row version per claim on PostgreSQL, which
  ordinary autovacuum absorbs at claim frequencies.

### 2026-11-15 — independent review: PASS

- **Read everything, adversarially.** Card, `RunService.ClaimAsync` (whole flow), the new
  `LockWorkerForClaimAsync`, the whole regression file, both CHANGELOG hunks.
- **(a) Lock correctness, both providers.** `ClaimAsync` opens the transaction at
  RunService.cs:736 and `LockWorkerForClaimAsync` (RunService.cs:737) is its first statement; the
  slot count (746), the candidate read (761) and the CAS loop (778) all run on the same context
  inside that transaction, and commit (789) releases the lock. On PostgreSQL an `UPDATE` takes the
  worker row's write lock even when the value is unchanged, and the racing claim blocks, then
  re-evaluates the WHERE (id still matches) and proceeds — its count read is a fresh READ
  COMMITTED snapshot that sees the first claim's committed lease. On SQLite the first write
  upgrades the deferred transaction to the single writer, held to commit, so the same
  serialization holds. The CAS is `ExecuteUpdateAsync` on the same context — enlisted, its own
  transaction is not opened.
- **(b) Deadlock audit.** The claim's order is worker row → run rows. The only writers that touch
  both are the offline sweep (`TryMarkWorkerOfflineAsync` then run CASes — each statement
  autocommits, so the worker-row lock is released before any run-row lock) and registration/
  heartbeat (worker + token rows only, never run rows). Lease sweeper, deadline sweeper, cancel,
  accept/complete all touch run rows only, one CAS at a time. Two claims of different workers
  attempt the shared queued runs in the same FIFO (CreatedAt, Id) order, so a loser blocks on the
  current holder and then skips it — no ABBA cycle. The worker eligibility read outside the
  transaction is unchanged from the pre-B-002 shape and matches the card's stated scope (the AC
  is the claim race, not a re-registration/heartbeat cap change racing a claim — see (c)).
- **(c) Invariant holds.** Exactly one statement in the product stamps a non-null
  `BuildRuns.WorkerId` (`TryLeaseBuildRunAsync`, ZenkinsDbContext.cs:234), and its only caller is
  the CAS loop inside the locked transaction. Requeue, cancel, terminate clear `WorkerId`;
  accept moves the worker's own Queued→Running (count unchanged — both statuses count); there is
  no retry/scheduler assigner. The cap value itself is read before the transaction, so a
  concurrent cap change via heartbeat/re-registration can transiently use the old value —
  pre-existing behaviour, consistent with the card's scope, not a regression this card introduced.
- **(d) Regression test.** Tenant scoping verified correct (`ReadRunsAsync(organizationId)` runs
  the product's system scope with an explicit org filter, RunTestHost.cs:309; the per-round
  predicate matches the claim's own slot-count predicate). Any round exceeding the cap trips the
  per-round assert; `Assert.Equal(1, worstAssigned)` defeats a vacuous pass (0 admissions). Run
  5 consecutive times green (5/5). Independent pre-fix red: with the two product files restored
  to HEAD (stash), the very first attempt failed in round 1 with the same message the dev
  recorded — "the maxConcurrency=1 worker holds 4 assigned runs (4 of 8 concurrent claims
  returned a run)" — then the stash was popped and the tree rebuilt.
- **(e) Portability.** No raw SQL, no provider-specific API in either new hunk (LINQ
  `ExecuteUpdateAsync` only). The rejected one-statement alternative is soundly analysed: on
  PostgreSQL READ COMMITTED each statement snapshots independently, so two CAS UPDATEs on
  different queued runs evaluate the cap subquery before either commits — and they touch
  different rows, so no row-lock contention saves them.
- **(f) PostgreSQL gate re-run by the reviewer.** `has-pending-model-changes` with
  `Database__Provider=Postgres` (and a design-time `ConnectionStrings__Default`) printed "No
  changes have been made to the model since the last migration." Live probe (Q-008 pattern, no
  Docker): throwaway PostgreSQL 16.15 (Homebrew keg, `initdb` to /tmp, port 5433), the real
  Server applied all **18** migrations (latest `20261114134917_AddProjectGitCredentialForeignKey`),
  then **3 rounds** over HTTP — per round a fresh `maxConcurrency=1` worker registered through
  the real `/api/workers/register` endpoint, 4 seeded queued runs, 8 concurrent authenticated
  claims through the real `/api/workers/claim`: **exactly 1×200 + 7×204 per round**, and psql
  rows show exactly **1** assigned run per worker (Queued, lease still live). Cluster stopped and
  the /tmp cluster, probe script and server log deleted; nothing left running.
- **(g) Bookkeeping.** CHANGELOG entry wording checked against the preamble's card-coupling rule
  (past tense for what released binaries did, fix dated, "Ships with the next release"); the
  release-section "1045 tests at this revision" correctly still describes the tagged release.
  The PM-flagged omission was real: `docs/development.md` §2 still said fast=1045/total=1064 —
  fixed in this review's edit set (fast 1046, all six 1065, header note dated).
- **Reviewer's own runs.** `dotnet build -c Release` 0/0; `ConcurrencyClaimCapTests` 5/5
  consecutive; `FullyQualifiedName~Zenkins.Tests.Concurrency` 5 passed / 0 failed; fast suite
  1046 passed / 0 failed / 0 skipped; Recovery+Workers 88 passed / 0 failed / 0 skipped; pre-fix
  red on attempt 1 as above; PG probe numbers as above. No commit made.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for the seam Q-005 recorded (P2, deferred with rationale). | Closed | Closed here: the cap is enforced inside the claim's transaction (per-worker row lock + count + unchanged compare-and-set); the pinning evidence is TC-001, the post-fix numbers are in the development log. |

## Verification

- Related files: `src/Server/Runs/RunService.cs` (`ClaimAsync` - transaction + lock + count +
  unchanged CAS loop), `src/Server/Data/ZenkinsDbContext.cs` (`LockWorkerForClaimAsync`, new),
  `tests/Zenkins.Tests/Concurrency/ConcurrencyClaimCapTests.cs` (new regression),
  `CHANGELOG.md` (B-002 entry reworded to reflect the fix).
- How to run/verify: `dotnet build -c Release`; the new regression test;
  `FullyQualifiedName~Zenkins.Tests.Concurrency`; the fast suite; Recovery+Workers namespaces;
  `dotnet ef migrations has-pending-model-changes` with the PostgreSQL provider; the real-PG claim
  probe (throwaway cluster, since deleted) as recorded in the development log.
- Results (observed 2026-11-15): build 0 warnings / 0 errors. Pre-fix red: `ConcurrencyClaimCapTests`
  failed 3 consecutive runs, 4 of 8 concurrent claims admitted past `maxConcurrency=1` in round 1
  each. Post-fix green: the new test (repeated runs, SQLite) and the real-PG probe (3/3 rounds,
  exactly 1 admitted of 8); `FullyQualifiedName~Zenkins.Tests.Concurrency` → 5 passed / 0 failed /
  0 skipped; fast suite → 1046 passed / 0 failed / 0 skipped; Recovery+Workers → 88 passed /
  0 failed / 0 skipped. Migration gate: "No changes have been made to the model since the last
  migration." (PostgreSQL provider).
