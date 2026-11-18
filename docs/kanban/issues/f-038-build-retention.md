---
id: F-038
title: Build retention — keep N builds and/or D days, delete the rest for real
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-19
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

- [x] `Project.RetentionKeep` (last N builds, nullable) and `RetentionDays` (newer
      than D days, nullable); both unset means keep everything (today's behaviour).
- [x] `BuildRun.Pinned` ("keep this build forever") exempts a run from both rules;
      pinning/unpinning is a Member action on the run page with the same guards as
      other run actions.
- [x] Evaluation runs after each run reaches a terminal state and on a daily sweep;
      it never deletes a queued or running run, and never deletes the **newest** run of
      a project (a project with 1 retained build always keeps its last result).
- [x] Deletion is complete and idempotent: DB rows (run, steps, logs, artifact rows,
      deliveries referencing it), artifact store directories, and the run's work
      directory; a partial failure is logged and retried, never fatal to the sweep.
- [x] The dashboard, project overview and runs list show no ghosts after a sweep, and
      a deleted run's permalink (F-036) answers 404.
- [x] In multibranch mode (F-023) retention applies per branch job.
- [x] The retained counts are visible on the settings card, with "the next sweep will
      delete 12 builds" previewed before saving a tighter policy.

## Subtasks

- [x] Entity + migration: two nullable ints on `Project`, `Pinned` on `BuildRun`
      (`20261118141858_AddBuildRetention`).
- [x] `RetentionSweeper` background service (daily tick, configurable interval for
      tests) with a per-project candidate query that excludes pinned and non-terminal
      runs.
- [x] Deletion service shared with project deletion: one code path that removes a run's
      rows and bytes, so "delete project" and "retention" cannot diverge.
- [x] Work-directory cleanup: **the worker's own age-based sweep** (`ZENKINS_WORK_RETENTION_HOURS`,
      default 168 h, 0 = off), which never removes a run it is executing. The server cannot reach a remote
      worker's disk, so the two halves are separate and both idempotent - the card says so instead of pretending
      otherwise.
