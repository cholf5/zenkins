---
id: Q-022
title: The F-023 review proved four branch-discovery properties by reading code, and the suite does not carry them
type: test
priority: P2
size: S
status: done
created: 2026-11-17
updated: 2026-11-17
blocked: none
---

## Goal

Four properties F-023's review had to establish by reading the code become facts in `tests/`, so the
next change to branch discovery cannot undo them silently.

## Background

F-023 passed after two review rounds, and the reviewer closed it with four minor findings it
deliberately left out rather than inflating the card. Each one is a shape that will not fail loudly:

- **`WebhookOutcomes` has no `All` and no rule fact.** The set is a hand-maintained list of constants and
  F-023 added `ignored:branch-too-long` to it, but nothing asserts that the set is complete or that the
  values written to `WebhookDeliveries.Outcome` come from it. A misspelled outcome is stored and
  rendered on the settings page as-is; the column is deliberately open (it also carries run validation
  codes such as `branch-invalid`, which is why F-023's review accepted no check constraint there), so the
  vocabulary has to be held by a test rather than by the database.
- **"One probe per project per tick" has no fact.** The reviewer's mutation that moved the probe inside
  the per-branch loop stayed green. The code is structurally protected today — I-005's registry
  serialises per `(project, repository)` with a 15 s window — but a single probe can wait up to 10 s, so
  three or more new heads in one tick produce genuine duplicate git sessions with nothing to warn.
- **Case sensitivity is pinned only at the database.** A PostgreSQL fact proves `Main` and `main` coexist
  as rows, and the reviewer's mutation that lower-cased branch names in the service stayed green: the
  in-memory dictionary, the ordering and the run filter are all Ordinal by intent and none of them has a
  case-differing fact.
- **Two documents state more or less than their facts.** The Q-006 surface row for
  `/api/workers/definitions/{id}/git-credential` claims credentials cannot be fetched across id types;
  that holds by construction (run, probe and definition ids live in three disjoint stores) but no fact
  proves it. And the outcome enumeration in `WebhookEndpoints` is already missing
  `ignored:no-repository`, and never mentions `awaiting-observation`, `no-pipeline` or
  `ignored:branch-too-long` — the list a client reads is behind the set it describes.

## Acceptance Criteria

- [x] A fact holds the webhook outcome vocabulary: every value the delivery path writes is a member of
      `WebhookOutcomes` (an `All` collection or an equivalent rule), and adding a value to the enum
      without the set fails.
      → `WebhookOutcomes.All` + `WebhookOutcomes.Require` as the delivery path's write barrier;
      `WebhookOutcomeVocabularyTests.The_webhook_outcome_vocabulary_is_exactly_the_declared_constants`
      (reflection: the constants and the set are equal both ways, no duplicates, every value fits
      `MaxLength`) and `.The_delivery_path_only_stores_outcomes_the_vocabulary_declares` (seven real
      deliveries, each stored outcome a member and the expected constant).
- [x] A fact pins "one probe per project per tick": a tick with several new heads issues a single probe,
      by counting invocations rather than by reading the structure.
      → `BranchObservationTests.One_tick_with_several_new_heads_asks_for_one_probe`: one sweep tick, four
      new heads answered by one probe report, and the registry's `RequestCount` (how many callers one
      probe served) is 1.
- [x] A case-differing fact pins the service-side Ordinal behaviour — a dictionary, an ordering and a run
      filter each distinguish `Main` from `main` — so the database fact is no longer the only guard.
      → `BranchCaseSensitivityTests.Discovery_keeps_heads_that_differ_only_in_case`,
      `.The_branch_list_orders_names_ordinally_so_Main_and_main_sit_apart`,
      `.The_runs_filter_distinguishes_branches_that_differ_only_in_case`, and the review's follow-up
      `.The_branch_list_gives_branches_that_differ_only_in_case_their_own_last_run` (the registry read's
      own two folds - four surfaces, not three).
