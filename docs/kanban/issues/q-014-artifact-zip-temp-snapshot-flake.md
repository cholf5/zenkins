---
id: Q-014
title: The artifact zip fact snapshots the OS temp directory and fails whenever anything else writes there
type: test
priority: P2
size: S
status: done
created: 2026-11-16
updated: 2026-11-17
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

- [x] The fact no longer asserts on the OS temp directory as a whole: it scopes the comparison to the
      test's own run root (or to a pattern its own download is the only writer of).
- [x] The property it really wants still holds and is asserted: the download is streamed, not
      buffered to a temp archive, verified by a scoped observation rather than a global one.
- [x] The fast suite passes repeatedly (at least five consecutive runs) with the rest of the suite
      running concurrently, including the other temp-writing harnesses.
- [x] No assertion is deleted to get there; if the old global comparison cannot be scoped, the card
      records what replaced it and why the property is still covered.

## Subtasks

- [x] Replace the global before/after snapshot with a scoped one.
- [x] Loop-verify five consecutive fast runs and record the counts.

## Dependencies

- none (Q-011 closed the leaking half)

## Test Cases

### TC-001: Five consecutive fast runs are green

Steps:
1. Run the fast filter five times in a row on a quiet machine.

Expected:
- Five green runs; no failure of the zip fact; the loop-verified counts are recorded on the card.

Recorded (2026-11-17). Command, run from the repository root on the final tree, five times in a row
(no rebuild between runs, each run independent):

```
dotnet test -c Release --no-build --filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres"
```

| run | exit | wall | total | failed | log |
|-----|------|------|-------|--------|-----|
| 1 | 0 | 338 s | 1242 | 0 | `artifacts/q-014/dev-fast-run-1.log` |
| 2 | 0 | 339 s | 1242 | 0 | `artifacts/q-014/dev-fast-run-2.log` |
| 3 | 0 | 341 s | 1242 | 0 | `artifacts/q-014/dev-fast-run-3.log` |
| 4 | 0 | 335 s | 1242 | 0 | `artifacts/q-014/dev-fast-run-4.log` |
| 5 | 0 | 340 s | 1242 | 0 | `artifacts/q-014/dev-fast-run-5.log` |

5/5 green, 1242 tests passed each time, including the zip fact, `ArtifactRoundTripTests` (which writes
its own `.zip` into the shared temp root from a parallel class) and the other temp-writing harnesses.
No `zenkins-f016-zip-scope-*` directory is left behind afterwards.

Note for anyone re-running this: a full fast run takes about 5.5 minutes on this machine, so a loop of
five is ~28 minutes and has to be run detached (or with a long enough tool timeout); a run that is cut
short prints only the `Running tests from ...` header and looks like a hang.

## Notes and Decisions

- Rejected: keeping the whole-root walk and narrowing it further (by `*.zip`, by `zenkins-*`, by
  artifact or run id, or by mtime). Other writers of those patterns exist inside this suite - the
  reviewer's round-1 baseline on this machine found `.zip` fixtures in the shared root and
  `ArtifactRoundTripTests.cs:71` writes `zenkins-f016-<guid>.zip` there from a parallel class - so the
  pattern still spans other writers, which is the defect this card is about.
- Rejected: snapshotting a fresh empty directory that nothing consults. That is a tautology; the scope
  is only meaningful because the process's temp answer points at it, which the fact asserts in two
  places.
- Rejected: dropping the mid-flight sample now that the before/after pair is scoped. A temp archive
  lives and dies inside the response, so before/after alone is weaker than what it replaces.
- Rejected: relaxing the archive, store or chunk assertions to get a green loop. They are unchanged.
- Isolation is unchanged on purpose: the collection stays non-parallel for the heap measurement, which
  samples the whole process. No `DisableParallelization` or `maxParallelThreads` was widened and no
  test moved collections.
- Round-2 review correction (raised by the reviewer, wording only, no assertion touched). The comment
  next to the mid-flight `BytesSent < 32 MiB` bound, and the matching paragraph here, claimed the bound
  would also catch a buffered implementation writing its archive "somewhere this fact does not watch".
  That overstates it: `BytesSent` is recorded only in the instrumented copy loop, so the bound goes red
  only when a buffered shape reuses that loop for its temporary archive; a shape that copies with its
  own loop, or via `ZipFile.CreateFromDirectory`, leaves `BytesSent` at zero and the bound green. The
  two places were rewritten to say exactly that, and to state that the scoped snapshot therefore stays
  the only guard for those shapes - otherwise a later reader could delete the snapshot as redundant.

## Resolution (2026-11-17)

**What the fact watches now.** `SnapshotDirectory(root)` still renders every entry under a directory as
`path|length` (directories and links as `|-1`), and the fact still takes three samples: before the
request, **at the 64th 64 KiB chunk of the body** (about 4 MiB of the 32 MiB archive arrived, so an
archive that is created and deleted inside the response is still seen), and after the last byte. What
changed is the root. `ScopedTempRoot` creates a unique directory and points the process's temp
directory at it (`TMPDIR`, `TMP`/`TEMP`) for exactly the download's lifetime, then restores the
variables and deletes it. `Path.GetTempPath()` re-reads the environment on every call (checked on this
machine before relying on it), so a path captured earlier is not a way around the scope, and the fact
asserts `TempPathNow() == Root` **before the request** and again **while the body streams** - a
redirection that silently stopped working fails the test instead of quietly emptying the assertion.

