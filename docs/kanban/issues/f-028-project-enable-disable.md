---
id: F-028
title: Project enable/disable — stop every trigger without deleting history
type: feature
priority: P2
size: S
status: done
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

- [x] `Project.Disabled` with a required reason, plus who and when; settable by an
      Owner or Member (matching the project's other mutations).
- [ ] Every trigger path refuses for a disabled project with the reason surfaced:
      the form says so on the project page, the API answers 409 `project-disabled`,
      the webhook records `project-disabled` on the delivery and answers accepted
      (so the git host does not retry forever), schedules and polls record
      `skipped-disabled`.

      **Partial — two of the four named surfaces do not exist yet.** The form
      (page notice + `?runError=project-disabled`), the webhook (`project-disabled`
      delivery outcome, 202) and the service-level refusal are done and tested; the
      JSON API is F-027's (not landed: the run service already returns the dedicated
      `Refused` status and the `project-disabled` code an API maps to 409, but there
      is no API endpoint to answer it), and schedules/polls are F-024/F-025 (not
      landed: `ProjectTriggerRefusals.SkippedDisabled` and the shared gate are
      declared for them, nothing records it yet).

      **Update 2026-11-16: the API's 409 is now covered by F-027.**
      `POST /api/v1/projects/{id}/runs` answers `409` with `error: project-disabled`
      (and queues nothing), proven by
      `ApiV1RunApiTests.A_disabled_project_answers_409_project_disabled_and_queues_nothing`.
      AC2 still stays unticked: `skipped-disabled` for schedules/polls needs
      F-024/F-025, so two of the four named surfaces remain open.
- [x] Runs queued at the moment of disabling are canceled with reason
      `project-disabled` — a disabled job never builds something it queued while
      enabled. A running run is left alone (canceling it would be a second,
      separate decision).
- [x] The disabled state is visible in the projects list, the dashboard, the runs
      list and the run page, and the project's tabs stay readable (history,
      artifacts, settings).
- [x] Re-enabling clears the state and does **not** resurrect canceled runs or
      replay missed schedules.
- [x] Deletion is still a separate, confirmed action; disabling is not a soft
      delete and stores no deleted marker.

## Subtasks

- [x] Entity + migration: `Disabled`, `DisabledReason`, `DisabledAt`,
      `DisabledByUserId` (no FK, the `GitCredential` attribution pattern).
- [x] One guard used by all trigger paths (`RunService`'s one staging routine - the
      manual form and the webhook today; the schedule/poll/upstream/reactor entry
      points of F-024/F-025/F-026 do not exist yet and will call the same routine).
      The guard lives in the service (`ProjectTriggerGuard` + one `StageQueuedRunAsync`
      under the project row lock), never in an endpoint.
- [x] Endpoints `POST /projects/{id}/disable` and `/enable` with antiforgery, the
      same 404/403 shapes as the rest.
- [x] UI: badge, reason, a Disable/Enable form on the settings tab, the notice
      strings in the repo's existing style.
- [x] PRD §3.2: record the disabled state and the per-trigger refusal contract.

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

Result (observed, 2026-11-16): PASS.
`ProjectEnableDisableTests.A_disabled_project_refuses_the_trigger_form_and_queues_nothing`
(the redirect is `?runError=project-disabled`, the run table stays empty, the page
renders the reason and offers no form) and
`WebhookProjectGateTests.A_disabled_project_records_project_disabled_and_answers_accepted`
(202 with `project-disabled`, the delivery row carries the outcome and no run id, the
webhook's "last delivery" line agrees, and a push after re-enabling queues exactly one
run).

### TC-002: Queued runs are canceled, running ones are not

Steps:
1. Queue two runs (no worker), start a third on a slow-step pipeline, then disable.

Expected:
- The two queued runs are `Canceled` with reason `project-disabled`.
- The running run continues to its own terminal state; its page notes the project
  was disabled mid-flight.

Result (observed, 2026-11-16): PASS.
`ProjectEnableDisableTests.Disabling_cancels_the_queue_and_leaves_a_running_run_alone`:
two seeded runs, one claimed and accepted (Running) by a real worker, then the
settings form disables the project — the still-Queued run becomes `Canceled` with
`FailureReason = project-disabled`, no lease and no worker, while the Running run
keeps `Running`, has no `CancelRequestedAt`, and completes to `Success`; its run page
renders `Project disabled`.

