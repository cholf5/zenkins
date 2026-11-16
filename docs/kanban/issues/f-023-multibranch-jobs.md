---
id: F-023
title: Multibranch jobs — one branch job per branch, its own history and its own last status
type: feature
priority: P1
size: L
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A repository project can track its branches: every branch that contains the
pipeline file becomes a branch job with its own runs and its own last status, and
a push to one branch only ever touches that branch's history.

## Background

Today a project is one job: a default branch, an optional per-trigger branch
override, and a webhook branch filter that decides whether to queue. "Build every
branch" therefore means N projects, N webhooks, N copies of the same settings, and
a new project every time somebody branches.

Jenkins models this as a Multibranch Pipeline: a container that discovers
branches, builds each with that branch's pipeline file, and marks a branch with no
pipeline as "not built" rather than failed. The pieces already exist here:

- the webhook delivery already names the pushed branch (`WebhookDelivery`), so a
  push is sufficient discovery for a branch that already has a webhook;
- F-017 already pins the commit a run must build (`RunClaim.GitCommit`), so a
  discovered branch can be built at exactly the detected commit;
- runs already snapshot branch and path, so per-branch history is a grouping
  problem, not a schema rewrite.

Branch discovery without any push (a repository cloned elsewhere, a branch created
while the Server was down) needs the probe from I-005.

## Acceptance Criteria

- [ ] A project can be switched to branch discovery `branches`; `single` (today's
      behaviour) stays the default and its behaviour does not change.
- [ ] A push to a new branch whose checkout contains the pipeline file queues a
      run for that branch, pinned to the pushed commit, and the branch appears in
      the project's branch list.
- [ ] A branch without the pipeline file is listed as **no pipeline** and is never
      queued — it is not a failure and it does not produce a run.
- [ ] The branch list shows, per branch, the last run's status, number (F-036),
      commit and time; the project's runs page can be filtered by branch.
- [ ] A manual trigger on a multibranch project can pick the branch (default: the
      project's default branch), with the same validation as today's override.
- [ ] With I-005 available, branches are discovered on an interval too, so a
      branch created without a push is picked up; without I-005 the feature works
      webhook-only and says so on the page.
- [ ] A branch deleted upstream keeps its history, marked orphaned; nothing is
      deleted implicitly.
- [ ] Retention (F-038) applies per branch job, not per container project.

## Subtasks

- [ ] `Project.BranchDiscovery` (`single` | `branches`) — a project setting, not a
      kind (a freestyle project has no branches).
- [ ] Branch identity on the run: the existing `GitBranch` becomes the key (it is
      already the snapshot), plus a "no pipeline" record per branch that has no
      run, so the list can show a branch that was seen and not built.
- [ ] Webhook path: with `branches`, the branch filter becomes the discovery rule
      (default: every branch); a delivery for a branch without the pipeline file
      records `no-pipeline` on the delivery instead of queueing.
- [ ] Discovery path (I-005): a probe per repository per interval; a head that has
      no branch job and whose tree has the pipeline file queues a run.
- [ ] Project page: a Branches tab (list, last status, filter link into runs);
      the overview badge shows `Multibranch`.
- [ ] Runs page: branch filter (today it lists everything for the project).
- [ ] PRD §3.2/§3.3: record the discovery mode and the per-branch job model.

## Dependencies

- I-005 (probe) for push-free discovery; the webhook half must not block on it
- F-017 (webhook delivery, branch filter, pinned commit) — accepted
- F-036 (numbers) for the branch list display

## Test Cases

### TC-001: A push to a new branch creates its branch job

Steps:
1. Seed a multibranch project and a webhook on it.
2. Deliver a signed push for `feature/x`, whose checkout contains
   `pipeline.lua`.

Expected:
- Exactly one run is queued, branch `feature/x`, pinned to the pushed commit.
- The branch list shows `feature/x` with that run's status.

### TC-002: A branch without a pipeline is listed, not built

Steps:
1. Deliver a signed push for `docs-only`, whose tree has no `pipeline.lua`.

Expected:
- No run is created; the delivery records `no-pipeline`.
- The branch list shows `docs-only` as **no pipeline**.

### TC-003: Two branches never share history

Steps:
1. Build `main` (success) and `feature/x` (failure).
2. Open the project's runs page filtered by `main`.

Expected:
- Only `main`'s runs are listed; the branch list still shows both statuses.

## Notes and Decisions

- Rejected for v1.2: an "organization folder" that scans a git host's whole
  organization (Jenkins' other discovery type). It needs host APIs, tokens and a
  token-per-host UI; branch discovery inside a project delivers most of the value.
- The container stays the project row on purpose: settings, credentials, secrets
  and parameters remain defined once, not copied per branch.
