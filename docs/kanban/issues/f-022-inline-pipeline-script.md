---
id: F-022
title: Inline pipeline script — a Pipeline project whose pipeline.lua lives on the Server, not in SCM
type: feature
priority: P1
size: M
status: done
created: 2026-11-16
updated: 2026-11-17
blocked: none
---

## Goal

A `pipeline` project can take its definition from the Server — a Lua body typed
and edited in the UI — instead of a repository, so a job can exist with no Git
remote and still get the full definition phase (a `pipeline{...}` table, Lua
logic that chooses steps, a `triggers` block, pipeline-level `env`).

## Background

Jenkins gives a Pipeline job two Definitions: **Pipeline script** (inline) and
**Pipeline script from SCM**. Zenkins only has the second one. F-020 added the
other *kind* (Freestyle), but Freestyle is not the same thing:

- Freestyle freezes a **spec** the Server built from UI step drafts; there is no
  definition phase, so no `pipeline{}` table, no Lua logic, no `triggers` block,
  no Lua-declared pipeline `env`, and every step is a `script` step.
- An inline pipeline runs the **script** on the Worker exactly like a repository
  pipeline; only the source of the bytes changes.

The enabling insight: `Project.Kind` is data, the frozen spec is the one
execution contract for both kinds, and the Worker's claim payload already carries
either a repository or a frozen spec (F-020). An inline pipeline needs a third
shape: no repository, no frozen spec *yet*, and a script text the Worker runs the
definition phase against.

## Acceptance Criteria

