---
id: B-006
title: Two small inconsistencies the F-020/F-021 review found — duplicate choice entries and a Freestyle row's inherited pipeline path
type: bug
priority: P3
size: S
status: done
created: 2026-11-16
updated: 2026-11-17
blocked: none
---

## Goal

Neither inconsistency can reach a run: a parameter's choice list cannot hold the same entry twice,
and a Freestyle project cannot carry a `pipeline.lua` path into its runs.

## Background

Both surfaced during F-020/F-021's independent review on 2026-11-16 and are too small to reopen
either card.

1. **Duplicate choices are accepted.** `ProjectParameters.cs:286-313` parses a choice list without
   deduplicating, so `fast,fast` renders two identical `<option>`s. The repo's discipline elsewhere
   is "duplicate means refused" (for example `PipelineSpec.cs:459-464` for artifacts).
2. **`Project.PipelinePath` defaults to `pipeline.lua` at the entity level** (`Project.cs:42`).
   Every Freestyle row that goes through `ProjectService.Validate` is written correctly, so nothing
   is broken today — but any row created another way (seed, import, a future migration) would carry
   the path into a run snapshot, and the run page would show a pipeline path for a project with no
   repository.

## Acceptance Criteria

- [x] A choice list with a repeated entry is refused at save time with a stable code, or deduplicated
      deterministically — pick one and record why; no duplicate `<option>` can render.
- [x] A Freestyle project's runs report an empty pipeline path regardless of how the project row was
      created (entity default removed, or the run snapshot zeroes it by kind).
- [x] Both behaviours carry a test; existing Freestyle and parameter suites stay green.

## Subtasks

- [x] Decide refuse-vs-dedupe for the choice list and implement it in the resolver.
- [x] Fix the `PipelinePath` default (migration if the column default changes).
- [x] Tests for both.

## Dependencies

- none (F-020 and F-021 are accepted)

## Test Cases

### TC-001: A repeated choice cannot be saved or rendered

Steps:
1. Save a `choice` parameter with `fast,fast`.
2. Read the trigger form.

Expected:
- The save is refused with a stable code; the form never renders two identical options.

Covered by `A_repeated_choice_entry_is_refused_at_save_and_no_duplicate_option_renders`
(`tests/Zenkins.Tests/Runs/ParameterizedRunTests.cs:119`) over the real editor endpoint, and by
`A_repeated_choice_entry_is_refused_and_the_comparison_is_ordinal`
(`tests/Zenkins.Tests/Projects/ProjectParametersTests.cs:86`) at the catalog itself.

### TC-002: A Freestyle run carries no pipeline path

Steps:
1. Insert a Freestyle project row without the service's validation (the seed/import shape).
2. Trigger it and open the run page.

Expected:
- The run's pipeline path is empty and the page shows no pipeline path for it.

Covered by `A_freestyle_project_written_without_the_service_reports_no_pipeline_path`
(`tests/Zenkins.Tests/Runs/FreestyleRunTests.cs:64`), which inserts the row straight through the
context and asserts both sides — the row and the page.

## Notes and Decisions

### ① Duplicate choice entries: refused at save, ordinal, order kept

`ParseChoicesText` now refuses a repeated entry, in the same pass that already enforces "at least
one", "at most 32" and "at most 200 characters per entry":

```
{path}.choices[{i + 1}] '{choice}' is declared twice.
```

