---
id: Q-003
title: Close the v0.3 test debt recorded on accepted cards
type: test
priority: P2
size: M
status: done
created: 2026-11-13
updated: 2026-11-14
blocked: none
---

## Goal

Every gap that an accepted v0.3 card handed over as "recorded, not changed" is closed by a test that
fails without the assertion. Nothing here is a feature; the value is that the next person cannot
delete a guard without a red test.

## Background

The v0.3 cards were accepted with explicit follow-ups rather than silent omissions. These are the ones
that are test-shaped and still open:

1. **`WebhookRateLimiter` has no tests of its own** (F-017): bounded bucket count and eviction, the
   fixed window's double burst at a minute boundary, per-bucket concurrency, and the documented
   fail-open on clock rollback. The class documents these properties; nothing pins them.
2. **The artifact temp-snapshot test is sensitive to a second `dotnet test` process** (F-016, observed
   again in F-017): it scans the whole OS temp tree by design, so a concurrent run on the same machine
   can add a file between snapshots. The trade was accepted; the flake is not.
3. **The product path from a project's repository URL to a clone has no end-to-end test** (F-014):
   pipeline fixtures write the repository URL with direct SQL because a `file://` URL does not pass
   the product's own validation, so nothing proves that a URL saved through the real form is the URL
   the worker clones.
4. **The "abandoned run" branch has runner-level tests but no loop-level one** (F-014): a run the
   server has disowned must not have steps executed, and that is currently proven one layer below the
   loop that decides it.
5. **A second definition report carrying a different commit is not asserted** (F-014): the same
   compare-and-set guards it, and the database-level tests cover the compare-and-set, but nothing
   asserts the endpoint refuses to let a definition report change a pinned commit.
6. **The relative-`Data Source` branch of key-ring resolution has no test row** (F-012): the three
   other branches are pinned; the relative path form is documented as resolved against the process
   working directory.

Decisions:

- **No product behaviour changes are intended.** If closing one of these reveals a real defect, it is
  reported and fixed here rather than deferred again — that is exactly how F-015's crash-path hole was
  found.
- **Each item must demonstrate that it can fail.** A new assertion is worth nothing if deleting the
  code it guards leaves it green; the card asks for the mutation to be recorded (changed, ran, went
  red, restored) rather than claimed.
- **Item 2 is a flake, not a feature**: the fix is to make the snapshot tolerant of foreign entries
  without narrowing it (archives are never empty, so a zero-length or clearly foreign entry may be
  excluded deliberately and documented), and to prove that a newly written non-empty file is still
  caught.

## Acceptance Criteria

- [x] Rate limiter: tests cover bucket bound and eviction, per-bucket concurrency, window boundary
      behaviour, and the documented fail-open after a backwards clock jump; each is red if the
      corresponding guard is removed.
- [x] Artifact temp snapshot: a repeated run with an unrelated process writing into the temp tree does
      not fail, while a newly written non-empty file is still detected; both directions asserted.
      (Landed with the F-017 review commit before this card started work; verified and re-proven red
      here — see the Development Log.)
- [x] Repository URL: a project whose repository URL is saved through the product's own form is the
      URL the worker is told to clone (asserted through claim, not through a seeded row).
- [x] Abandoned run: at the loop level, a definitively refused report means no step is started and no
      completed report is sent.
- [x] Definition report: a second report carrying a different commit is refused and the pinned commit
      is unchanged.
- [x] Key ring: all four resolution branches (explicit relative, explicit absolute, SQLite beside the
      database, PostgreSQL content root) are pinned, including the relative-`Data Source` form.
- [x] Full suite remains at zero skipped and the category counts are recorded before and after, so the
      card cannot hide a deletion.

## Subtasks

- [x] Rate limiter unit tests (`Category=Webhooks`).
- [x] Artifact snapshot tolerance plus its two-direction proof (`Category=Artifacts`) — landed with
      F-017's review commit; verified and mutation-proven under this card.
- [x] Repository URL through the real form (`Category=Pipeline`; fast suite, in-process host).
- [x] Loop-level abandoned run and the differing-commit definition report (`Category=Pipeline`).
- [x] Key-ring branch test (`Category=Secrets`).
- [x] Record the mutation evidence for each item in the log, not only the passing state.