**Why the property is still covered.** The claim was never "this directory is quiet"; it was "the
download materialises no temporary archive", and the place a temp archive is written is the directory
the OS temp API hands out. That directory is now the fact's own, so the scan *loses* both of its old
narrowing filters - the `.zip` suffix filter and the zero-length skip - and covers more entries than
before (any name, any size, empty ones included), which was impossible over a root shared with other
applications' sockets and other tests' scratch zips. The per-run roots, SQLite `-wal`/`-shm` sidecars
and publish-smoke copies this card blames can no longer be attributed to this download.

**A further assertion in the same window, supplementary only.** `telemetry.BytesSent < 32 MiB` at the
mid-flight sample catches one of the two buffered shapes, not both. `BytesSent` is incremented only in
`ArtifactStore.CopyFileToAsync`'s instrumented copy loop, once per completed file, so: a buffered
implementation that *reuses* that loop to fill its temporary zip has already had all 32 MiB counted by
the time the first body byte reaches the client, and this bound goes red; one that copies with its own
loop, or via `ZipFile.CreateFromDirectory`, leaves `BytesSent` at zero and this bound stays green - the
scoped temp snapshot above is the only guard for that shape. The bound is therefore kept as extra
evidence, not as a replacement for the snapshot, and nothing may be dropped on the grounds that it
covers the same ground. The store-side negative is kept (`StoreFiles()` unchanged, no `.zip` under the
store) and extended to the run's own work tree, so "materialised next to the artifact" is covered too.
The archive itself is still validated entry by entry, and `PeakChunkBytes <= 64 KiB` is unchanged.

**Red proof, recorded.** A temporary local change to `ArtifactEndpoints.DownloadAsync` that wrote
`zenkins-mutant-<artifact-id>.zip` into the process temp root (reverted; `src/` is not in the diff)
turned the fact red at the mid-flight snapshot: `Assert.Equal() Failure: Collections differ,`
`Actual: ["zenkins-mutant-48d6…"]` - `artifacts/q-014/dev-mutation-probe.log`. The suite keeps that
sensitivity as `The_scoped_scan_catches_a_temp_archive_written_the_way_the_product_would_write_one`,
which writes through `Path.GetTempPath()` and requires the snapshot to change.

**The two harness self-tests (AC-4 record).** `The_temp_snapshot_catches_a_new_non_empty_file` became
the scoped witness above: same assertion, new scope. `A_zero_length_entry_in_the_temp_root_does_not_break_the_scan`
existed only because the shared root holds other applications' AF_UNIX sockets (length 0) that had to
be skipped; with a private root the skip is gone, so its replacement,
`A_zero_length_entry_in_the_scope_is_part_of_the_snapshot`, asserts the opposite - an empty entry is
part of the snapshot - and is strictly stronger. No assertion was deleted.

**Files.** `tests/Zenkins.Tests/Artifacts/ArtifactStreamingTests.cs` only: the fact, the two harness
self-tests, `SnapshotDirectory`/`SnapshotFiles`/`TempPathNow`, `ScopedTempRoot`, and the
`ArtifactStreamingCollection` doc comment (it now says which test still needs isolation and why).

## Verification

Independent review PASSed all four acceptance criteria before this card moved to Done; every number
below comes from a run on the final tree, not from the implementer's report.

- **The tree.** `HEAD f08e3a2` plus this card's two files; `src/` clean, `board.md` untouched, card
  `status` at `review` until the reviewer signed off. Built binary `sha256 87fb814738196a61…`,
  unchanged across the reviewer's five runs.
- **Build.** `dotnet build -c Release` → exit 0, 0 warnings, 0 errors.
- **Fast suite, five consecutive runs** (reviewer's own, `--no-build`, no rebuild between runs):
  `1242 total / 0 failed` every time, 330 / 327 / 342 / 345 / 340 s. Logs:
  `artifacts/q-014/review-fast-run-{1..5}.log`. The implementer's independent five runs:
  `artifacts/q-014/dev-fast-run-{1..5}.log` (1242 / 0 each, 338–341 s).
- **The fact alone.** `--filter FullyQualifiedName~ArtifactStreamingTests` → 5 / 5 passed, 0 skipped.
- **Red proof.** The `zenkins-mutant-<artifact-id>.zip` probe failed at the mid-flight snapshot,
  `Assert.Equal() Failure: Collections differ`, `Actual: ["zenkins-mutant-48d6…"]` — the same call in
  the server's own context resolves `Path.GetTempPath()` to the scope, which is the counter-example to
  a redirect that only takes effect on the test thread. `artifacts/q-014/dev-mutation-probe.log`.
- **The one change after the verdict.** The reviewer's verdict carried one non-blocking must-fix: the
  comment above the `BytesSent` bound (and the matching Resolution paragraph) overstated it as catching
  a buffered implementation "even if it wrote its archive somewhere this fact does not watch". Wording
  only — the comment is now marked supplementary and names both shapes; `Assert` count unchanged (39
  new / 34 at `HEAD`), the assertion itself byte-identical, only a +1 line shift below `:182`. Re-confirmed
  by the reviewer: verdict stands, build exit 0, the collection 5 / 5 green.
  `artifacts/q-014/dev-fix-round.log`, `artifacts/q-014/review-fix-confirm.log`.
- **Not covered by this card.** The private scope is a process-level `TMPDIR`/`TMP`/`TEMP` change, so
  the fact's safety rests on `ArtifactStreamingCollection` staying non-parallel; the dependency is
  written into that collection's doc comment, and the failure is loud rather than silent. The shared
  temp root still holds residue from other cards (668 `zenkins-*` `-wal`/`-shm` files on the reviewing
  machine) — the new observation no longer touches it, and that surface stays Q-011's other half.
