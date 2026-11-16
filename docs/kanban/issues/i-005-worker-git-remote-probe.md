---
id: I-005
title: Worker git remote probe — branches and head commits without a clone, over the pull model
type: feature
priority: P1
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

The Server can ask a Worker "what heads does this repository have right now?" and
get `refs/heads/* → commit` back, without cloning and without the Server running
git, so branch discovery (F-023) and SCM polling (F-025) have a primitive to
build on.

## Background

Both polling and multibranch discovery need the same fact: which commits exist on
a remote. The architecture forbids the three shortcuts:

- **The Server runs git.** PRD §3.2 and §8.1: the Server never touches a
  repository. It also has no credential material path that is not run-scoped.
- **The Server calls an HTTP endpoint on the Worker.** PRD §8.3: workers dial
  out; they must not accept inbound connections (that is what makes a worker
  deployable behind NAT).
- **Each feature invents its own probe.** Two features probing the same
  repository on two schedules is twice the credential use and twice the load.

So a probe is a *work item* claimed through the existing pull channel, like a run
but without a build: the Worker runs `git ls-remote --heads` with the project's
assigned credential (the F-013/F-018 plumbing) and reports the refs or a typed
failure.

## Acceptance Criteria

- [ ] A probe can be handed to a Worker through the pull channel and reports
      either `refs/heads/* → sha` plus the remote HEAD, or a typed failure
      (`git-auth-rejected`, `git-unreachable`, `git-timeout`).
- [ ] Probing never occupies a build slot: a Worker with all build slots busy
      still answers probes, and a probe appears as no run anywhere in the UI,
      the dashboard counts or the run history.
- [ ] Credential material for a probe is scoped to that probe and that
      repository — a probe cannot replay a run's credential, and a run cannot
      consume a probe's.
- [ ] Probing is deduped per (project, repository): one in-flight probe serves
      every caller asking within the interval, regardless of how many features
      asked.
- [ ] A probe failure is recorded and visible (log line plus the last outcome on
      the project), never interpreted as "no change".
- [ ] With no Worker capable of probing, the Server says so instead of waiting
      forever: the probe reports `no-worker` after a bounded wait.

## Subtasks

- [ ] Shared contract for the probe request/result in `src/Shared` (same
      provider-neutral style as `PipelineSpec` and `ArtifactContract`).
- [ ] Server: probe queue plus lease (a short-lived row or an in-memory channel —
      pick one and record why; the product is single-Server, so a lost in-memory
      probe is re-requested, not replayed).
- [ ] Server: `POST /api/workers/probes/claim` and `POST
      /api/workers/probes/{id}/report`, worker-token authenticated, tenant
      resolved from the token row exactly like the run claim.
- [ ] Server: incoherence test — a token of organization A cannot claim or report
      B's probe.
- [ ] Worker: probe worker loop with its own bounded concurrency, independent of
      the build loop; `git ls-remote` through the existing git invocation helper;
      timeout; typed failure mapping.
- [ ] Worker: credential fetch through the run-less probe path (new endpoint or a
      probe-scoped scope on the existing one — no broader access than before).
- [ ] Capability reporting: the Worker's registration says whether it can probe
      (a Worker built before this card must not be handed probes).

## Dependencies

- none (F-013/F-018 give the credential plumbing; Q-009 is the SSH verdict)

## Test Cases

### TC-001: A probe reports heads and creates no run

Steps:
1. Seed a project with a repository served by the test git host and an assigned
   credential.
2. Request a probe; let the test worker claim, run `git ls-remote`, and report.

Expected:
- The report carries the seeded branch names with their commit shas.
- The `BuildRuns` table gained no row; the dashboard's queued/running counts are
  unchanged.

### TC-002: A wrong credential is a typed failure, not an empty ref list

Steps:
1. Seed a project whose assigned credential is wrong for the repository.
2. Request a probe and let the worker report.

Expected:
- The probe result is `git-auth-rejected` with a diagnostic message.
- The Server records the failure as the project's last probe outcome; nothing is
  treated as "no new commits".

### TC-003: Probes do not cross the tenant boundary

Steps:
1. Mint a worker token for organization A.
2. Claim/report a probe fabricated for organization B.

Expected:
- Both calls answer a bare 404, the same shape the run report endpoints use.

## Notes and Decisions

- Single-Server assumption: the queue may be in memory. Recorded here because a
  future multi-Server deployment (parking lot) would need it persisted.
- No probe scheduling in this card — only the primitive. F-024/F-025 own the
  schedules; a schedule *may* use a probe-free path.
