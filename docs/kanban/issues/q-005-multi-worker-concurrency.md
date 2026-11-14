---
id: Q-005
title: Multi-worker concurrency and tag assignment, end to end
type: test
priority: P1
size: L
status: done
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

Prove with real worker processes what PRD §10 scenarios 2–3 promise: several workers online at once
claim distinct work, one run is never executed twice, and a run's required tags keep it on matching
workers only.

## Background

Roadmap v1.0 ("Multi-worker concurrency and no-double-assignment tests") and PRD §10 scenario 3. The
mechanisms exist and are tested one level down: F-006's claim is a single-statement compare-and-set
with a lease, unit-tested at the database level; F-004 stores tags and `maxConcurrency` and validates
required tags at trigger time (`RunService` refuses tags no organization worker declares); Q-004 proved
crash and lease recovery — but always with one worker in the ring at a time. What nobody has exercised
is the composition under true concurrency: multiple worker processes racing the same queue, a
tag-restricted run competing with unrestricted runs, and the queue holding a run whose tags match no
live worker.

Decisions:

- **Acceptance tests first, product gaps second**, the Q-002/Q-004 rule. If a scenario reveals a real
  defect (a run claimed twice, a tag-restricted run landing on the wrong worker, a worker holding more
  runs than its `maxConcurrency`), it is fixed here and the failing scenario stays as the regression.
- **Real processes again**: the Q-004 harness pattern — real server process, real worker executables,
  three or more workers at once — with assertions on rows, HTTP responses and step records.
- **Trigger-side validation must match claim-side filtering.** The card specifically checks that a
  required tag enforced when the run is created is enforced again where runs are claimed, not only in
  the trigger form.

## Acceptance Criteria

- [x] Three or more real worker processes are connected concurrently; ten or more runs are triggered;
      every run is claimed exactly once, no run is ever executed by two workers, and every run ends in
      the terminal state its worker reported.
- [x] A run with required tags is claimed only by a worker whose tags cover them; non-matching workers
      polling the same queue never receive it.
- [x] A run whose required tags match no live worker stays `Queued` (it does not go `Lost` or `Failed`
      for lack of a worker) and executes once a matching worker registers.
- [x] Two workers racing to claim a single queued run: exactly one receives it; the other's claim
      returns no work; the executed-once property is provable from step records/logs, not from
      absence of errors.
- [x] A worker never holds more running runs than the `maxConcurrency` it reports in its heartbeat.
- [x] Run, step and log rows written by concurrent workers remain attributable: each row names the
      worker that produced it and no cross-worker interleaving corrupts a run's step sequence.

## Subtasks

- [x] Extend the Q-004 process harness to a multi-worker ring (N workers, shared server, per-worker
      process handles and identity assertions).
- [x] Concurrency scenarios (mass trigger, claim race, `maxConcurrency` ceiling).
- [x] Tag scenarios (placement, queue patience for unmatched tags, trigger/claim consistency).
- [x] Run the full suite and the PostgreSQL contract gate if the claim query changes.

## Dependencies

- none (F-004, F-005, F-006 and Q-004 are accepted)

## Test Cases

### TC-001: Ten runs across three workers, executed exactly once — PASS

Steps:
1. Start three real workers against a real server; trigger ten runs of a pipeline that writes its
   worker identity into a step log.

Expected:
- Ten `Success` runs; ten distinct step-log identities; no run's logs mention a second worker; no
  worker claims a run already `Running` elsewhere.

Result (observed, 2026-11-14): PASS. Twelve runs across three real worker processes (twelve, so the
queue stays deeper than the workers can drain in one pass), all `Success` with the worker-reported
terminal state and `FailureReason` null. Executed-once is three-way evidenced per run: the run row
names one `WorkerId`, the owner process printed `run {id} executing in`, and the run's persisted log
contains only that worker's name (steps echo `$ZENKINS_WORKER_NAME`). Every worker took at least one
run, and step sequences survive concurrency uncorrupted (per-run assertion: declared count, contiguous
indexes, unique step ids, all `Success`).

### TC-002: Required tags place a run and starve it honestly — PASS

Steps:
1. Register workers tagged `linux` and `windows`; trigger a run with required tags `linux`; then one
   with required tags `gpu` that no worker declares.

Expected:
- The `linux` run is claimed only by the `linux` worker; the `gpu` run stays `Queued` while workers
  poll, and executes when a `gpu`-tagged worker registers.

