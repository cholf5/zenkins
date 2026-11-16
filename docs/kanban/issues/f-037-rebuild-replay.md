---
id: F-037
title: Rebuild and replay — re-run a finished run with the same inputs, or with the current definition
type: feature
priority: P2
size: S
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A Member can re-run a finished run in one click, either exactly as it was (same
commit, parameters, tags, timeout and frozen definition) or against the project's
current definition, and the new run says which one it is.

## Background

Retrying a flaky failure today means re-entering the trigger form: pick the branch
again, retype the parameters, rediscover that the commit has moved in the meantime.
Jenkins has the Rebuild plugin for exactly this, and "Replay" for re-running with the
same script after an edit.

Two different intents have to stay distinct, because mixing them silently rebuilds
different code:

- **Rebuild** = the same inputs, including the **commit pinned by the original run**
  (F-017 already pins a commit; a rebuild must pin it again, not take the branch head).
- **Replay** = the same definition text, no matter what the project or repository says
  now (for an inline project, the run's own script snapshot).

## Acceptance Criteria

- [x] The run page offers Rebuild (same inputs) and Replay (same definition), each
      producing a new run whose page links back ("rebuilt from #N") and whose
      `TriggerType` is `Rebuild`/`Replay`.
      *Both buttons are POST forms on the run page, rendered only when `RunReruns.Refusal` - the same
      function the service refuses with - returns null. The new run's page prints "This run is rebuilt
      from #N" (or "replayed from #N"), linked to the original's permalink, with the short id as the
      fallback when the original's row is gone. Pinned on the rendered page, not only in the rows (review
      M3):
      `A_rebuild_pins_the_original_commit_while_a_fresh_trigger_takes_the_branch` fetches the new run's
      page and asserts the `run-rebuilt-from-link` element, its `#1` text and its
      `/projects/{id}/runs/1` href;
      `A_replay_reruns_the_runs_own_definition_and_a_repository_pipeline_cannot_be_replayed` asserts the
      "replayed from" line on the replay's page and, on the repository pipeline's page, the
      `run-replay-unavailable` control with `disabled` and the "not the source that produced it" tooltip
      (plus the presence of the Rebuild form beside it).*
- [x] Rebuild copies the original snapshot: branch, pinned commit, parameters, required
      tags, timeout, and the definition the original ran (a Freestyle project whose
      steps changed since must still rebuild the old steps).
      *The new run is built from the original's row: `RequiredTags`, `GitBranch`,
      `PipelinePath`, `TimeoutSeconds`, `GitCommit` (for a repository run) and
      `PipelineSpecJson` (for a Freestyle run). TC-002 is pinned by
      `RebuildReplayTests.A_rebuild_runs_the_original_freestyle_steps_after_the_project_changed`
      (rebuild gets the old spec, a fresh trigger gets the new one, both through the claim).*
- [x] Replay uses the original definition text only: for an inline project or a
      Freestyle project the run's own snapshot; for a repository pipeline the run's
      spec cannot be replayed as source, so Replay is offered only where a script text
      exists and the button's tooltip says why it is absent otherwise.
      *F-022 (inline scripts) does not exist, so today the only definition-as-text run is a
      Freestyle run: Replay is offered there and `replay-unavailable` is refused for a
      repository pipeline, with a disabled button carrying the reason as its tooltip and the
      same sentence on the refused redirect. Both halves are asserted on the rendered pages (review M3):
      the repository pipeline's page carries the disabled control and its tooltip text, and the refused
      POST's redirect carries the code and the page it lands on repeats the sentence.*
- [x] A rebuild of a run whose commit no longer exists fails honestly at clone time
      (`git-*` failure reason) rather than silently building the branch head.
      *The claim pins the commit (server half, `RebuildReplayTests`); the worker's pin failure
      is `git-failed` with the commit named in the run log, and the branch head is never built -
      `RebuildPinTests.A_vanished_commit_fails_with_a_git_reason_and_the_branch_head_is_not_built`.
      The same file's other fact proves the pinned commit is what gets built after the branch
      moved, with a fresh trigger taking the tip in the same repository and moment. The run
      page explains a `git-*` failure of a rebuild by naming the pinned commit and `#N`.*