The stable code the user sees is the one that already exists: `parameters-invalid`
(`ProjectErrorCodes.ParametersInvalid`, `ProjectEndpoints.cs:180` → PRG
`?error=parameters-invalid` on the parameters tab, which renders "The parameter list was refused").
The sentence naming the entry index goes to the Server log, exactly like every other parameter
refusal (`ProjectService.SaveParametersAsync`'s `"Project parameters refused"` line) — the definition
list is one JSON blob and the page carries a field-level code, not a per-entry message.

**Why refuse rather than de-duplicate.** Three reasons, in the order they decided it:

1. It is the repository's discipline for a repeated entry in a declared list — `PipelineSpec` refuses
   a repeated artifact path, `ProjectParameters` already refuses a repeated parameter name
   (`parameters[2].name 'X' is declared more than once`). A silent collapse here would be the one
   place where repeating yourself is quietly forgiven.
2. The order is semantic, so collapsing would not be a no-op: the trigger form renders the entries in
   the declared order, and `Choices[0]` is the value a trigger that supplies nothing resolves to
   (`ResolveValues`, `ProjectParameters.cs:222-224`). A deduplicator that kept the first occurrence
   would be defensible, but it would still be *deciding* which of the author's entries the headless
   default lands on — a definition error is better answered than guessed at.
3. A refusal costs nothing that a dedupe would save: the stored JSON is unchanged, so a trigger keeps
   resolving the previously saved list (asserted byte for byte).

**The comparison is `Ordinal`, and that is load-bearing.** `TryNormalizeForTrigger`
(`ProjectParameters.cs:255`) matches a supplied choice value with `StringComparer.Ordinal`, so
`fast` and `Fast` are two *distinct legal values*. Refusing them as "duplicates" would be a rule the
resolver disagrees with — it would refuse a list whose entries can each be resolved. Only an exact
repeat is refused; `fast,Fast` stays legal and keeps both entries in order.

**Rule order pinned.** A list that breaks two rules reports the earlier one: 33 entries where some
repeat reports `at most 32 values`, not "declared twice". The fact asserts that, so a later reshuffle
of the checks cannot silently change which sentence a user gets.

**What it means for a list saved before this fix.** No migration rewrites `ParametersJson`: the
column is a JSON blob whose only writer is the validated path, and there is no per-entry table to
backfill. A project that already stored `fast,fast` keeps rendering two options until its parameters
are saved again — and that save is now refused until the repeat is removed, which is the same
self-healing shape the artifact list has (the operator sees the refusal on the tab and fixes the
entry). No run is affected either way: the resolver matches the value it is given, duplicate or not.

### ② The run snapshot decides by kind, not the entity default

`BuildQueuedRun` (`RunService.cs:1650`) now takes the pipeline path the way it already takes the
branch — from the kind, not from the row:

```csharp
PipelinePath = project.Kind == ProjectKinds.Freestyle
    ? string.Empty
    : project.PipelinePath ?? string.Empty,
```

**Why this and not "remove the entity default".** The card allowed either. Removing
`Project.cs:42`'s `= "pipeline.lua"` is not free, and the cost lands on the wrong side:

- The initializer is what gives **Pipeline** rows their path at eight `new Project { … }` sites in the
  test harnesses that omit the field (the runs/webhook hosts and their deliveries). Removing it would
  leave those rows with a null path, so their runs would snapshot an empty pipeline path — five
  existing assertions (`RunEndpointTests.cs:52`, `:124`, `WebhookDeliveryTests.cs:58`, `:320`,
  `:329`) would go red, and the honest fix would be to write `PipelinePath = "pipeline.lua"`
  explicitly at those eight sites. That is a real, load-bearing F-014 fact (a Pipeline run names the
  file it built) which this card has no business editing, and getting it wrong is exactly the
  "Pipeline run with no definition path" the AC forbids.
- The snapshot fix is one expression, damages nothing, and satisfies the AC's literal wording —
  "regardless of how the project row was created" — including the case the entity fix would *not*
  cover: an import that copies a Pipeline project's `PipelinePath` onto a Freestyle row.

The card records the rejected alternative and its five-assertion bill so a later reader does not
"simplify" this by deleting the initializer.

All three trigger sources (manual, webhook, schedule) build the run through this one routine
(`BuildQueuedRun`, `RunService.cs:1620-1670`), so the rule holds for every one of them. Rebuild copies the
original run's own row (`RunService.cs:1379`): a Freestyle run's snapshot is now always empty, so a
rebuild of one is empty too. A run row **stored before this change** keeps whatever it recorded —
history is not rewritten, which is the same rule the branch and the frozen spec follow.

**No migration.** Nothing on the column changes: `Project.PipelinePath` has no database default
(`HasDefaultValue` has zero hits under `src/Server/Data`), the column was made nullable by F-020's
migration, and the entity's CLR initializer is not part of the model — the snapshot and the
`DbContext` are untouched. `Category=Postgres` was therefore not run.

### The residue this leaves, stated

A Freestyle row written outside the service still *stores* `pipeline.lua` (that is the entity
initializer, kept on purpose for Pipeline rows). What B-006 guarantees is that no **run** and no run
page ever reports it. The product's own write paths already null it (`ProjectService.Validate` at
`:695-703`, create at `:247-258`, edit at `:317-324`), so the stored value is only visible on a
seeded/imported row's own settings page — a row that cannot be created through the UI. Making the
entity truthful for both kinds is the alternative recorded above, not a silently dropped half of
this one.

