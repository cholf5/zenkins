---
id: Q-012
title: The F-020/F-021 review proved four behaviours with probes that the suite does not carry
type: test
priority: P2
size: S
status: done
created: 2026-11-16
updated: 2026-11-17
blocked: none
---

## Goal

The behaviours the F-020/F-021 review had to prove with throwaway probes become regression tests in
`tests/`, so the next refactor cannot delete them as dead code.

## Background

Both cards passed review on 2026-11-16 with every criterion ticked, but four of those criteria had
no test behind them; the reviewer wrote probes in its own checkout to establish the truth and the
probes were not kept. Each is a defensive branch or a snapshot semantic, which is exactly the shape
that dies quietly in a refactor.

**The card's line numbers were stale when the work started; every one was re-located before a test
was written.** The table below is what the code says today (verified 2026-11-17), with the review's
first round agreeing on all six:

| AC | Card said | Today | Verdict |
|---|---|---|---|
| 1a | `WebhookService.cs:186-193` | `:189-193` | exact |
| 1b | `WebhookDeliveryService.cs:179-191`, `:342-346` | `:179-185` (guard `:182-185`), `:341-345` | exact / drifted −1 |
| 2 | `RunService.cs:456-459` | `:847-849` (the trigger's snapshot), `:1652` (`BuildQueuedRun`'s write), `:1389` (rebuild passes the original through) | stale by ~+390 |
| 3 | `ProjectService.cs:242-257` | `UpdateAsync:310-325` | **pointed at the wrong method** (`:242-257` is `CreateAsync`) |
| 4 | `ProjectDetail.razor:264-325` | `:334-398` | stale by ~+70 |
| 5 | `ProjectParametersTests.cs:207` | `:207` | exact (and still the only coverage) |
| 6 | `PipelineRunner.cs:942-960` | `:942-960` | exact |

Two consequences the work had to design around, both confirmed in the code:

- **AC-3's card position is a trap.** `CreateAsync` cannot change a kind, so a test written there is
  forever green and covers nothing; and today's update path never produces a *field error* for the
  kind, so "assert it is refused with a kind error" is a falsifiable shape. The fact therefore
  asserts **persisted state**: after an edit that posts a different kind, `Project.Kind` is unchanged
  (both directions), which is what `UpdateAsync:310-325` actually guarantees.
- **AC-1b is unreachable through the product** (creation refuses the kind and the kind is immutable),
  so the fact seeds the webhook row itself. Its branch filter names the pushed branch on purpose and
  its assertions name the outcome itself, so it cannot pass for the unrelated `ignored:branch` reason.
  Measured under M2 (guard deleted): the delivery does not become an ignored branch and does not queue
  a run either — this kind has no default branch, so `WebhookBranchFilter.Matches` throws
  (`ArgumentException: ... (Parameter 'defaultBranch')`) and the request fails. The fact is red either
  way; what it pins is the guard's own outcome.

## Acceptance Criteria

- [x] A Freestyle project cannot be given a webhook, and a delivery to one records
      `ignored:no-repository` without queueing a run — both in `tests/`
      (`WebhookService.cs:189-193`, `WebhookDeliveryService.cs:182-185`, `:341-345`).
      → `WebhookFreestyleGateTests.A_freestyle_project_cannot_be_given_a_webhook` and
      `.A_delivery_to_a_freestyle_webhook_is_recorded_as_ignored_without_queueing_a_run`.
- [x] Editing a Freestyle project's steps after a trigger does not move the already-queued or
      already-running run's snapshot (`RunService.cs:847-849`).
      → `FreestyleRunTests.Editing_the_steps_after_a_trigger_does_not_move_the_queued_or_running_runs_snapshot`.
- [x] `Project.Kind` cannot be changed by an edit that posts a different kind
      (`ProjectService.cs:310-325`).
      → `ProjectCrudTests.An_edit_that_posts_a_different_kind_does_not_change_it`.
- [x] The trigger form renders each parameter input pre-filled with its definition's default, not
      just with a valid field name (`ProjectDetail.razor:334-398`).
      → `ParameterizedRunTests.The_trigger_form_prefills_every_parameter_input_with_its_definitions_default`.
