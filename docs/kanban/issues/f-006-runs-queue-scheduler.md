---
id: F-006
title: Build runs, queue and scheduler (atomic claim/lease)
type: feature
priority: P1
size: L
status: done
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

An Owner or Member can trigger a run for a Project (a native-process command given as argv, no
shell), the run sits in a Queued queue, and the Server hands queued runs to eligible idle Workers
through an atomic claim so one run is never executed twice; Workers report accepted/finished with an
exit code and the run's state, timing and worker are persisted and visible.

## Background

PRD §3.5 (run states and metadata), §8 constraints 1 (Server never executes builds) and 6 (no shell
concatenation). Phase 2 has no `pipeline.lua` yet (v0.3): a v0.2 run executes a manually specified
argv command inside the worker's per-run work directory (F-007). `BuildRun.Pipeline*` fields stay
nullable for Phase 3; Git fields stay out until Phase 4.

Decisions:

- Run states: `Queued → Running → Success | Failed | Canceled | Lost` (PRD §3.5). Transitions are
  validated in one service; illegal transitions are `409`.
- Trigger form (Project detail, Owner or Member): command as **one argument per line** (trimmed,
  no shell quoting interpretation), optional required tags (comma-separated). Empty argv or >100
  args / >32 KiB per arg is a field error.