- [x] Disabled projects (F-028), queue caps (F-029) and concurrency limits apply to
      rebuilds exactly as to a fresh trigger.
      *A rebuild is built and then handed to `QueueStagedRunAsync` - the same project row
      lock, the same `ProjectTriggerGuard`, the same insert. TC-004 is pinned by
      `RebuildReplayTests.The_projects_gate_refuses_a_rebuild_exactly_as_it_refuses_a_fresh_trigger`
      (`queue-full` with nothing written, `project-disabled` after a disable, and a rebuild that
      works again once re-enabled). `MaxConcurrentRuns` is enforced at claim time on the same
      rows, so it cannot distinguish a rebuild from anything else.*
- [x] Parameters are editable before a rebuild ("Rebuild with parameters…") — one
      form, defaults prefilled from the original, the same validation.
      *A `<details>` form on the run page, inputs prefilled from the run's snapshot, posting
      `Parameters[NAME]` to the same route. The service merges the overrides onto the
      original's values and re-resolves them with `ProjectParameters.ResolveValues` - the
      trigger form's own routine - so a removed name is `parameter-unknown` and a retyped value
      `parameter-invalid`. Pinned by
      `RebuildReplayTests.Rebuild_with_parameters_prefills_the_originals_values_and_resolves_overrides`.*
- [x] Artifacts, logs and steps of the original are never modified by a rebuild.
      *A rebuild is a new row; nothing writes to the original. Pinned by
      `RebuildReplayTests.A_rebuild_leaves_the_originals_artifacts_logs_and_steps_untouched`
      (the original's step and log rows unchanged, the rebuild holding none).*

**Partial, and stated as such:** the subtask's "re-validate it against today's project" is
implemented for the checks the run's own snapshot can support - kind unchanged
(`rebuild-kind-changed`), a readable Freestyle definition (`rebuild-spec-unavailable`), a pinned
commit for a repository run (`rebuild-no-commit`), the project's assigned credential still
resolving (`rebuild-credential-missing`), and today's parameter definitions still accepting the
values. Two of the subtask's words are **not** implemented as refusals, because the run does not
snapshot the facts: *whether the repository URL changed* and *whether a credential was assigned
when the original ran* are not recorded on `BuildRun`, so a rebuild cannot compare them. A
cleared or rotated credential is therefore answered by the clone (`git-credential-not-assigned` /
`git-auth-rejected`, which the run page already explains), and a changed repository URL is answered
by the pinned commit not existing (`git-failed`). Snapshotting those two fields is a follow-up, not
a silently-claimed AC.

## Subtasks

- [x] `BuildRun.RebuiltFromRunId` (nullable, no FK) + trigger types `Rebuild`/`Replay`.
- [x] `RunService` rebuild path: read the original's snapshot, re-validate it against
      today's project (kind unchanged, credential still assigned, parameter definitions
      still containing the names), then queue through the same create path.
      *(See the partial note above: "credential still assigned" is implemented as "the assigned
      credential still resolves"; the two facts the run does not snapshot are answered by the
      clone.)*
- [x] UI: two buttons on the run page (Rebuild enabled only for terminal runs), the
      optional parameter form, and the "rebuilt from" line.
- [x] Log/run page wording for "the commit this rebuild pinned is no longer available".
      *`run-rebuild-commit-notice` on the failure card: it names the pinned commit, the `#N` it
      came from, that the remote could not provide it, and that the branch head is deliberately
      not built in its place.*
- [x] PRD §3.5: record both intents and why they are separate buttons.

## Dependencies

- F-036 (numbers) for the "from #N" link (a guid fallback is acceptable) - **landed**, and the
  rebuild's page link is the number permalink.
- F-017 (pinned commit semantics) — accepted, and reused unchanged: a rebuild is a trigger that
  already names its commit, exactly like a webhook run.

## Test Cases

### TC-001: Rebuild pins the same commit even after the branch moved

Steps:
1. Build run `#1` at commit A.
2. Move the branch head to B; rebuild `#1`.

Expected:
- The new run's claim pins A; its branch and parameters match `#1`.
- A plain fresh trigger at that moment would have used B (asserted in the same test).

### TC-002: Rebuild survives a definition change

Steps:
1. Freestyle project with steps S1; run it.
2. Change the steps to S2; rebuild the old run.

Expected:
- The rebuild executes S1 (the original snapshot) and the page says so; a fresh
  trigger executes S2.

### TC-003: A vanished commit fails honestly

