---
id: P-001
title: Release engineering — version, tag, CHANGELOG, verify.sh, CI (the v1.0 gate)
type: release
priority: P1
size: M
status: review
created: 2026-11-14
updated: 2026-11-15
blocked:
  reason: The annotated v1.0 tag and the CI-green-on-the-tagged-revision verification (AC-4/AC-5) require a push to origin, which only the user can authorize; the independent review passed the mechanical work (2026-11-15), so nothing in the repository blocks it.
  unblock: The user authorizes (or performs) the push to origin; CI runs the gate on the release revision and is green; then the annotated tag v1.0 is cut on that revision (PM/user step, per the card's pending-state prose).
  owner: user
---

## Goal

The repository can back the claim "v1.0" to someone who was not in this conversation: a version
number stamped in the build, a CHANGELOG, an annotated tag, a one-command local verification script,
and CI running the same gate.

## Background

I-003 gave the repo packaging (the `scripts/publish.sh` matrix and the `deploy/` service templates)
and Q-008 proved the published form on a real PostgreSQL, but nothing answers "which version is
this?": there is no version property, no tag, no CHANGELOG, no `verify.sh` (`scripts/` holds only
`publish.sh`), and no CI. The acceptance gate exists as five per-suite filtered runs documented in
development.md §2 — typed by hand, known mainly to the cards that ran them. Roadmap Phase 6 targets
a stable v1.0 release; PRD §10's seven scenarios now have real-machine evidence, so this is the last
gate before the claim goes out.

