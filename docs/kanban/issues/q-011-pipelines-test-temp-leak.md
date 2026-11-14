---
id: Q-011
title: Pipelines tests leak per-run temp roots — Dispose swallows only IOException
type: test
priority: P3
size: S
status: done
created: 2026-11-15
updated: 2026-11-15
blocked: none
---

## Goal

The Pipelines suites' per-run work directories are cleaned up when a test
finishes, even when the deletion hits a transient error — a green suite run
leaves zero (or a documented residual of) `zenkins-*` temp roots behind, so
hundreds of runs do not accumulate thousands of leaked directories in the
system temp.

## Background

Found during P-001's release run (2026-11-15): the final sweep removed
~9,500 disposable test temp roots from the system temp, dominated by
`zenkins-f014-def-*` (× 8425) and similar Guid-suffixed roots the Pipelines
tests create per run. The affected harnesses' `Dispose` swallows only
`IOException`, so any other transient deletion failure (an open handle that
releases a moment later, a read-only bit, a platform-specific error) abandons
the directory permanently. The same P-001 run also found one leaked Worker
process from a Nov-14 F-007-era E2E session — evidence the pattern ages
badly. The fix is test-harness hygiene only; no product behaviour changes.

## Acceptance Criteria

- [x] The per-run temp-root deletion in the affected Pipelines harnesses no
      longer treats a non-`IOException` deletion failure as permanent: the
      deletion is retried or best-effort completed, and any unremovable
      residual is recorded (one warning line), not silently abandoned.
- [x] After a green run of the leaking filtered suite(s), the temp root
      contains zero new `zenkins-*` roots attributable to the suite (or the
      residual is documented with a reason on the card).
- [x] No test behaviour or assertion changes; the fast suite stays green.

## Subtasks

- [x] Locate every Dispose that swallows only `IOException` around temp-root
      deletion in the Pipelines/definition harnesses.
- [x] Fix the deletion (retry/best-effort + recorded residual), keeping the
      harness style.
- [x] Loop-verify: run the filtered suite green, then count remaining
      `zenkins-*` roots in temp; record the number.

## Dependencies

- none

## Test Cases

### TC-001: A green run leaves nothing behind

Steps:
1. Snapshot the count of `zenkins-*` roots in the system temp; run the
   affected filtered suite(s) green; snapshot again.

Expected:
- The delta is zero, or the residual is explained on the card.

Result (2026-11-15, macOS arm64, `$TMPDIR` = `/var/folders/yq/…/T/`):

