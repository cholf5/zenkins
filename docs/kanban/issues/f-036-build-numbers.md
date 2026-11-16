---
id: F-036
title: Build numbers and permalinks — #N per project, /runs/last, /lastSuccessful
type: feature
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

Every run gets a per-project build number displayed as `#N` everywhere (UI, logs,
env, API, notifications) with stable permalinks, so a person or a script can name a
build without pasting a guid.

## Background

Runs are identified by guid and shown as the first 8 hex characters. That is
unreadable in chat ("the 4f2a1c9e build failed?"), impossible to type, and not
orderable by a human. Jenkins' `#N` plus `lastSuccessfulBuild` permalinks are the
convention people already use in dashboards and badges.

## Acceptance Criteria

- [ ] `BuildRun.Number` is a per-project positive integer assigned atomically at
      trigger time; concurrent triggers of the same project never collide (unique
      index on (ProjectId, Number)).
- [ ] Everything human-facing shows `#N`: run lists, the run page, the project's
      last-run column, the dashboard, the log download filename, notifications
      (F-039) and API responses (F-027).
- [ ] Steps' environment gets `ZENKINS_BUILD_NUMBER` (and `ZENKINS_BUILD_ID` keeps
      the guid), consistent with the existing `ZENKINS_*` metadata.
- [ ] Permalinks resolve: `/projects/{id}/runs/{number:int}` renders the run;
      `/projects/{id}/runs/last` and `/projects/{id}/lastSuccessful` redirect to the
      right run, and answer 404 honestly when there is none.
- [ ] The guid route keeps working (bookmarks, the log hub's ticket flow), so no
      existing link breaks.
- [ ] Existing runs are backfilled deterministically by creation order, and the
      backfill is one migration that cannot produce duplicates.
- [ ] In multibranch mode (F-023) numbering is per branch job, not shared across the
      container.

## Subtasks

- [ ] Entity + migration: `Number`, unique index, backfill statement ordered by
      `CreatedAt` then `Id`.
- [ ] Assignment: inside the trigger transaction, take the project row lock (same
      pattern as the claim) and increment; retry once on a unique-violation race.
- [ ] Display sweep: every page and message that currently prints the short guid.
- [ ] Permalink routes plus their 404 behaviour and Q-006 inventory rows.
- [ ] `RunView`/API: expose `Number` (and keep `Id`).
- [ ] PRD §3.5 and the run-list UI doc: record the number contract.

## Dependencies

- none (F-023 and F-039 consume the number; both reference it)

## Test Cases

### TC-001: Concurrent triggers get distinct, increasing numbers

Steps:
1. Fire N triggers for one project in parallel (test host, no worker).

Expected:
- N distinct numbers, exactly 1..N as a set; no duplicate, no gap-causing retry loop
  left behind.

### TC-002: Permalinks resolve and refuse honestly

Steps:
1. Project with two runs, the second successful: GET `/runs/1`, `/runs/last`,
   `/lastSuccessful`; then repeat on a project with no runs.

Expected:
- The three routes render/redirect to `#1`, the newest, and the newest successful
  run respectively; the empty project answers 404 on all three.

### TC-003: The number is in the environment and the log filename

Steps:
1. Trigger and complete a run; inspect the step's `ZENKINS_BUILD_NUMBER` and the log
   download's filename.

Expected:
- The env value equals the displayed `#N`; the filename contains it.

## Notes and Decisions

- A per-project counter row was rejected in favour of the row lock: the project row
  is already locked on hot paths, and one counter table would need its own tenant
  and cleanup story.