- [x] An out-of-bounds parameter value is refused at the trigger endpoint, not only by the unit-level
      resolver (`ProjectParametersTests.cs:207` is the only coverage today).
      → `ParameterizedRunTests.An_out_of_bounds_value_is_refused_at_the_trigger_endpoint`.
- [x] The script step's on-disk shape is pinned: `temp/step-N.cmd|sh`, UTF-8 **without a BOM**, and
      the `sh -e` / `cmd /d /s /c` invocation (`PipelineRunner.cs:942-960`). The Windows half may be
      asserted at the code level with a recorded reason if no Windows machine is in the loop.
      → `PipelineRunnerTests.A_script_step_lands_in_temp_as_step_N_as_utf8_without_a_bom`,
      `.A_script_step_runs_under_sh_minus_e_so_a_failing_line_ends_the_script`,
      `.The_windows_half_of_the_script_invocation_is_pinned_at_the_source_level`.
- [x] The fast suite stays green and its test count moves by exactly the number of facts added
      (`docs/development.md` §2).

## Subtasks

- [x] Add the four missing facts in the suites they belong to (Webhooks, Runs, Projects, Pipelines).
- [x] Add the bound-violation case to the existing `ParameterizedRunTests` theory.
      (Landed as a separate endpoint-level fact beside the theory rather than as a row of it: the
      theory drives `ProjectParameters.ResolveValues` directly, and the criterion is about the
      *endpoint*, so a row there would have re-proved the unit layer.)
- [x] Update `docs/development.md` §2's counts.

## Evidence

### The nine facts

