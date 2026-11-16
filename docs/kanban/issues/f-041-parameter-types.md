---
id: F-041
title: More parameter types — password, file, run and git ref, with the same snapshot contract
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

Build parameters cover what people actually pass today: a password (never displayed
after trigger), an uploaded file, a choice of a previous run, and a git ref — all under
the contract F-021 established: definitions on the project, resolved values frozen onto
the run, injected into every step's environment.

## Background

F-021 shipped four types (`string`, `text`, `boolean`, `choice`), which cover
configuration but not the cases that force people back to shelling out: a deploy
password must not be echoed on the run page; a build needs a small input file (a
license, a config overlay); "deploy the previous good build" needs to name a run; "build
this tag" needs a ref that is not a branch.

Jenkins has `password`, `file`, `run` and `git parameter`; each has a security or
provenance detail that must not be glossed over.

## Acceptance Criteria

- [ ] Four new types: `password`, `file`, `run`, `gitRef`. Existing types are unchanged
      and old parameter JSON still parses.
- [ ] `password`: the value is masked on the run page and in every list, is registered
      with the redactor so a step that echoes it logs `***`, and is never returned by an
      API (F-027). It is a per-run input, not a stored Secret, and the UI says so.
- [ ] `file`: the trigger form uploads a bounded file (e.g. 1 MiB); it is stored under
      the run's own directory, its **path** is the injected value, and it is not
      downloadable from the run page (it may be a secret). A file the worker cannot
      materialize fails the run with a typed reason before any step starts.
- [ ] `run`: a single-select list of the project's last N runs (`#N status branch`),
      prefilled with the previous run; the injected value is the run's number, plus
      `<NAME>_RUN_ID` and `<NAME>_COMMIT` as convenience variables for the step.
- [ ] `gitRef`: free text, validated against the parameters' rules (no whitespace, no
      leading `-`); when the project pins a commit (webhook/rebuild) the run's own
      commit wins for the checkout and the parameter is only what the step reads —
      the card must document which one is authoritative.
- [ ] All four: defaults for headless triggers (schedules, polls, upstream, API without
      a value), the 32-parameter cap, unknown-name refusal, type-mismatch refusal.
- [ ] The run page shows each value with the right treatment (masked, path, run link,
      ref), and the JSON snapshot on the run cannot be used to recover a password.

## Subtasks

- [ ] `ProjectParameter` type enum + validation and JSON (versioned so F-021 rows are
      readable), server-side resolution for each type.
- [ ] File upload path and storage: the trigger form posts multipart (or a two-step
      upload), the file lands under the run's directory, size bound enforced in the
      request pipeline as well as the handler.
- [ ] Redaction: register password values with the existing redactor and prove it with a
      test where a step echoes the value.
- [ ] Environment injection: value + the two `<NAME>_RUN_ID` / `<NAME>_COMMIT`
      conveniences only for `run`.
- [ ] UI: per-type controls in the trigger form and the parameter editor.
- [ ] PRD §3.11: record the seven types, the masking rules and the file rules.

## Dependencies

- F-021 (parameter definitions, snapshot, injection) — in review

## Test Cases

### TC-001: A password is masked everywhere, redacted in logs

Steps:
1. Define a `password` parameter; trigger with `s3cr3t`; a step echoes the variable.

Expected:
- The run page shows `***` (not the value); the log line shows `***`.
- The API's run read (F-027) omits the value.

### TC-002: A file parameter becomes a readable path

Steps:
1. Define a `file` parameter; upload 3 bytes; a step reads the path and prints the
   content length.

Expected:
- The step prints `3`; the path is inside the run directory; the run page offers no
  download link.

### TC-003: A run parameter names an earlier build

Steps:
1. Two successful runs; trigger with the `run` parameter selecting `#1`.

Expected:
- The step sees the number `1` and `<NAME>_RUN_ID` equal to that run's id.

### TC-004: A ref that cannot exist fails honestly

Steps:
1. A `gitRef` parameter with a nonexistent ref; run.

Expected:
- The clone fails with a `git-*` reason naming the ref; no step runs.

## Notes and Decisions

- A `password` parameter is deliberately not a Secret: it is an input to one run, not
  organization state, and requiring Owner rights to trigger a deploy would be wrong.
  The masking is a display contract, and the redaction is the safety net for logs.
- `file` uploads are per-run and short-lived; the card keeps them out of the artifact
  store so a "download artifacts" link cannot leak an input file.
