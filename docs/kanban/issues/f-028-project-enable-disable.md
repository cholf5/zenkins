---
id: F-028
title: Project enable/disable — stop every trigger without deleting history
type: feature
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A Member can disable a project: no trigger queues anything for it any more, its
history and artifacts stay readable, the page says it is disabled and why, and
re-enabling restores every trigger exactly as it was.

## Background

Today a project can only be deleted. A pipeline that a webhook keeps triggering —
because the branch is broken, or because the project is mid-migration — has no
brake: the choices are deleting the project or disabling the webhook (which loses
the webhook's id and secret). Jenkins' "Disable Project" is the missing brake, and
this product already has the pattern at worker level (`Workers.IsDisabled` with a
`worker-disabled` notice).

## Acceptance Criteria

- [ ] `Project.Disabled` with a required reason, plus who and when; settable by an
      Owner or Member (matching the project's other mutations).
- [ ] Every trigger path refuses for a disabled project with the reason surfaced:
      the form says so on the project page, the API answers 409 `project-disabled`,
      the webhook records `project-disabled` on the delivery and answers accepted
      (so the git host does not retry forever), schedules and polls record
      `skipped-disabled`.
- [ ] Runs queued at the moment of disabling are canceled with reason
      `project-disabled` — a disabled job never builds something it queued while
      enabled. A running run is left alone (canceling it would be a second,
      separate decision).
- [ ] The disabled state is visible in the projects list, the dashboard, the runs
      list and the run page, and the project's tabs stay readable (history,
      artifacts, settings).
- [ ] Re-enabling clears the state and does **not** resurrect canceled runs or
      replay missed schedules.
- [ ] Deletion is still a separate, confirmed action; disabling is not a soft
      delete and stores no deleted marker.

## Subtasks

- [ ] Entity + migration: `Disabled`, `DisabledReason`, `DisabledAt`,
      `DisabledByUserId` (no FK, the `GitCredential` attribution pattern).
- [ ] One guard used by all trigger paths (`RunService.CreateRunAsync` plus the
      webhook/schedule/poll/upstream/reactor entry points) — the guard must live in
      the service, not in each endpoint.
- [ ] Endpoints `POST /projects/{id}/disable` and `/enable` with antiforgery, the
      same 404/403 shapes as the rest.
- [ ] UI: badge, reason, a Disable/Enable form on the settings tab, the notice
      strings in the repo's existing style.
- [ ] PRD §3.2: record the disabled state and the per-trigger refusal contract.

## Dependencies

- none

## Test Cases

### TC-001: A disabled project refuses every trigger

Steps:
1. Disable a project with a reason.
2. Trigger it from the form, then deliver a signed webhook for it.

Expected:
- The form shows `project-disabled` and queues nothing.
- The delivery records `project-disabled` and queues nothing; the git host sees a
  2xx so it does not retry.

### TC-002: Queued runs are canceled, running ones are not

Steps:
1. Queue two runs (no worker), start a third on a slow-step pipeline, then disable.

Expected:
- The two queued runs are `Canceled` with reason `project-disabled`.
- The running run continues to its own terminal state; its page notes the project
  was disabled mid-flight.

### TC-003: Re-enabling restores triggers and replays nothing

Steps:
1. Disable, deliver two webhooks, re-enable, deliver one more.

Expected:
- The two webhooks produced no runs; the third queues exactly one.
- The canceled runs stay canceled.

## Notes and Decisions

- The webhook answers 2xx for a disabled project on purpose: a permanent 4xx makes
  most git hosts disable the webhook automatically, which would silently break the
  "re-enable and it works again" promise.
