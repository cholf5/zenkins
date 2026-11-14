---
id: P-001
title: Release engineering — version, tag, CHANGELOG, verify.sh, CI (the v1.0 gate)
type: release
priority: P1
size: M
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
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
v1.0.1 plus users already bitten).

Decisions to make on the card:

- **Version source of truth** — one `Version` in `Directory.Build.props` consumed by Server and
  Worker, surfaced somewhere an operator can find it without the source (binary `--version`, or a
  version line on an existing page) — pick and record.
- **`verify.sh` is the development.md §2 gate** — build Release, then the fast suite and the four
  harness filters, failing non-zero on the first red suite; the script and §2 must agree, and §2
  should point at the script as the entry point. Windows equivalent documented or delegated to CI.
- **CI scope** — GitHub Actions running the §2 gate on push/PR; matrix breadth (Linux minimum;
  Windows/macOS given Q-007's three-OS evidence) and what stays local-only (PublishAot needs OS
  native toolchains) are decided here, with exclusions recorded rather than silent.
- **Known issues go in the CHANGELOG** — the deferred hardening cards (B-002, Q-010) and any other
  open limits get honest entries; a v1.0 that pretends to have no known issues is the failure mode
  this card exists to prevent.

## Acceptance Criteria

- [ ] Server and Worker binaries report the same version, stamped at build time from a single
      source, discoverable by an operator without reading the repository.
- [ ] `CHANGELOG.md` exists with the v1.0 entry, including known-issue entries for the deferred
      cards (B-002, Q-010) and any other recorded limits.
- [ ] `scripts/verify.sh` runs the documented §2 gate in one command and exits non-zero on the first
      red suite; development.md points at it (or the two demonstrably agree).
- [ ] CI runs the gate on push/PR, is green on the revision that gets tagged, and any suite
      exclusions are recorded with reasons.
- [ ] Annotated tag `v1.0` on the release revision; the CHANGELOG names it.
- [ ] The tag is cut only after B-001 and Q-009 are done — recorded here, not just remembered.

## Subtasks

- [ ] Version stamping (`Directory.Build.props` + surfacing) with a test or smoke asserting Server
      and Worker agree.
- [ ] `scripts/verify.sh` + the development.md §2 hand-off.
- [ ] `CHANGELOG.md` (v1.0 entry, known issues from B-002/Q-010 and recorded limits).
- [ ] CI pipeline + recorded exclusions.
- [ ] The tag itself, after the blockers are done — the last commit-and-tag of the sequence.

## Dependencies

- B-001 (flake fixed before CI exists, so CI is trustworthy from day one)
- Q-009 (SSH verdict before the tag)

## Test Cases

### TC-001: The gate, one command

Steps:
1. On a clean checkout of the release revision, run `scripts/verify.sh`; then break something
   (e.g., introduce a failing assertion) and run it again.

Expected:
- Green end to end on the clean checkout; non-zero exit naming the first red suite on the broken one.

### TC-002: The claim is checkable

Steps:
1. Run Server and Worker from the release artifacts; ask each for its version; open the tag's
   CHANGELOG entry; check CI's run for that revision.

Expected:
- The same version string from both binaries, matching the CHANGELOG entry for the tag; CI green on
  exactly that revision.

## Development Log

### 2026-11-14

- Card opened from the user's ledger, which named release engineering "the last gate" for v1.0 and
  sequenced it after B-001 and Q-009. Next step: the version/verify.sh/CI decisions above, then the
  mechanical work.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | None — card not started. | — | — |

## Verification

- Related files: `Directory.Build.props`, `Directory.Packages.props` (if versioning touches CPM),
  `scripts/verify.sh` (new), `CHANGELOG.md` (new), CI pipeline definition (new),
  `docs/development.md` (§2 hand-off), `docs/install.md` (if operator-facing version discovery lands)
- How to run/verify: `scripts/verify.sh` on a clean checkout; the CI run for the tagged revision.
- Results: not run yet (card creation).