- [x] The Q-006 row's cross-type credential claim is backed by a fact (a definition id cannot fetch a
      run's or a probe's credential and vice versa), or the row is narrowed to what is proven.
      → **proved, not narrowed**: `BranchObservationTests.A_definition_lease_opens_no_run_or_probe_credential_and_neither_opens_the_definitions`
      holds all three leases (run, probe, definition) at once and shows each id opens exactly its own door.
- [x] `WebhookEndpoints`' outcome list is brought up to date with the set, and a fact keeps it that way
      rather than relying on the next reader to notice.
      → the response site now names all ten members (it was missing `ignored:no-repository` and never
      mentioned `awaiting-observation`, `no-pipeline` or `ignored:branch-too-long`), the second hand list
      in the record's doc was collapsed into a reference,
      `WebhookOutcomeVocabularyTests.The_endpoint_documentation_names_every_outcome_the_vocabulary_declares`
      compares the file's tokens with the set in both directions.
- [x] The fast suite stays green and its count moves by exactly the number of facts added.
      → `--list-tests` under the sextuple fast filter: **1368 → 1377**, i.e. +9 for nine facts.

## Subtasks

- [x] Give `WebhookOutcomes` an `All` and rule the delivery path against it; back the endpoint list with
      the same source. (`All`, `IsKnown`, `Require`; `WebhookDeliveryService.ClaimAndQueueAsync` requires
      both the classified outcome and the project-gate refusal code through it.)
- [x] Count probes per tick in the discovery sweeper's seam. (`BranchSweepResult` already reported the
      tick; the count is the probe registry's `RequestCount`, which the sweep's own `DiscoverAsync` call
      moves once per project.)