Deliberate ordering (the user's ledger decision): this card is the **last** card before the tag, not
the first — a gate is only cut after the known, fixable correctness holes are closed. Two cards block
it: B-001 (the captured Dashboard flake would make CI red on arrival) and Q-009 (the SSH half of the
credential story gets a verdict from a real sshd before the tag, because a tag-after-discovery means
v1.0.1 plus users already bitten). Both are **done** (B-001 2026-11-15, Q-009 2026-11-15).

Decisions to make on the card:

- **Version source of truth** — one `Version` in `Directory.Build.props` consumed by Server and
  Worker, surfaced somewhere an operator can find it without the source (binary `--version`, or a
  version line on an existing page) — pick and record. **Decided: 1.0.0, surfaced as a combination**
  (see the 2026-11-15 log entry).
- **`verify.sh` is the development.md §2 gate** — build Release, then the fast suite and the five
  harness filters, failing non-zero on the first red suite; the script and §2 must agree, and §2
  should point at the script as the entry point. Windows equivalent documented or delegated to CI.
  **Decided: the script runs §2's commands verbatim; the Windows gate is the same script under Git
  Bash, proven by CI** (log entry below).
- **CI scope** — GitHub Actions running the §2 gate on push/PR; matrix breadth (Linux minimum;
  Windows/macOS given Q-007's three-OS evidence) and what stays local-only (PublishAot needs OS
  native toolchains) are decided here, with exclusions recorded rather than silent. **Decided: all
  three OSes, one gate each, no suite excluded anywhere** (log entry below).
- **Known issues go in the CHANGELOG** — the deferred hardening cards (B-002, Q-010) and any other
  open limits get honest entries; a v1.0 that pretends to have no known issues is the failure mode
  this card exists to prevent. **Done** — see CHANGELOG.md's known-issues list.

## Acceptance Criteria

- [x] Server and Worker binaries report the same version, stamped at build time from a single
      source, discoverable by an operator without reading the repository.
- [x] `CHANGELOG.md` exists with the v1.0 entry, including known-issue entries for the deferred
      cards (B-002, Q-010) and any other recorded limits.
- [x] `scripts/verify.sh` runs the documented §2 gate in one command and exits non-zero on the first
      red suite; development.md points at it (or the two demonstrably agree).
- [ ] CI runs the gate on push/PR, is green on the revision that gets tagged, and any suite
      exclusions are recorded with reasons.
      *Pending: the workflow is authored (`.github/workflows/ci.yml`, three-OS matrix, recorded
      exclusions) but this repository has not been pushed yet, so no CI run exists. "Green on the
      revision that gets tagged" is verified by the PM/user after the push, from the run for the
      tagged revision — that check cannot be made locally.*
- [ ] Annotated tag `v1.0` on the release revision; the CHANGELOG names it.
      *Pending: the PM cuts the tag after the push/CI protocol is decided; the CHANGELOG's 1.0.0
      entry already names `v1.0`.*
- [ ] The tag is cut only after B-001 and Q-009 are done — recorded here, not just remembered.
      *Recorded here: B-001 and Q-009 are both done (2026-11-15, board moved the same day). The tick
      is claimed at tag time, together with the tag AC — the constraint is satisfied and written
      down, the tag itself has not been cut.*

## Subtasks

- [x] Version stamping (`Directory.Build.props` + surfacing) with a test or smoke asserting Server
      and Worker agree.
- [x] `scripts/verify.sh` + the development.md §2 hand-off.
- [x] `CHANGELOG.md` (v1.0 entry, known issues from B-002/Q-010 and recorded limits).
- [x] CI pipeline + recorded exclusions.
- [ ] The tag itself, after the blockers are done — the last commit-and-tag of the sequence.
      *PM's step, after the push/CI protocol is decided.*

## Dependencies

- B-001 (flake fixed before CI exists, so CI is trustworthy from day one) — done 2026-11-15
- Q-009 (SSH verdict before the tag) — done 2026-11-15

## Test Cases

### TC-001: The gate, one command — PASS

Steps:
1. On a clean checkout of the release revision, run `scripts/verify.sh`; then break something
   (e.g., introduce a failing assertion) and run it again.

Expected:
- Green end to end on the clean checkout; non-zero exit naming the first red suite on the broken one.

Result (observed 2026-11-15, macOS 26.5 arm64, SDK 10.0.403): PASS, both runs.

- **Green run** — `./scripts/verify.sh` exit 0, total 311 s, in order: build (Release) 0 warnings /
  0 errors; Fast suite 1045/1045, 0 skipped, 2 m 23 s; Milestone 8/8 (33 s); Recovery 4/4 (28 s);
  PublishSmoke 2/2 (35 s); PublishAot 2/2 (40 s, including the fixture's new AOT `--version` probe);
  GitSshd 3/3 (20 s). Final line: `verify.sh: gate green - build, fast suite and five harnesses in
  311s.`
- **Red run** — a scratch break in `src/Shared/ProductVersion.From` (pretend the stamp cannot be
  read; compiles clean) and a second `./scripts/verify.sh`: build green, Fast suite red at
  `Zenkins.Tests.ProductVersionTests.The_server_and_worker_binaries_report_one_build_stamp_version`
  (1045 total / 1 failed), the script printed `verify.sh: FAILED - the 'Fast suite' suite went red.
  Its output above names the failing test(s).` and exited **1** with nothing after the fast suite
  run — first red wins. The break was reverted immediately (build 0/0 and the version fact green
  again).

### TC-002: The claim is checkable — PASS locally; the post-push parts recorded as pending

Steps:
1. Run Server and Worker from the release artifacts; ask each for its version; open the tag's
   CHANGELOG entry; check CI's run for that revision.

Expected:
- The same version string from both binaries, matching the CHANGELOG entry for the tag; CI green on
  exactly that revision.

Result (observed 2026-11-15): the local parts PASS. `scripts/publish.sh --worker-aot osx-arm64`
(exit 0) produced the four artifact shapes, and every one answers `--version` with exit 0:
`Server/Zenkins.Server` → `Zenkins Server 1.0.0`; `Worker/Zenkins.Worker` (folder) →
`Zenkins Worker 1.0.0`; `WorkerSingleFile/Zenkins.Worker` → `Zenkins Worker 1.0.0`;
`WorkerAot/Zenkins.Worker` (the NativeAOT single executable) → `Zenkins Worker 1.0.0`. The built
framework-dependent binaries answer identically (`dotnet exec … --version`, asserted permanently by
the fast-suite fact). The CHANGELOG's 1.0.0 entry names `v1.0` and carries the same number.
**Not verifiable locally, on purpose**: the annotated tag does not exist yet (PM cuts it after the
push protocol is decided) and CI has no runs (nothing pushed), so "CI green on exactly the tagged
revision" and "the tag's own CHANGELOG" are the PM/user's post-push verification of AC-4/AC-5.

## Development Log

### 2026-11-14

- Card opened from the user's ledger, which named release engineering "the last gate" for v1.0 and
  sequenced it after B-001 and Q-009. Next step: the version/verify.sh/CI decisions above, then the
  mechanical work.

### 2026-11-15

- **Decision: the version's single source is `<Version>1.0.0</Version>` in `Directory.Build.props`.**
  The .NET SDK writes that property into every assembly's `AssemblyInformationalVersion` — observed
  as `1.0.0+<git sha>`, because the SDK appends the source revision — so there is no generated code
  and no second place to bump. `Zenkins.Shared.ProductVersion` is the one reader and strips the
  `+revision` for display. The read is attribute metadata reflection, which NativeAOT keeps, so the
  AOT Worker can print its own stamp.
- **Decision: surfacing is a combination — a `--version` flag on both binaries plus a version line
  in the shell footer.** Both binaries answer `--version` before anything else runs (no
  configuration, database or key ring needed — a broken deployment still answers; one line, exit 0),
  which is the shell-only operator's surface and the testable one. The shell footer
  (`MainLayout.razor`, sidebar footer, collapsed with the sidebar) shows `CI server · shell preview
  · v1.0.0` on every signed-in page, which is the browser-only operator's surface for a running
  deployment. A Server **info endpoint was weighed and rejected**: Q-006's machine-enforced surface
  inventory would need a new row plus a probe for a read-only string — real surface for no new
  capability. `docs/install.md` gained the operator-facing "Finding the version" section; the
  no-application-level-flags sentence in its §1 now names `--version` as the one flag that exists.
- **The agreeing-version test is one fast-suite fact**:
  `The_server_and_worker_binaries_report_one_build_stamp_version` runs the built framework-dependent
  Server and Worker DLLs through `dotnet exec --version` (the harnesses' own binary-selection rule,
  reusing `MilestoneHost`'s locator) and asserts a five-way stamp agreement (Tests / Server / Worker
  assemblies read from the attribute in-process; both binaries' printed lines). The AOT side is the
  PublishAot fixture's new `--version` probe on the native Worker executable before the scenario
  runs — it passed in this card's PublishAot suite run. Count effect: fast suite 1044 → **1045**;
  no-filter total 1063 → **1064** (1045 + 8 + 4 + 2 + 2 + 3). development.md §2's table and total
  updated to match.
- **Decision: `scripts/verify.sh` is §2 verbatim.** The script `cd`s to the repository root and runs
  exactly §2's seven commands in §2's order (echoing each command line), stopping at the first red
  suite: a non-zero exit prints `FAILED - the '<suite>' suite went red` to stderr and exits 1, so
  nothing after a red suite runs. Green output ends with the gate's wall-clock time. §2 stays the
  written gate (prerequisites, needs, counts) and now points at the script as the entry point, with
  the individual filters kept for understanding; the two change together.
- **Decision (Windows equivalent): the same script under Git Bash, delegated to CI for proof —
  deliberately no PowerShell transcript.** Git for Windows ships a bash, the script uses nothing
  beyond it, and the CI workflow runs `bash scripts/verify.sh` on windows-latest, so the Windows
  gate is the same document rather than a second transcript that can drift. Recorded in the script
  header, §2, and the workflow comments.
- **Decision: CI runs the same gate on all three OSes — no suite excluded anywhere.** Reasons: this
  is the release whose claims (I-003's three RIDs, Q-007's three real machines) span the platforms,
  and I-004 explicitly wants each OS to publish its own RID's AOT artifact, which only the PublishAot
  suite does. What is recorded rather than silent, in `.github/workflows/ci.yml`'s comments:
  GitHub-hosted runners carry the native toolchains (VS C++ / Xcode CLT / clang); the Linux job adds
  two guarded prep steps with reasons — `apt-get install clang zlib1g-dev` (§1's AOT link
  prerequisite, installed not assumed) and `mkdir -p /run/sshd` (Q-009's sshd runs as a non-root user
  process and refuses to start without the privilege-separation directory; the runner images usually
  ship it). Windows has no sshd, so GitSshd's designed named-skip fires there and **the suite still
  exits 0 with the reason recorded** — verified empirically with a throwaway xunit.v3 4.0.3 project
  outside the repository (one fact throwing `SkipException.ForSkip`; `dotnet test` reported
  `skipped: 1`, exit code 0; the probe directory was deleted afterwards), so the Windows gate stays
  green on skips by mechanism, not by hope. macOS at 10× runner billing is accepted for the
  osx-arm64 AOT proof. Mechanics: triggers are push to `main`, push of `v*` tags (the tagged revision
  is the one CI proved), and PRs to `main`; `workflow_dispatch` for manual runs; PRs cancel
  superseded runs, main/tag pushes never do; `fail-fast: false` so one OS's red does not hide the
  others; `timeout-minutes: 45` against an expected ~15; the SDK is pinned through
  `setup-dotnet@v5` with `global-json-file: global.json` (floor 10.0.100, `rollForward:
  latestFeature`); NuGet cached against `Directory.Packages.props`; `actions/checkout@v6`. The
  workflow runs the script, not a transcription of it.
- **CHANGELOG.md written** (new file, English, the repo's tone): the 1.0.0 entry names `v1.0` and
  summarizes what shipped per roadmap phase, then PRD §10's seven scenarios each with their
  real-machine evidence cards (Q-002/Q-005/Q-006/Q-004/Q-007/Q-008, F-018's credential proof, and
  now Q-009's real-sshd verdict), the verification gate, and **Known issues** with one entry per
  recorded limit: B-002 (check-then-act cap; executed-once holds; scheduled v1.0.1), Q-010 (union
  accept vs single-worker coverage → honest starvation; scheduled v1.0.1), Q-009's wrong-host-key
  vocabulary observation (`git-failed` with ssh's own words in the log; accepted), I-004's
  Server-stays-JIT verdict (plus win-x64/linux-x64 AOT analyzer-only evidence), Q-006's adjudicated
  residuals (webhook capability-URL 401 with its mitigations; the prose invariants outside machine
  validation), Q-007's start-on-boot-as-configuration-evidence and the unexercised version-to-version
  upgrade, the macOS worker memory-cap gap (development.md §7), no artifact retention (PRD v1
  scope), the Workers-page preview and the beyond-`push` webhook event gaps, F-018's accepted
  attribution-columns behaviour, and the license (PRD says TBD — nothing invented). The B-002/Q-010
  entries are worded as their cards' to update or remove when they land (v1.0.1).
- **TC-001** (both runs) and **TC-002** (local parts) observed as recorded above. The gate and the
  counts are the release reality: build 0/0, Fast 1045/0/0, Milestone 8, Recovery 4, PublishSmoke 2,
  PublishAot 2, GitSshd 3 — sum 1064, which is what an unfiltered `dotnet test` runs.
- **Observation from the final sweep (recorded, not this card's scope): the per-test temp roots of
  the Pipelines definition/migration tests leak.** The system temp folder had accumulated
  8,425 `zenkins-f014-def-*`, 400 `zenkins-f014-migration-*`, 368 `zenkins-f014-down-*`,
  159 `zenkins-f016-work-*` and smaller counts of the other suites' roots — `DefinitionRun.Dispose`
  (PipelineDefinitionTests) deletes its root but swallows only `IOException`, so a lock-held or
  read-only file leaves the whole tree behind, a few per suite run, across every session since F-014.
  One leaked worker process from a 2026-11-14 F-007-era E2E was still running against a dead server
  (its work root was a `zenkins-f007-e2e-*` temp tree). Final-check cleanup, on the run's own
  evidence: the worker terminated, and every disposable Guid-suffixed test root was removed — the
  only `zenkins*` entries left in the temp folder are the human-authored `zenkins-d006-*.log` /
  `zenkins-doc003-*.log` session notes, untouched. Worth a small hygiene card if it keeps growing
  (catch `UnauthorizedAccessException` too, or delete with retry).
- **Pending after push (recorded, not hidden)**: the first CI run for this revision, CI green on the
  revision the PM tags, the annotated `v1.0` itself. None of the three can exist before the
  repository is pushed; the card stays in Todo at `status: review` until the reviewer passes it and
  the PM handles the tag.

### 2026-11-15 — independent review: PASS on the mechanical work

Adversarial re-derivation of every claim from the files, plus an independent full gate run. The two
ACs that need a push stay unticked by design; the front matter's `blocked` now maps the push gate
(reason/unblock/owner = user). Not committed, not tagged by the review.

- **Version plumbing** — `<Version>1.0.0</Version>` (Directory.Build.props:22) is the only
  product-version property in the build: no csproj/targets overrides anywhere, and the only other
  `1.0.0`s in the tree are the workers' self-reported registration payload version (a different,
  pre-existing field) and build outputs. Generated stamps observed as `1.0.0+1af99e3…` on all three
  assemblies, and `ProductVersion.From` strips exactly at the `+` (ProductVersion.cs:39-40) on that
  real shape. Ordering traced: the Server answers `--version` at Program.cs:32 before
  `CreateBuilder` (line 38) — no configuration, DB or key-ring side effect; the Worker at
  Program.cs:37 before `WorkerConfiguration.FromEnvironment` (line 43); one line, exit 0. The
  AOT-safety claim holds (metadata-only attribute reflection survives ilc) and was proven again in
  the review's own PublishAot run.
- **Program.cs surface** — only the version block, `Main(string[] args)` on the Worker, and the
  trailing `return 0` on the Server (top-level statements became int-returning; graceful shutdown
  was always exit 0). Nothing else moved, in either file.
- **verify.sh** — the seven commands are byte-identical to development.md §2's block (mechanically
  diffed, doc comments stripped); `set -euo pipefail` plus an explicit per-suite check; `cd` to the
  repository root makes it cwd-independent; LF endings + `#!/usr/bin/env bash` (Git Bash-safe); the
  executable bit is set (`-rwxr-xr-x`).
- **CI** — the YAML parses (checked locally, no run possible unpushed); triggers include `v*` tags
  so the tagged revision runs the gate; the matrix really runs all seven suites on all three OSes
  with no exclusion anywhere; the Linux prep steps match §1's documented prerequisites (clang +
  zlib1g-dev; `/run/sshd` for the non-root sshd). The Windows GitSshd named-skip is unconditional in
  the fixture (`SshdGitServer.UnavailabilityReason` returns the Windows reason before any tool
  resolution), so it never depends on ssh-keygen being present — and a fully-skipped suite exits 0:
  the reviewer reproduced the throwaway probe independently (a fresh xunit.v3 4.0.3 project outside
  the repository reported `skipped: 1`, exit code 0; deleted afterwards). `actions/checkout@v6` and
  `setup-dotnet@v5` resolve to live major tags (v6.1.0 / v5.4.0 — one major behind latest each,
  valid, no forced upgrade); `timeout-minutes: 45` is a sane ceiling.
- **CHANGELOG** — all seven PRD §10 scenarios map to real evidence (spot-checked against the cards:
  Q-002, Q-005's compare-and-set/lease, Q-006's 64-surface inventory, Q-007's `AUTO_START`/`enabled`
  + TC-003, Q-008's PostgreSQL 16.15); known issues cover exactly the open cards (B-002 and Q-010
  are the only non-P-001 cards not done) plus the recorded limits found in a sweep of every card;
  the license entry matches the PRD's "开源协议：TBD" with nothing invented; the B-002/Q-010 entries
  carry the "this entry goes with that card" wording both cards anticipate.
- **Counts** — confirmed by the review's own run (below): fast 1045, 8, 4, 2, 2, 3 = 1064, matching
  §2's table and total.
- **The review's own green run** (macOS 26.5 arm64, SDK 10.0.403): `./scripts/verify.sh` exit 0,
  gate line **313 s**; build 0 warnings / 0 errors; Fast 1045/1045, 0 skipped (2m 08s); Milestone
  8/8 (33 s); Recovery 4/4; PublishSmoke 2/2; PublishAot 2/2 (the AOT `--version` probe green);
  GitSshd 3/3 (21 s). Both built binaries answer identically, exit 0: `Zenkins Server 1.0.0`,
  `Zenkins Worker 1.0.0`.
- **The review's own red-path probes (two)**: (1) a constant return from `ProductVersion.From` does
  not compile (CS0162 under TreatWarningsAsErrors) — the script named the build suite and exited 1
  with nothing after it; (2) the card-shaped break (attribute read returning null; compiles clean)
  made the Fast suite red at
  `Zenkins.Tests.ProductVersionTests.The_server_and_worker_binaries_report_one_build_stamp_version`
  (1045 total / 1 failed), the script printed the Fast-suite FAILED line and exited **1** with
  nothing after the Fast suite — first red wins, as recorded in TC-001. Both reverted; rebuild 0/0
  and the version fact green again.
- **One observation, not a defect**: the version fact asserts five-way *agreement*, not a pinned
  value — the reviewer's first (agreement-preserving) scratch break passed it, which is correct by
  design: a pinned value in the test would be a second version string to bump. The human-readable
  `1.0.0` lives only in Directory.Build.props and the prose documents.
- **Tag check**: `git tag -l` is empty (no tag exists, none created by the review); the working tree
  contains exactly the card's claimed artifacts and nothing else.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | None found during the card's work. (One self-inflicted, immediately-fixed miss: the first draft of the `Directory.Build.props` comment contained `--version`, which is an illegal `--` inside an XML comment and failed the build with MSB4024; reworded.) | — | — |

## Verification

- Related files: `Directory.Build.props` (the `Version` property), `src/Shared/ProductVersion.cs`
  (the reader), `src/Worker/Program.cs` and `src/Server/Program.cs` (the `--version` flags),
  `src/Server/Components/Layout/MainLayout.razor` + `src/Server/Components/_Imports.razor` (the
  footer version line), `tests/Zenkins.Tests/ProductVersionTests.cs` (the fast-suite fact),
  `tests/Zenkins.Tests/Milestone/PublishedAotSmokeTests.cs` (the AOT `--version` probe),
  `scripts/verify.sh` (new), `CHANGELOG.md` (new), `.github/workflows/ci.yml` (new),
  `docs/development.md` (§2 hand-off + counts), `docs/install.md` (Finding the version).
- How to run/verify: `scripts/verify.sh` on a clean checkout (TC-001's two runs are the
  demonstration); `dotnet exec <bin>/Zenkins.Server.dll --version` and the Worker equivalent, or the
  published shapes' apphosts; `dotnet test -c Release --filter
  "FullyQualifiedName~ProductVersionTests"` for the agreement fact; the CI run for the tagged
  revision once pushed.
- Results (observed 2026-11-15): TC-001 PASS (green 311 s / red exit 1 naming the Fast suite /
  revert green); TC-002 PASS locally (all four published shapes and both built binaries print
  `1.0.0`, exit 0); build 0 warnings / 0 errors throughout; fast suite 1045, 0 skipped; gate sum
  1064. Post-push parts pending: CI green on the tagged revision, the `v1.0` tag itself.