- [x] UI: retention fields, the pin toggle, the deletion preview.
- [x] PRD §3.7 / §3.5: record retention, pinning and the "always keep the last build"
      rule (PRD §3.5's "构建保留策略与固定", plus the §3.7 artifact lifetime paragraph).

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

## Delivered

Schema: `20261118141858_AddBuildRetention` (generated with `Database__Provider=Postgres`): `Projects.RetentionKeep`,
`Projects.RetentionDays` (both nullable) and `BuildRuns.Pinned` (`boolean NOT NULL DEFAULT false`). No new table, no
new `DbSet`; `has-pending-model-changes` answers "no changes" and `ModelSnapshotTests` is green.

**The policy's shape** (`src/Server/Runs/RetentionService.cs`): both rules are keeps, unioned - a build is deleted
only when it is **both** beyond the newest N of its branch **and** older than D days. Both unset is a no-op by
construction (the evaluation returns before it looks at a single run). The group key is the run's own
`GitBranch`, so a multibranch project keeps N per branch job and a single-branch project has one group.

**Never deleted:** pinned runs, `Queued`/`Running` runs, the newest build of each branch (so a project always keeps
its last result), and any run whose F-026 chain obligation is still `pending` - deleting that row would cascade the
outbox row away and lose a "queued exactly once" downstream trigger.

**Two clocks, one evaluation:** the run paths pulse `RunTerminalSignal` when a run becomes terminal (completed,
canceled, deadline-lost, worker-offline-lost, queued-cancel); `RetentionSweeper` drains the pulses and evaluates
just that project, and runs the full sweep every `Retention:SweepIntervalHours` (default 24). The evaluation is off
the request path, so trimming history can never slow or fail a worker's report.

**Deletion** (`RetentionService.DeleteRunAsync`, the one path): rows first (the run plus everything that cascades:
steps, logs, artifact rows, outbox obligations, API idempotency keys), then the artifact store directory. A crash
between the two leaves an orphaned directory the F-016 sweeper already removes, so both halves are idempotent. Rows
that are dedupe/audit records - webhook deliveries, schedule occurrences, the change ledger, a re-run's origin, a
downstream run's `TriggeredByRunId` - keep their row but stop naming the deleted run, so no page renders a dead link.

| Fact | What it pins | Mutation that turns it red |
|---|---|---|
| `Retention_with_both_rules_unset_keeps_every_build` | both rules unset is a no-op: 5 old terminal builds, 0 deleted, and the evaluation names 0 candidates | the unset short-circuit disabled: `Expected 0 / Actual 4` candidates |
| `A_pinned_build_is_exempt_from_both_rules_and_pinning_is_a_member_action` | the pin POST (member + antiforgery) pins the oldest build under keep=1 **and** days=0; the sweep keeps it and deletes the middle one; unpinning alone deletes nothing, the next sweep does | the `run.Pinned` exemption removed: `Expected 1 deleted / Actual 2` |
| `A_queued_or_running_build_is_never_deleted` | keep=0/days=0 deletes the two old terminal builds and leaves the queued, the running and the newest | the `IsTerminal` filter dropped: `Expected 2 / Actual 4` deleted |
| `The_newest_build_of_a_project_survives_the_day_rule` | TC-003: one run from ten days ago (the age is read back and asserted, not assumed) with days=1 is kept, `NewestPerBranch == 1`, 0 candidates | the newest-of-branch protection removed: `Expected 1 / Actual 0` |
| `A_deleted_build_takes_its_rows_its_bytes_and_its_links` | the deleted run's row, step, log, artifact row and store directory are gone; the kept build's are not | the store-directory delete removed: "the artifact directory of the deleted run survived" |
| `A_second_sweep_deletes_nothing_and_an_orphaned_directory_is_retried` | idempotent second sweep; and rows-without-bytes (the crash between the halves) is closed by F-016's sweeper | the orphan retry disabled (`rows.Count >= 0`): `Assert.True` fails, the directory stays |
| `A_swept_build_leaves_no_ghost_on_any_surface` | AC-5: the runs list and overview do not name it, `/runs/{guid}` and `/projects/{id}/runs/{number}` answer 404, the kept run answers 200 | the run row not removed: the permalink stops being a 404 |
| `Retention_is_applied_per_branch_job` | keep=1 over `main` x2 + `feature` x1 deletes only `main`'s older build | the branch grouping collapsed to one bucket: `Expected 1 / Actual 2` |
| `The_settings_card_counts_the_history_and_previews_a_tighter_policy` | AC-7: the card states the stored policy in words and the retained counts; the preview answers the next sweep's number for the values in the form and stores nothing; the promised sweep then deletes exactly that | the preview block not rendered: `project-retention-preview` not found |
| `An_old_run_tree_is_removed_and_a_young_one_is_kept` / `A_run_this_worker_is_executing_is_never_removed_even_when_its_tree_looks_old` / `Only_run_trees_are_swept` / `A_zero_window_turns_the_sweep_off` | the worker half: old trees go, young and live ones stay, `probes/`/`validations/`/stray directories are never touched, and 0 turns it off | window ignored -> young tree removed; active-set guard removed -> live tree removed; the GUID check removed -> `probes/` removed; `Enabled => true` -> `Assert.False` fails |
| `Zero_disables_the_work_directory_sweep_and_garbage_is_a_configuration_error` | `ZENKINS_WORK_RETENTION_HOURS=0` is legal, `-1`/`8761`/`seven` are configuration errors that name the variable | the bounds check removed: `Assert.NotNull` fails (no error) |
| `Retention_and_pin_endpoints_cannot_reach_another_tenants_rows` | Q-006: the policy save and both pin routes answer a bare 404 for another organization's project/run, leave the rows untouched, and work on the caller's own rows (positive control) | the pin lookup without the tenant filter: `Expected NotFound / Actual InternalServerError` (the data layer's write guard is the second line) |
| `A_build_inside_the_day_window_is_kept_by_the_day_rule` | the day rule in the keeping direction: a ten-day-old build inside a 30-day window survives, and it is deliberately **not** the branch's newest, so the newest-build exemption cannot be the reason | the day comparison replaced by `false`: the candidate list grows to 1 and the sweep deletes it |
| `A_build_older_than_the_day_window_is_deleted` | the day rule in the deleting direction: the same ten-day-old seed under days=1 is a candidate named "older than 1 day(s)", is deleted, and the newest build stays | the day comparison replaced by `true`: `Expected 1 / Actual 0` deleted |
| `A_run_that_still_owes_a_downstream_trigger_is_spared_until_the_obligation_settles` | F-026's exactly-once against trimming: a run with a real pending `OutboxMessages` row is not a candidate and is not deleted; settling the row lets the same policy take the run, and the settled row goes with it | the `owed.Contains(run.Id)` guard removed: `Expected 0 / Actual 1` candidates on the first sweep |
| `A_deleted_build_clears_the_rows_that_name_it_instead_of_taking_them` | a `WebhookDelivery` and a `ScheduleOccurrence` naming the deleted build both survive with `RunId == null` (the premise - both name the run before the sweep - is asserted first) | the delivery's clear replaced by a delete: `Assert.NotNull(keptDelivery)` fails; the occurrence's, the same at its own assertion |
| `Saving_a_policy_as_a_non_member_is_forbidden_and_leaves_the_row_alone` | the save answers `Forbidden` for a caller who is not a member of the active organization and both rules stay unset; the owner's identical call is stored (positive control) | the membership guard short-circuited (`false && ...`): `Expected Forbidden / Actual Success` |
| `Pinning_a_run_as_a_non_member_is_forbidden_and_leaves_the_row_alone` | the pin toggle answers `Forbidden` for a non-member and `Pinned` stays false; the owner's identical call pins | the same guard mutation: `Expected Forbidden / Actual Success` |

Q-006's inventory carries the three new rows (`POST /projects/{id}/retention`, `POST /runs/{id}/pin`,
`POST /runs/{id}/unpin`), all naming `Retention_and_pin_endpoints_cannot_reach_another_tenants_rows` for the
foreign-id 404; the 403 half of those rows is pinned at the service by the two non-member facts (see the review
patch below), because the HTTP surface cannot reach it. No new
tenant-owned entity, so `TenantIsolationProbes.CoveredEntityTypes` is unchanged and the reflection guard stays green.

**Decisions worth knowing:**
- "Deletes deliveries referencing it" is implemented as **clearing the reference**, not deleting the row: a
  `WebhookDelivery` is the dedupe record that makes a git host's retry a 202 instead of a second run, and a
  `ScheduleOccurrence` is the due-time ledger. Both keep their row and stop naming the deleted run.
- The worker's `work/{run-id}` tree is removed by **age on the worker**, not by a Server instruction (the card's
  own decision). `ZENKINS_WORK_RETENTION_HOURS` defaults to 168 h; the active-run set is the guard that no live
  tree is ever swept.
