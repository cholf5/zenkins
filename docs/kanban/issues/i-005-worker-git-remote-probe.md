---
id: I-005
title: Worker git remote probe — branches and head commits without a clone, over the pull model
type: feature
priority: P1
size: M
status: done
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

- [x] A probe can be handed to a Worker through the pull channel and reports
      either `refs/heads/* → sha` plus the remote HEAD, or a typed failure
      (`git-auth-rejected`, `git-unreachable`, `git-timeout`).
      *Verified:* `A_probe_reports_the_remote_heads_and_creates_no_run` (the real
      in-process `WorkerRunner` against a local bare repository) and
      `GitRefsProbeTests` (heads + remote HEAD from one `git ls-remote --symref`,
      plus a real 401, a refused connection and a killed budget each producing its
      own typed outcome). A fourth value, `git-failed`, is the closed set's honest
      remainder (no git binary, a URL that is not a repository) and is never
      conflated with the three.
- [x] Probing never occupies a build slot: a Worker with all build slots busy
      still answers probes, and a probe appears as no run anywhere in the UI,
      the dashboard counts or the run history.
      *Verified:* `A_probe_completes_while_every_build_slot_is_busy` (one worker,
      one slot held by a claimed+accepted run, probe leased and answered anyway)
      and `A_probe_reports_the_remote_heads_and_creates_no_run` (no `BuildRun` row,
      dashboard counts and recent-runs list unchanged).
- [x] Credential material for a probe is scoped to that probe and that
      repository — a probe cannot replay a run's credential, and a run cannot
      consume a probe's.
      *Verified:* `Probe_and_run_credentials_do_not_replay_each_other` — the same
      worker holds both; the probe route serves only while the probe's lease is
      live, a probe id on the run route is a bare 404, a run id on the probe route
      is a bare 404, and the run's own material still resolves afterwards.
