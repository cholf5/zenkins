---
id: F-026
title: Upstream and downstream jobs — run a job after another one succeeds, with loop protection
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A project can declare upstream triggers ("run me after X succeeds") and downstream
targets ("run Y after I succeed"), so a build → test → deploy chain runs in order
without an external orchestrator, and a loop can never spin forever.

## Background

There is no cross-project trigger today. The only way to chain jobs is a step that
calls the Server's own HTTP API — which does not exist either (F-027) — and even
then the chain is invisible in the UI.

Jenkins does this with "Build after other projects are built" plus downstream
post-build actions, and its failure modes are well known: a chain that loops, a
chain that silently stops when the upstream is disabled, and a trigger lost when
the master dies between "build finished" and "queue downstream". This card has to
answer all three.

## Acceptance Criteria

- [ ] A project can declare N upstreams (project + "only on success" flag) and M
      downstreams; both are visible on the overview and in the run's "triggered
      by" / "triggered" sections.
- [ ] When an upstream run reaches a terminal state, a downstream run is queued if
      the rule allows it; the downstream run records `TriggerType = Upstream` and
      the triggering run id, and its page links to it.
- [ ] Branch policy per link: `sameBranch` (default, meaningful with F-023) or
      `defaultBranch` (the downstream builds its default branch); a branch the
      downstream cannot build is recorded as `skipped-branch`, not a failure.
- [ ] Loop protection at two levels: a cycle is refused at save time (graph
      check), and a runtime hop counter (e.g. 20) stops a chain that only loops
      through branch policies, with a recorded reason on the run that stopped.
- [ ] The trigger is delivered exactly once even if the Server dies right after the
      upstream completes: an outbox row is written in the same transaction as the
      terminal status and dispatched after commit, retried until delivered.
- [ ] A disabled project (F-028) is skipped with a recorded reason; a missing
      credential or an invalid downstream is not retried forever — it is recorded
      as a failed trigger, visible on the upstream run page.
- [ ] Downstream parameter values are the definitions' defaults, and the run's
      environment carries the upstream facts (`UPSTREAM_PROJECT`, `UPSTREAM_RUN_ID`,
      `UPSTREAM_STATUS`, `UPSTREAM_BRANCH`, `UPSTREAM_COMMIT`).
- [ ] Deleting a project removes its links in both directions (no dangling
      triggers), and the deletion is confirmed with the affected jobs listed.

## Subtasks

- [ ] `ProjectLink` entity (upstream, downstream, onlyOnSuccess, branchPolicy),
      unique per pair, tenant-owned.
- [ ] `OutboxMessage` entity plus a dispatcher background service — the shared
      mechanism F-039 also needs (introduce it here, reuse it there).
- [ ] Graph validation at save: DFS over the organization's links, refusing cycles
      with the path that would close it.
- [ ] Terminal-state hook in the run completion path: write outbox rows for every
      matching downstream, inside the same transaction.
- [ ] Dispatch: resolve the target, apply the branch policy, re-validate the
      project (enabled, kind, credential), queue through `RunService.CreateRunAsync`.
- [ ] UI: an Upstream/Downstream card on project settings; the run page's chain
      section; the "triggered by #N" and "triggered #M" links.
- [ ] PRD §3.13 (new): the chain contract, the loop rules and the delivery
      guarantee.

## Dependencies

- F-036 (numbers) for readable chain links (a guid reference works in the interim)

## Test Cases

### TC-001: A successful upstream queues its downstream once

Steps:
1. Link project A → B with `onlyOnSuccess`.
2. Run A to success; let the dispatcher tick twice.

Expected:
- Exactly one B run exists, `TriggerType = Upstream`, referencing A's run.
- A second tick adds nothing (the outbox row is consumed).

### TC-002: A failed upstream with onlyOnSuccess queues nothing, and says why

Steps:
1. Same link; run A to failure.

Expected:
- No B run; A's page records "downstream B not triggered: upstream failed".

### TC-003: A cycle is refused at save, and a runtime loop stops

Steps:
1. Link A → B, then try to link B → A.
2. Separately, seed a chain of 25 projects linked A→B→…→Y and run A.

Expected:
- Step 1 is refused with the closing path in the error.
- Step 2 stops at the hop cap with a recorded reason; no project runs twice in the
  same chain.

## Notes and Decisions

- Rejected: triggering downstreams by shelling out to the API from a step. It
  cannot be transactional, it hides the chain from the UI, and it needs F-027 to
  exist before the feature does.
- The outbox is deliberately a table and not an in-memory queue: the guarantee is
  "a completed upstream does not lose its downstream".