## Dependencies

- F-012, F-014, F-016, F-017 (each item was recorded on one of these cards)

## Test Cases

### TC-001: The limiter's documented properties are real

Steps:
1. Drive the limiter past its bucket bound; advance the clock across a minute boundary; move the clock
   backwards.

Expected:
- Eviction happens at the bound, the boundary behaves as documented, and the backwards jump fails open
  exactly as the comment says; each of the three is red if its guard is removed.

Result: **pass.** `tests/Zenkins.Tests/Webhooks/WebhookRateLimiterUnitTests.cs` (Category=Webhooks,
5 tests, a settable `TimeProvider`): the flood test proves the bucket map never exceeds the cap while
no acquisition is ever refused for the bound's sake (the cap eviction removes one bucket per overflow);
the boundary test proves a full second burst across the minute boundary (the documented 2× peak) while
each window's own budget is still enforced; the rollback test proves the backwards jump fails open and
the rolled-back window is limited again inside itself; the concurrency test proves 8 threads × 125
acquisitions against a budget of 500 accept exactly 500. Mutation evidence per guard in the log —
all four red.

### TC-002: The snapshot still catches what it is for

Steps:
1. Write a non-empty file into the temp tree during a download; then repeat with only a foreign
   zero-length entry present.

Expected:
- The first fails the test; the second does not.

Result: **pass.** Both directions were already implemented and pinned by the F-017 review commit
(505e52e) before this card started work — the snapshot skips zero-length entries (documented: an
archive is never empty) and `tests/Zenkins.Tests/Artifacts/ArtifactStreamingTests.cs` carries the
two-direction harness proof (`The_temp_snapshot_catches_a_new_non_empty_file`,
`A_zero_length_entry_in_the_temp_root_does_not_break_the_scan`, Category=Artifacts). This card
verified the implementation against the decision and re-proved each direction red: removing the
zero-length skip turned only the zero-length test red; excluding regular files from the snapshot
turned only the non-empty test red. No new code needed; see the Development Log.

## Development Log

### 2026-11-13

- Planned after the v0.3 board was cleared: these are the follow-ups the accepted cards recorded, none
  of which is large enough for its own feature card but all of which guard something real.

### 2026-11-14 — implemented

- **Item 1 — rate limiter (`Category=Webhooks`).** New
  `tests/Zenkins.Tests/Webhooks/WebhookRateLimiterUnitTests.cs` (5 tests) over a steerable
  `TimeProvider`: budget refusal with `Retry-After` in [1s, 60s] and a fresh window afterwards; the
  fixed window's full second burst across the boundary; the documented fail-open on a backwards clock
  jump (and the rolled-back window limited again); the bucket cap under a flood of distinct ids (bound
  asserted through the limiter's own window map — the documented property is about that private state
  and no public surface reports it — while never refusing a flood for the bound's sake); per-id
  concurrency (8 threads × 125 attempts, exactly the budget accepted).
  Mutation evidence (each mutation on `src/Server/Webhooks/WebhookRateLimiter.cs`, run, recorded red,
  restored with `git checkout --`):
  - *budget refusal guard (`window.Count >= RateLimitPerMinute`) deleted* → red: 4 of 5, including
    `A_spent_budget_is_refused_with_a_retry_after_until_the_window_ends`.
  - *`EnforceBucketCap` call deleted* → red: `A_flood_of_distinct_ids_stays_within_the_bucket_cap_and_is_never_refused_for_it`
    (map grew past the cap).
  - *window-index reset (`window.Index != index` block) deleted* → red:
    `The_fixed_window_hands_out_a_full_second_burst_at_a_minute_boundary`,
    `A_backwards_clock_jump_fails_open_and_the_rolled_back_window_is_limited_again`,
    `A_spent_budget_…`.
  - *per-window `lock (window)` replaced by an unlocked block* → red:
    `Concurrent_acquires_for_one_id_never_exceed_the_budget`. Honest note: at 50 contested acquisitions
    this was probabilistic (1 red in 3 runs — a lost-update race is not deterministic), so the test was
    strengthened to 500 contested acquisitions and the mutated build then went red in 5 of 5 runs;
    the unmutated limiter stays green.
- **Item 2 — artifact temp snapshot (`Category=Artifacts`).** No new code: the tolerance and the
  two-direction proof landed with F-017's review commit (505e52e, "review m10") before this card began
  — the card's Background item pre-dates that commit. Verified against the binding decision: the
  snapshot compares directories, links and every non-empty file by path and length, and excludes
  zero-length entries deliberately and documented (an archive is never empty, so the exclusion cannot
  hide one); TC-002's two directions are pinned by the harness self-tests. Mutation evidence (each on
  `tests/Zenkins.Tests/Artifacts/ArtifactStreamingTests.cs`, restored afterwards):
  - *zero-length skip deleted* → red: `A_zero_length_entry_in_the_temp_root_does_not_break_the_scan`
    (the non-empty test stayed green — the directions are independent).
  - *regular files excluded from the snapshot entirely* (the final add gated on a condition that never
    holds) → red: `The_temp_snapshot_catches_a_new_non_empty_file` (the zero-length test stayed green).
