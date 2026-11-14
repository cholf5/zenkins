---
id: Q-005
title: Multi-worker concurrency and tag assignment, end to end
type: test
priority: P1
size: L
status: todo
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

- [ ] Three or more real worker processes are connected concurrently; ten or more runs are triggered;
      every run is claimed exactly once, no run is ever executed by two workers, and every run ends in
      the terminal state its worker reported.
- [ ] A run with required tags is claimed only by a worker whose tags cover them; non-matching workers
      polling the same queue never receive it.
- [ ] A run whose required tags match no live worker stays `Queued` (it does not go `Lost` or `Failed`
      for lack of a worker) and executes once a matching worker registers.
- [ ] Two workers racing to claim a single queued run: exactly one receives it; the other's claim
      returns no work; the executed-once property is provable from step records/logs, not from
      absence of errors.
- [ ] A worker never holds more running runs than the `maxConcurrency` it reports in its heartbeat.
- [ ] Run, step and log rows written by concurrent workers remain attributable: each row names the
      worker that produced it and no cross-worker interleaving corrupts a run's step sequence.

## Subtasks

- [ ] Extend the Q-004 process harness to a multi-worker ring (N workers, shared server, per-worker
      process handles and identity assertions).
- [ ] Concurrency scenarios (mass trigger, claim race, `maxConcurrency` ceiling).
- [ ] Tag scenarios (placement, queue patience for unmatched tags, trigger/claim consistency).
- [ ] Run the full suite and the PostgreSQL contract gate if the claim query changes.

## Dependencies

- none (F-004, F-005, F-006 and Q-004 are accepted)

## Test Cases

### TC-001: Ten runs across three workers, executed exactly once

Steps:
1. Start three real workers against a real server; trigger ten runs of a pipeline that writes its
   worker identity into a step log.

Expected:
- Ten `Success` runs; ten distinct step-log identities; no run's logs mention a second worker; no
  worker claims a run already `Running` elsewhere.

### TC-002: Required tags place a run and starve it honestly

Steps:
1. Register workers tagged `linux` and `windows`; trigger a run with required tags `linux`; then one
   with required tags `gpu` that no worker declares.

Expected:
- The `linux` run is claimed only by the `linux` worker; the `gpu` run stays `Queued` while workers
  poll, and executes when a `gpu`-tagged worker registers.

### TC-003: The claim race never double-executes

Steps:
1. Queue exactly one run; start two workers whose first poll lands inside the same lease window.

Expected:
- One worker receives the run and executes it; the other receives an empty claim; the run has exactly
  one step sequence and one terminal report.

## Development Log

### 2026-11-14

- Planned from roadmap v1.0. Reuses the Q-004 real-process harness rather than inventing a second one;
  the new capability is N workers in the ring and per-worker identity assertions.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Runs/RunService.cs` (claim query), `src/Server/Workers/*`,
  `tests/Zenkins.Tests/*` (recovery/concurrency harness location)
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "FullyQualifiedName~Concurrency"`;
  full suite.
- Results: not run (card not started).
