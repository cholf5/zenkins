---
id: Q-011
title: Pipelines tests leak per-run temp roots — Dispose swallows only IOException
type: test
priority: P3
size: S
status: todo
created: 2026-11-15
updated: 2026-11-15
blocked: none
---

## Goal

The Pipelines suites' per-run work directories are cleaned up when a test
finishes, even when the deletion hits a transient error — a green suite run
leaves zero (or a documented residual of) `zenkins-*` temp roots behind, so
hundreds of runs do not accumulate thousands of leaked directories in the
system temp.

## Background

Found during P-001's release run (2026-11-15): the final sweep removed
~9,500 disposable test temp roots from the system temp, dominated by
`zenkins-f014-def-*` (× 8425) and similar Guid-suffixed roots the Pipelines
tests create per run. The affected harnesses' `Dispose` swallows only
`IOException`, so any other transient deletion failure (an open handle that
releases a moment later, a read-only bit, a platform-specific error) abandons
the directory permanently. The same P-001 run also found one leaked Worker
process from a Nov-14 F-007-era E2E session — evidence the pattern ages
badly. The fix is test-harness hygiene only; no product behaviour changes.

## Acceptance Criteria

- [ ] The per-run temp-root deletion in the affected Pipelines harnesses no
      longer treats a non-`IOException` deletion failure as permanent: the
      deletion is retried or best-effort completed, and any unremovable
      residual is recorded (one warning line), not silently abandoned.
- [ ] After a green run of the leaking filtered suite(s), the temp root
      contains zero new `zenkins-*` roots attributable to the suite (or the
      residual is documented with a reason on the card).
- [ ] No test behaviour or assertion changes; the fast suite stays green.

## Subtasks

- [ ] Locate every Dispose that swallows only `IOException` around temp-root
      deletion in the Pipelines/definition harnesses.
- [ ] Fix the deletion (retry/best-effort + recorded residual), keeping the
      harness style.
- [ ] Loop-verify: run the filtered suite green, then count remaining
      `zenkins-*` roots in temp; record the number.

## Dependencies

- none

## Test Cases

### TC-001: A green run leaves nothing behind

Steps:
1. Snapshot the count of `zenkins-*` roots in the system temp; run the
   affected filtered suite(s) green; snapshot again.

Expected:
- The delta is zero, or the residual is explained on the card.

## Development Log

### 2026-11-15

- Card opened from P-001's release run: ~9,500 leaked test temp roots swept
  from system temp (`zenkins-f014-def-*` × 8425 dominant), mechanism pinned
  to Dispose swallowing only `IOException`; a leaked F-007-era Worker process
  was found the same day. Next step: locate the swallowing Disposes and fix
  the deletion path.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for the temp-root leak found during P-001 (P3). | Open | Closed here when the acceptance criteria pass. |

## Verification

- Related files: `tests/Zenkins.Tests/Pipelines/*` (harness Dispose paths —
  locate exactly during the card's work)
- How to run/verify: the affected filtered run; TC-001's snapshot delta.
- Results: not run yet (card creation).