- Claim/lease: `POST /api/workers/claim` (worker token) atomically leases the next eligible Queued
  run (tag ⊆ worker tags, worker Online, worker's Running count < its max concurrency) for the
  worker's Organization and sets `LeaseExpiresAt`. The worker must report `accepted` within the
  lease window or the run returns to `Queued`. After `accepted` there is no lease — cancellation
  (F-009) and offline detection (F-010) own the rest.
- Reporting: `POST /api/workers/runs/{id}/accepted` and `…/completed {exitCode}` (worker token, own
  run only). `completed` with exit code 0 → `Success`, non-zero → `Failed`.
- A background scheduler assigns nothing by itself if workers pull; its job is lease expiry
  (requeue runs whose lease expired without an `accepted`) — the pull model is the PRD's
  "Worker 主动拉取" and avoids push fan-out.
- Minimal UI: per-project runs list (`/projects/{id}/runs`) and run detail (`/runs/{id}`) showing
  status, worker, command, trigger user, start/end/duration, exit code.

## Acceptance Criteria

- [x] `BuildRun` entity (tenant-owned) + PostgreSQL-contract migration; states and transitions
      enforced in one service, illegal transitions rejected.
- [x] Trigger form on Project detail creates a Queued run with validated argv and optional tags;
      anonymous/unauthenticated → sign-in redirect; cross-tenant project id → 404.
- [x] Concurrent claims never return the same run: parallel `claim` calls (SQLite and PostgreSQL
      semantics) each yield distinct runs or 204; a run is claimed exactly once. The atomicity does
      not depend on application-level locks alone in PostgreSQL (`FOR UPDATE SKIP LOCKED` or
      equivalent), and SQLite relies on a transactional compare-and-set.
- [x] Lease expiry requeues a claimed-but-never-accepted run (never one already Running).
- [x] Scheduler eligibility: Online worker, free concurrency slot, required tags satisfied; an
      Offline worker or tag mismatch is never handed a run.
- [x] `accepted` sets Running + worker + start time; `completed` sets terminal state + end time +
      duration + exit code; foreign run ids and foreign worker tokens are 401/404 with no data.
- [x] Runs list and detail pages show the recorded data and respect tenant scoping.

## Subtasks

- [x] `BuildRun` entity + migration + transition-rule service.
- [x] Trigger form endpoint + validation (argv rules) + pages (runs list, run detail).
- [x] Claim endpoint with atomic claim/lease (tests for double-claim on both providers' semantics).
- [x] Reporting endpoints + lease-expiry background service.
- [x] Tests (`Category=Runs`): happy path, double-claim concurrency, lease expiry, eligibility
      (tags/concurrency/offline), transition rules, cross-tenant denials, argv validation.

## Dependencies

- F-004 (worker identity)
- F-005 (a real consumer for the end-to-end test)

## Test Cases

### TC-001: Trigger → claim → complete happy path

Steps:
1. Member triggers `dotnet --version` run on a project with required tags matching a registered
   worker; worker claims, reports accepted then completed with exit 0.

Expected:
- Queued → Running → Success; start/end/duration and exit code persisted; run detail shows them.

### TC-002: Double-claim concurrency

Steps:
1. Queue N≥2 runs. Fire concurrent claim requests (single worker or several).

Expected:
- Every run returned at most once across all claims; no run handed to two workers (pinned by a
  concurrency test).

### TC-003: Eligibility

Steps:
1. Queue a run requiring tag `windows`; only a tagless worker exists.
2. Mark that worker Offline and queue another run.

Expected:
- Neither run is ever claimed; both stay Queued.

### TC-004: Lease expiry

Steps:
1. Claim a run and never report accepted; advance past the lease window (configurable short window
   in tests).

Expected:
- Run returns to Queued and is claimable again; a run already accepted/Running is never requeued by
  the lease sweeper.

### TC-005: Transition and tenant guards

Steps:
1. `completed` before `accepted` (409). Complete with exit 3 (Failed).
2. Claim with Organization B's worker token against A's queue.

Expected:
- 409 for the illegal transition; Failed recorded with exit 3; B's token gets 401/404 and never
  sees A's runs.

## Development Log

### 2026-11-13

**Data model.** `BuildRun` (F-006) in `src/Server/Data/Entities/BuildRun.cs`, tenant-owned, with
`BuildRunStatus` as a string (six PRD §3.5 states) behind the same kind of check constraint as
`WorkerStatus`. Migration `20261112195128_AddBuildRuns` generated under the **Npgsql** provider
(`uuid` / `timestamp with time zone` / `character varying` — the contract). `CommandJson` is the
argv JSON array stored as `text` **without** a max length on purpose (100 × 32 KiB arguments must
fit). `ProjectId` cascades with the project (F-003's deferred cascade decision lands here: build
history without its project is meaningless); `WorkerId` is `ON DELETE SET NULL` so attribution is
best-effort and can never block an organization cascade. `TriggerUserId` deliberately has **no**
foreign key — history survives account deletion. Indexes: `(OrganizationId, Status)` for the
claim query, `ProjectId` for the runs list, plus the FK index on `WorkerId`.

**State machine.** `BuildRunStateMachine` is the one authority for transitions:
`Queued → {Running, Canceled, Lost}`, `Running → {Success, Failed, Canceled, Lost}`, terminal
states have no outgoing edges. The F-009 (cancel) and F-010 (lost) edges are already legal — those
cards add callers, never new rules; and the closed terminal states make "no code path requeues or
retries a finished run" structural. `RunService` answers illegal transitions with
`RunOperationStatus.Conflict` (409 + a stable error code).

**Atomic claim — the card's "or equivalent".** The claim is a **single conditional UPDATE per
candidate** (compare-and-set), implemented as
`ZenkinsDbContext.TryLeaseBuildRunAsync` — the one guarded `ExecuteUpdate` on a tenant-owned
entity outside a system scope, added exactly where the context's class doc requires it ("add a
guarded method here if one is ever needed"): the SET is fixed in the context (WorkerId,
LeaseExpiresAt, UpdatedAt — never caller-supplied) and the WHERE is the tenant query filter
(which applies to `ExecuteUpdate`) plus `Status == 'Queued' && LeaseExpiresAt IS NULL`.
Atomicity argument per provider: PostgreSQL re-evaluates the predicate after granting the row
lock (READ COMMITTED), which is what the `SELECT … FOR UPDATE SKIP LOCKED` dance collapses to —
without native SQL and without the system-scope bypass the raw-SQL variant would need; SQLite
serializes database writes, so the losing claim re-evaluates the WHERE and updates 0 rows. The
loser moves to the next candidate (FIFO, in-memory order) or answers 204. A claim is **not** a
transition: a leased run stays `Queued` — which is precisely what makes the sweeper safe.

**SQLite cannot compare DateTimeOffset in SQL — at all.** Discovered by probe while implementing
the sweep: the EF SQLite provider refuses `<=` (and ORDER BY) on `DateTimeOffset` columns even in
plain SELECTs; only equality translates. Consequences taken deliberately:
- the candidate queue selects `LeaseExpiresAt == null` (equality + IS NULL translate) and orders
  **in memory** (the F-003 house style for small lists anyway);
- an expired lease is **not** claimable at claim time — the lease sweeper is the single authority
  on expiry, so the claim never needs a time predicate;
- the sweep reads leased queued runs cross-tenant, filters expiry **in memory**, and requeues
  each with an exact-value compare-and-set (`RequeueExpiredBuildRunLeaseAsync`, system scope):
  `WHERE Id = … AND Status = 'Queued' AND LeaseExpiresAt = <the expired value read>`. It can
  therefore never touch an accepted/Running run (no lease anymore) and never clobber a lease
  stamped after the read.

**Scheduling eligibility.** Worker `Online` (F-004 status), a free concurrency slot, tags ⊆
worker tags (case-insensitive), own organization only (the token selects the tenant). The
concurrency count treats every run still assigned to the worker as occupying a slot — `Running`
**and** queued-but-leased (the lease columns are cleared by the sweeper or by acceptance) — so
claiming cannot outrun `MaxConcurrency` through leases. The count and the CAS are not one
transaction; a same-worker claim storm can transiently oversubscribe (documented, scheduling
hint, not an invariant).

**Worker API.** `POST /api/workers/claim`, `…/runs/{id}/accepted`, `…/runs/{id}/completed
{exitCode}` reuse F-004's pattern verbatim: `WorkerService.AuthenticateWorkerAsync` (extracted
from `HeartbeatAsync`'s first phase, which now calls it too) hashes the bearer, resolves it via
the system scope, sets the tenant; then the tenant-side re-read rechecks revocation; only then is
a payload validated (under a well-formed payload, a tokenless caller gets a bare 401, never a
400 — the one 400 a tokenless caller can see is a `[FromBody]` body that fails JSON parsing,
rejected by the framework before the endpoint runs). Foreign/unknown run ids
are 404 (tenant filter — no existence oracle); not-my-lease / expired-lease / illegal-transition
are 409 with `run-not-assigned-to-worker` / `lease-expired` / `invalid-transition`. `accepted`
is the single `Queued → Running` edge (start time set, lease cleared); `completed` maps exit 0 →
`Success`, anything else → `Failed` (end time + exit code; duration is derived on display).
Claim responses carry `runId`, `projectId`, the argv and `leaseExpiresAt`; a claim with nothing
eligible is 204 and never says why.

**Trigger form.** `POST /projects/{id}/runs/start` on the project detail page (form-bound →
antiforgery, authenticated, any member — Owner or Member — in the service). One argument per
line, trimmed, blank lines dropped (a trailing newline must not become an empty argument), no
shell quoting interpretation anywhere; > 100 args or an argument > 32 KiB is a field error
(`argv-empty` / `argv-too-many-arguments` / `argv-argument-too-long`). Optional required tags are
validated for **existence** among the organization's workers (`tag-unknown`), so a typo cannot
queue a run no worker will ever take. Success redirects to `/runs/{id}` (PRG); validation
failures redirect back with `?runError=<code>`. **Deviation from F-003's pattern, deliberate**:
the submitted command is not echoed into the redirect URL — an argument may be 32 KiB and cannot
ride a URL — so the field error names the problem and the input is re-entered.

**Pages.** `/projects/{id}/runs` (per-project list, newest first) and `/runs/{id}` (status,
worker, command rendered one argument per line, trigger user's email, start/end/duration, exit
code, failure reason). Trigger-user email and worker name are resolved in two batched lookups per
page render. Both use the project-detail 404 pattern (`IStatusCodePagesFeature` disabled for the
request; pinned by the HTML-navigation cross-tenant test).

**Lease sweeper.** `LeaseSweeper` (BackgroundService) sweeps on `Runs:SweepIntervalSeconds`
(default 15) and logs failures without dying; `Runs:LeaseSeconds` (default 120) is the lease
window. Tests never wait on real time: they drive `LeaseSweeper.SweepOnceAsync` directly and move
a run's `LeaseExpiresAt` into the past through the system scope.

**Isolation harness.** `BuildRun` joined Q-001's registry: `TwoOrgsFixture` seeds one queued run
per organization, and the read/write/find probes cover it (`Build_runs_are_invisible_across_tenants`,
`Cross_tenant_build_run_write_is_rejected_and_persists_nothing`,
`Find_after_a_tenant_switch_returns_null_for_build_runs`). The reflection guard
`Every_tenant_owned_entity_is_covered_by_the_harness` forced this — exactly as designed.

**Red proof (claim atomicity).** With the compare-and-set conditions stripped from
`TryLeaseBuildRunAsync`'s WHERE (`WHERE Id = …` only — an unconditional UPDATE), both concurrency
tests go red:
- `Two_concurrent_claims_for_a_single_run_return_it_exactly_once`:
  `Assert.Equal() Failure: Expected: 1, Actual: 2` (two winners for one run);
- `Concurrent_claims_never_return_the_same_run`: `Expected: 12, Actual: 4` distinct handed-out
  ids (runs handed out several times).
The conditions were restored immediately afterwards and both tests are green again in the final
runs.

**Test-infra fix on F-005's files (load-exposed latent race, not a product change).** After F-006
added ~35 hosts to the suite, `WorkerProcessE2ETests` started failing intermittently **only**
under full-suite load (empty worker-log snapshots; both E2E tests pass deterministically in
isolation). Root cause: the timed `Process.WaitForExit(int)` can return before the asynchronous
output readers have drained the pipes — the documented gap that the parameterless
`WaitForExit()` closes. Baseline check: three full-suite runs at the pre-F-006 HEAD were green,
so the extra load statistically exposed the race; the fix (a `WaitForWorkerExit` helper that
calls the parameterless overload before every snapshot) is in `WorkerProcessE2ETests.cs` only.
Five consecutive full-suite runs are green with it.

**Known gaps.**
- No live PostgreSQL instance exists in this environment (documented since I-002); the claim's
  PostgreSQL atomicity is argued from READ COMMITTED row-lock semantics and pinned on SQLite end
  to end, the same asymmetry every card so far accepts for the migration contract.
- The sweeper's first tick waits one interval; a lease that expires 1 ms after startup waits up
  to `SweepIntervalSeconds` before requeueing. Honest (the claim would not take it anyway).
- A same-worker claim storm can transiently exceed `MaxConcurrency` (count-then-CAS is not one
  transaction). Cross-worker double-claim is impossible; revisit only if workers are allowed to
  be adversarial against themselves.
- PRD §3.5's "View all Build Runs" (org-wide, filters) is not built — this card ships the
  per-project runs list the decisions name; an org-wide overview can reuse `RunView` later.
- A deleted account's runs keep their `TriggerUserId`; the pages then show the trigger type
  instead of an email (no FK by design).
- `FailureReason` stays null for a plain non-zero exit (the exit code is the reason); F-009/F-010
  own the populated values.

### 2026-11-13 — review fixes (minors)

1. **Over-long or control-bearing required tags are a field error, not a silent drop.**
   `RunValidation.ValidateRequiredTags` let `ParseTags` silently discard any entry over 64
   characters or carrying a control character — a user submitting a single 65-character tag got
   `requiredTags = ""`, i.e. a run any worker could claim, contradicting the form's own error copy
   ("at most 64 characters per tag"). Such entries now fail validation with `tag-invalid`
   (field-level, the same `?runError=` PRG path); trim, empty-entry drops and deduplication stay
   silent, and the joined list's >400 rule is unchanged. New tests: form case
   `A_required_tag_over_64_characters_is_a_field_error_and_creates_no_run` (field-level error
   against the 64-character copy, no run created) and three unit pins in `RunValidationTests`
   (over-long entry rejected, control-bearing entry rejected, silent normalization with the
   64-character and >400 boundaries).
2. **Pinned: a queued-but-leased run occupies the worker's concurrency slot.** New endpoint test
   `A_queued_but_leased_run_occupies_the_workers_concurrency_slot`: a `maxConcurrency = 1` worker
   claims and never reports `accepted`; the next claim answers 204 and the second queued run keeps
   `WorkerId = null` — the assigned count that treats `Running` **and** queued-but-leased runs as
   slot-occupying (the count-then-CAS caveat above) cannot be bypassed into a second lease.
3. **Log wording corrected (no code change).** "A tokenless caller gets a bare 401, never a 400"
   was too strong: a `[FromBody]` body that fails JSON parsing is rejected 400 by the framework
   before the endpoint runs. The Worker-API paragraph now states the accurate boundary —
   authentication precedes every service-side validation, so the 401-vs-400 ordering holds for
   well-formed payloads — and the gap is recorded as B2 in the Bugs table.

Five tests added overall (3 unit + 2 endpoint); suite now 255.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| B1 | major | The first sweep implementation compared `LeaseExpiresAt <= now` in SQL and every claim/sweep query failed to translate on SQLite — the EF SQLite provider cannot compare `DateTimeOffset` values in SQL at all (equality only), which the house in-memory-ordering note hinted at but no card had pinned | Fixed | Claim selects `LeaseExpiresAt == null` only and the sweeper is the single expiry authority with an in-memory expiry check + exact-value compare-and-set per run; probed and documented above |
| B2 | minor | The Worker-API log entry overstated the authentication guarantee ("a tokenless caller gets a bare 401, never a 400"): a `[FromBody]` body whose JSON fails to parse is rejected 400 by the framework before the endpoint — and its token check — ever runs | Fixed (documentation only, no code change) | Log wording corrected to the accurate boundary: authentication precedes every service-side validation, so the 401-vs-400 ordering holds for well-formed payloads; see the review-fixes log section |

## Verification

- Related files:
  - `src/Server/Data/Entities/BuildRun.cs`,
    `src/Server/Data/Migrations/20261112195128_AddBuildRuns.cs` (+ `.Designer.cs`),
    `src/Server/Data/Migrations/ZenkinsDbContextModelSnapshot.cs`, `src/Server/Data/ZenkinsDbContext.cs`
    (DbSet, model configuration, the two guarded CAS methods)
  - `src/Server/Runs/{BuildRunStateMachine,RunValidation,RunOptions,RunService,RunEndpoints,LeaseSweeper,RunsServiceCollectionExtensions}.cs`
  - `src/Server/Workers/{WorkerService,WorkerEndpoints}.cs` (AuthenticateWorkerAsync extraction;
    BearerToken private → internal, reused by the run endpoints)
  - `src/Server/Program.cs` (AddZenkinsRuns + MapRunEndpoints),
    `src/Server/Components/_Imports.razor`
  - `src/Server/Components/Pages/Runs/{ProjectRuns,RunDetail}.razor`,
    `src/Server/Components/Pages/Projects/ProjectDetail.razor` (Start-run form + runs link +
    runError field errors)
  - `tests/Zenkins.Tests/Runs/{RunValidationTests,RunEndpointTests,RunClaimConcurrencyTests,RunTestHost}.cs`
  - `tests/Zenkins.Tests/Tenancy/{TwoOrgsFixture,TenantIsolationProbes,TenantIsolationProbeTests}.cs`
    (BuildRun joined the Q-001 harness)
  - `tests/Zenkins.Tests/Workers/{WorkerTestHost,WorkerProcessE2ETests}.cs` (config override
    through the host chain; the WaitForExit output-flush fix described in the log)
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test -c Release` (Runs cases carry `Category=Runs`)
  3. `dotnet test -c Release --filter "Category=Runs"`
  4. PostgreSQL contract gate:
     `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
  5. Fresh-database migration gate (SQLite):
     `Database__Provider=Sqlite ConnectionStrings__Default="Data Source=/tmp/zenkins-f006-migrate.db" dotnet ef database update --project src/Server`, inspect `BuildRuns`, delete the file.
- Results (2026-11-13):
  - Build: **PASS** — `dotnet build -c Release`, 0 warnings / 0 errors (`TreatWarningsAsErrors`
    active).
  - Tests: **PASS** — **250/250** across the suite (212 pre-card + 38: 10 argv/tag validation
    cases, 23 endpoint tests, 2 concurrency tests, 3 isolation probes), five consecutive full-suite
    runs all green.
  - `Category=Runs`: **PASS** — 35/35.
  - `Category=TenantIsolation`: **PASS** — 46/46 (43 pre-card + 3 BuildRun probes).
  - Concurrency stability: **PASS** — `RunClaimConcurrencyTests` run 5× consecutively, all green.
  - Red proof: **PASS** — CAS conditions stripped from `TryLeaseBuildRunAsync` → both concurrency
    tests red (Expected: 1 / Actual: 2; Expected: 12 / Actual: 4 distinct) → restored → green.
  - PostgreSQL contract gate: **PASS** — "No changes have been made to the model since the last
    migration."
  - Fresh-database migration gate: **PASS** — all five migrations apply on an empty SQLite file;
    `BuildRuns` carries the Postgres store types (`uuid`, `timestamp with time zone`,
    `character varying`), `CK_BuildRuns_Status`, the two cascades and the `SET NULL` worker FK;
    temp file deleted.
