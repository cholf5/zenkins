---
id: Q-014
title: The artifact zip fact snapshots the OS temp directory and fails whenever anything else writes there
type: test
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

`Artifacts.ArtifactStreamingTests.A_directory_download_is_zipped_on_the_fly_and_writes_no_temp_archive`
asserts only about its own run's temp directory, so the fast suite stops going red for reasons the
test does not own.

## Background

Found on 2026-11-16 while reviewing D-007: the reviewer's fast-suite run on the accepted tree failed
exactly this fact, and it reproduced on a clean `eceb019` worktree (1 green / 2 red) while failing
5/5 on the newer tree. The mechanism is a snapshot comparison of the **whole** OS temp directory
before and after the request — any concurrent temporary write by another test in the same run
(which the suite does constantly: per-run roots, SQLite `-wal`/`-shm` sidecars, published-smoke
copies) breaks the equality.

Q-011 fixed leaked temp roots; this is the other half of the same surface: a test whose assertion
spans a directory the whole suite shares.

## Acceptance Criteria

- [ ] The fact no longer asserts on the OS temp directory as a whole: it scopes the comparison to
      the test's own run root (or to a pattern its own download is the only writer of).
- [ ] The property it really wants still holds and is asserted: the download is streamed, not
      buffered to a temp archive, verified by a scoped observation rather than a global one.
- [ ] The fast suite passes repeatedly (at least five consecutive runs) with the rest of the suite
      running concurrently, including the other temp-writing harnesses.
- [ ] No assertion is deleted to get there; if the old global comparison cannot be scoped, the card
      records what replaced it and why the property is still covered.

## Subtasks

- [ ] Replace the global before/after snapshot with a scoped one.
- [ ] Loop-verify five consecutive fast runs and record the counts.

## Dependencies

- none (Q-011 closed the leaking half)

## Test Cases

### TC-001: Five consecutive fast runs are green

Steps:
1. Run the fast filter five times in a row on a quiet machine.

Expected:
- Five green runs; no failure of the zip fact; the loop-verified counts are recorded on the card.