- No new endpoint for the sweep: both halves are services plus one hosted loop each, so nothing needs a caller.

Nothing in this card's acceptance criteria is open.

## Close-out verification (2026-11-18)

History had no bound: every run kept its log lines, step rows, artifacts and its directory, and nothing
deleted a run. Retention arrives as two nullable rules, and "really delete" is the part that decides whether
this card works at all.

- **Both rules unset means keep everything** - today's behaviour, pinned by a fact that would otherwise
  delete four candidates. `Pinned` exempts a run from both rules, and pinning is a Member action on the run
  page with the same guards as cancel (cookie, anti-forgery, tenant filter, membership).
- **Three things are never deleted**: a queued or running run, the newest build of a project (so a project
  can never be emptied), and - a protection the card did not ask for but the pipeline demanded - a run whose
  downstream obligation is still pending in the outbox. Without that last one, retention would eat F-026's
  exactly-once row and the chain would stop silently. It is in the card and in the PRD.
- **"Delete" means four surfaces plus two caches**: run, step and log rows, artifact rows and the artifact
  store directory, and the worker's own `work/{run-id}` tree - swept by the worker, because the Server
  cannot reach a remote disk. Four facts, four mutations, one per surface, and a second sweep that deletes
  nothing while an orphaned directory is retried by F-016's sweeper.