Steps:
1. Build at commit A, then rewrite history so A is gone; rebuild.

Expected:
- The rebuild fails with a `git-*` failure reason naming the missing commit; it does
  not build the branch head.

### TC-004: Limits still apply

Steps:
1. Fill the queue to the cap (F-029), then rebuild.

Expected:
- The rebuild is refused with the same `queue-full` message a fresh trigger gets.

## Notes and Decisions

- Replay for a repository pipeline is deliberately absent: the run stores the frozen
  spec, not the source that produced it, and pretending the spec can be re-executed as
  a definition would erase the definition phase.

## Implementation notes (2026-11-16)

- **One insert path, so the gate cannot be bypassed.** `RunService.RebuildAsync`/`ReplayAsync` share
  a private `RecreateRunAsync` that builds the new `BuildRun` from the original's snapshot and hands it
  to `QueueStagedRunAsync` - the same routine the form, the API and the webhook use. The only thing the
  refactor needed was to extract the "open the transaction, stage, commit, log" tail of
  `CreateQueuedRunAsync` into `QueueStagedRunAsync`. A rebuild is therefore an ordinary queued run
  from the gate's point of view, and F-036 numbers it like any other.
- **Two intents, two tables of inputs.** Rebuild: the original's `RequiredTags`, branch, pipeline path,
  timeout, pinned `GitCommit`, and (Freestyle) its frozen spec. Replay: the same, but the definition is
  *always* the run's own snapshot and is offered only where that snapshot is a definition (a Freestyle
  run today). A repository pipeline's replay is `replay-unavailable`.
- **One predicate decides the buttons.** `RunReruns.CanRebuild`/`CanReplay` back both the page's
  rendering and the service's acceptance, so a button cannot be offered for a request that would be
  refused (and the absent Replay button carries the reason as its tooltip).
- **Refusals ride the existing PRG vocabulary.** `RunOperationStatus.Refused` with
  `RunFields.Rebuild` and a stable `rebuild-*`/`replay-*` code goes back as
  `?rebuildError=<code>`/`?replayError=<code>`; the run page turns every code into a sentence. Parameter
  errors reuse the trigger form's own codes (`parameter-unknown`, `parameter-invalid`).
- **Antiforgery stayed where it was.** Both routes bind a form model (empty for Replay, optional
  parameters for Rebuild). Because Rebuild's form is legitimately empty, a null model alone is not a
  refusal: `HasUnboundFieldAsync` looks for a non-antiforgery field the model failed to bind, which is
  the only case where a body arrived and could not be read. A tokenless POST is refused by the model
  binder (400) before the handler runs - pinned by `A_tokenless_rebuild_is_refused_before_it_can_queue_anything`.
- **The original is only ever read.** No statement in either path writes to the original run or its
  steps/logs/artifacts.

### Verification (2026-11-16)

