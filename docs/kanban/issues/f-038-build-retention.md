---
id: F-038
title: Build retention — keep N builds and/or D days, delete the rest for real
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A project keeps a bounded history: the last N builds and/or builds newer than D days,
with everything else — rows, step records, logs, artifacts and run directories —
actually removed, while pinned builds and in-flight work are never touched.

## Background

History grows without bound today: every run keeps its log lines, step rows,
artifacts and its `work/` directory. The only cleanup is `ArtifactSweeper`, which
removes abandoned uploads and store directories of runs that were **already deleted**
— it is not retention policy, and nothing deletes runs. The PRD parked artifact
retention; a CI server that never forgets eventually fills a disk.

## Acceptance Criteria

- [ ] `Project.RetentionKeep` (last N builds, nullable) and `RetentionDays` (newer
      than D days, nullable); both unset means keep everything (today's behaviour).
- [ ] `BuildRun.Pinned` ("keep this build forever") exempts a run from both rules;
      pinning/unpinning is a Member action on the run page with the same guards as
      other run actions.
- [ ] Evaluation runs after each run reaches a terminal state and on a daily sweep;
      it never deletes a queued or running run, and never deletes the **newest** run of
      a project (a project with 1 retained build always keeps its last result).
- [ ] Deletion is complete and idempotent: DB rows (run, steps, logs, artifact rows,
      deliveries referencing it), artifact store directories, and the run's work
      directory; a partial failure is logged and retried, never fatal to the sweep.
- [ ] The dashboard, project overview and runs list show no ghosts after a sweep, and
      a deleted run's permalink (F-036) answers 404.
- [ ] In multibranch mode (F-023) retention applies per branch job.
- [ ] The retained counts are visible on the settings card, with "the next sweep will
      delete 12 builds" previewed before saving a tighter policy.

## Subtasks

- [ ] Entity + migration: two nullable ints on `Project`, `Pinned` on `BuildRun`.
- [ ] `RetentionSweeper` background service (daily tick, configurable interval for
      tests) with a per-project candidate query that excludes pinned and non-terminal
      runs.
- [ ] Deletion service shared with project deletion: one code path that removes a run's
      rows and bytes, so "delete project" and "retention" cannot diverge.
- [ ] Work-directory cleanup: the worker's run directory is on the worker's disk, so
      either the server's delete triggers a worker-side cleanup command, or the
      directory is removed by the worker's own age-based sweep — pick one and record it
      (the honest option is the worker-side sweep: the server cannot reach the disk).
- [ ] UI: retention fields, the pin toggle, the deletion preview.
- [ ] PRD §3.7 / §3.5: record retention, pinning and the "always keep the last build"
      rule.

## Dependencies

- F-016 (artifacts and their store layout) — accepted
- F-036 (numbers, for the preview text)

## Test Cases

### TC-001: Keep-last-N deletes older runs and their bytes

Steps:
1. `RetentionKeep = 3`; create 5 runs with artifacts and logs; sweep.

Expected:
- The newest 3 remain; the oldest 2 are gone from lists, their permalinks 404, their
  artifact bytes are removed from the store, and their step/log rows are gone.

### TC-002: Pinned and in-flight runs survive

Steps:
1. `RetentionKeep = 1`; pin an old run; leave one run queued.

Expected:
- The pinned run and the queued run remain; only unpinned terminal runs beyond the
  limit are removed.

### TC-003: The newest run is never deleted by a day rule

Steps:
1. `RetentionDays = 1`; a single run from 10 days ago.

Expected:
- The run is kept (it is the project's only/newest result); the sweep records "kept:
  last build".

### TC-004: A partial failure is retried, not fatal

Steps:
1. Make one run's work directory unreadable on the worker side; sweep.

Expected:
- The DB deletion for the other candidates proceeds; the failure is logged with the
  run id and the next sweep retries it.

## Notes and Decisions

- The worker owns its disk. A Server-side delete cannot remove `work/{run-id}` on a
  remote worker, so the sweep's server half and worker half are separate and both
  idempotent; the card must not claim otherwise.