- [x] A `pipeline` project can be created with source `inline` and a Lua body;
      the repository fields are null and the injectable defaults (branch, commit,
      pipeline path) record the honest empty value, exactly as Freestyle does.
      (Read with the AC-1 vs TC-001 ruling below: the **project row's**
      repository fields are null; the **run's** pipeline path is `pipeline.lua`.)
- [x] A run of an inline project skips the clone and runs the definition phase
      from the script; the frozen spec, step records, logs, secrets, artifacts,
      cancellation and deadline behave exactly as for a repository pipeline.
- [x] The script is **snapshotted onto the run** at trigger time: editing the
      project afterwards does not change a queued or running run.
- [x] A syntax or runtime error in the definition phase fails the run with the
      same reason taxonomy and the same `pipeline.lua:(line,col)` diagnostic a
      repository pipeline produces (`pipeline-syntax` for a compile failure,
      `pipeline-invalid` for a raising definition - see the TC-003 note).
- [x] Webhook delivery is refused for an inline project (no repository, nothing
      to push) with the same guard shape Freestyle already has, and the Git
      credential settings are hidden and refused **on the server**.
- [x] The Pipelines tab offers a Validate action that runs the definition phase
      on a Worker and shows the resulting step list without executing a step and
      without creating a run or taking a build slot.
- [x] PRD §3.2/§3.3 record the inline source as part of the Pipeline kind (and
      §7.2's "no visual pipeline editor" note stays true: the editor is text).

## Subtasks

- [x] `Project.PipelineSource` (`repository` | `inline`, immutable after create
      like `Kind`) plus `Project.InlineScript` (bounded at 64 KiB).
- [x] `BuildRun.PipelineScriptText` snapshot, carried in the claim payload
      (`pipelineScript`) alongside `repositoryUrl` and `pipelineSpecJson`, plus an
      explicit `definitionSource` word the Worker switches on.
- [x] `RunService.CreateRunAsync` freeze path: inline projects snapshot the
      script, skip the credential/branch requirements, and keep the parameter and
      required-tag validation unchanged.
- [x] Worker: when the claim says `inline`, write the script to
      `work/{run-id}/temp/pipeline.lua` and run the definition phase against that
      path so error positions and the frozen spec name stay `pipeline.lua`.
- [x] Validate action: a definition-only task (no clone, no steps, no run row,
      no build slot) that returns the spec or the diagnostic.
- [x] UI: script editor on the Pipelines tab for the inline source (plain
      `<textarea>`, no editor library), save through the existing POST +
      antiforgery pattern, Validate button with its result.
- [x] Secret reference capture: the definition phase records
      `secretReferences` on the frozen spec exactly as a repository run does (the
      same `PipelineDefinitionRunner` / `SecretReferenceCapture` path).

## Dependencies

- F-020 (kind column, server-frozen spec, script steps) — in review
- F-014/F-015 (definition phase, frozen spec, step records) — accepted

## Test Cases

### TC-001: An inline pipeline builds with no repository

Steps:
1. Create an inline pipeline project whose script declares two `run` steps.
2. Trigger a run; let the test worker claim and execute.

Expected:
- The run is `Success` with two step records and their logs.
- The run's `PipelinePath` is `pipeline.lua`, branch and commit are empty.
- The worker's work directory has no `repository/` entry.

**Read as the shipped work-directory trio, not as a literal absence.** §3.4.6's
`Prepare` always creates `repository/`; the honest assertions are that
`repository/` is **empty** and the steps execute in `workspace/` (the Freestyle
trio). The runner fact asserts both.

### TC-002: The script is frozen per run

Steps:
1. Trigger a run of an inline project; leave it queued (no worker).
2. Edit the project script; then let a worker execute the queued run.

Expected:
- The queued run executes the **old** script, and the run page's spec matches it.
- A newly triggered run uses the new script.

### TC-003: A broken script is a pipeline failure, not a crash

Steps:
1. Save a script with a syntax error; trigger a run.

Expected:
- The run ends `Failed` with its definition-phase failure reason and one log line
  carrying `pipeline.lua:(line,...)`.
- No step record was created; the worker is still healthy and claims the next run.

**Wording note.** The card first said `pipeline-invalid` for a syntax error. The
shipped taxonomy (F-014) already calls a compile failure `pipeline-syntax` and a
raising/rejected definition `pipeline-invalid`; AC-2 requires the inline source to
behave *exactly* like a repository pipeline, so the implementation reuses that
taxonomy rather than special-casing inline. The fact is a Theory that runs the
same assertion against both sources, so the two cannot drift.

## Notes and Decisions

### Data model and migration

- `Projects.PipelineSource` is `NOT NULL DEFAULT 'repository'` with
  `CK_Projects_PipelineSource` (`repository` | `inline`); `Projects.InlineScript`
  is nullable text; `BuildRuns.PipelineScriptText` is nullable text.
- Migration `20261117073131_AddProjectPipelineSourceAndInlineScript` (generated
  against **PostgreSQL**, the provider the migration set is generated for). Adding
  the column with `defaultValue: "repository"` is what keeps **every historical
  Pipeline row a repository project**; it is never turned inline. SQLite's
  dynamic typing fills existing rows from the same default.
- `PipelineSource` is immutable after create, exactly like `Kind`: the edit
  service validates against the stored source and never writes the posted one, so
  a queued run's shape cannot be stranded. `Kind`'s own protection is untouched.
- An inline project's `InlineScript` is required (non-empty) and bounded by
  `PipelineScriptLimits.MaxScriptBytes` = 64 KiB, checked as UTF-8 bytes on save;
  the Worker re-checks the same bound. The text is stored verbatim (trimming would
  move every diagnostic's column).
- AC-1 vs TC-001 ruling: the **project row** records honest nulls for
  `RepositoryUrl`/`DefaultBranch`/`PipelinePath`; the **run snapshot** names
  `pipeline.lua`, because the script really becomes that file and the diagnostic
  depends on the name. Both facts are asserted (row null, run `pipeline.lua`), so
  neither literal statement is left unproven.

### Run snapshot and claim

- `BuildQueuedRun` sets `PipelineScriptText` from the project's script and
  `PipelinePath = pipeline.lua` for an inline project (B-006's kind-based path
  rule is extended, not replaced: Freestyle stays empty).
- The claim reads the **run row**, never the project: it carries
  `pipelineScript` and an explicit `definitionSource` (`repository` |
  `freestyle` | `inline`) computed from the run's own snapshot columns. Presence of
  a spec is not the discriminator: an inline run may also carry one on a re-run.
- The Worker switches on `definitionSource`; the inline path writes the script to
  `work/{run-id}/temp/pipeline.lua` (UTF-8, no BOM), runs the same
  `PipelineDefinitionRunner` core with chunk name `pipeline.lua`, and re-checks the
  same 64 KiB bound before compiling. It reports the
  spec, and executes the steps in `workspace/`. `repository/` stays empty.
  Freestyle/Repository handling, secrets, artifacts root, cancellation and the
  deadline are the one existing path.

### The two guards: one predicate, one place

`ProjectSources.HasRepository(kind, source)` (subject: `(Kind, PipelineSource)`,
never `RepositoryUrl`) is now the single answer used by:

1. `WebhookService.CreateAsync` — creation refuses with the unchanged vocabulary
   (`project-not-pipeline`);
2. `WebhookDeliveryService` — delivery records the unchanged
   `ignored:no-repository`;
3. `RunService.QueueWebhookRunAsync` — defence in depth, unchanged
   `not-pipeline`;
4. `ProjectSettings.razor` — the webhook/credential section is hidden;
5. `GitCredentialService.AssignToProjectAsync` — **new server-side refusal**
   (until this card only the UI hid it, so a direct POST could assign a
   credential to any project with no repository). This also closes the same hole
   for Freestyle; no existing fact asserted "assign a credential to Freestyle", so
   nothing was contradicting it. Recorded here as a fix-by-necessity.
6. `GitProbeService.RequestAsync` — the probe uses the same predicate plus its
   separate "URL is empty" row-shape check.

### Validate (AC-6) and its hard constraints

`DefinitionValidationService` reuses card I-005's **no-run worker task** shape
instead of inventing a run: an in-memory registry, a worker claim/report pair, a
lease and a bounded caller wait. A Worker answers it on its own validation loop,
beside (never inside) the build loops. Consequences, each asserted or structurally
true:

- no `BuildRun`, no `BuildStep`, no run number consumed, no dashboard/history row;
- no build slot: the claim does not consult `MaxConcurrentRuns` (the fact claims
  while the project's only slot is occupied by a Running run);
- no clone and no step execution — only the definition phase;
- a Worker-side bounded budget (five minutes) so `while true do end` cannot hold
  the loop forever, and the Server-side wait is bounded (20 s default) so a page
  always gets a typed answer (`no-worker`);
- no secret **value** is fetched: the definition runtime only records names;
- the registry key is `(validationId, token tenant)`; a foreign worker gets 204 on
  claim and a bare 404 on report;
- the posted textarea text is validated, not the stored script; saving alone
  never claims validity. A repository project is refused with
  `pipeline-source-invalid` (it has no script; its definition phase belongs to a
  run's clone).
- The last result is kept in memory per project purely for the PRG redirect to
  render; it is not a stored row.

### Rebuild/replay discriminator (not in the AC, but it had to be answered)

`RunReruns.IsFreestyleShape(empty branch)` would have called an inline run
"Freestyle", then refused it as `rebuild-kind-changed` because the project is a
Pipeline. Fixed: `RunView.InlineDefinition` (`PipelineScriptText is not null`) is a
third shape; an inline re-run carries the same script and its definition phase
runs again (the frozen spec is not reused as the definition). Rebuild and replay
both work for inline runs; repository and Freestyle behaviour is unchanged.

### Worker source word

The Worker reads the claim's `definitionSource`, not "is `pipelineSpecJson`
non-null" (three former sites: runner dispatch, step cwd, artifacts root). An
inline run that also carries a spec is still inline.

### Other

- Rejected: a third `Kind` (`inline-pipeline`). Kind is immutable and every guard
  (webhook, credential, artifact paths) would multiply; a source column keeps one
  kind with two definition sources.
- Rejected: routing the inline Lua through the Freestyle spec editor. Two
  editors, two validation paths, and the definition phase would be lost.
- Validate runs on a Worker (the Server has no Lua engine, I-004). Saving alone
  must never claim a script is valid.
- The create form does not echo the script back on a validation redirect (it can
  be 64 KiB, and the redirect URL is not the place for it); the editor starts
  empty after a refused create. The error code still names the field.
- Untouched: B-006's kind-based `BuildQueuedRun` rule, Q-012's Freestyle guard
  facts, F-020's `Kind` immutability, `board.md`.

### Review round 1: the three blocking evidence holes

**F1 - the diagnostic's name is now pinned.** The old assertion
`Assert.Matches(@"pipeline\.lua:\(1,\d+\)")` also matched an absolute path, and
`DoesNotContain(":/ ")` is meaningless on POSIX. The Theory now anchors the whole line:
`Assert.StartsWith("zenkins: pipeline.lua:(")` plus
`Assert.Matches(@"^zenkins: pipeline\.lua:\(1,\d+\)")`. Mutation M9 (chunk name = the
temp file's absolute path) is red on the inline case only; the repository case stays green,
which is what makes it a name pin rather than a taxonomy pin.

**F2 - the real Worker validation loop now has facts.** Two facts in
`tests/Zenkins.Tests/WorkerRuntime/WorkerDefinitionValidationLoopTests.cs` run the actual
`WorkerDefinitionValidationLoop` against the real claim/report API, with the real
`PrepareValidation` scratch tree and the real `PipelineDefinitionRunner`: one valid script
(the reported spec must carry that script's own name and steps, and the scratch tree must be
gone), one syntax error (the report must be `invalid`, `pipeline-syntax`, and its diagnostic
must match the anchored `pipeline.lua:(line,col)`). Mutation M10 (report a canned spec and
never describe the script) is red on both; before this pair it survived all 58 inline/runner
facts.

**F3 - the run page and the service agree about inline re-runs.** `RunView.CanRebuild`/
`CanReplay` passed `IsFreestyleShape(empty branch)` as `projectIsFreestyle`, so the page
computed "the project's kind changed" and rendered neither button while the service would
have accepted both. They now pass `!InlineDefinition && IsFreestyleShape(...)`. The fact
`An_inline_run_offers_rebuild_and_replay_and_a_rebuild_reuses_the_same_script` asserts both
buttons render and that a rebuild/replay each produce a run carrying the same script with no
reused spec. Reverting the page argument (mutation M11) is red.

### Review round 1: RECORD ledger

| Item | Disposition |
|---|---|
| Inline `triggers` / pipeline `env` / `secretReferences` had no fact | **Fact** `An_inline_definition_carries_its_triggers_pipeline_env_and_secret_references` (runner): the frozen spec carries `triggers.branches`, `env.REGION`, and `secretReferences == ["TOKEN"]` from a real `secret()` call. |
| Artifact root by source was unproven | **Fact** `An_inline_run_publishes_an_artifact_from_its_workspace` (real `WorkerRunLoop` + real artifact API): the step writes `report.txt` in `workspace/`, the run is Success, one `Ready` artifact exists with the file. Reverting the root to `spec != null ? workspace : repository` (M12) is red. |
| `while true do end` timeout had no fact | **Fact** `An_inline_definition_that_never_returns_times_out_instead_of_holding_the_run`: one-second deadline, `pipeline-timeout`, no definition report, the deadline line in the log. |
| Freestyle half of the credential refusal had no fact | **Fact** `A_freestyle_project_also_refuses_a_git_credential_assignment_on_the_server`: direct attach POST is 403 and the column stays null. Recorded as a fix-by-necessity (the guard was missing before this card). |
| Migration fact was empty-database-only | **Changed** to seed-then-migrate: migrate to `AddGitProbeCapability`, insert the historical Pipeline row on that schema, then migrate F-022 over it; the row reads `repository`. |
| Manual trigger passed `project.DefaultBranch` for inline | **Fixed**: the manual path now passes `""` for inline, symmetric with the schedule path. **Fact** `An_inline_run_ignores_an_out_of_band_default_branch` seeds a row with `DefaultBranch = "main"` and asserts the run's branch is empty. |
| `?validated=<id>` was unused | **Fixed**: the id is now consumed. The page shows only that settled validation of this project and ignores a stale/forged id (asserted in the Validate fact). With no id, a direct visit still shows the last result. |
| `ZenkinsDbContext` comment claimed the trigger re-checks the 64 KiB bound | **Corrected**: the trigger only requires the script to exist; the Worker enforces the byte bound before compiling. |
| R9 proved only inline -> repository immutability | **Fact** extended in `A_repository_project_keeps_the_historical_source_and_no_script`: a repository project's edit posting `Kind = Freestyle` + `PipelineSource = inline` leaves both unchanged and adds no script. |

**Predicate honesty.** The review showed that reverting the two pure-rendering sites in
`ProjectDetail.razor` to `Kind == Freestyle` leaves 46 tests green. The card therefore does
**not** claim "all call sites are pinned": *any hand-written `|| source == Inline` is pinned
only where it changes a decision; pure rendering is not pinned.* Every decision site
(webhook create/delivery, run's webhook defence, credential assignment, probe) is pinned.

**Follow-up (not this card).** A run row should store an explicit definition-source word (or
reuse `PipelinePath`) instead of deriving "Freestyle" from an empty branch. F3 is the first
symptom: every new source adds another exception to that heuristic. Recorded here so the
next source does not rediscover it.

## Verification (2026-11-17)

- `dotnet build -c Release` — `Build succeeded.` / `0 Warning(s)` / `0 Error(s)`.
- Fast suite (the sextuple exclusion filter of `docs/development.md` §2) —
  **1310 passed, 0 failed, 0 skipped** (baseline 1287 + this card's 23 fast
  facts). Log: `artifacts/f-022/fast-round1.log`.
- `Category=Postgres` on a disposable PostgreSQL 16 instance — **5 passed, 0
  failed, 0 skipped** (baseline 4 + this card's migration fact). Log:
  `artifacts/f-022/postgres-round1.log`. The instance was `initdb --locale=C.UTF-8`,
  a random loopback port, `pg_ctl start`, `createdb zenkins_test`; connection
  details appear in no tracked file.
- `dotnet ef migrations has-pending-model-changes` (PostgreSQL) — `No changes have
  been made to the model since the last migration.`
- New facts: 23 fast + 1 Postgres. Fast 1287 → 1310; Postgres 4 → 5; all seven
  1314 → 1338 (12 + 4 + 2 + 2 + 3 + 5). `docs/development.md` §2 carries the same
  numbers.

### The facts

| Fact | Where | What it asserts |
|---|---|---|
| `An_inline_project_stores_the_script_and_null_repository_fields_and_its_source_is_immutable` | `tests/Zenkins.Tests/Runs/InlinePipelineRunTests.cs:47` | Create through the real form: kind `pipeline`, source `inline`, script stored, all three repository fields null. An edit posting a different kind **and** source leaves both untouched (and updates the script); a second edit carrying only the name keeps the stored script (the kind-locked form renders no script field). |
| `A_repository_project_keeps_the_historical_source_and_no_script` | `:103` | The historical default: a Pipeline created without a source reads `repository`, has no script, and keeps its URL. |
| `An_inline_run_snapshots_the_script_and_the_claim_carries_it_with_pipeline_lua_and_no_repository` | `:152` | After a trigger the run carries the script, `PipelinePath == pipeline.lua`, empty branch, null commit/spec. The project is then edited for real (asserted), and the claim still carries the **old** script, `definitionSource=inline`, `repositoryUrl=null` — the claim reads the run row, not the project. |
| `An_empty_or_over_bound_inline_script_is_refused_at_create_and_at_save` | `:193` | Whitespace-only at create → `error=inline-script-required`; 64 KiB + 1 at save → `error=inline-script-too-long`; the stored script is byte-for-byte unchanged. |
| `Validate_runs_a_definition_phase_on_a_worker_and_creates_no_run_and_takes_no_slot` | `:233` | With `MaxConcurrentRuns = 1` and a Running run, a build claim finds nothing while a validation claim succeeds. The worker reports a spec; the page renders the step list; no run, step or build number was created, and the busy run is untouched. |
| `Validate_reports_a_broken_script_as_invalid_with_the_positioned_diagnostic` | `:321` | An `invalid` report with `pipeline.lua:(2,1)` renders the diagnostic and `pipeline-syntax`, and queues no run. |
| `Validate_without_a_worker_settles_honestly_and_creates_nothing` | `:367` | A short wait settles `no-worker`, the page says so, and nothing is queued. |
| `Validate_refuses_a_repository_project_with_the_editor_code` | `:388` | A repository project is refused with `error=pipeline-source-invalid`; no validation entry, no run. |
| `The_inline_editor_and_the_validation_worker_api_stay_inside_the_token_tenant` | `:406` | A foreign project's save/Validate are 404s (nothing written/queued); a foreign worker gets 204 on claim and 404 on report; the owning worker settles it and the foreign diagnostic never lands. |
| `An_inline_script_runs_its_steps_in_the_workspace_and_leaves_repository_empty` | `tests/Zenkins.Tests/Pipelines/PipelineRunnerTests.cs:479` | The script is materialised at `temp/pipeline.lua` byte-for-byte, read once; the three steps append their markers in `workspace/`; `repository/` exists (the §3.4.6 trio) and is **empty**; one definition report with no commit; the log says `inline pipeline run`. |
| `A_syntax_error_carries_the_same_reason_and_the_same_position_for_both_sources` (Theory, 2 cases) | `:558` | One assertion over a repository script and an inline script: both are `pipeline-syntax`, both log `pipeline.lua:(1,\d+)`, and neither executes or reports a spec. |
| `An_inline_project_cannot_be_given_a_webhook` | `tests/Zenkins.Tests/Webhooks/WebhookInlineGateTests.cs:33` | The settings form refuses with the unchanged `webhookError=project-not-pipeline`; no webhook row. |
| `A_delivery_to_a_seeded_inline_webhook_is_recorded_as_ignored_without_queueing_a_run` | `:60` | A seeded row + the real signed delivery endpoint → 202 `ignored:no-repository` on the delivery and the webhook row, no run. |
| `An_inline_project_refuses_a_git_credential_assignment_on_the_server` | `tests/Zenkins.Tests/Git/GitCredentialInlineGuardTests.cs:31` | The settings page renders no credential section, and a direct attach POST is 403 with the project's `GitCredentialId` still null. |
| `The_pipeline_source_column_defaults_history_to_repository_and_the_script_column_exists` | `tests/Zenkins.Tests/Concurrency/PostgresInlinePipelineMigrationTests.cs:29` | On real PostgreSQL: the migration set applies; a historical row inserted with **no** `PipelineSource` reads `repository`; an inline row is accepted; `BuildRuns.PipelineScriptText` exists; the check constraint refuses `elsewhere`. |
| `An_inline_run_offers_rebuild_and_replay_and_a_rebuild_reuses_the_same_script` | `tests/Zenkins.Tests/Runs/InlinePipelineRunTests.cs:504` | A finished inline run renders both re-run forms; rebuild and replay each produce a run carrying the same script, `pipeline.lua`, and no reused spec. |
| `An_inline_run_ignores_an_out_of_band_default_branch` | `:569` | A seeded inline row with `DefaultBranch = "main"` still produces a run with an empty branch (the manual path is symmetric with the schedule path). |
| `An_inline_definition_carries_its_triggers_pipeline_env_and_secret_references` | `tests/Zenkins.Tests/Pipelines/PipelineRunnerTests.cs:514` | A real inline definition freezes `triggers.branches`, pipeline `env.REGION`, and `secretReferences == ["TOKEN"]`. |
| `An_inline_definition_that_never_returns_times_out_instead_of_holding_the_run` | `:539` | `while true do end` under a one-second deadline ends `pipeline-timeout` with no definition report. |
| `The_real_loop_describes_the_claimed_script_and_publishes_its_own_step_list` | `tests/Zenkins.Tests/WorkerRuntime/WorkerDefinitionValidationLoopTests.cs:33` | The real `WorkerDefinitionValidationLoop` claims, describes, reports the script's own name/steps, creates no run, and deletes the `validations/{id}` scratch tree. |
| `The_real_loop_reports_a_syntax_error_with_the_pipeline_lua_position` | `:85` | The same loop reports `invalid` / `pipeline-syntax` with an anchored `pipeline.lua:(line,col)` diagnostic and cleans the scratch tree. |
| `An_inline_run_publishes_an_artifact_from_its_workspace` | `tests/Zenkins.Tests/WorkerRuntime/WorkerRunLoopTests.cs:188` | The real run loop publishes the step's `report.txt` from `workspace/`; one `Ready` artifact with the file exists. |
| `A_freestyle_project_also_refuses_a_git_credential_assignment_on_the_server` | `tests/Zenkins.Tests/Git/GitCredentialInlineGuardTests.cs:59` | The same server refusal for the Freestyle half (403, column stays null). |

### Mutations (each restored; hashes in `artifacts/f-022/mutation-N-restored.sha256`)

| # | Mutation | Result |
|---|---|---|
| 1 | Claim stops sending the script/source | red: `An_inline_run_snapshots…` — `pipelineScript` is null / `definitionSource` is null (`mutation-1-claim.log`, 1/9) |
| 2 | Inline run path falls back to the (null) project pipeline path | red: same snapshot fact — `PipelinePath == ""` instead of `pipeline.lua` (`mutation-2-run-path.log`, 1/9) |
| 3 | Worker's inline branch made unreachable (the old "spec decides" thinking) | red: inline runner fact **and** the inline Theory case (`mutation-3-worker-source.log`, 2/17) |
| 4 | Webhook guards reverted to `Kind == Freestyle` | red: both inline webhook facts, including the seeded delivery (`mutation-4-webhook.log`, 2/2) |
| 5 | Credential server guard deleted | red: the attach POST succeeds instead of 403 (`mutation-5-credential.log`, 1/1) |
| 6 | Validation lease stops filtering by organization | red: the foreign worker claims the tenant's validation (`mutation-6-validate-scope.log`, 1/1) |
| 7 | Migration default changed from `repository` to `""` | red on PostgreSQL: the historical insert violates `CK_Projects_PipelineSource` (`mutation-7-migration.log`, 1/1) |
| 8 | Inline syntax folded to `pipeline-invalid` (the card's original wording) | red: **all three** syntax facts, including the repository case — with chunk name `pipeline.lua` the special case cannot tell the sources apart, which is exactly why the taxonomy is shared (`mutation-8-taxonomy.log`, 3/3) |
| 9 | Inline chunk name changed from `pipeline.lua` to the temp file's absolute path | red: the inline Theory case fails the anchored `zenkins: pipeline.lua:(` assertion; the repository case stays green (`mutation-9-chunk-name-path.log`, 1/2) |
| 10 | The validation loop reports a canned spec and never describes the script | red: both real-loop facts (`mutation-10-canned-spec.log`, 2/2) |
| 11 | The run page passes `IsFreestyleShape` as `projectIsFreestyle` again | red: the inline run page renders neither Rebuild nor Replay (`mutation-11-rerun-page.log`, 1/1) |
| 12 | The artifact source root reverts to `spec != null ? workspace : repository` | red: the inline artifact step fails and no artifact exists (`mutation-12-artifact-root.log`, 1/1) |

Each mutation was applied to the product, built, run against its named filter,
and reverted; the restored file hash is recorded next to the log. Logs:
`artifacts/f-022/` (`fast-round1.log`, `postgres-round1.log`, `review-round1.log`,
`mutation-1..12-*.log`, `mutation-1..12-restored.sha256`).

## Close-out review (2026-11-17)

Three rounds: the implementation passed on the third. The first review round returned it not for wrong
code but for **missing evidence** - four of its own mutations stayed green - and that shape is worth
recording, because every one of them was a fact that would have survived a real regression.

- **`M5`, the diagnostic name.** Pointing the inline chunk name at the temp file's absolute path left
  the "both sources carry the same position" theory green: the assertion was an unanchored
  `pipeline\.lua:\(1,\d+\)` (so `.../temp/pipeline.lua:(1,10-11)` matched) plus a `:/ ` check that means
  nothing on POSIX. The repository already pins "a chunk name carrying a path is prefixed with it"
  (`LuaErrorPositionTests.cs:57`), so the guard existed and this fact simply did not use it. Now anchored
  with `StartsWith("zenkins: pipeline.lua:(")` and `^zenkins: pipeline\.lua:\(1,\d+\)`, and the mutation
  reds the inline case while the repository case stays green - a half-red, which is the point.
- **`M2c`, the worker's validation loop had no test at all.** Replacing `WorkerDefinitionValidationLoop`
  with one that reports a canned spec left 58 tests green; `--filter
  FullyQualifiedName~WorkerDefinitionValidation` found zero. Every Validate fact drove a hand-written
  claim/report pair, so AC-6's "runs the definition phase on a Worker" proved the plumbing and the
  rendering and nothing about the sentence itself. `WorkerDefinitionValidationLoopTests` now drives the
  real loop, the real claim/report API, the real `PrepareValidation` and the real
  `PipelineDefinitionRunner`, and asserts that the reported spec carries the claimed script's own name
  and steps (a canned spec fails there) plus that the `validations/{id}` scratch tree is gone.
- **`F3` was a real bug, not a gap.** `RunView.CanRebuild/CanReplay` passed `IsFreestyleShape(GitBranch)`
  as `projectIsFreestyle`; an inline run's branch is empty, so the page and the service disagreed and
  both buttons vanished - behind a refusal message claiming "the project's kind changed", which was
  false. The service was right; the page now passes `!InlineDefinition && IsFreestyleShape(GitBranch)`,
  and the Freestyle argument reaching `Refusal` is bit-identical to before, which the eight
  `RebuildReplayTests` confirm.
- **Cross-kind behaviour change, recorded.** Server-side credential assignment had no kind guard at all
  (`GitCredentialService`), only a hidden settings section. Refusing it for inline meant closing the same
  hole for Freestyle; the reviewer grepped every attach call site and fixture and found no fact that
  asserted the old behaviour. Both halves now have facts.
- **Recorded, not fixed** (each in the card's RECORD ledger): inline definition-phase cancellation has no
  own fact (the shared kill token plus F-009's repository fact and the inline timeout fact cover the same
  code); `DefinitionValidationService`'s `Forbidden` still redirects with a code where F-003's rule is a
  hard refusal; `RunDefinitionSources.For`'s script-first branch has no fact, though the claim now carries
  an explicit `definitionSource` so the worker no longer infers; and two loop facts carry a 90-second xunit
  timeout, which is a harness budget rather than a product one.
- **Deliberately out of scope, filed as follow-up:** the run row should store an explicit
  definition-source word instead of inferring Freestyle from an empty branch - F3 was that heuristic's
  first symptom, and every new source would add another exception to it.
- **Counts.** Fast **1287 -> 1310** (+23), Postgres **4 -> 5**, all seven **1338**. The migration fact is
  seed-then-migrate: migrate to `AddGitProbeCapability`, insert a historical row (before the column
  existed), then migrate across this one, which is the only shape that proves historical rows default to
  `repository` instead of becoming inline.
- The reviewer re-applied its own seven mutations on the final tree (7/7 red) and verified the counts and
  the Postgres run independently.
