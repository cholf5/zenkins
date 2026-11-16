---
id: F-025
title: SCM polling — queue a run when the remote branch head moves, for hosts that cannot deliver webhooks
type: feature
priority: P2
size: M
status: todo
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