## Verification (2026-11-17)

- `dotnet build -c Release` — `Build succeeded.` / `0 Warning(s)` / `0 Error(s)`.
- Fast suite (the sextuple exclusion filter of `docs/development.md` §2) — **1287 passed, 0 failed,
  0 skipped** (baseline 1284 + this card's 3). Log: `artifacts/b-006/fast-suite.log`.
- `Category=Postgres` — not run, and not needed: see "No migration" above.
- Logs: `artifacts/b-006/` (`fast-suite.log`, `mutation-1-…`, `mutation-2-…`, `mutation-3-…`,
  `mutation-baseline-sha256.txt`).

### The facts

| Fact | Where | What it asserts |
|---|---|---|
| `A_repeated_choice_entry_is_refused_and_the_comparison_is_ordinal` | `tests/Zenkins.Tests/Projects/ProjectParametersTests.cs:86` | `fast,fast` is refused with "declared twice"; `" fast , fast "` too (the entries are trimmed before they are compared); `fast,Fast` is accepted and keeps both entries in declared order (the comparison is ordinal, like the resolver's); a 33-entry list with a repeat reports the bound, not the repeat (rule order) |
| `A_repeated_choice_entry_is_refused_at_save_and_no_duplicate_option_renders` | `tests/Zenkins.Tests/Runs/ParameterizedRunTests.cs:119` | Through the real editor endpoint (TC-001): `302` to `?error=parameters-invalid` on the parameters tab, the rendered refusal, `ParametersJson` **byte for byte** the value saved before the attempt, the trigger form's `<select>` rendering exactly two options with one `value="fast"`, and a trigger still resolving `fast` |
| `A_freestyle_project_written_without_the_service_reports_no_pipeline_path` | `tests/Zenkins.Tests/Runs/FreestyleRunTests.cs:64` | TC-002's shape, both ways a row reaches the table without `ProjectService.Validate`: one seeded **stating** `pipeline.lua` (asserted on the row, so the fact cannot pass on an empty row) and one seeded **leaving it out** (the entity initializer supplies it), each with a valid `FreestyleSpecJson`; both runs report `PipelinePath == ""` and both run pages render the empty marker (`dd#run-pipeline-path` = `-`) with no `pipeline.lua` anywhere in the markup |

The third fact is the one that matters historically: a row created by `ProjectService.Validate` can
never show this bug, because that path nulls the field itself. Only a seed/import shape reaches it,
and the fact builds one the way a seed does — straight through the context, never the service.

### Mutations (each restored; both files back at the hashes in `mutation-baseline-sha256.txt`)

| Mutation | Result |
|---|---|
| ① The duplicate check deleted from `ParseChoicesText` | red in **both** ① facts: the catalog fact at `Assert.Null` (the list is accepted), the endpoint fact at the redirect (`notice=parameters-saved` instead of `error=parameters-invalid`) — `artifacts/b-006/mutation-1-choice-duplicate-check-removed.log` (2 failed / 2) |
| ① The comparison changed to `StringComparer.OrdinalIgnoreCase` | red: the catalog fact's `fast,Fast` list is refused, `Validate`'s `Assert.Null(error)` sees an error — `artifacts/b-006/mutation-3-choice-comparer-case-insensitive.log` (1 failed / 1) |
| ② The kind guard reverted (`PipelinePath = project.PipelinePath ?? string.Empty`) | red at `FreestyleRunTests.cs:88`: `Assert.Equal("", run.PipelinePath)` → `Expected: "" / Actual: "pipeline.lua"` — i.e. the run inherits the row, the bug the card names — `artifacts/b-006/mutation-2-snapshot-kind-guard-removed.log` (1 failed / 1) |

Each source file was copied aside before its mutation, restored afterwards, and the tree re-checked
against `artifacts/b-006/mutation-baseline-sha256.txt` (`shasum -a 256` → `HASHES MATCH BASELINE`)
before the counted fast run.

### Counts

Fast **1284 → 1287** (+3 facts), all seven **1311 → 1314**; Postgres stays 4 (not run), as no
migration was added. `docs/development.md` §2 records the same numbers (the per-card note, the Fast
table row and the all-seven line).

**Independent review, 2026-11-17 — PASS, three of three criteria.** The reviewer ran the counts itself
(fast **1287 / 0 failed / 0 skipped**, 5m54s), checked the by-case arithmetic against 1284, searched for
any deletion, `Skip=` or `[Trait]` change (0 hits - under a negative filter, re-categorising is the one
way to pass off a deleted test as green), and re-applied seven mutations of its own.

- **The route that costs less was also the right one, and the mutation proves it.** Deleting the
  entity's initializer would have reddened four facts (`RunEndpointTests:52`, `:124`,
  `WebhookDeliveryTests:58`, `:320`/`:329`) whose **Pipeline** rows borrow `pipeline.lua` from it, and
  would have left the eight `new Project { }` harness sites without a path. The snapshot-by-kind route
  costs one line and covers a shape the other cannot: an import copying a Pipeline row's path onto a
  Freestyle row.
- **`M5` - unconditional `string.Empty` at the snapshot - reddens five facts**, one more than the review
  predicted: `PipelineRunEndpointTests`' custom path `ci/pipeline.lua` goes red too. The guard that keeps
  this "by kind" rather than "for everything" was already thicker than either of us had written down.
- **`M2` proves the case sensitivity is load-bearing**, not decorative: swapping `Ordinal` for
  `OrdinalIgnoreCase` reds the catalog fact with `parameters[1].choices[2] 'Fast' is declared twice.`
  while the endpoint fact stays green. **`M7`** - moving the 32-value cap after the duplicate check -
  reds the 33-entry case, because "declared twice" would otherwise answer first. Rule order is pinned.
- **The un-touched files are still biting.** `RunEndpointTests.cs`, `WebhookDeliveryTests.cs`,
  `OrganizationTestHost.cs`, `TwoOrgsFixture.cs` and `Tenancy/` show zero diff, and mutations `M5` and
  `M8` both redden them - so "not edited" is not the same as "no longer load-bearing".
- **No migration, correctly.** `HasDefaultValue` has 0 hits, `src/Server/Data/` and `src/Shared/` are
  zero-diff, and no migration file was added, so "there is no migration" is itself the conclusion and
  skipping `Category=Postgres` was right.
- **`M8` closed the reviewer's own round-one prediction**: deleting the initializer reds four facts
  *and* leaves the TC-002 fact green - which is exactly why the fact needed the one line added at
  close-out, asserting that the *seeded* row (the one that omits the field) really does carry
  `pipeline.lua`. Without it that half of the shape had only a comment as its subject.
- **Stated, not fixed.** A Freestyle row written outside the service still *stores* `pipeline.lua`, and
  the four project pages still show it (`ProjectDetail.razor:156`, `ProjectSettings:126`,
  `ProjectPipelines:81`, `Index:85`); AC-2 is about runs, and the card records the residue. Separately,
  `ProjectRuns.razor:71` renders a bare `" · "` for a Freestyle run - cosmetic, untouched here, no fact
  guards it.
- Logs: `artifacts/b-006/` (both sides) - `mutation-*.log`, `logs/my-M{1,2,4,5,6,7,8}.log`,
  `fast-suite.log`, `review-verdict.md`, `f1-seeded-row-assertion.log`.