- `dotnet build Zenkins.slnx -c Release`: 0 warnings, 0 errors (`artifacts/logs/f-036-f-037-build.log`).
- The gate (`scripts/verify.sh`'s commands and filters, one suite at a time): fast **1165**, Milestone
  **12**, Recovery **4**, PublishSmoke **2**, PublishAot **2**, GitSshd **3**, Postgres **2** (a live
  throwaway PostgreSQL 16.15 named by `ZENKINS_TEST_POSTGRES`; both facts skip with the recorded reason
  when it is unset - verified); all seven **1190**. Logs:
  `artifacts/logs/f-036-f-037-fast.log`, `artifacts/logs/f-036-f-037-gate.log`.
- `dotnet ef migrations has-pending-model-changes` (PostgreSQL provider): **no pending changes** -
  `RebuiltFromRunId` is in the F-036 migration and the snapshot agrees with the model.
- This card's own tests: `RebuildReplayTests` (8) and `RebuildPinTests` (2) - green in the fast suite.
  The two page assertions review M3 asked for are inside those facts (see AC1/AC3 above).

### Not covered, and why

- **A full end-to-end rebuild through a real worker process** is not in the fast suite: the server half
  (the claim pins the original commit, the spec, the gate and the numbering) is tested through the real
  HTTP surfaces, and the worker half (the pin really wins after the branch moved; a vanished commit
  fails as `git-failed` and never builds the tip) through `PipelineRunner` against a real local
  repository. The Milestone harness would be the next layer, at process cost.
- **The `MaxConcurrentRuns` half of AC5** is not tested *for a rebuild specifically*: it is decided at
  claim time on rows that cannot tell a rebuild from a fresh run, so a rebuild-specific test would pin
  the shared claim path twice.

## Review round 1 (2026-11-16) - the fixes

- **M3 - "Pinned by …" now means pinned.** Two AC evidence sentences claimed the page behaviour was
  pinned by tests that never fetched a page. The behaviour was right; the claim was not. Both facts now
  GET the new run's page and assert the elements (the `rebuilt from` link's text and href, the disabled
  Replay control's presence, `disabled` attribute and tooltip sentence), so a regression in the Razor
  markup is red.
- **L1 - one predicate, for real.** `RunService.RecreateRunAsync` used to carry its own copy of the
  refusal order while `RunView.CanRebuild`/`CanReplay` used `RunReruns`; the two could drift. Both sides
  now call `RunReruns.Refusal(...)`, which returns the refusal (code + sentence) or null, and the page's
  `CanRebuild`/`CanReplay` are `Refusal(...) is null`. The service passes the project's current kind, the
  page passes the run's own shape, so the kind check can only be *stricter* in the service - never the
  other way round. The two remaining service-only checks (a resolvable Git credential, today's parameter
  definitions) are documented at the function as the one direction the drift can still take: a rendered
  button whose click is refused with a complete sentence.
- **L3 - the vanished-commit approximation is stated.** `RebuildPinTests` uses a commit that was never in
  the repository, not one that was rewritten away; the code path is identical (`checkout()` can only answer
  "the object is not here"), and the comment now says so and why the real rewrite (force-push +
  `gc --prune=now`) was not used.
- **L5 - the permalink line reads `#12`.** The card's motivation is a name a person can say out loud, so
  the visible text is the number; the permalink is the link's `href`, and the card's "rebuilt from #N" link
  already rendered that way.
- **L2 - the display sweep is asserted.** The dashboard's recent runs/failures and the project overview
  are covered by `BuildNumberTests.The_dashboard_and_the_project_pages_name_a_run_by_its_number` (F-036's
  card records it).
- **L4 - the backfill's SQLite premise is written down.** On SQLite `CreatedAt` is TEXT, so the backfill's
  ordering is lexical; every row the product writes is UTC in one spelling, which makes lexical order
  chronological, and the migration comment now states that premise instead of leaving it implicit.
- **Still open, as a follow-up (not claimed):** snapshotting the credential assignment and the repository
  URL on `BuildRun` would let a rebuild refuse up front when either changed. The run does not store them
  today, so those two facts are answered by the clone (`git-credential-not-assigned`/`git-auth-rejected`,
  `git-failed`), which the run page explains.

### 2026-11-16 — independent review: PASS

Two rounds in an isolated copy of the batch. All seven criteria satisfied; the submissions of two
criteria were corrected rather than the behaviour.

- Rebuild really pins the original commit: the test moves the branch head and asserts the rebuild
  claims the **old** commit while a simultaneous fresh trigger would take the tip — not merely that
  two `GitCommit` fields are equal. A commit that cannot be prepared fails honestly as `git-*` with
  the commit named in the log and no step artifacts produced, because the runner checks out the
  expected commit after cloning the branch and never falls back to the branch head.
- Rebuild runs the original snapshot even after the definition changed (both directions asserted:
  the rebuild claims the old spec, a fresh trigger claims the new one), and it goes through the same
  `QueueStagedRunAsync`, so F-028's disabled gate and F-029's queue cap apply — queue-full and
  project-disabled both refuse with zero rows written, and re-enabling restores the ability.
- The original run's artifacts, steps and logs are untouched; the new run starts empty.
- Two criteria were first claimed as "pinned" by tests that never fetched a page. They now do: the
  rebuilt run's page carries `run-rebuilt-from-link` with the exact href and `#1`, the pipeline run's
  page carries `run-replay-unavailable` with its disabled state and tooltip, and a replayed run's
  page says "This run is replayed from".
- One predicate now really is one: the page's buttons and the server's refusal both call
  `RunReruns.Refusal`. Two checks necessarily read today's project (credential still resolvable,
  parameter definitions still accept the old values), and the card says so instead of claiming a
  single closure point; the drift they allow is button-present-but-refused-with-a-complete-sentence.
