---
id: F-016
title: Artifacts: safe local store, per-run download
type: feature
priority: P1
size: M
status: done
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

A pipeline step can publish a file or directory as a run artifact (`artifact("bin/Release")`); the
worker uploads it to the server's local store without traversing out of the run's workspace; the run
page lists artifacts and each one downloads as the original file, or as a zip for a directory, only
to members of the owning organization.

## Background

PRD §3.7: `artifact("bin/Release")`, single files and folders, multiple artifacts per run, download,
query by run, local filesystem storage organised by Organization/Project/Run, no path traversal
across tenants, S3 as a later backend. PRD §6 lists `Artifact`. F-007 already creates
`work/{run-id}/artifacts/`; this card gives it meaning.

Decisions:

- **The primitive runs in the step's workspace and must be called explicitly** — collection is not
  automatic at the end of the run, because "which files are the artifact" is a pipeline decision.
  `artifact()` is an I/O primitive: it suspends the script (§5.2 of
  [`docs/lua-runtime.md`](../../lua-runtime.md)) while upload happens, so the run's deadline and
  cancellation apply to it like any other host call.
- **Path safety is resolved, then re-checked.** The declared path is resolved against
  `work/{run-id}/repository` and must stay inside it after canonicalisation; symlinks are resolved and
  a link whose target escapes the repository is rejected by name. The check runs on the worker
  (before upload) *and* on the server (on the received relative paths), because the server must not
  trust a worker binary — two independent validations, same rules, shared test vectors.
- **Storage layout** is `App_Data/artifacts/{organizationId}/{runId}/{artifactId}/{relativePath}` with
  the row holding only the artifact id — never a client-supplied absolute path. A directory artifact
  keeps its tree; the `relativePath` recorded per file is also what the zip download restores.
- **Caps are explicit and fail loudly**: per-file size (`Artifacts:MaxFileBytes`, default 512 MB),
  per-artifact total (`Artifacts:MaxArtifactBytes`, default 2 GB), file count
  (`Artifacts:MaxFiles`, default 20 000). Exceeding a cap fails the calling step with the limit named
  — a truncated artifact would be a lie about the build output.
- **Upload protocol is create → stream files → finalize**, over the existing worker-authenticated
  channel: `create` reserves the artifact row (name, kind), files stream with their relative paths
  and a per-file SHA-256, `finalize` writes the manifest (count, total bytes, manifest hash) and flips
  the row to `Ready`. A worker that dies mid-upload leaves a `Pending` row that the sweeper deletes
  (or marks `Incomplete`); the download endpoint only serves `Ready`.
- **Downloads** stream: a single-file artifact streams the file with its name; a directory artifact
  streams a zip built on the fly (no temp archive on disk). Content-Disposition names are sanitised
  to avoid header-splitting. Foreign organization / unknown id → plain 404, empty body, no timing
  oracle.
- **No retention policy in v1** — artifacts live until the run is deleted with its project. Recorded
  in the parking lot rather than invented here.
- **Duplicate names per run** are rejected: `artifact("bin")` twice is a pipeline bug, and a
  second artifact with the same name would make downloads ambiguous.

## Acceptance Criteria

- [x] `Artifact` entity (`OrganizationId`, `RunId`, `Name`, `Kind` = `File|Directory`, `Status` =
      `Pending|Ready|Incomplete`, `FileCount`, `TotalBytes`, `ManifestJson`, `CreatedAt`) +
      migration, unique `(RunId, Name)`, cascade from the run; PostgreSQL contract gate clean.
- [x] `artifact(path)` works for a single file and a directory from a step; the file/directory lands
      in the store byte-identical (per-file SHA-256 verified after upload), and the run page lists it.
- [x] Path safety: `../../etc/passwd`, an absolute path, a symlink escaping the repository, and a
      drive-qualified Windows path are each rejected with a message naming the path, on the worker and
      (independently) on the server when a hand-crafted upload claims such a path.
- [x] Every cap is enforced with the limit named in the error; no partial artifact is downloadable
      after a cap failure (status is not `Ready`).