Result (observed, 2026-11-14): PASS. The `linux` run lands only on the linux-declaring worker: the
windows worker polls the whole time and its output never mentions the run, and a windows-tagged probe
claiming directly against the API returns 204 at every sample. The `gpu` run is queued through the real
trigger form — legal because a gpu-declaring worker *row* exists (registered, then its process killed;
the server's own offline monitor flips the row), so the trigger-side union check accepts the tag while
no live process declares it. The run stays `Queued` through an 8 s settle window (probe 204 at every
sample, never `Lost`, never `Failed`), then executes exactly once after a fresh gpu worker registers,
with attribution checked against all four workers. A tag no worker row declares is refused by the real
form (`tag-unknown`) and never enters the queue — trigger-side and claim-side agree.

### TC-003: The claim race never double-executes — PASS

Steps:
1. Queue exactly one run; start two workers whose first poll lands inside the same lease window.

Expected:
- One worker receives the run and executes it; the other receives an empty claim; the run has exactly
  one step sequence and one terminal report.

Result (observed, 2026-11-14): PASS. One queued run, two real workers polling from start: exactly one
executes it (same three-way attribution) with one step sequence and one terminal report; the loser's
output never mentions the run, and a probe claim afterwards returns 204. One honest caveat, recorded:
the loser's empty claim is evidenced indirectly (probe 204 plus negative output) because a real worker
does not surface its claim response — the AC's "provable from records" standard is met by the records
that exist.

## Development Log

### 2026-11-14

- Planned from roadmap v1.0. Reuses the Q-004 real-process harness rather than inventing a second one;
  the new capability is N workers in the ring and per-worker identity assertions.
- Implemented in one pass. Harness: `tests/Zenkins.Tests/Concurrency/` (6 files, 788 lines) —
  `ConcurrencyHost` extends Q-004's `RecoveryHost` into an N-worker ring (per-worker env-declared tags
  and `maxConcurrency`, tagged trigger forms, tagged probe claims, read-only SQL sampling of run and
  lease state, cross-worker attribution and step-sequence assertions); `ConcurrencyPipelines` adds an
  identity pipeline (steps echo `$ZENKINS_WORKER_NAME`, so the executing worker's name lands in the
  run's persisted log). `RecoveryHost.cs` opened for reuse with a 2-line, behaviour-preserving change
  (unsealed + protected constructor; Q-004 regression 80/80).
- No product-code changes; the claim query is untouched. Two seams recorded, not defects (dev and the
  independent reviewer concur): the server's `maxConcurrency` slot check is check-then-act, so a
  non-compliant client holding a valid token could exceed the cap while real worker processes are
  structurally bounded (claim loops × at most one run each) — a candidate hardening card, not filed
  here; and trigger-side tag validation checks the union of all workers' tags while claiming requires
  one worker to cover all required tags, so a multi-tag run split across two workers passes the form
  and starves honestly (`Queued`), which is AC-3's semantics.
- Verification (dev run + fully independent review run): `dotnet build -c Release` 0 warnings;
  `dotnet test -c Release --filter "FullyQualifiedName~Zenkins.Tests.Concurrency"` 4 passed — green in
  4 dev rounds and 2 review rounds (the earlier `~Concurrency` filter also matches 13 pre-existing test
  names, all green; the precise filter is the one above); Recovery+Workers regression 80/80; full
  suite 963/963, 0 failed, 0 skipped; PostgreSQL contract gate not required (claim query unchanged,
  verified from the diff by the reviewer).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | No product defect found. Every scenario behaved as the mechanisms promise: executed once under true concurrency, honest tag placement and starvation, the `maxConcurrency` ceiling held and actually touched by real processes. | Closed | Two seams recorded in the development log (check-then-act cap vs non-compliant clients; union-vs-single-worker tag asymmetry) as hardening candidates, not defects. |

## Verification

- Related files: `src/Server/Runs/RunService.cs` (claim query), `src/Server/Workers/*`,
  `tests/Zenkins.Tests/*` (recovery/concurrency harness location)
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "FullyQualifiedName~Concurrency"`;
  full suite.
- Results (observed 2026-11-14; dev run, then an independent review run): `dotnet build -c Release`
  0 warnings / 0 errors; `dotnet test -c Release --filter
  "FullyQualifiedName~Zenkins.Tests.Concurrency"` → 4 passed / 0 failed / 0 skipped, green across six
  rounds (4 dev + 2 review); Recovery+Workers regression → 80/80; full suite `dotnet test -c Release`
  → 963/963, 0 failed, 0 skipped; PostgreSQL contract gate not needed (claim query unchanged,
  reviewer-verified from the diff).