- **Item 3 — repository URL through the real form (`Category=Pipeline`).** New test in
  `tests/Zenkins.Tests/Pipelines/PipelineRunEndpointTests.cs`:
  `A_repository_url_saved_through_the_real_form_is_what_the_claim_tells_the_worker_to_clone`. It
  creates the project through the real form (`POST /projects/create`, antiforgery included, an
  `https://` URL that passes the product's own validation — no seeded row anywhere), triggers the run
  through the real start-run form, claims it through the real claim endpoint and asserts the claim
  payload's `repositoryUrl` equals the URL the form saved. Mutation evidence: `RunService.ClaimAsync`'s
  claim payload changed from `candidate.Project.RepositoryUrl` to `string.Empty` → red (`Assert.Equal`
  string mismatch), restored.
- **Item 4 — loop-level abandoned run (`Category=Pipeline`).** New
  `tests/Zenkins.Tests/Pipelines/PipelineRunLoopAbandonmentTests.cs` (a plain `HttpMessageHandler`
  stub, one layer up from the runner-level tests):
  `A_run_the_server_disowns_at_its_definition_report_starts_no_step_and_sends_no_completed_report`.
  Run A's commit-only definition report is answered 409 (definitive per F-014's reporting contract):
  the loop gives it up — exactly one definition attempt, zero step reports, zero completed reports —
  and the run behind it is claimed, executed (2 definition sends: commit + spec) and completed with
  exit 0. Mutation evidence (each run, red, restored):
  - *loop level*: `WorkerRunLoop.ExecutePipelineAsync`'s `outcome.Abandoned` branch changed to return a
    completed result (`RunExecutionResult(0, null)`) → red: the disowned run received a completed
    report (`CompletedAttempts(disowned)` 1 instead of 0).
  - *runner level (the guard one layer below, which this test also now watches)*:
    `PipelineRunner.RunAsync`'s `if (!commitReported) return GiveUp(commit)` deleted → red: the
    disowned run proceeded to the definition phase and the spec send (`DefinitionAttempts(disowned)` 2
    instead of 1).
- **Item 5 — definition report with a different commit (`Category=Pipeline`).** New test in
  `PipelineRunEndpointTests.cs`:
  `A_second_definition_report_carrying_a_different_commit_is_refused_and_the_pinned_commit_is_unchanged`.
  First report pins the commit and freezes a spec (200); a second, commit-only report naming a
  different commit is refused 409 `git-commit-mismatch`, and the pinned commit, the frozen spec and the
  Running status survive untouched. Mutation evidence: the
  `run.GitCommit != gitCommit → Conflict(GitCommitMismatch)` guard deleted from
  `RunService.ReportDefinitionAsync` → red (endpoint answered OK instead of Conflict — the DB-level
  compare-and-set still kept the first commit, which is exactly why the endpoint behaviour needed its
  own pin), restored.