- First pass (deletion-path fix only) exposed two leak classes the recorded
  mechanism did not cover: 41 `zenkins-f014-def-*` roots from harness
  instances created inline and never disposed, and 4 `zenkins-f014-*-migrat*`
  `.db-wal`/`.db-shm` sidecar files the old `TryDelete` never targeted
  (before 360 → after 405, +45, suite 96/96 green). Both classes fixed (see
  Development Log), the 45 strays plus the 360 pre-fix residuals swept
  (mirroring P-001's sweep) to get a clean baseline.
- Second pass, `dotnet test -c Release --filter "Category=Pipeline"`:
  96/96 green, before 0 → after 0. **Delta: 0** — no `zenkins-*` root
  attributable to the Pipelines suites remains.
- Fast suite (Release, the §2 quintuple exclusion filter): 1047/1047 green,
  before 0 → after **+5**, none attributable to the Pipelines suites — all
  five are residuals of the out-of-scope harnesses listed below
  (`zenkins-d006-migration-*.db-wal`/`-shm` from
  `Secrets/SecretRotationMigrationTests.cs`,
  `zenkins-f018-migration-*.db-wal`/`-shm` from
  `Git/GitCredentialAssignmentMigrationTests.cs`,
  `zenkins-f016-work-*` from `Artifacts/ArtifactsTestHost.cs`). The five
  files were swept after recording; the leak rate of the remaining harnesses
  is one to two transient failures per fast-suite run.
- No Worker process leaked in any run (`pgrep -fl Zenkins.Worker` = 0
  before and after each).

## Development Log

### 2026-11-15

- Card opened from P-001's release run: ~9,500 leaked test temp roots swept
  from system temp (`zenkins-f014-def-*` × 8425 dominant), mechanism pinned
  to Dispose swallowing only `IOException`; a leaked F-007-era Worker process
  was found the same day. Next step: locate the swallowing Disposes and fix
  the deletion path.
- Sweep of the whole `tests/` tree for swallow-only-`IOException` (or
  silent-abandon) temp-root deletion. In scope, fixed:
  - `Pipelines/PipelineSpecTests.cs:339` — `LuaRuntimeTestDefinition.Dispose`
    (`zenkins-f014-def-*`, created per test case: the dominant backlog source).
  - `Pipelines/PipelineDefinitionTests.cs:368` — `DefinitionRun.Dispose`
    (same prefix).
  - `Pipelines/PipelineRunnerTests.cs:488` — the per-run harness `Dispose`
    (`zenkins-f014-run-*`).
  - `Pipelines/PipelineRunLoopAbandonmentTests.cs:228` — the test class
    `Dispose` (`zenkins-q003-loop-*`).
  - `Pipelines/PipelineMigrationTests.cs:97,163` — the `finally` deletes of
    the migration databases (`zenkins-f014-migration-*` /
    `zenkins-f014-down-*`; the local `TryDelete` was removed).
  - `Git/GitTestEnvironment.cs:42` — `GitTestDirectory.Dispose`, fixed in
    place because it is the deletion path `PipelineRepository.Dispose`
    (a Pipelines harness, `Pipelines/PipelineFixtures.cs:96`) delegates to;
    without it the `zenkins-f013-pipeline-*` roots of the Pipelines runner
    suites would still leak.
- Out of scope, same pattern, recorded not fixed (owners are the cards that
  introduced them): `Artifacts/ArtifactsTestHost.cs:382`,
  `Artifacts/ArtifactExecutorTests.cs:344`,
  `Artifacts/ArtifactUploadSafetyTests.cs:270`, `Auth/AuthTestHost.cs:252,268`,
  `Secrets/SecretProtectorTests.cs:139`,
  `Secrets/SecretRotationMigrationTests.cs:122` (its `d006-migration` wal/shm
  leak is observable in the fast suite),
  `Secrets/SecretReferenceCaptureTests.cs:213`, `Secrets/SecretsTestHost.cs:221`,
  `Secrets/SecretKeyRingTests.cs:225`, `Secrets/SecretDataProtectionTests.cs:115`,
  `Secrets/SecretUiReferencesTests.cs:483`, `Tenancy/TwoOrgsFixture.cs:630,646`,
  `Steps/StepLogWriterTests.cs:216`, `Steps/StepsRun.cs:529`,
  `Workers/WorkerTokenStoreTests.cs:30`,
  `Workers/WorkerRegistrationConcurrencyTests.cs:299`,
  `RunLifecycle/InProcessWorker.cs:135`, `Errors/ClientVisibleFailureTests.cs:228`,
  `Git/GitCredentialAssignmentMigrationTests.cs:152` (its `f018-migration`
  wal/shm leak is observable in the fast suite),
  `Git/GitCredentialEndpointsTests.cs:347`,
  `Git/AuthenticatedHttpsGitServer.cs:438`,
  `WorkerRuntime/WorkerApiDefinitiveReportingTests.cs:306`,
  `WorkerRuntime/WorkerRunLoopTests.cs:252`,
  `WorkerRuntime/WorkDirectoryTests.cs:118`,
  `WorkerRuntime/ProcessRunnerTests.cs:362`,
  `Milestone/PublishedSmokeTests.cs:410`,
  `Milestone/PublishedAotSmokeTests.cs:470`. Six more places already retry
  (10 × 200 ms) but still abandon silently after the last attempt, without the
  residual warning: `Milestone/MilestoneHost.cs:600`, `Git/SshdGitServer.cs:554`,
  `Milestone/PublishedSmokeTests.cs:296`, `Milestone/PublishedAotSmokeTests.cs:296`,
  `Workers/WorkerProcessE2ETests.cs:735`,
  `WorkerRuntime/WorkerRunProcessE2ETests.cs:581`. A follow-up card could give
  them the same "retry + one warning line" shape and the `DeleteDatabase`
  sidecar handling.
- TC-001's first pass exposed two leak classes beyond the recorded one:
  (a) **never-disposed fixtures** — 13 inline `new LuaRuntimeTestDefinition(…)`
    call sites and 6 `new DefinitionRun(…)` call sites created the harness and
    dropped it without `using`, leaking one `zenkins-f014-def-*` root per test
    case deterministically (41 roots in one green run). This, not the
    `IOException` swallow, is the dominant source of the × 8425 backlog. Fixed
    mechanically with `using var` (19 sites in `PipelineDefinitionTests.cs` and
    `PipelineSpecTests.cs`); no assertion or behaviour change.
  (b) **SQLite sidecars** — the migration tests' delete targeted only the
    `.db`; SQLite's WAL journal leaves `-wal`/`-shm` next to it while open, so
    two extra files leaked per migration test. `TempRootCleanup.DeleteDatabase`
    now deletes the trio (the same enumeration `Auth/AuthTestHost.cs` already
    used).
- Fix shape: a shared `TempRootCleanup` helper in the Pipelines plumbing file
  (`Pipelines/PipelineFixtures.cs:166`). Deletion is retried up to 10 attempts
  200 ms apart (`IOException` or `UnauthorizedAccessException` — the same pair
  the existing repo retry loops use; the usual cause, a dying child's handle,
  releases inside that window, so the suite pays nothing on the green path and
  at most ~2 s on a failing teardown). A root that survives every attempt gets
  exactly one `Console.Error` warning line (visible in verbose/detailed runner
  output and trx results) — never silently abandoned, and never thrown out of
  `Dispose`, because a teardown failure must not mask the test's own result.
  `GitTestDirectory.Dispose` carries the same bounded retry in place (it is
  in the `Git` namespace, so it does not reference the Pipelines helper).
- Worker-process class (card work item 3): **not observed in the current
  tree**. All four kill sites kill the entire process tree and then wait —
  `Milestone/MilestoneHost.cs:582`, `Workers/WorkerProcessE2ETests.cs:725`,
  `Git/SshdGitServer.cs:538`, `WorkerRuntime/WorkerRunProcessE2ETests.cs:569` —
  and no Worker process leaked in any of this card's runs. The Nov-14 F-007-era
  leak predates those guards (or was a crashed session); no action here.
- `docs/development.md` §2 counts re-verified untouched: the fast suite
  reports 1047 (the §2 snapshot) and the change adds/removes no tests — it is
  harness hygiene only, so §2 was not edited.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for the temp-root leak found during P-001 (P3). | Closed | Fixed here: bounded retry + recorded residual in the Pipelines harness deletion paths, `using` on the never-disposed fixtures, SQLite sidecar deletion; TC-001's delta is 0 for the Pipelines suites. The out-of-scope harnesses listed in the Development Log keep the old pattern. |

## Verification

- Related files:
  `tests/Zenkins.Tests/Pipelines/PipelineFixtures.cs` (new `TempRootCleanup`),
  `tests/Zenkins.Tests/Pipelines/PipelineDefinitionTests.cs`,
  `tests/Zenkins.Tests/Pipelines/PipelineSpecTests.cs`,
  `tests/Zenkins.Tests/Pipelines/PipelineRunnerTests.cs`,
  `tests/Zenkins.Tests/Pipelines/PipelineRunLoopAbandonmentTests.cs`,
  `tests/Zenkins.Tests/Pipelines/PipelineMigrationTests.cs`,
  `tests/Zenkins.Tests/Git/GitTestEnvironment.cs` (delegated path).
- How to run/verify:
  `dotnet test -c Release --filter "Category=Pipeline"` (the affected suites,
  96 tests), with `ls -d "$TMPDIR"/zenkins-*` counted before and after (TC-001);
  then the fast suite `dotnet test -c Release --filter
  "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd"`
  for AC-3.
- Results: Pipelines filter 96/96 green with **delta 0**; fast suite 1047/1047
  green (matches §2's 1047 snapshot — counts untouched), delta +5 fully
  attributed to out-of-scope harnesses and swept; 0 Worker processes leaked in
  any run. A transient-failure residual now emits one warning line instead of
  vanishing silently (not observed in these runs — the retry absorbs the
  transient failures; the shape is exercised by the 10-attempt loop with the
  final-attempt warning branch).

### 2026-11-15 — independent review: PASS

Adversarial re-verification of the whole diff (7 test files + this card), each
claim re-derived from the tree, plus the reviewer's own runs. All checks hold.

- **(a) The 19 `using var` conversions.** Counted from the diff: 13
  `LuaRuntimeTestDefinition` (9 in `PipelineSpecTests.cs` at :72, :93, :115,
  :154, :169, :178, :193, :205, :228; 4 in `PipelineDefinitionTests.cs` at
  :177, :206, :228, :248) + 6 `DefinitionRun` (`PipelineDefinitionTests.cs`
  :261, :272, :288, :307, :320, :329) = 19, and `grep` finds zero remaining
  un-`using` instantiations of either type anywhere in `tests/`. Both are
  per-test-method scope: the owning classes (`PipelineSpecTests`,
  `PipelineDefinitionTests`) are plain sealed xunit classes — no
  `IClassFixture`/`ICollectionFixture` anywhere under `tests/Zenkins.Tests/
  Pipelines/` — and each instance builds its own Guid-suffixed root
  (`LuaRuntimeTestDefinition` :301-302, `DefinitionRun` :345), so nothing is
  shared and disposal cannot race another test. Mechanical: the only hunks
  beyond the `using` rewrite are the three Dispose bodies and the two
  migration `finally` comments; every assertion line is untouched (diff
  reviewed hunk by hunk; the deadline test's object initializer
  `using var runner = new DefinitionRun(…) { Deadline = … }` is valid C# and
  changes nothing). Disposal order is safe: in `PipelineRunnerTests` the
  harness (`run`) disposes before `repository`, and its Dispose deletes only
  its own `zenkins-f014-run-*` work root (:488-493) — it never disposes or
  touches the repository the test's own `using var` then releases; no fixture
  referenced by a still-live fixture is disposed early anywhere.
- **(b) `TempRootCleanup` (`PipelineFixtures.cs:166-224`).** Bounded 10 ×
  200 ms with `Thread.Sleep` only on the failing path — the green path pays
  one `Directory.Exists` + delete, zero delay. Catch set is right:
  `DirectoryNotFoundException` and `FileNotFoundException` are `IOException`
  subclasses in .NET, so the `when (failure is IOException or
  UnauthorizedAccessException)` filter (:210) subsumes them; nothing
  realistic is leaked. Exactly one warning line per survivor naming the path
  and the exception (:214-216), then `return` — no second warning possible.
  Dispose never throws for any realistic deletion failure, and no GC
  finalizer/background-thread tricks. Sidecars are the correct SQLite names
  (`<db>-wal`/`<db>-shm`, `PipelineFixtures.cs:184`), matching the
  pre-existing enumeration at `Auth/AuthTestHost.cs:243`; the paths are
  test-private Guid names and deletion runs only in test teardown after the
  `await using` contexts closed, so no live file can be hit.
- **(c) `Git/GitTestEnvironment.cs:42`.** `PipelineRepository.Dispose` is
  `PipelineFixtures.cs:96` (`=> _repository.Dispose()`), which is
  `GitFixtureRepository.Dispose` → `GitTestDirectory.Dispose`
  (`GitTestEnvironment.cs:295`, :42) — the delegation justification is real.
  Behavior-neutral for the existing Git/Steps/WorkerRuntime consumers: the
  delete outcome is unchanged (root gone, or left behind exactly as before),
  nothing asserts on `Dispose` or stderr, and every consumer suite is inside
  the reviewer's green fast-suite run below.
- **(d) Reviewer's own delta runs (macOS arm64, `$TMPDIR` +
  `/tmp` snapshotted by exact name list, not just count).**
  `dotnet test -c Release --filter "Category=Pipeline"`: **96/96 passed**,
  0 failed / 0 skipped (21.7 s); delta **0** in both temp locations.
  Fast suite with the §2 filter: **1047/1047 passed**, 0 failed / 0 skipped
  (1 m 59 s); delta **+5**, every file matching the card's attribution
  exactly — `zenkins-d006-migration-*.db-wal`/`-shm` ×2,
  `zenkins-f018-migration-*.db-wal`/`-shm` ×2, `zenkins-f016-work-*` ×1 —
  all from the recorded out-of-scope harnesses; swept after recording.
  /tmp's 87 pre-existing `zenkins-doc003*` install-guide straws are unrelated
  to the per-run Guid roots and unchanged. 0 Worker processes before/after
  every run. `dotnet build -c Release`: 0 warnings / 0 errors. development.md
  §2 untouched by the diff and still accurate: the fast row's 1047 snapshot
  and the quintuple exclusion filter match the run verbatim.
- **(e) Out-of-scope list.** Spot-checked four entries against the tree:
  `Artifacts/ArtifactsTestHost.cs:382` (IOException-only catch),
  `Secrets/SecretRotationMigrationTests.cs:122` (`TryDelete`,
  IOException-only), `Workers/WorkerTokenStoreTests.cs:30` (IOException-only
  Dispose) and the retry-but-silent `Milestone/MilestoneHost.cs:600`
  (10-attempt loop, no final warning) — all match. The kill sites
  `MilestoneHost.cs:582` and `WorkerProcessE2ETests.cs:725` are
  `Kill(entireProcessTree: true)` + `WaitForExit` as recorded. The card
  cleanly separates in-scope-fixed from recorded-not-fixed throughout.
- **(f) Bookkeeping.** Every file:line the dev log cites was re-opened and
  matches (`PipelineSpecTests.cs:339`, `PipelineDefinitionTests.cs:368`,
  `PipelineRunnerTests.cs:488`, `PipelineRunLoopAbandonmentTests.cs:228`,
  `PipelineMigrationTests.cs:97,163`, `GitTestEnvironment.cs:42`,
  `PipelineFixtures.cs:166`). TC-001's numbers agree with the reviewer's own
  runs; ACs and subtasks are truthfully ticked; `git status` shows only the
  7 test files + this card, no untracked files, development.md and board.md
  untouched by the developer.
- **Verdict: PASS.** Status → done; board row moved to Done (2026-11-15).
  The recorded out-of-scope swallow sites remain the natural follow-up card.