### TC-003: Re-enabling restores triggers and replays nothing

Steps:
1. Disable, deliver two webhooks, re-enable, deliver one more.

Expected:
- The two webhooks produced no runs; the third queues exactly one.
- The canceled runs stay canceled.

Result (observed, 2026-11-16): PASS.
`ProjectEnableDisableTests.Re_enabling_restores_the_trigger_and_replays_nothing`
(two refused triggers leave the run table unchanged, the enable clears
`Disabled`/reason/when/who, the run canceled by the earlier disable stays `Canceled`,
and the next trigger queues exactly one run) plus the webhook half in
`WebhookProjectGateTests` listed above. No schedule exists to replay (F-024).

## Notes and Decisions

- The webhook answers 2xx for a disabled project on purpose: a permanent 4xx makes
  most git hosts disable the webhook automatically, which would silently break the
  "re-enable and it works again" promise.
- **Where the guard runs, and why under a lock.** Both trigger sources already read
  the project row for their own reasons; the refusal itself is one routine
  (`RunService.StageQueuedRunAsync`) that takes the project row's write lock
  (`ZenkinsDbContext.LockProjectForTriggerAsync`, a deliberate no-op `UPDATE`, the
  B-002 shape) and *then* re-reads `Disabled`. Without the lock a trigger that read
  the flag before a disable committed could still insert a run that the disable's
  cancel statement had already passed — an orphan queued run on a disabled project.
  The disable's own `UPDATE Projects …` takes the same row lock, so the two
  serialize. F-029's queue-cap count rides on the same lock (one lock, two rules).
- **Precedence, as implemented: the disabled gate answers before the trigger's own
  field validation; the queue cap (F-029) answers last, under the project lock.** The
  two halves of the gate sit at different points on purpose. `RunService.TriggerAsync`
  asks the disabled question as soon as it has the project row, so a form the project
  will refuse never spends the caller's attention on a branch name (the page does not
  render the form at all, and a forged POST still reads the reason first). The ordering
  is pinned by
  `ProjectEnableDisableTests.A_disabled_project_answers_its_reason_before_the_triggers_field_errors`
  (three invalid fields in one request still answer `project-disabled`). The queue cap
  cannot move up there without giving up the lock that makes it atomic (counting outside
  the transaction is exactly the check-then-act F-029 closes), so a malformed trigger on
  a *full* queue reads its field error, and any trigger on a *disabled* project reads the
  reason. On the webhook path the delivery's own ref/branch classification still runs
  first, so a push the webhook ignores anyway keeps recording `ignored:branch` /
  `ignored:delete` (the more specific truth: nothing was going to queue); any push that
  *would* have queued records `project-disabled`.
