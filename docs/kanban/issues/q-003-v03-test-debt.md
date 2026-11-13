---
id: Q-003
title: Close the v0.3 test debt recorded on accepted cards
type: test
priority: P2
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
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

- [ ] Rate limiter: tests cover bucket bound and eviction, per-bucket concurrency, window boundary
      behaviour, and the documented fail-open after a backwards clock jump; each is red if the
      corresponding guard is removed.
- [ ] Artifact temp snapshot: a repeated run with an unrelated process writing into the temp tree does
      not fail, while a newly written non-empty file is still detected; both directions asserted.
- [ ] Repository URL: a project whose repository URL is saved through the product's own form is the
      URL the worker is told to clone (asserted through claim, not through a seeded row).
- [ ] Abandoned run: at the loop level, a definitively refused report means no step is started and no
      completed report is sent.
- [ ] Definition report: a second report carrying a different commit is refused and the pinned commit
      is unchanged.
- [ ] Key ring: all four resolution branches (explicit relative, explicit absolute, SQLite beside the
      database, PostgreSQL content root) are pinned, including the relative-`Data Source` form.
- [ ] Full suite remains at zero skipped and the category counts are recorded before and after, so the
      card cannot hide a deletion.

## Subtasks

- [ ] Rate limiter unit tests (`Category=Webhooks`).
- [ ] Artifact snapshot tolerance plus its two-direction proof (`Category=Artifacts`).
- [ ] Repository URL through the real form (`Category=Pipeline` and `Category=Milestone` as fits).
- [ ] Loop-level abandoned run and the differing-commit definition report (`Category=Pipeline`).
- [ ] Key-ring branch test (`Category=Secrets`).
- [ ] Record the mutation evidence for each item in the log, not only the passing state.

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

### TC-002: The snapshot still catches what it is for

Steps:
1. Write a non-empty file into the temp tree during a download; then repeat with only a foreign
   zero-length entry present.

Expected:
- The first fails the test; the second does not.

## Development Log

### 2026-11-13

- Planned after the v0.3 board was cleared: these are the follow-ups the accepted cards recorded, none
  of which is large enough for its own feature card but all of which guard something real.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Webhooks/WebhookRateLimiter.cs`, `tests/Zenkins.Tests/Webhooks/*`,
  `tests/Zenkins.Tests/Artifacts/ArtifactStreamingTests.cs`, `tests/Zenkins.Tests/Pipelines/*`,
  `tests/Zenkins.Tests/Secrets/SecretDataProtectionTests.cs`
- How to run/verify: `dotnet build -c Release`; the five categories above; full suite.
- Results: not run (card not started).
