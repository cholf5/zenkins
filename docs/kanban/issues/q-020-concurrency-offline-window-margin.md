---
id: Q-020
title: The concurrency mass trigger gives the offline window no margin, so a stalled heartbeat on a 2-core runner reads as a product failure
type: test
priority: P2
size: S
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: windows: the new fact has only run on POSIX
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

- [x] The harness's offline window has margin over the heartbeat period and the backoff ladder: either
      `OfflineAfterSeconds` raised to at least 15, or the heartbeat interval reduced to the sub-second
      range — whichever is chosen, the reasoning is recorded next to the constants.
- [x] A fact proves the margin rather than asserting it: suspending a live worker long enough to
      exceed the old window but not the new one leaves all twelve runs successful.
- [x] When the fact does fail, the message carries each suspect worker's stdout/stderr tail (today it
      reports only `Lost`, which is why the CI log could not be diagnosed from its own output).
- [ ] The fact passes on a two-core configuration and on the Windows runner. Unverified: it has only
      been run on a normal POSIX machine (8 cores). The mechanism it drives is platform-independent
      (a loopback TCP relay, no signals), and the load-only failure modes the card recorded all
      reproduced on Windows, so the remaining risk is timing, not mechanism.
- [x] `docs/development.md` §2 counts re-recorded if facts were added. (No new category, no new
      exclusion; the fast suite grew by one.)

## Subtasks

- [x] Raise the window (or shorten the heartbeat) and record why next to the knobs.
- [x] Add the suspend-based margin fact.
- [x] Attach worker output tails to the failure message.

## Dependencies

- none (F-010's `Lost` semantics are correct and unchanged)

## Notes and Decisions

- Rejected: changing the product's offline detection. The window is a product decision, and the
  evidence shows the product detecting a real stall correctly — it is the test that compressed the
  window until a scheduler hiccup looks like an outage.
- The suspend-based reproduction is worth keeping as a fact precisely because load alone cannot
  reproduce this class; it is a deterministic way to produce "process alive, heartbeat stopped".

## Resolution (2026-11-16)

**The window.** `ConcurrencyHost` now runs the ring under `OfflineWindowSeconds = 15` (was a literal
`OfflineAfterSeconds: 5`), with the arithmetic and the two rejected alternatives written next to the
constant: 15 s tolerates two full 10 s request timeouts plus jitter on the backoff ladder, where 5 s
tolerated one stalled request and no backoff at all. The heartbeat stays at 1 s (the finest the worker
accepts) and is now a named constant beside it — the fix widens the ratio the offline decision runs on
instead of making the traffic between three workers and one SQLite writer denser. `RecoveryHost` keeps
its own 5 s window: Q-004's lease facts use the short threshold as an observation window on purpose,
and no concurrency fact depends on the window being short.

**The fact.** `Concurrency/ConcurrencyOfflineWindowTests.A_stalled_worker_inside_the_window_loses_no_run`
puts two ring workers directly on the server and the third behind a `HeartbeatGate` — a loopback TCP
relay that can be shut. With the gate shut, the gated worker's bytes stop reaching the server: its
heartbeat request hangs until its own 10 s request timeout, its claim poll and log stream stall with it,
and nothing it owes the server arrives, exactly as the CI incident looked. The fact waits until the
*server's own row* says that heartbeat has been silent for more than the window this card replaced
(5 s + 1 s of headroom) and less than the new one (15 s), asserts the worker is still `Online` and still
holding its `Running` run **and its process is alive**, reopens the gate, waits for the stored heartbeat
to move forward again, and then requires all twelve runs to finish `Success` with exit 0 and no failure
reason. The stall length is a measurement from the row, never a sleep.

**Why a relay and not `SIGSTOP`.** The card's Windows diagnosis suspended the process with
`NtSuspendProcess`; the POSIX equivalent (`kill -STOP`) is **not usable from this harness**, and that is
worth recording because it looks like it should be. The test host is the worker's parent, so stopping
the worker sends it `SIGCHLD`, and .NET's `Process` bookkeeping wedges there: the next
`Process` call — even the `Process.Dispose` of the very object that sent the signal — never returns,
with a thread spinning inside `Process.OnSigChild` while the rest of the harness waits for
`ProcessWaitState`. It was reproduced twice in the harness and then in a three-child repro on
macOS 26.5 + .NET 10.0.403 (a `sleep` child, `kill -STOP`, then any further `Process` use hangs), so it
is a property of the platform, not of this fact's code. The relay stalls the *arrival* of the heartbeat
rather than the process, needs no signals and no privileges, and is identical on all three OSes.

**The failure message.** Both facts now build their message from the run rows (`status`, `exit`,
`FailureReason`, assigned worker) plus `ConcurrencyHost.FormatWorkerTailsAsync`, which emits, per ring
worker, its name, pid, the server's row for it (`Online`/`Offline` and how old its last heartbeat is) and
the tail of its own stdout/stderr. The CI log named `Lost … worker-offline` and nothing else; the next
occurrence explains itself.

## Verification

- POSIX (macOS 26.5 / arm64, 8 cores, .NET 10.0.403):
  - `--filter FullyQualifiedName~ConcurrencyOfflineWindowTests` → 1/1 pass in 62 s.
  - **Mutant, the window back at 5 s** (`OfflineWindowSeconds = 5`): the fact fails in 13 s with
    `worker 'stall-c' was already Offline after 6.1s without a heartbeat, inside the 5s window` plus
    both other workers' tails. That is the card's mechanism, measured: 6 s of silence is enough to lose
    a run under the old window and not under the new one. Log:
    `artifacts/logs/q020-mutant-window-5.log`.
  - `--filter Category=Concurrency` and the full fast suite: green (see the gate log
    `artifacts/logs/b004-q020-b009-gate.log`).
- Two cores / the Windows runner: **not run here** (AC4 above). Nothing in the fact depends on the
  platform, and the numbers it asserts come from the server's rows rather than from wall-clock sleeps.

## Resolution 2026-11-16 — measured on Windows, and the window was wrong there too

The fix raises the harness window to 15 s and proves the margin with a loopback relay it can close,
rather than by sleeping. The relay is also the answer to a POSIX trap: the test host is the worker's
parent, so `SIGSTOP`ing the child wedges .NET's process bookkeeping (reproduced twice, then in a
three-process minimum), which is why the stall is produced by stopping heartbeats *arriving* instead.

Verified on Windows 10 (20 cores, `cb2ec4d`):

- `Category=Concurrency` → **8/8**, including the new stall fact (~62 s), twice identically.
- With the window at 15 s the measured silence was **6.009 s** and no run was lost; with the window
  put back to 5 s the same measurement (**6.202 s**) turned the fact red with
  "worker 'stall-c' was already Offline after 6.2s without a heartbeat, inside the 5s window: the
  window has no margin".

That last line matters beyond this card: the silent period is a stable ~6 s once the heartbeats stop,
so the old 5 s window had no margin **regardless of core count** — two cores merely made CI more
likely to reach it. The fact is a real gate, not a green-making ritual.