- [x] Probing is deduped per (project, repository): one in-flight probe serves
      every caller asking within the interval, regardless of how many features
      asked.
      *Verified:* `Three_callers_inside_the_window_share_one_probe_and_one_worker_conversation`
      (three concurrent callers, one probe, `RequestCount == 3`, `Attempts == 1` =
      one lease = one git conversation; `GitRefsProbeTests` pins "one `ls-remote`
      per probe" through the runner's own trace).
- [x] A probe failure is recorded and visible (log line plus the last outcome on
      the project), never interpreted as "no change".
      *Verified:* `A_reported_failure_is_recorded_on_the_project_and_never_reads_as_no_change`
      (the project row keeps the outcome word and the diagnostic, the project page
      renders both, and the outcome is explicitly not `refs`). The registry writes
      one log line per settle, and the service writes "settled on the project" with
      `Outcome`/`Heads`/`Succeeded` - so a log reader can tell success from failure
      without parsing text.
- [x] With no Worker capable of probing, the Server says so instead of waiting
      forever: the probe reports `no-worker` after a bounded wait.
      *Verified:* `With_no_probe_capable_worker_the_probe_is_settled_as_no_worker_after_a_bounded_wait`
      (a pre-I-005 worker is never handed a probe; the probe settles `no-worker`
      with a message naming the wait, is recorded on the project, and a later
      caller gets a fresh probe rather than the cached refusal).

## Subtasks

- [x] Shared contract for the probe request/result in `src/Shared` (same
      provider-neutral style as `PipelineSpec` and `ArtifactContract`).
      *`src/Shared/Probes/GitProbeContract.cs`: the outcome vocabulary, the
      `GitProbeRef`/`GitProbeRefs` payload, the caps and the one report validator
      both sides call, plus the capability field name.*
- [x] Server: probe queue plus lease (a short-lived row or an in-memory channel —
      pick one and record why; the product is single-Server, so a lost in-memory
      probe is re-requested, not replayed).
      *In memory: `src/Server/Probes/GitProbeRegistry.cs`. Reason recorded on the
      type and below; only the outcome survives a restart (on the project).*
- [x] Server: `POST /api/workers/probes/claim` and `POST
      /api/workers/probes/{id}/report`, worker-token authenticated, tenant
      resolved from the token row exactly like the run claim.
      *`src/Server/Probes/ProbeEndpoints.cs`; 204 when nothing is claimable, bare
      401 for a dead token, bare 404 for a foreign/unknown probe id, 409 for a lost
      lease.*
- [x] Server: incoherence test — a token of organization A cannot claim or report
      B's probe.
      *`The_probe_api_never_crosses_the_tenant_boundary`.*
- [x] Worker: probe worker loop with its own bounded concurrency, independent of
      the build loop; `git ls-remote` through the existing git invocation helper;
      timeout; typed failure mapping.
      *`src/Worker/WorkerProbeLoop.cs` (own loop count, `ZENKINS_PROBE_CONCURRENCY`)
      over `src/Worker/Git/GitRefsProbe.cs` (the shared `GitCommandRunner`, its own
      budget, the one `GitFailureKind` → outcome mapping).*
- [x] Worker: credential fetch through the run-less probe path (new endpoint or a
      probe-scoped scope on the existing one — no broader access than before).
      *New route `GET /api/workers/probes/{id}/git-credential`, authorized by the
      probe lease. The decryption itself stays one internal method shared with the
      run path, so no third, broader path exists.*
- [x] Capability reporting: the Worker's registration says whether it can probe
      (a Worker built before this card must not be handed probes).
      *`canProbe` on registration and heartbeat; `Workers.CanProbe` defaults false
      and "absent" reads as false, so a pre-I-005 binary re-registering over a
      capable row clears the flag instead of inheriting it. Both promises are
      pinned: `Re_registering_without_the_capability_clears_it_and_the_page_says_so`
      and `A_heartbeat_without_the_capability_field_keeps_the_stored_value`.*

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

*Covered by `A_probe_reports_the_remote_heads_and_creates_no_run`, with a local bare
repository and the real worker runtime (the credential half is covered by
`Probe_and_run_credentials_do_not_replay_each_other`, which assigns a real
credential through the settings form).*

### TC-002: A wrong credential is a typed failure, not an empty ref list

Steps:
1. Seed a project whose assigned credential is wrong for the repository.
2. Request a probe and let the worker report.

Expected:
- The probe result is `git-auth-rejected` with a diagnostic message.
- The Server records the failure as the project's last probe outcome; nothing is
  treated as "no new commits".

*Covered in two halves: `GitRefsProbeTests.A_wrong_token_is_git_auth_rejected_and_never_an_empty_ref_list`
(a real HTTP 401 against the fixture's dumb-HTTP server, with the wrong token in
neither the outcome nor the message) and
`A_reported_failure_is_recorded_on_the_project_and_never_reads_as_no_change` (the
typed value travels the wire and is recorded on the project and the page).*

### TC-003: Probes do not cross the tenant boundary

Steps:
1. Mint a worker token for organization A.
2. Claim/report a probe fabricated for organization B.

Expected:
- Both calls answer a bare 404, the same shape the run report endpoints use.

*Covered by `The_probe_api_never_crosses_the_tenant_boundary`, with one documented
difference: the **claim** answers 204, not 404, because a claim carries no id - its
"nothing for you" answer is the run claim's own shape, and answering 404 would
invent a distinction the claim cannot make. The **report**, which does carry an id,
answers the bare 404 of the run report endpoints.*

## Notes and Decisions

- **In-memory queue, and why.** `GitProbeRegistry` holds the queue, the leases and the
  dedupe window; only the settled outcome is persisted (on the project). The product is
  single-Server, and a probe is a short-lived question whose answer nobody replays: if the
  process dies, the caller that was waiting is gone too, and F-023/F-025 ask again - a lost
  probe is re-requested, never replayed. A table would buy durability the feature has no use
  for while putting a second write path inside every claim. A future multi-Server
  deployment (parking lot) would have to persist this table; that change starts at the
  registry, which is one type with all the decisions in it.
- **Dedupe key = (project, repository).** A caller joins the newest probe of that pair while
  it is in flight, and a completed one for `Probes:DedupeSeconds` after it settled (its
  answer is still fresh). A `no-worker` probe is deliberately **not** joined: the next
  caller wants a fresh attempt, so a worker that registers a minute later is not punished by
  a cached "nobody was there".
- **The bounded wait has two bounds.** `Probes:WaitSeconds` bounds the wait for a claimant
  (that is the card's `no-worker` promise), and one extra `Probes:LeaseSeconds` bounds a
  claimant that never reports. A request therefore always ends with a typed outcome - never
  with a null.
- **Leases, not slots.** A probe takes no build slot and is not counted in
  `BuildRuns`; its lease exists only so a silent worker's probe can be re-offered. A worker
  with every slot busy answers probes on its own loop count (`ZENKINS_PROBE_CONCURRENCY`).
- **A failure is a value, never an empty list.** `refs` is the only outcome that may carry
  refs; the shared validator refuses a failure that carries any. An empty repository is
  `refs` with zero heads (a legitimate answer), which is exactly the distinction branch
  discovery needs.
- **Capability is opt-in and sticky only while announced.** `canProbe` on registration is
  what the worker *is* (absent = false, so a pre-probe binary re-registering clears it); the
  heartbeat refreshes it only when present, so an old worker's heartbeat cannot silently
  revoke what a new registration granted.
- **One credential path.** `GitCredentialService.FetchMaterialForProjectAsync` is the single
  decrypt step; the run fetch proves a run lease, the probe fetch proves a probe lease, and
  both are internal to those two pods. The probe route and the run route take different ids,
  which is what makes "neither can replay the other" structural rather than a check.
- **The clone's diagnosis is now pinned.** Extracting the auth/unreachable table for the probe made
  `GitWorkspace`'s classification the shared one, and the extraction briefly changed two behaviours
  (branch-not-found lost its priority to an auth-sounding stderr; the "no stderr output" tail was
  dropped, leaving messages ending in `: )`). Both are restored and pinned by
  `GitFailureClassificationTests` - the order (branch, then auth/unreachable, then the generic fallback)
  is documented on the method because it is diagnosis, not implementation detail.
- **Callers must not block a request thread.** `RequestAsync` waits for the answer (bounded, but the
  worst case is `Probes:WaitSeconds` + one `Probes:LeaseSeconds` - 180s with the defaults). F-023/F-025
  must call it from their own background loop, never inline in an HTTP request or a Blazor event: the
  caller wants the *answer*, not the wait, and a page must not hang on a remote that is not answering.
- **Grading honesty on what is not covered:** the SSH transport of a probe is not exercised
  (no real `sshd` in this card's facts - Q-009 owns that harness); the real remote is a
  local bare repository and the fixture's dumb-HTTP server, not a hosted Git service; and
  the "one git request for three callers" evidence is the registry's `Attempts == 1` plus
  the runner's single-invocation trace, not a process-boundary recording shim (the shim's
  Windows form needs environment the in-process worker does not receive).
- No probe scheduling in this card — only the primitive. F-024/F-025 own the schedules; a
  schedule *may* use a probe-free path.

## Review

2026-11-16 — independent review: **PASS** after one rework round, six of six criteria, on a clean
rebuild of the batch in an isolated copy.

The architecture red lines are held structurally rather than by discipline: the Server has no
`Process`/`ProcessStartInfo` and no `HttpClient` at all, so it can neither run git nor call a Worker
over HTTP; a probe is claimed by the Worker through the existing pull channel; and there is exactly
one probe implementation (`RequestAsync` + one registry + one `ls-remote`) for F-023 and F-025 to
share. `FetchMaterialForProjectAsync` has exactly two callers, each of which proves its own lease
first, and `Unprotect` has one caller in the whole tree — so the probe and run credential scopes
cannot replay each other, and three cross-route 404s prove it.

Also measured rather than assumed: a probe round-trip leaves `Projects.UpdatedAt` untouched (a guarded
write sets only the three probe columns), the closed outcome vocabulary really rejects a wrong word at
the database, `no-worker` is never joined into a dedupe window (the next call gets a new id), and a
probe completes while every build slot is busy without appearing in runs, dashboard counts or history.

Rework round findings, all fixed and mutation-verified: the extracted clone-side classifier had
reversed two F-013 diagnoses (branch-not-found losing to an auth-sounding stderr, and `: )` where the
empty-stderr sentence belonged) — it is now a pure function with three facts that go red when either
old behaviour returns; the shared decrypt entry now throws unless exactly one of run/probe scope is
present; and the `CanProbe` backward-compatibility promises finally have facts (an old-shaped
re-registration clears the flag, a heartbeat without the field keeps it).

One process note worth keeping: a pre-existing tenant-scan flake was first "fixed" by stripping
antiforgery inputs, which provably deleted zero bytes. That change was reverted and the real
mechanism — a short organization name landing inside machine-generated base64 — is Q-019.

Callers must not block a request thread: the defaults wait 60 s for a claimant and 120 s for a
silent lease (180 s worst case), so F-023 and F-025 must call `RequestAsync` from their own
background loops.