- **Leased-but-unstarted runs are canceled too.** A run claimed a moment before the
  disable is still `Queued`, so the disable cancels it; the claim holder's `accepted`
  report is then the documented 409 (F-009's race-window semantic, unchanged).
- **Attribution resolves on read, not on write.** `DisabledByUserId` stores the
  Identity id with no FK; the settings page resolves it to an e-mail and says
  "not recorded" when it no longer resolves — an account deletion never rewrites a
  project's history.

## Development Log

### 2026-11-16

- **Entity + migration.** `Project.Disabled`, `DisabledReason` (required on disable,
  ≤ 500 chars, control characters refused), `DisabledAt`, `DisabledByUserId` (no FK).
  Migration `20261116044928_AddProjectEnableAndQueueControls` (PostgreSQL-generated,
  the shape the other migrations in this repository use; the model gate
  `dotnet ef migrations has-pending-model-changes` with
  `Database__Provider=Postgres` prints "No changes have been made to the model since
  the last migration.", and the migration applies cleanly to a throwaway PostgreSQL
  16.15 cluster as well as to SQLite).
- **One gate, in the service.** New `src/Server/Projects/ProjectTriggerGuard.cs` holds
  the refusal vocabulary (`project-disabled`, `queue-full`, `skipped-disabled`) and the
  two predicates/messages; `RunService.StageQueuedRunAsync` is the only place a trigger
  inserts a run, and both existing sources (manual form, webhook) go through it. The
  lock-then-read shape is recorded in "Notes and Decisions" above and in the
  `LockProjectForTriggerAsync` doc comment.
- **New operation status.** `RunOperationStatus.Refused` (+
  `RunTriggerResult.Refused(code, message)`) says "the request was well-formed, the
  project's state refused it". The form endpoint maps it to the existing PRG
  (`?runError=<code>`), which is what the project page renders; an API maps the same
  status to 409 (F-027).
- **Disable cancels the queue in one statement.** `ZenkinsDbContext.TryCancelQueuedProjectRunsAsync`
  cancels every `Queued` run of the project in a single guarded `ExecuteUpdate` inside
  the same transaction as the flag; `Running` rows are untouched by construction.
- **Endpoints + UI.** `POST /projects/{id}/disable|enable` (form-bound, so antiforgery
  applies; the same 404/403 shapes as the other mutations) and the settings tab's
  Triggers card; the badge/notice/dashboard line are in the projects list, the project
  overview (the start-run form is replaced by the reason, not merely refused), the runs
  list, the run page and the dashboard's recent-run rows plus a "N projects are
  disabled" line. PRD §3.2 records the state and the per-trigger contract.
- **Tenant-isolation inventory.** The three new POSTs are added to
  `docs/audits/q-006-surface-inventory.md` and the committed probe test
  (`Member_of_a_cannot_read_or_mutate_bs_project`) now proves a foreign project id is
  the same 404 and leaves the row untouched; the inventory's machine check is part of
  the fast suite.
- **Not covered, and why.** The API's 409 (F-027 not landed) and
  `skipped-disabled` on schedules/polls (F-024/F-025 not landed) are the two AC
  surfaces this card cannot exercise; the codes and the shared gate are in place for
  both. Nothing else in the AC was left unverified.
- **Verification.** `dotnet build Zenkins.slnx -c Release` clean; fast suite 1124
  passed / 0 failed / 0 skipped; Milestone 12, Recovery 4, PublishSmoke 2, PublishAot 2,
  GitSshd 3, Postgres 1 (real PostgreSQL 16.15; skips with a recorded reason without
  `ZENKINS_TEST_POSTGRES`) — all green (logs under `artifacts/logs/`). New facts: 7 in
  `tests/Zenkins.Tests/Runs/ProjectEnableDisableTests.cs` (the last one pins the
  disabled-before-field-validation order), the webhook half in
  `tests/Zenkins.Tests/Webhooks/WebhookProjectGateTests.cs`.
- **Review fixes (2026-11-16, second round).** The precedence contract was reversed in
  the first draft (the gate ran *after* field validation while the notes claimed the
  opposite): the disabled check now runs as soon as the project row is read, and the
  ordering is pinned by a test; the precedence note above and
  `ProjectTriggerGuard`'s doc say which half of the gate sits where and why. Also fixed:
  the refusal message's missing separator (a reason ending without punctuation used to
  render as `…mid-migration Nothing is queued`), the dead duplicate branch in
  `RunEndpoints` (both cases produced the same PRG — now one case with an honest
  comment), stray whitespace left by an earlier edit, the dashboard's disabled-project
  count (one grouped aggregate over the project table instead of materializing it), and the lock
  comments now separate the insert path (reads `Disabled` under the lock) from the claim
  path (a candidate snapshot; the disable's cancel plus the lease CAS are the guarantee).

### 2026-11-16 — independent review: PASS

Two review rounds, both in an isolated copy of the uncommitted batch (clone + patch), never in the
developer's tree. Verdict after the fixes: PASS, five criteria satisfied, AC2 honestly uncovered.

- The reviewer proved the mechanisms rather than reading the ticks: the disable transaction writes
  the flag and cancels the project's `Queued` runs in one commit (`ProjectService.cs:575-596`,
  `ZenkinsDbContext.cs:255-273`, `Running` structurally unreachable); re-enabling has no
  un-cancel path; the webhook answers **202** with `project-disabled` recorded rather than the 4xx
  that would make a git host disable its webhook.
- The precedence contract was wrong in the first draft — the notes claimed the disabled gate ran
  before field validation while the code ran it after. It now runs as soon as the project row is
  read (`RunService.cs:432-446`), the queue cap deliberately stays last and under the lock, and a
  test pins the order by posting three invalid fields at a disabled project and asserting
  `runError=project-disabled`.
- AC2 stays unticked on purpose: the JSON API's 409 needs F-027, and `skipped-disabled` needs
  F-024/F-025. The service already returns the dedicated `Refused` status and the codes those
  callers map.
- 2026-11-16, full gate: build 0 warnings, fast 1124, seven suites 1148, 0 failed / 0 skipped.