- **Item 6 — key-ring relative `Data Source` branch (`Category=Secrets`).** New fifth row in
  `SecretDataProtectionTests.Each_resolution_branch_lands_where_the_documentation_says`:
  `relative-data-source-keeps-the-ring-beside-the-working-directory` — `Data Source=App_Data/zenkins.db`
  (relative) with no key-ring configuration resolves to `<process working directory>/App_Data/keys`,
  the resolution Microsoft.Data.Sqlite applies. Mutation evidence: the SQLite-`Data Source` branch
  deleted from `SecretDataProtection.ResolveKeysDirectory` (everything falls back to the content root)
  → red: the new row and the existing `sqlite-keeps-the-ring-next-to-the-database` row (both depend on
  that branch), the three configuration-driven rows stayed green; restored.
- **Bugs: none.** No product defect surfaced — every item's guard held; no product behaviour change
  was made, and `git status`/`git diff` show only the two modified and two new test files.
- Verification (all recorded outputs, `dotnet` 10.0.403):
  - `dotnet build -c Release` → 0 warnings, 0 errors.
  - `Category=Webhooks` 69 → **74** (0 failed, 0 skipped); `Category=Artifacts` 89 → **89**; 
    `Category=Pipeline` 93 → **96**; `Category=Secrets` 64 → **65**.
  - Fast suite `--filter "Category!=Milestone&Category!=Recovery"`: 940 → **949** (0 failed, 0 skipped).
  - `Category=Recovery` → **4 passed** (0 failed, 0 skipped); `Category=Milestone` → **6 passed**.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| Q-003-B1 | P2 | The lock-removal mutation evidence is probabilistic: without `lock (window)` the concurrency test went red 6/10 runs (reviewer) and 5/5 (developer) — a future mutation could slip through a single green run. | Deferred | The guard is real and the test is the right shape; the evidence is recorded as probabilistic. Repeating the test in CI or accepting the recorded evidence explicitly is the lever. |
| Q-003-B2 | P2 | The temp-snapshot acceptance wording is broader than the guarantee: only zero-length/non-regular foreign entries are tolerated; a foreign non-empty file between snapshots still fails (deliberately — TC-002 specifies it). Plausibly the identity of the once-seen fast-suite flake recorded on D-002 (D-002-B2): an unrelated process writing a non-empty file into the OS temp tree during a run. | Deferred | Matches TC-002's defined semantics and the in-file comment. If D-002-B2 reproduces, its failing test should be identified and reconciled with this card's trade. |
| Q-003-B3 | P2 | The relative-`Data Source` key-ring row asserts against `Directory.GetCurrentDirectory()` — correct per Microsoft.Data.Sqlite's documented resolution and deterministic under the test host, but CWD-dependent by design. | Deferred | Documented behaviour; revisit only if the host's working-directory convention changes. |

## Verification

- Related files: `src/Server/Webhooks/WebhookRateLimiter.cs` (mutated and restored only — no change),
  `tests/Zenkins.Tests/Webhooks/WebhookRateLimiterUnitTests.cs` (new),
  `tests/Zenkins.Tests/Artifacts/ArtifactStreamingTests.cs` (verified; mutated and restored only — no
  change; the tolerance itself landed with F-017's commit 505e52e),
  `tests/Zenkins.Tests/Pipelines/PipelineRunEndpointTests.cs` (2 new tests),
  `tests/Zenkins.Tests/Pipelines/PipelineRunLoopAbandonmentTests.cs` (new),
  `tests/Zenkins.Tests/Secrets/SecretDataProtectionTests.cs` (new theory row),
  `src/Server/Runs/RunService.cs`, `src/Worker/WorkerRunLoop.cs`,
  `src/Worker/Pipelines/PipelineRunner.cs`, `src/Server/Secrets/SecretDataProtection.cs` (all mutated
  and restored only — no product change).
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Webhooks"`;
  `--filter "Category=Artifacts"`; `--filter "Category=Pipeline"`; `--filter "Category=Secrets"`;
  fast suite `--filter "Category!=Milestone&Category!=Recovery"`; `--filter "Category=Recovery"`;
  `--filter "Category=Milestone"`.
- Results: 2026-11-14 — build 0 warnings / 0 errors. Webhooks 69→74, Artifacts 89→89, Pipeline 93→96,
  Secrets 64→65 (each 0 failed, 0 skipped). Fast suite 940→949, 0 failed, 0 skipped. Recovery 4/0/0,
  Milestone 6/0/0. Per-item mutation evidence in the Development Log (mutated → red → restored →
  green).
