---
id: Q-010
title: Tag validation asymmetry — the trigger accepts a union no single worker can cover (Q-005 hardening)
type: refactor
priority: P2
size: S
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

The tag rule the trigger enforces and the tag rule the claimer enforces are the same rule: either
the trigger refuses a run whose required tags no single worker can cover, or "union accepts, queue
starves honestly" is pinned as the documented semantics and asserted on both sides.

## Background

Q-005 recorded the asymmetry verbatim (development log, concurred by the independent reviewer):
trigger-side tag validation checks the **union** of all workers' tags, while claiming requires **one
worker** to cover all required tags — so a multi-tag run split across two workers passes the form and
then starves honestly (`Queued`), which the card adjudicated as AC-3's honest-starvation semantics.
The user's ledger names it a hardening candidate ("tag 并集校验与单 worker 全覆盖的不对称").

The risk is not incorrect execution — a run never lands on a worker that cannot cover it, and an
unmatched run never goes `Lost`/`Failed` for lack of a worker (Q-005 TC-002 proved both). The risk
is operator-facing surprise: the form accepts something the queue can only starve, and the only
explanation lives in a card's development log. Decide, align, pin.

The decision belongs on this card, in writing: tightening the trigger (a form error naming the
uncoverable tag set, checked against the same single-worker coverage the claim applies) changes
behaviour real users may have leaned on; keeping the semantics means documenting it where the
operator meets it (form help text / docs) and pinning it with a test that asserts both the form
response and the queue outcome.

## Acceptance Criteria

- [ ] A written decision on this card: tighten trigger-side validation to single-worker coverage, or
      keep union-accept + honest starvation as documented semantics — with the reasoning.
- [ ] Whichever way, trigger side and claim side demonstrably agree, and a permanent end-to-end test
      pins the chosen behaviour (form response + queue outcome for a split-across-workers tag set).
- [ ] Q-005's tag scenarios stay green (`FullyQualifiedName~Zenkins.Tests.Concurrency`); any claim
      query change runs the PostgreSQL contract gate and records it.
- [ ] The CHANGELOG mentions the decision if behaviour changed (via P-001's known-issues/notes
      entry).

## Subtasks

- [ ] Write the decision (with the trade-off) on this card.
- [ ] Implement the alignment (validation change, or documentation + pin).
- [ ] End-to-end pin test + Q-005 regression.

## Dependencies

- none technically; scheduled **after the v1.0 tag** (P-001) per the user's ledger decision

## Test Cases

### TC-001: The split tag set, end to end

Steps:
1. Register two workers whose tags cover the required set only in union (`linux` + `cuda` vs. a run
   requiring `linux,cuda`); submit the run through the real form; observe the response and the
   queue.

Expected:
- The chosen semantics, deterministically: either the form refuses naming the uncoverable set, or
  the run is accepted, stays `Queued` through polling, and executes when a covering worker
  registers — matching the written decision, asserted by the test.

### TC-002: The matched case unchanged

Steps:
1. Trigger a run whose required tags one worker covers.

Expected:
- Accepted and placed exactly as Q-005 TC-002 proved; no behaviour change.

## Development Log

### 2026-11-14

- Card opened from Q-005's recorded seam and the user's ledger decision: deferred past v1.0 with a
  CHANGELOG mention, scheduled v1.0.1. Next step: write the tighten-vs-document decision.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for the asymmetry Q-005 recorded (P2, deferred with rationale). | Open | Closed here when the acceptance criteria pass. |

## Verification

- Related files: `src/Server/Workers/*` (trigger-side tag validation), `src/Server/Runs/RunService.cs`
  (claim-side coverage), `tests/Zenkins.Tests/Concurrency/*` (harness to extend)
- How to run/verify: the new pin test; `FullyQualifiedName~Zenkins.Tests.Concurrency`; PostgreSQL
  contract gate if the claim query changed.
- Results: not run yet (card creation).