- **Evaluation runs where the states change**: five `RunTerminalSignal.Pulse` sites (completed,
  worker-cancelled, deadline-lost, worker-offline-lost, queued-cancel) plus a daily sweep, always
  asynchronous so retention can never fail a request.
- **References are cleared rather than rows deleted** where deleting would resurrect work: a
  `WebhookDelivery` is a dedupe record (deleting it lets a git host retry queue a new run) and a
  `ScheduleOccurrence` is a due-time ledger; `TriggeredByRunId`, `RebuiltFromRunId` and `ChangeTrigger.RunId`
  are nulled so no page shows a dead link.
- **A deleted build leaves no ghost**: gone from the runs list and the dashboard, and both run URLs 404
  while a retained run still answers 200.
- **multibranch retention is per branch job**, and a tighter policy can be previewed from the settings card
  using the same evaluation the sweep uses, without writing anything - the preview and the sweep agreeing is
  itself a fact.
- **Counts.** fast 1577 -> 1592 measured on the merged tree, Postgres stays 9, the slow categories 24,
  everything 1625. The single batch run at the end of the series verifies the full suite and Postgres.

## Review patch (2026-11-18) - three claims the facts did not measure

A re-check of the merged card found three of its statements green for the wrong reason or unbacked. This patch
adds the missing facts and touches **no product code**: `RetentionService` and `ZenkinsDbContext` are unchanged.

- **The date rule had no coverage, and one fact asserted something untrue.** `SeedRunAsync(daysOld: 10)` never
  made a run ten days old: `ZenkinsDbContext.StampTimestamps` overwrites `CreatedAt` on every added row, so the
  seed set the value and the insert replaced it with "now". `The_newest_build_of_a_project_survives_the_day_rule`
  therefore passed for the wrong reason (the row was brand new, inside every window) and stayed green when only
  the newest-build guard was removed. The seed now writes the two instants back with `ExecuteUpdateAsync` after
  the insert, and the date facts read `CreatedAt` back and assert the age, so the wrong reason cannot return
  unnoticed. Two new facts make the day comparison load-bearing in both directions:
  `A_build_inside_the_day_window_is_kept_by_the_day_rule` and `A_build_older_than_the_day_window_is_deleted`.
  Breaking the comparison to `false` reddens the first, to `true` the second, and removing the seed's backdate
  reddens all three on the premise assertion (`the seeded build is not ten days old: CreatedAt=...`).
- **"A run whose downstream obligation is still pending is spared" had no fact.** Deleting
  `if (owed.Contains(run.Id)) continue;` left the whole class green, because nothing ever wrote an
  `OutboxMessages` row. `A_run_that_still_owes_a_downstream_trigger_is_spared_until_the_obligation_settles`
  seeds a real pending obligation (due in an hour, so the background dispatcher keeps its hands off while the
  fact runs), asserts the run is neither a candidate nor deleted, settles the row, then asserts the next sweep
  takes the run and the settled row cascades with it.
- **"Clears the reference, does not delete the row" had no fact either**: turning either
  `ExecuteUpdate(RunId = null)` into a delete stayed green.
  `A_deleted_build_clears_the_rows_that_name_it_instead_of_taking_them` seeds a `WebhookDelivery` and a
  `ScheduleOccurrence` that name the run, asserts that they do, sweeps, and asserts both rows are still there
  with `RunId == null`.
- **Q-006's "a non-member gets 403" claimed more than the named probe measured.** The HTTP probe pins the
  foreign-id 404 only. The 403 is real but not reachable over HTTP - the active organization is chosen from the
  caller's own memberships - so it is now pinned where it lives, at the service:
  `Saving_a_policy_as_a_non_member_is_forbidden_and_leaves_the_row_alone` and
  `Pinning_a_run_as_a_non_member_is_forbidden_and_leaves_the_row_alone`, each with the owner's identical call as
  the positive control. The three inventory rows now say exactly that instead of "a non-member gets 403".

Verification for this patch: `dotnet build -c Release` 0/0, and the targeted filters
`~Retention|~TenantIsolation|~ModelSnapshot` pass (158 facts). Six facts are new, so the counts in the close-out
above move by six; they are not re-measured here, because this patch deliberately runs neither the full suite
nor Postgres.