- [x] Single-file download returns the original bytes and file name; directory download is a zip
      whose entries restore the original relative tree; both are streamed (memory stays bounded —
      asserted by a large fixture, as F-008's log tests do).
- [x] Access control: a member of another organization gets 404 for both artifact kinds; an
      unauthenticated request is 401/not-found per the app's existing convention; a `Pending` artifact
      is 404.
- [x] A worker that disconnects mid-upload leaves no downloadable artifact and no permanently
      `Pending` row (sweeper path covered by a test).
- [x] Cancellation/deadline during `artifact()` kills the upload, fails the step with the F-009
      reason, and leaves no `Ready` row.
- [x] `artifact()` called from the definition phase is refused with the same "available inside steps
      only" error as F-014's other I/O primitives.

## Subtasks

- [x] `Artifact` entity + migration + store layout + sweeper for abandoned uploads.
- [x] Server: create/upload/finalize endpoints (worker token, run-scoped), per-file SHA-256
      verification, manifest; download endpoints (single file / on-the-fly zip) with tenant checks.
- [x] Worker: `artifact()` primitive (F-011 registration, yielding), path resolution and symlink
      rules, streaming multipart/chunk upload, cap pre-checks.
- [x] Run page: artifact list with size and download links.
- [x] Tests (`Category=Artifacts`): file/directory round-trip, traversal vectors (worker + server),
      caps, streaming memory bound, download fidelity, access control, abandoned upload, cancel,
      definition-phase refusal, tenancy probes for `Artifact`.

## Dependencies

- F-014 (steps and the runtime primitive seam)
- F-015 (step failure attribution for cap/cancel errors)

## Test Cases

### TC-001: File and directory round-trip

Steps:
1. Step publishes a 1 MB binary file and a directory with three nested files.
2. Download both.

Expected:
- Bytes and SHA-256 match; the directory download is a zip with the same relative paths; the page
  lists both with sizes.

### TC-002: Traversal is refused on both sides

Steps:
1. Pipeline calls `artifact("../../etc/passwd")`, then an absolute path, then a symlink pointing
   outside the repository.
2. Craft an upload whose relative path is `../escape.txt` and send it with a valid worker token.

Expected:
- Worker rejects all three, naming the path, step fails, no row created.
- Server rejects the crafted path independently; no file is written outside the store.

### TC-003: Caps fail loudly

Steps:
1. Set the per-file cap to 1 MB, publish a 2 MB file.
2. Publish 20 001 files with the count cap at 20 000.

Expected:
- Each failure names the limit; the artifact is not `Ready`; nothing partially downloadable.

### TC-004: Tenant isolation and streaming

Steps:
1. As an organization-B member, request A's artifact by id.
2. Publish a 300 MB file and download it while measuring the server's managed memory.

Expected:
- (1) 404 with an empty body.
- (2) memory stays bounded (no whole-file buffering), download completes with matching SHA-256.

## Development Log

### 2026-11-13

- Planned from the v0.3 plan; no work started. Retention/disposal is deliberately absent (parking
  lot), and the upload protocol is chunked rather than a single multipart request so a future S3
  backend can implement the same three verbs.

### 2026-11-13 — implemented, reviewed, reworked, accepted

- **`artifact()` as a Lua call inside a step is unreachable, and the card now says so.** The contract
  already fixes v0.3 as "the pipeline never calls an I/O primitive itself; the C# executor does", and
  step bodies are argv. The delivered surface is therefore a declared step field,
  `artifacts = { "bin/Release" }`, published by the executor after the step succeeds; the Lua
  primitive is refused in both phases with a message pointing at the declaration. This is recorded as
  a deviation from the card's wording rather than papered over.
- **The path check had a real hole and the first review caught it.** The worker resolved only the
  last component, so `artifact("link/secret.txt")` published a file from outside the repository
  whenever the *parent* directory was the link - `File.ResolveLinkTarget` returns null for that case.
  Resolution is now per segment: each component is canonicalised, compared against the checkout root,
  and becomes the parent for the next one, so an intermediate or chained link is caught, while a link
  that stays inside the repository still publishes. The server applies the same rules independently
  to the relative paths it receives.
- **A metric that could not fail.** Memory boundedness was asserted with `PeakBufferBytes`, whose only
  writer was the configured copy-buffer constant - a value compared against itself. Loading the whole
  file into memory first passed every assertion. It is now a measured heap delta sampled per chunk,
  with a paired test that runs a streamed copy and a buffered copy through the same production
  telemetry: the streamed one stays under the bound and the buffered one exceeds it.
- Gates that would have been green and wrong: "no temp zip" checks a recursive snapshot of the OS
  temp tree before, during and after the download instead of looking for a file name that would never
  match; "the upload was cancelled mid-flight" cancels after the server has received bytes rather
  than before the call starts; "the sweeper only takes stale rows" has a negative case for a fresh
  Pending row and for one created long ago but written recently, and is aged by last write.
- Two more real defects were fixed on the way: a non-cancellation transport failure during upload
  left the step Running forever (it now fails the step with `artifact-failed`), and the sweeper aged
  rows by creation time, which would have swept a legitimate large upload out from under itself.
- Repository hygiene: test hosts point the store at their own temp directories, because booting the
  real program from `src/Server` otherwise created `src/Server/App_Data/artifacts` on every suite
  run - hidden by the existing `**/App_Data/` ignore rule, so a plain `git status` reported clean.
  A guard test asserts the resolved root is never inside the repository.
- `.gitignore`'s unanchored `artifacts/` was also swallowing `src/Server/Artifacts`,
  `src/Worker/Artifacts` and `tests/**/Artifacts`; anchoring it to `/artifacts/` keeps the publish
  output ignored without silently dropping this feature's source.
- Gate: `dotnet build -c Release` 0 warnings 0 errors; `Category=Artifacts` 87/0/0; full suite
  801/0/0, zero skipped; `Category=Pipeline` 93, `Steps` 33, `Lua` 94, `Secrets` 64, `Git` 48,
  `TenantIsolation` 68 unchanged; PostgreSQL contract gate exits 0 without a running database.
- Recorded, not changed: `PeakChunkBytes` can only ever prove an upper bound (a read never returns
  more than the buffer it was given), so it catches "the whole artifact as one read" and nothing
  else - the heap delta is the real discriminator, and the telemetry comment says so now. Retention
  and disposal remain out of scope, as the card said.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Data/Entities/Artifact.cs`, `src/Server/Data/Migrations/*`,
  `src/Server/Artifacts/*`, `src/Server/Components/Pages/Runs/*`, `src/Server/appsettings.json`,
  `src/Worker/Artifacts/*`, `src/Worker/Lua/*`, `src/Shared/*`,
  `tests/Zenkins.Tests/Artifacts/*`, `tests/Zenkins.Tests/Tenancy/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Artifacts"`;
  full suite; PostgreSQL contract gate.
- Results: accepted 2026-11-13 after two review rounds and two rework rounds. `dotnet build -c Release`
  0 warnings 0 errors; `Category=Artifacts` 87/0/0; full suite 801/0/0, zero skipped; six categories
  unchanged; PostgreSQL contract gate exits 0 without a running database; a suite run leaves no
  artifact directory inside the repository.
