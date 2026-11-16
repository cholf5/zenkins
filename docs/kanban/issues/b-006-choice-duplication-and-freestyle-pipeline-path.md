---
id: B-006
title: Two small inconsistencies the F-020/F-021 review found — duplicate choice entries and a Freestyle row's inherited pipeline path
type: bug
priority: P3
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
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

- [ ] A choice list with a repeated entry is refused at save time with a stable code, or deduplicated
      deterministically — pick one and record why; no duplicate `<option>` can render.
- [ ] A Freestyle project's runs report an empty pipeline path regardless of how the project row was
      created (entity default removed, or the run snapshot zeroes it by kind).
- [ ] Both behaviours carry a test; existing Freestyle and parameter suites stay green.

## Subtasks

- [ ] Decide refuse-vs-dedupe for the choice list and implement it in the resolver.
- [ ] Fix the `PipelinePath` default (migration if the column default changes).
- [ ] Tests for both.

## Dependencies

- none (F-020 and F-021 are accepted)

## Test Cases

### TC-001: A repeated choice cannot be saved or rendered

Steps:
1. Save a `choice` parameter with `fast,fast`.
2. Read the trigger form.

Expected:
- The save is refused with a stable code (or the entry appears once, if deduplication is chosen);
  the form never renders two identical options.

### TC-002: A Freestyle run carries no pipeline path

Steps:
1. Insert a Freestyle project row without the service's validation (the seed/import shape).
2. Trigger it and open the run page.

Expected:
- The run's pipeline path is empty and the page shows no pipeline path for it.
