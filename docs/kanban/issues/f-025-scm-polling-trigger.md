---
id: F-025
title: SCM polling — queue a run when the remote branch head moves, for hosts that cannot deliver webhooks
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A repository project can poll its remote on an interval and queue a run when the
tracked branch's head moves, for Git hosts that cannot reach the Server (or that
nobody wants to configure a webhook on).

## Background

The webhook path needs inbound connectivity to the Server. A self-hosted git behind
a firewall, or a host with no webhook support, has no trigger path today at all —
Jenkins' "Poll SCM" is the fallback everyone knows. The Server cannot run git
(PRD §8.1) and workers only dial out (PRD §8.3), so polling is built on I-005's
probe rather than on a Server-side clone or a `git ls-remote` in the web process.

Note the overlap risk: a repository can have both a webhook and polling, and both
will observe the same commit. Deduplication is therefore part of this card, not a
later hardening.

## Acceptance Criteria

- [ ] A project can enable polling with an interval (minutes, minimum 1, disabled
      by default) for the tracked branch; in multibranch mode (F-023) it polls all
      heads instead.
- [ ] When the observed head differs from the branch's last known head, exactly
      one run is queued, pinned to the observed commit (`RunClaim.GitCommit`
      semantics, as the webhook path does).
- [ ] Polling and a webhook observing the same commit never queue two runs: the
      dedupe key is (project, branch, commit) and is shared by both paths.
- [ ] Several missed heads collapse to the newest: if the head moved twice while a
      run was in flight, one run for the newest head is queued, not both.
- [ ] A probe failure never reads as "no change": the project shows the failure and
      its time, the next poll backs off (interval, 2×, 4× … capped at e.g. 30
      minutes), and a success resets the backoff.
- [ ] Polling respects F-028 (disabled project), F-029 (queue cap, concurrency) and
      F-024's overlap policy — a skipped poll is recorded, not silent.
- [ ] A project with no repository, or a repository with no assigned credential
      that needs one, says so on the settings card instead of failing every poll.

## Subtasks

- [ ] Per-branch head tracking: a `BranchHead` row (project, branch, sha, last
      polled at, last run at, last probe outcome) — the polling state must not be
      derived from `Run.GitCommit`, which is null before the worker reports.
- [ ] `PollSweeper` background service: selects projects whose interval elapsed,
      requests one deduped probe per repository (I-005), compares heads, queues.
- [ ] Shared change-trigger path with F-017: one function that takes
      (project, branch, commit, triggerType, dedupeKey) so webhook, poll,
      schedule and upstream cannot drift.
- [ ] UI: a Polling card (enable, interval, last poll, last outcome, backoff state).
- [ ] PRD §3.9: record polling beside webhooks, including the dedupe rule.

## Dependencies

- I-005 (the probe primitive)
- F-017 (dedupe key pattern, pinned commit) — accepted
- F-023 (polling all heads) — optional, single-branch polling must ship without it

## Test Cases

### TC-001: A moved head queues one pinned run

Steps:
1. Seed a project with polling on and a known head `A`.
2. Make the remote's head `B`; let the sweeper tick.

Expected:
- One run is queued for the tracked branch with `GitCommit = B`.
- The stored head is now `B`; a second tick with no change queues nothing.

### TC-002: Webhook and poll do not double-queue

Steps:
1. Deliver a signed webhook for commit `C` (queues a run).
2. Let a poll observe head `C`.

Expected:
- Exactly one run for commit `C` exists.
- The poll records `duplicate` (or "unchanged") rather than queueing.

### TC-003: A failed probe is not "no change"

Steps:
1. Point the project's remote at an unreachable host; let a poll run.

Expected:
- No run is queued; the project's last poll outcome is `git-unreachable`.
- The next interval is backed off, and the UI shows both facts.

## Notes and Decisions

- Rejected: polling from the Server with a git CLI. It contradicts PRD §8.1 and
  would need credential material in the web process.
- Polling is opt-in per project: a fleet-wide default interval would silently load
  every repository in an organization.

## What landed

- **One shared change-trigger entry point.** `Runs/ChangeTriggerService.TriggerAsync` takes
  `(project, branch, commit, triggerType, requiredTags)` and owns the ledger row plus the queue call.
  `ChangeTrigger` is unique on `(ProjectId, Branch, Commit)`; the pre-check reads the winner, and the
  unique index (a savepoint + `DatabaseErrors.IsUniqueViolation`) is the actual guard - two callers that
  race both end up with the one row. The poll always goes through it.
