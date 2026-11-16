---
id: Q-020
title: The concurrency mass trigger gives the offline window no margin, so a stalled heartbeat on a 2-core runner reads as a product failure
type: test
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

`ConcurrencyMassTriggerTests.Twelve_runs_across_three_workers_are_claimed_and_executed_exactly_once`
stops failing on Windows CI for a reason that is not in the product, and when it does fail the message
carries enough of the worker's own output to prove what happened.

## Background

The fact failed on Windows CI (run 43870254355): three of twelve runs `Lost` with
`FailureReason = worker-offline`. A Windows machine with the repository was used to pin the mechanism
(2026-11-16), and the answer is that the product behaved correctly:

- **Pure load does not reproduce it.** Four consecutive runs on 20 cores passed (14.8–16.3 s); pinning
  the whole process tree to two cores passed (26 s); saturating those two cores with three spin loops
  still passed in 4 m 39 s. Heartbeats are an async I/O loop, so CPU starvation alone does not beat the
  1 s period.
- **A stalled heartbeat does, decisively.** Suspending the live worker process (`NtSuspendProcess`) for
  6 s and resuming it produced exactly `1 out of 12 … Lost, worker-offline`, with every worker process
  still alive and heartbeating again afterwards. 9 s (still under the 10 s request timeout) produced
  the same single loss. That is the CI signature: the process never died, its heartbeat stopped for
  longer than the offline window.

Why it has no margin: the harness compresses time to `OfflineAfterSeconds = 5` with a 1 s heartbeat and
a 1 s sweep, so **five dropped beats** is all it takes; `WorkerRunner.HeartbeatLoopAsync` only delays a
second on success and otherwise backs off exponentially (1 s, 2 s, 4 s, ±25 % jitter, reset on success),
so three consecutive failures already exceed the window; and one request stalling up to the 10 s
timeout breaks it on its own. A two-core runner hosts the server, three workers and the test host, with
SQLite's single writer and three claim polls per second — a single write-lock queue or GC pause inside
that arithmetic is enough. `Lost` rather than `Failed`, with no rerun, is F-010's design working.

## Acceptance Criteria

- [ ] The harness's offline window has margin over the heartbeat period and the backoff ladder: either
      `OfflineAfterSeconds` raised to at least 15, or the heartbeat interval reduced to the sub-second
      range — whichever is chosen, the reasoning is recorded next to the constants.
- [ ] A fact proves the margin rather than asserting it: suspending a live worker long enough to
      exceed the old window but not the new one leaves all twelve runs successful.
- [ ] When the fact does fail, the message carries each suspect worker's stdout/stderr tail (today it
      reports only `Lost`, which is why the CI log could not be diagnosed from its own output).
- [ ] The fact passes on a two-core configuration and on the Windows runner.
- [ ] `docs/development.md` §2 counts re-recorded if facts were added.

## Subtasks

- [ ] Raise the window (or shorten the heartbeat) and record why next to the knobs.
- [ ] Add the suspend-based margin fact.
- [ ] Attach worker output tails to the failure message.

## Dependencies

- none (F-010's `Lost` semantics are correct and unchanged)

## Notes and Decisions

- Rejected: changing the product's offline detection. The window is a product decision, and the
  evidence shows the product detecting a real stall correctly — it is the test that compressed the
  window until a scheduler hiccup looks like an outage.
- The suspend-based reproduction is worth keeping as a fact precisely because load alone cannot
  reproduce this class; it is a deterministic way to produce "process alive, heartbeat stopped".