- [x] Add the Ordinal case-differing facts for dictionary, ordering and filter. (Four facts: the review's
      follow-up added the branch list's own run/record folds.)
- [x] Prove or narrow the cross-type credential claim. (Proved; see the AC-4 line and Notes.)

## Evidence

### The eight facts

| AC | Fact | File | What it asserts |
|---|---|---|---|
| 1 | `The_webhook_outcome_vocabulary_is_exactly_the_declared_constants` | `tests/Zenkins.Tests/Webhooks/WebhookOutcomeVocabularyTests.cs` | Every public `const string` on `WebhookOutcomes` is a member of `All` and vice versa, no two spell the same word, each fits the 32-char column, `Require` accepts each member and throws for an unknown/empty/null value. |
| 1 | `The_delivery_path_only_stores_outcomes_the_vocabulary_declares` | same file | Seven deliveries through the real endpoint (queued, ignored:branch, ignored:delete, ignored:not-branch, awaiting-observation, no-pipeline, ignored:branch-too-long) store exactly the expected constants, and every stored outcome is a member of `All`. |
| 5 | `The_endpoint_documentation_names_every_outcome_the_vocabulary_declares` | same file | Reads `src/Server/Webhooks/WebhookEndpoints.cs`: every member of `All` is named, and no documented outcome-shaped token is outside `All` except `duplicate` (the replay *status*, never a stored outcome). |
| 2 | `One_tick_with_several_new_heads_asks_for_one_probe` | `tests/Zenkins.Tests/Branches/BranchObservationTests.cs` | One `SweepOnceAsync` tick with four new heads: four branch rows registered, exactly one probe for the project, `RequestCount == 1`. |
| 3 | `Discovery_keeps_heads_that_differ_only_in_case` | `tests/Zenkins.Tests/Branches/BranchCaseSensitivityTests.cs` | A probe answering `refs/heads/Main` + `refs/heads/main` registers two rows with their own pending commits and queues two tree reads (`Heads/Registered/Awaiting` = 2/2/2). |
| 3 | `The_branch_list_orders_names_ordinally_so_Main_and_main_sit_apart` | same file | Rows `Main`, `main`, `a`, `B` come back as `B`, `Main`, `a`, `main` — an ordinal sort, which a culture or case-insensitive sort cannot produce. |
| 3 | `The_runs_filter_distinguishes_branches_that_differ_only_in_case` | same file | Runs on `Main` and `main`: each filter returns only its own run, and the unfiltered list holds both. |
| 3 | `The_branch_list_gives_branches_that_differ_only_in_case_their_own_last_run` | same file | Two registry rows `Main`/`main`, each with one run: the two views' `LastRun.RunId` differ, and each is its own run's id. A case-insensitive fold keeps both branches listed but hangs the higher-numbered run on both. |
| 4 | `A_definition_lease_opens_no_run_or_probe_credential_and_neither_opens_the_definitions` | `tests/Zenkins.Tests/Branches/BranchObservationTests.cs` | With a run, a probe and a definition task all leased to one worker: each own route answers 200, and all four crossings (`runs`/`probes` with the definition id, `definitions` with the run id, `definitions` with the probe id) are bodyless 404s. |

### Mutation proofs (9 named mutations, 9 reds, all restored)

Every mutation was applied to a production file, the tree was built, the named fact was run alone, and
the file was restored from a copy taken before the edit (`git status` after the last restore shows only
this card's own files).

| # | AC | What was broken | Fact that went red |
|---|---|---|---|
| M1 | 1 | a `ProbeFailed = "probe-failed"` constant added to `WebhookOutcomes` without adding it to `All` | `The_webhook_outcome_vocabulary_is_exactly_the_declared_constants` (collections differ) |
| M2 | 1 | the `BranchTooLong` assignment replaced by the literal `"ignored:too-long"` | `The_delivery_path_only_stores_outcomes_the_vocabulary_declares` (`ArgumentOutOfRangeException` out of `Require`, and the response is no longer `Accepted`) |
| M3 | 5 | `ignored:no-repository` removed from the endpoint's list | `The_endpoint_documentation_names_every_outcome_the_vocabulary_declares` (`… does not name the outcome 'ignored:no-repository'`) |
| M4 | 2 | the review's shape: a `probes.RequestAsync` call per head inside the registration loop | `One_tick_with_several_new_heads_asks_for_one_probe` (`RequestCount` expected 1, actual 5) |
| M5 | 3 | the discovery head->commit dictionary switched to `StringComparer.OrdinalIgnoreCase` | `Discovery_keeps_heads_that_differ_only_in_case` — red on `Registered` (expected 2, actual 1); `Heads` stays 2 because it counts the separate Ordinal `headNames` set, which this mutation does not touch |
| M6 | 3 | the branch list's ordering switched to `StringComparer.OrdinalIgnoreCase` | `The_branch_list_orders_names_ordinally_so_Main_and_main_sit_apart` (expected first `B`, actual `a`) |
| M7 | 3 | the runs filter lower-cases both sides (`GitBranch.ToLower() == branch.ToLower()`) — the review's own mutation | `The_runs_filter_distinguishes_branches_that_differ_only_in_case` (the `Main` filter returns both runs) |
| M8 | 4 | the definition material route falls back to the run material fetch for an unknown definition id | `A_definition_lease_opens_no_run_or_probe_credential_and_neither_opens_the_definitions` (`/api/workers/definitions/{runId}/git-credential answered 200`) |
| M9 | 3 | `BranchService.GetBranchesAsync`'s `lastRunByBranch` switched to `StringComparer.OrdinalIgnoreCase` (the review's finding: this one stayed green across all 29 Branches facts) | `The_branch_list_gives_branches_that_differ_only_in_case_their_own_last_run` — `Assert.NotEqual(upper.LastRun.RunId, lower.LastRun.RunId)` fails: both branches carry the same (higher-numbered) run id |

### Counts

- `--list-tests` under the sextuple fast filter (`Category!=Milestone&…&Category!=Postgres`), measured on
  this tree: **1368 before** (at branch point `31e4fc5`) → **1377 after** — +9, exactly the number of
  facts added (eight, plus the review's follow-up fact). No existing test was edited for this card.
- Targeted runs on the macOS host (the full fast suite and `Category=Postgres` belong to the Windows
  machine): `Zenkins.Tests.Branches` (30 total, 29 passed, 1 pre-existing skip), `Zenkins.Tests.Webhooks`
  (85 passed) and the new facts each on their own.
- `dotnet build -c Release`: 0 warnings / 0 errors.

## Dependencies

- none (F-023 is accepted; I-005 and F-017 provide the surfaces)

## Notes and Decisions

- Same shape as Q-012: these are evidence debts, not defects. Nothing here is known to be broken; what is
  missing is the fact that would catch the next person who breaks it.
- Not to be folded into F-023: that card was already L and its reviewer drew the line explicitly.
- **AC-4: proved, not narrowed.** The row's claim ("a definition id cannot fetch a run's or a probe's
  material and neither can fetch this one") is true by construction - three disjoint stores - but
  "by construction" is exactly what a suite cannot read. Narrowing the row would have been cheaper, yet
  it would have left the one property that makes the three material routes safe unproven: the ids are
  interchangeable at the *route* level, so a future shared lookup would silently widen one of them. The
  fact therefore holds all three leases at once, which is what makes a 404 meaningful: a 200 elsewhere
  would otherwise be indistinguishable from "that id was never live here". Cost: one fact and one helper.
- **The endpoint list is checked against the set, not generated from it.** Comments and XML docs cannot
  hold a runtime value, and the product renders no list at all (the settings page shows the *stored*
  outcome), so there is no place to derive a sentence from `All` at runtime without changing the wire or
  the UI - both out of scope for a test card. The fact reads the endpoint file and compares its named
  outcomes with `All` in both directions, which is the strongest form available: a value added to the set
  without the sentence, or a sentence naming a value the set does not declare, both go red. The second
  hand list (the record's doc comment) was collapsed into a reference to `All` so there is only one list
  left to check.
- **The stored column stays open.** `WebhookDeliveries.Outcome` also carries run-level refusal codes; a
  check constraint would be the wrong place, which is why the vocabulary is a set plus a write barrier
  rather than a constraint. The barrier is `WebhookOutcomes.Require` at the two places the delivery path
  assigns an outcome (the classified value and the project-gate refusal), so an invented word fails at
  the writer instead of appearing on the settings page.
- **`duplicate` is not an outcome.** It is the *status* a replay answers with, and the stored record keeps
  the original outcome, so it is deliberately outside `All` and is the one token the endpoint fact allows
  beside the set.
- **`All` can declare a member nothing writes (recorded, not fixed).** The reflection fact holds *set =
  constants*; it says nothing about *set = reachable*. A member added to both the type and `All` with no
  delivery path ever assigning it would be green, and it would widen the vocabulary a client is told about
  without widening what the product does. Making that a fact needs an exhaustive reachability proof
  (every member driven through the delivery path, or a source-level "this constant is assigned somewhere"
  scan); the matrix fact covers the seven paths one host reaches and the three project-shape members are
  covered by the facts that own those shapes, so the gap is a *new* member added in future, not one of the
  ten today. Out of scope for this card: recorded here so the owner of the vocabulary can decide whether
  the reachability scan is worth its false-positive risk.
- **The review's follow-up: the registry read's own folds.** `BranchService.GetBranchesAsync` folds runs by
  branch (`lastRunByBranch`) and views by branch (`byBranch`), both Ordinal; a case-insensitive
  `lastRunByBranch` stayed green across the whole `Branches` suite while quietly giving `Main` and `main`
  the same (higher-numbered) `LastRun` - no row disappears, so nothing looks wrong. That is Ordinal by
  intent (a run belongs to exactly one branch; the sibling folds and the ordering are Ordinal too), so the
  dictionary was left alone and the fact pins it. It is the half of the case-sensitivity hole that a
  "does the branch still appear?" assertion cannot see.
- **Found but not touched (out of scope):** `docs/development.md` §2 still quotes the fast suite as
  "1341 discovered tests" on this tree, while the filter discovers 1368 at `31e4fc5` - the doc is 27
  behind (several later cards did not re-baseline it). Re-baselining it needs a counted run of all seven
  suites on the Windows host, so it belongs to whoever owns the gate, not to this card.

## Close-out verification (2026-11-17)

Nine facts, up from eight, because the reviewer found one more place where a case fold is silent.

- **The vocabulary is now held by a rule, not by a habit.** `WebhookOutcomes` has `All`, `IsKnown` and a
  `Require` that both delivery-path assignments pass through, so a misspelled outcome throws where it is
  produced instead of being stored and rendered on the settings page. The endpoint's client-facing list
  was nine members behind the set; it now names all ten and a fact reads the source and compares both
  directions. The reviewer's four-quadrant mutation is what makes that fact non-tautological: renaming
  `ignored:no-repository`'s constant value turned the documentation fact red while the reflection and
  delivery facts stayed green — documentation-only red, which is what proves the list is *not* a
  reference to `All`. Deleting an entry reddens it too, and a mutation that decoupled the response's
  `Status` from the outcome reddened two existing `WebhookDeliveryTests` facts, so "the file says it"
  and "the wire does it" are guarded separately.
- **"One probe per tick" is counted, not read.** The fact puts four new heads in a single tick and
  asserts `GitProbeRegistry.RequestCount == 1`; the reviewer's control mutation (disabling the dedupe
  window *without* moving the call) stays green, which separates "the count is one" from "duplicates are
  suppressed". Moving `RequestAsync` into the per-head loop takes the count to five while the probe row
  count stays at one — exactly the state that used to be silent.
- **Case sensitivity is pinned in four places, not one.** Discovery's `headShas` and `headNames` are two
  separate dictionaries and each has its own fact; the branch list's ordering uses a sample whose order
  genuinely flips (`Main,main,a,B` → `B,Main,a,m`), because `Main`/`main` alone still sorts the same way
  under `OrdinalIgnoreCase` and would have been a vacuous mutation; the run filter returns one run per
  filter. The fourth place is the one the reviewer found: `BranchService`'s `lastRunByBranch` fold is
  silent because no row disappears — both branch views simply attach to the same, higher-numbered run.
  The ninth fact pins each branch's own `LastRun.RunId`, and the mutation that folds that dictionary
  reddens it.
- **The cross-type credential claim is proven rather than narrowed.** Three leases (run, probe,
  definition) are alive at once and the four crossings answer a body-less 404 in both directions;
  making either route fall back to another id kind's material reddens it. Narrowing the Q-006 row would
  have left the property unreadable to the suite, and the three id spaces are interchangeable at the
  routing layer, which is exactly where a shared lookup would quietly widen one of them.
- **Gates.** The full fast suite ran on the Windows host: **1377 total, 1373 passed, 1 failed, 3
  skipped, 520 s** at the final tip `f0021cb`, and the same single failure is main's own baseline
  (`ProcessRunnerTests.Disposing_an_abandoned_runner_kills_and_reaps_the_process`, a process-handle
  race) — the new-failure set is empty. The three skips are the POSIX-only facts. `Category=Postgres`
  ran there too against a disposable PG 17 cluster: **6/6**. The reviewer closed the card with fourteen
  mutations, all red, and one residual it reported rather than fixed.
- **Recorded, not fixed:** `All` can declare a member that no path writes — the reflection fact holds
  *set equals constants*, not *set is reachable*; pinning reachability needs either every member
  exercised through the delivery path or a source-level scan, which belongs to the vocabulary's owner.
  Also recorded while closing: `docs/development.md` §2's re-baseline note had itself gone stale
  (1341 counted, then F-034 and this card added 36 more); it is re-baselined to the counted 1377 in a
  follow-up commit, with the per-card figures left as the snapshots of their own trees.
