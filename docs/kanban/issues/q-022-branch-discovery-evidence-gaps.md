---
id: Q-022
title: The F-023 review proved four branch-discovery properties by reading code, and the suite does not carry them
type: test
priority: P2
size: S
status: todo
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

- [ ] A fact holds the webhook outcome vocabulary: every value the delivery path writes is a member of
      `WebhookOutcomes` (an `All` collection or an equivalent rule), and adding a value to the enum
      without the set fails.
- [ ] A fact pins "one probe per project per tick": a tick with several new heads issues a single probe,
      by counting invocations rather than by reading the structure.
- [ ] A case-differing fact pins the service-side Ordinal behaviour — a dictionary, an ordering and a run
      filter each distinguish `Main` from `main` — so the database fact is no longer the only guard.
- [ ] The Q-006 row's cross-type credential claim is backed by a fact (a definition id cannot fetch a
      run's or a probe's credential and vice versa), or the row is narrowed to what is proven.
- [ ] `WebhookEndpoints`' outcome list is brought up to date with the set, and a fact keeps it that way
      rather than relying on the next reader to notice.
- [ ] The fast suite stays green and its count moves by exactly the number of facts added.

## Subtasks

- [ ] Give `WebhookOutcomes` an `All` and rule the delivery path against it; back the endpoint list with
      the same source.
- [ ] Count probes per tick in the discovery sweeper's seam.
- [ ] Add the Ordinal case-differing facts for dictionary, ordering and filter.
- [ ] Prove or narrow the cross-type credential claim.

## Dependencies

- none (F-023 is accepted; I-005 and F-017 provide the surfaces)

## Notes and Decisions

- Same shape as Q-012: these are evidence debts, not defects. Nothing here is known to be broken; what is
  missing is the fact that would catch the next person who breaks it.
- Not to be folded into F-023: that card was already L and its reviewer drew the line explicitly.