- **The ledger's words are easy to "tidy away" - do not.** A poll stores
  `ProjectTriggerRefusals.SkippedDisabled`, `ScheduleOutcomes.SkippedQueueFull` and
  `ScheduleOutcomes.SkippedOverlap`; its duplicate marker is `WebhookOutcomes.IgnoredDuplicate`;
  `PollOutcomes` only aliases them. "Is a run in flight?" is `InFlightRuns.FindOldestAsync`, the one
  query `ScheduleService` calls too (a *queued* run counts for the schedule, not for a poll - there it
  is the queue cap's business). A second `skipped-*` list or a second in-flight count is a bug.
- **The webhook keeps its own queue call, deliberately.** `TriggerFromWebhookAsync` asks the ledger
  "did a poll already trigger this commit?" first, and records the row when no poll did; the queue call
  itself stays F-017's, because a delivery is already deduplicated per delivery id and two pushes of the
  same commit under two delivery ids are two deliveries - a contract the webhook tests pin. The card's
  rule is the *cross-path* one, and that is exactly what the ledger decides.
  **Why not one full-dedupe path for everything:** a push is a *delivery*, and F-017's contract is that
  two deliveries carrying the same commit are two deliveries. Routing every push through the
  `(project, branch, commit)` ledger would silently rewrite six accepted facts (the replay window, secret
  rotation, the rate-limit budget, "a delivery that cannot be queued leaves no record and a retry still
  builds", ...). AC-3 says "polling and a webhook", not "webhook and webhook", so the ledger decides the
  crossing and leaves the delivery contract alone. A later card that wants one run per commit *across
  deliveries* is changing that contract, and those six facts must move with it.
- **`BranchHead`** carries the polling state (`Sha`, `FirstSeenAt`, `LastSeenAt`, `LastRunAt`,
  `LastRunCommit`, `LastOutcome`, `LastOutcomeAt`); nothing reads `Run.GitCommit` to decide whether a
  branch moved.
- **First sighting records, does not trigger** - unless the shared ledger already has that commit, in
  which case the first sighting is `duplicate`.
- **Backoff**: `PollOptions.EffectiveInterval` is `min(interval x 2^streak, 30 min)`, and a successful
  probe resets the streak to zero. Skips (disabled / queue full / concurrency) do **not** grow the
  backoff: they are not failures of the remote.
- **Skips are recorded, never silent**, and **in the words that already existed**: a disabled project
  stores `ProjectTriggerRefusals.SkippedDisabled`, a full queue `ScheduleOutcomes.SkippedQueueFull`, a
  running run `ScheduleOutcomes.SkippedOverlap` - the schedule reactor's own vocabulary, aliased in
  `PollOutcomes` rather than re-spelled. The "is a run in flight?" question is
  `InFlightRuns.FindOldestAsync`, the one query `ScheduleService` now calls too (the schedule asks it
  with `includeQueued: true`, a poll with `false`: a *queued* run is the queue cap's business).
  The duplicate marker is `WebhookOutcomes.IgnoredDuplicate` - the ledger's one spelling, read by both
  paths.
- **UI**: Project settings > Polling (toggle, interval, last poll, last result, failure streak, and the
  `no-repository` / `missing-credential` explanations). PRD §3.9.1 documents the rules.

## Known blind spots / out of scope

- **Polling runs inside the Server process** against a worker-supplied probe, so a project with no
  online probe worker records `no-worker` (a failure, backed off) rather than waiting forever. That is
  the F-023 behaviour reused, not a new decision.
- **`docs/development.md` SS2's fast-suite count is stale again** (+7 here). Re-baselining needs a
  counted run of all seven suites on the Windows machine and belongs to the gate's owner.
- **The ledger row is written in the delivery's own transaction for a webhook**, so a rolled-back
  delivery leaves no ledger row either - which is what the "a delivery whose run cannot be queued
  leaves no record and a retry still builds" fact asks for.
- **No retention/pruning for `ChangeTriggers` or `BranchHeads` rows yet.** Both grow with distinct
  commits/branches seen; a pruning job is a separate card (noted, not fixed).
- **`ProjectTriggerGuard`'s refusal codes are now mapped into the poll vocabulary**
  (`queue-full` -> `skipped:queue-full`); the guard itself is unchanged.

## Facts this card leans on

- `PollingTests.A_push_for_a_commit_a_poll_already_triggered_is_recorded_as_a_duplicate_not_queued_again`
  - the poll-first direction of AC-3. Removing the ledger question from
  `ChangeTriggerService.TriggerFromWebhookAsync` reddens it (`Assert.Single` finds 2 runs).
- `PollSweeperTests.Only_the_project_whose_wait_has_passed_is_polled` - the sweeper itself: four projects
  (polling off / not due / in a x4 backoff / due), one real tick. Reddens when the `PollingEnabled` filter
  is dropped, and when the due-time comparison is dropped (the other two are then polled; "not polled" is
  proven by an outcome sentinel and an unchanged `LastPollAt`, not by "polled and found nothing").
- `PollSweeperTests.A_successful_probe_resets_the_stored_failure_streak` - the *stored* streak returns to
  0 after a good probe (reddens if the success path stops resetting it).
- `PollSweeperTests.A_multibranch_project_records_every_head_and_queues_one_run_for_the_head_that_moved` -
  three heads become three `BranchHead` rows and moving one queues exactly one run for that branch and that
  commit (reddens when the read is forced back to the default branch only).
- `PollingTests.The_poll_interval_bounds_are_one_to_1440_minutes` - 0 and 1441 are refused with
  `poll-interval-invalid`, 1 and 1440 are saved (reddens when the save stops checking the bounds).
- `PollingTests.The_polling_card_says_why_a_project_cannot_be_polled` - AC-7. Reddens three ways:
  delete the `#polling-no-repository` paragraph, delete the `#polling-no-credential` paragraph, or
  drop the `no-repository` recording in `PollService` (the recorded outcome is then null).
- `Polling.PostgresScmPollingUpgradeTests` (`Category=Postgres`) - upgrading a populated database: existing
  projects read `PollingEnabled = false`, `PollFailureStreak = 0` and null poll columns, the rest of each
  row is untouched, and `BranchHeads` / `ChangeTriggers` exist and accept rows. Reddens when the migration's
  `PollingEnabled` default is flipped to `true`.
- Tenancy: `BranchHead` and `ChangeTrigger` are registered in the committed coverage list with their own
  three probes each (seeded rows on both sides, cross-tenant write refused, Find after a tenant switch is
  null), and `POST /projects/{projectId:guid}/polling` is in `docs/audits/q-006-surface-inventory.md` and
  exercised by `Member_of_a_cannot_read_or_mutate_bs_project` (foreign id = 404, B's polling state untouched).
- **A poll asks F-023's branch verdict before it queues anything** (`BranchService.GetVerdictAsync`, the
  delivery path's own call): `has-pipeline` queues; `no-pipeline` *about this commit* records
  `WebhookOutcomes.NoPipeline`; anything else records `WebhookOutcomes.AwaitingObservation`. Unknown is
  never queued, from either source of the same observation - otherwise a documentation branch on a polled
  multibranch project collects one `pipeline-missing` run per commit. Both words are aliased in
  `PollOutcomes`, not re-spelled. **And the loop is closed, not only reported:** on
  `awaiting-observation` the poll asks the definition registry for a tree read
  (`EnqueueBranchObservationAsync`, the delivery path's own call, best effort and after the row
  committed). Recording the word alone would leave the branch unknown forever on a project whose whole
  reason for polling is that no push will ever arrive. **Timeliness is part of the rule**: a `no-pipeline`
  verdict counts only when it speaks about *this* commit; a stale one asks for a fresh observation instead.
  Fact:
  `PollSweeperTests.A_branch_without_a_pipeline_is_not_queued_and_the_poll_says_which_answer_it_gave`
  (reddens when the verdict call is removed: a run appears; when the enqueue is removed; and when a stale
  verdict is reused instead of re-observed).
- **The verdict gate leans on a coupling that is now pinned.** A poll asks for a verdict exactly when
  `BranchDiscoveryModes.IsMultibranch` says so; the discovery pass selects exactly the projects
  `BranchDiscoverySweeper.Selects` accepts. Today both read `Project.BranchDiscovery`, which is why an
  `awaiting-observation` always has somebody who resolves it. Give discovery its own switch and that stops
  being true - every tick records `awaiting-observation`, no run is ever queued for a branch that does
  carry a `pipeline.lua`, and nothing fails. `Branches.BranchDiscoveryCouplingTests` pins the agreement for
  every mode (and refuses a mode that is not in `All`).
- **Known difference from the delivery path, deliberately kept:** the poll enqueues the observation with
  `requiredTags: null`, because a poll has no webhook row to read tags from and a tree read needs none,
  while the delivery passes `webhook.RequiredTags` because it happens to have them. Do not invent a second
  tag source here. **Revisit when F-033 (`runs_on`) lands**: the observation task should then either carry
  the run's own requirement through the same `WorkerRequirement` implementation, or state explicitly that
  reading a tree has no worker requirement.

## Close-out verification (2026-11-18)

Four review rounds, nineteen mutations, one refused verdict. The card's own premise — webhook, poll,
schedule and upstream must not drift — is what failed first and it failed in a way no acceptance
criterion asks about directly.

- **The poll never asked F-023's verdict.** `PollService` had no `BranchService` dependency and
  `ChangeTriggerService` called `QueueWebhookRunAsync` directly, so on a multibranch project a
  `no-pipeline` documentation branch queued a run the moment its head moved and the worker answered
  `pipeline-missing` — while the delivery path answers `awaiting-observation`, queues nothing and reads
  the tree first. `unknown is never queued` was already accepted behaviour, so this was a regression in
  the shape of the feature, not a gap in a test. The poll now asks the verdict in the delivery path's
  own shape, records `ignored:no-pipeline` when the verdict names this exact commit, and
  `awaiting-observation` when it does not.
- **Reporting is not closing.** Recording `awaiting-observation` and leaving it there is a dead end: the
  branch would stay `unknown` forever, because polling would keep asking the same unanswered question and
  the next push is precisely what this feature exists without. The poll now also enqueues the tree read
  itself — after the branch row commits, best effort, the same call the delivery path makes.
- **Two facts that close the loop and keep it closed.** One proves the three answers and the enqueued
  read (`:228` no verdict, `:253` report-without-read, `:246` a stale verdict reused, `:235` the reverse:
  a settled `no-pipeline` must **not** ask for another read). The other pins that the poll's gate and the
  discovery pass are driven by the same field — `IsMultibranch(mode)` must equal
  `BranchDiscoverySweeper.Selects(project)`, for every mode, plus a guard that `BranchDiscoveryModes.All`
  lists every constant. Without it, splitting discovery into its own switch would turn
  `awaiting-observation` into a dead end with a green suite.
- **The dedupe is shared, but the webhook keeps its own queue call.** Routing every push through the full
  `(project, branch, commit)` key broke six accepted webhook facts, because a push is a delivery and two
  deliveries carrying the same commit are two deliveries by F-017's contract. The webhook asks the ledger
  whether a poll already triggered that commit, then queues as it always did, then records; the poll goes
  through `ChangeTriggerService` throughout. Both directions of the cross-path rule have a mutation.
- **The reviewer's refusal is the most useful event in this record.** Its first pass left eight mutations
  green, including one that removed the `PollingEnabled` filter from the sweeper: no fact ran
  `PollSweeper.SweepOnceAsync` at all, so "off by default" and "the backoff is actually applied" were
  proven only by arithmetic about `EffectiveInterval`. A four-project tick fact now proves both, with
  "not polled" established by a sentinel outcome and an unchanged `LastPollAt` rather than by "polled and
  found nothing", and the two properties now fail on different lines (`:80` and `:84`).
- **Two Windows-only regressions were caught by the repository's own gates**: `BranchHead` and
  `ChangeTrigger` were missing from the committed tenant-entity inventory and `POST
  /projects/{id}/polling` from the endpoint inventory. Both are registered rather than silenced — each
  new entity carries its own three probes (seeded rows in both tenants, a refused cross-tenant write, a
  null `Find` after a tenant switch), and the endpoint has a foreign-id 404 case that also asserts the
  other tenant's polling state is untouched.
- **`AddScmPolling` carries an upgrade fact**, for the same reason F-032's `Attempts` does: a schema
  created from scratch never exercises a column default, and this migration adds two non-null defaults to
  an existing table. Changing `defaultValue: false` to `true` reddens it at the assertion, not inside
  `MigrateAsync`.
- **One trade-off is written down where the next reader will hit it.** `Selects` runs in memory rather
  than in the query, so each tick materialises every non-disabled `Project` row including three large
  text columns. The XML doc now states the price, the scale at which it stops being acceptable (a
  thousand projects, project the five columns it needs), and that pushing it back into SQL would
  reintroduce the second copy of the rule the coupling fact exists to prevent.
- **Counts and gates.** The authoritative run is on the Windows host at the merged tree: **1430 total,
  1426 passed, 1 failed, 3 skipped, 602 s**, the single failure being main's own `ProcessRunnerTests`
  handle race and the three skips POSIX-only, so the new-failure set is empty. `Category=Postgres` went
  **7 -> 8** and passed 8/8. Both new facts were run individually and neither skips.