| AC | Fact | File:line | What it asserts |
|---|---|---|---|
| 1a | `A_freestyle_project_cannot_be_given_a_webhook` | `tests/Zenkins.Tests/Webhooks/WebhookFreestyleGateTests.cs:29` | The settings form refuses a Freestyle project with `webhookError=project-not-pipeline` and creates no webhook row. |
| 1b | `A_delivery_to_a_freestyle_webhook_is_recorded_as_ignored_without_queueing_a_run` | `…/WebhookFreestyleGateTests.cs:60` | A signed push to a (seeded) webhook on that kind answers `202` with `ignored:no-repository`, records that outcome on the delivery and the webhook, and leaves the run table empty. |
| 2 | `Editing_the_steps_after_a_trigger_does_not_move_the_queued_or_running_runs_snapshot` | `tests/Zenkins.Tests/Runs/FreestyleRunTests.cs:107` | A run keeps the spec it froze at trigger time through an edit while it is queued *and* through an edit after it was claimed and accepted; the project's own spec does move (the assertion is not vacuous). |
| 3 | `An_edit_that_posts_a_different_kind_does_not_change_it` | `tests/Zenkins.Tests/Projects/ProjectCrudTests.cs:244` | An edit posting `Kind=Pipeline` over a Freestyle project leaves it Freestyle (and its three repository fields null); the reverse edit leaves a Pipeline project a Pipeline with its repository fields intact. |
| 4 | `The_trigger_form_prefills_every_parameter_input_with_its_definitions_default` | `tests/Zenkins.Tests/Runs/ParameterizedRunTests.cs:250` | The string input carries `value="prefilled-hello"`, the multi-line text's own content is the two-line default, the boolean's *true* radio is the checked one (and false's is not), and the choice's default option is the selected one (and the other is not). |
| 5 | `An_out_of_bounds_value_is_refused_at_the_trigger_endpoint` | `tests/Zenkins.Tests/Runs/ParameterizedRunTests.cs:284` | A hand-built POST one character over `MaxStringValueLength` redirects with `runError=parameter-invalid`, renders the error, and queues no run; a value exactly at the bound is accepted and snapshotted. |
| 6 | `A_script_step_lands_in_temp_as_step_N_as_utf8_without_a_bom` | `tests/Zenkins.Tests/Pipelines/PipelineRunnerTests.cs:364` | Two script steps land as `temp/step-1.sh`/`step-2.sh` (1-based, the platform's extension, never the other platform's), byte-identical to the UTF-8 encoding of the script with no BOM, and the non-ASCII text reaches the child's stdout. |
| 6 | `A_script_step_runs_under_sh_minus_e_so_a_failing_line_ends_the_script` | `…/PipelineRunnerTests.cs:399` | A two-line script whose first line fails exits `1` with `StepFailed` and never prints the second line, while the file still holds both lines — the behaviour only `sh -e` produces. |
| 6 | `The_windows_half_of_the_script_invocation_is_pinned_at_the_source_level` | `…/PipelineRunnerTests.cs:430` | `PipelineRunner.cs` still carries `? "cmd" : "sh"` and `["cmd.exe", "/d", "/s", "/c", scriptFile]`. |

### Why the Windows half is a source-level pin

The facts above were executed on macOS 26.5 (arm64); no Windows host was in the loop for this card.
`ProcessRunner` has no seam that lets a test observe a child's argv, and `ScriptCommand` branches on
`OperatingSystem.IsWindows()`, so the `cmd` branch cannot be executed here at all. The card allows a
code-level assertion in that case: the two literals that make a script step work on Windows (the
extension the file is written with, and `/d /s /c` — `/s` is what makes a quoted path with spaces
safe) are pinned against the product source. The Unix half is proven behaviourally, not by source
text, so the platform that *can* run (`sh -e`) is pinned by its consequence.

### Mutation proofs (9 named mutations, 9 reds, all restored)

Every mutation was applied to a production file that is clean against HEAD, the mutated tree was
built, the named fact was run alone, and the file was then restored with `git checkout --` and
verified by blob hash against `HEAD:<path>`. Log: `artifacts/q-012/mutations.log`, driver:
`artifacts/q-012/mutations.py`.

| # | AC | What was broken | Fact that went red |
|---|---|---|---|
| M1 | 1a | `WebhookService.CreateAsync` stops refusing the kind | `A_freestyle_project_cannot_be_given_a_webhook` |
| M2 | 1b | `WebhookDeliveryService` loses its Freestyle guard (the push would be queued) | `A_delivery_to_a_freestyle_webhook_…` |
| M3 | 2 | saving the steps also rewrites every existing run's `PipelineSpecJson` | `Editing_the_steps_after_a_trigger_…` |
| M4 | 3 | `UpdateAsync` writes the kind the form posted | `An_edit_that_posts_a_different_kind_…` |
| M5 | 4 | the string input renders `value=""` instead of the default | `The_trigger_form_prefills_…` |
| M6 | 5 | the trigger ignores `ResolveValues`' refusal and queues anyway | `An_out_of_bounds_value_is_refused_at_the_trigger_endpoint` |
| M7 | 6 | `["/bin/sh", "-e", scriptFile]` → `["/bin/sh", scriptFile]` | `A_script_step_runs_under_sh_minus_e_…` |
| M8 | 6 | `step-{stepIndex + 1}` → `step-{stepIndex}` | `A_script_step_lands_in_temp_as_step_N_…` |
| M9 | 6 | `/d` → `/q` in the Windows invocation | `The_windows_half_…` |

Every restore ended with `hash-object` equal to `HEAD:<path>` (recorded per mutation in the log).
Two of the reds are worth naming so a second-round replay can recognise them: **M2** fails as a
thrown `ArgumentException` out of `WebhookBranchFilter.Matches` (a Freestyle project has no default
branch, so the delivery cannot even reach an "ignored" decision without its guard), and **M8**
fails on the fact's own message (`the second script step's file is missing: …/temp/step-2.sh`) rather
than on an equality assertion.

### Counts

- Baseline measured before the work, on this tree: **fast 1270 passed / 0 failed / 0 skipped**
  (`artifacts/q-012/fast-baseline.log`, `dotnet test -c Release --no-build --filter …`, 5m 59s).
- After the nine facts: **fast 1279**, counted three times — `artifacts/q-012/fast-new.log` and
  `fast-final.log` (the run on the final tree, 5m 37s); each is 1279 passed / 0 failed / 0 skipped — i.e.
  1270 + 9, exactly the number of facts added. No existing test was rewritten, tightened or deleted for this card (the one
  harness change, `PipelineRunHarness`'s frozen-spec constructor, is infrastructure: it adds no test).
- `docs/development.md` §2 re-baselined: fast **1270 → 1279** (a counted run), all seven
  **1296 → 1305** (the fast count plus the other six suites' figures — arithmetic, not a second
  seven-suite run), and the §2 table's fast row 1242 → 1279.

## Dependencies

- none (F-020 and F-021 are accepted)

## Notes and Decisions

- The review's own probe source lived only in its `/tmp` checkout and is deliberately not pasted
  here; each item above names the production file and line the probe exercised, which is what the
  test needs.
- AC-1b seeds its webhook row: the product path cannot create one for that kind, so a fact that went
  through the form would test the refusal twice and never the delivery branch.
- No production code was changed for this card. The line drift recorded above was left in the code
  (renumbering a card is cheap; touching a reviewed file for a comment is not).
- The three script-step facts each need a *runtime* to observe; `PipelineRunHarness` gained a
  `frozenSpecJson` constructor so a Freestyle run (no repository, no clone, no definition phase) runs
  through the same real `PipelineRunner` the worker uses.

## Verification

Independent review PASSed 7/7 criteria. It did not take the mutation log on trust: it re-applied its
own twelve mutations to production code, each one restored and hash-verified, and **12/12 went red**.

- **Ground truth first.** Before reading any implementation, the reviewer located all six behaviours in
  today's production code. All six hold, but the card's line references were wrong in four places, and
  one was a wrong *method*: AC-3 pointed at `CreateAsync`, while the behaviour lives in `UpdateAsync`
  (310-325). A test written where the card pointed would have been green forever and covered nothing.
  The drift table is recorded in Background; AC-3 is asserted as persisted state, not as an error.
- **The false-green risks it had named in round 1 are closed, and it proved that by making them red.**
  AC-1b: with the seeded branch filter unable to match the pushed branch, the fact still passes (it
  does not lean on the branch path), and with the same unmatchable filter *and* the guard removed it
  goes red - so it can neither lean on `ignored:branch` nor be green for that reason. AC-4: three
  mutations of three different input kinds each go red, because each assertion is scoped to its own
  element by id rather than to a field name.
- **The mutations that matter are the ones a weaker fact would have survived.** A guard that records
  `IgnoredBranch` instead of `no-repository` goes red, so the fact pins the string rather than merely
  "not queued"; flipping `>` to `>=` goes red on the *at-bound accepted* half, which a one-sided fact
  would have missed; adding a trailing newline goes red on the byte comparison while the script still
  runs - the reviewer added that one because a BOM-only mutation dies earlier on the child's exit code
  and never reaches the bytes.
- **Counts.** Fast **1279** = 1270 + 9, counted twice by the reviewer (5m38s, 5m47s). `[Fact]` 963 ->
  972, theories and inline data unchanged, no `[Fact(Skip=)]`, and the only two deletions in the whole
  test delta are the harness's nullable-repository lines. `docs/development.md` §2 carries 1279 in its
  table and `1279 / all seven 1305` in the note, with 1305 explicitly declared as arithmetic on 1296.
- **A finding about the product, not the test.** Removing the Freestyle delivery guard does not degrade
  to `ignored:branch` - it answers **500**, thrown out of `WebhookBranchFilter.Matches` because a
  Freestyle project has no default branch. Reproduced independently. That guard is load-bearing rather
  than decorative, and a 5xx is the exact shape F-028's design avoided (a git host retries, then
  disables the hook). It makes the fact more valuable, not weaker.
- **Stated, not fixed.** AC-2 stops at the run row although `claim.PipelineSpecJson` is parsed after the
  edit - one line from the worker-facing half. The two pre-existing weak assertions in
  `ParameterizedRunTests` (`:213-216` field names, `:223` a bare `selected`) were left in place: the
  count stays honest, but they remain a weaker sibling to the new facts. The Windows branch uses
  `SkipException.ForSkip`, the right mechanism, but nothing on this machine can execute it, so "reports
  as skipped, not failed" is unverified on a Windows runner. `scripts/verify.sh` was run by neither the
  implementer nor the reviewer; AC-7 did not require it.
- **Independence, recorded honestly.** The implementer read the reviewer's criteria document, so its
  alignment with that list is consumption rather than corroboration. The reviewer judged every fact by
  a production mutation and never by whether it matched its list, and noted two places where the
  implementation went past what was asked (claiming and accepting the run to cover the *running* half;
  asserting the other platform's extension is absent).
- Logs: `artifacts/q-012/` (both sides) - `mutations.log`, `review-mutations.log`, `review-verdict.log`,
  `review-fast*.log`, `review-facts.log`, `review-tree-snapshot.log`.
