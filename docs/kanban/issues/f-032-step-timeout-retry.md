---
id: F-032
title: Step-level timeout and retry — a hung or flaky step is the pipeline's problem, not the run's
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A step can declare its own timeout and a bounded retry count, so a hung command or a
flaky network call is handled where it happens and reported as such, instead of
consuming the whole run's deadline.

## Background

Only the run has a timeout today (`Runs:DefaultTimeoutMinutes` with a per-run
override); a step that hangs until the run deadline makes the run look like a
timeout when one step actually broke. Jenkins wraps steps in `timeout`/`retry` and
this product's executor already kills a process tree on cancellation, so both
mechanisms have their plumbing — what is missing is the per-step declaration and the
honest reporting.

## Acceptance Criteria

- [ ] Spec adds `timeoutSeconds` (1..86400) and `retry` (1..5 attempts) per step;
      Lua accepts `timeout = 600, retry = 2`; the Freestyle editor has both fields.
- [ ] A step that exceeds its own timeout is killed (process tree included), fails
      with the typed reason `step-timeout`, and does not consume other steps'
      budgets; the run continues as a normal failure (honoring
      `continue_on_error`).
- [ ] The step timeout is clamped by the run's remaining deadline — the earlier of
      the two wins — and the page/logs say which limit fired.
- [ ] Retry attempts: a failed or timed-out attempt is retried up to the declared
      count; a cancellation never retries; each attempt appends a delimited section
      to the step's log, and the step record carries the attempt count and the final
      exit code.
- [ ] The step list stays 1:1 with the definition (one record per declared step, not
      one per attempt), so run pages, artifacts and the frozen spec keep their
      meaning.
- [ ] Secret references are resolved once per step, not per attempt: a retry never
      re-fetches material (no mid-step rotation, no double redaction registration).
- [ ] Default behaviour is unchanged: a step without either field behaves exactly as
      today.

## Subtasks

- [ ] Spec: two nullable fields with schema validation and Lua sugar; envelope
      acceptance rules unchanged in version (additive optional fields).
- [ ] Executor: per-attempt process launch with its own timeout token, combined with
      the run's cancellation token and deadline via a linked source; attempt loop;
      attempt delimiters written through the existing step log sink.
- [ ] Failure reasons: add `step-timeout` and `step-retry-exhausted` to
      `PipelineFailureReasons`, plus the run page's wording.
- [ ] Step records: `Attempts` and the final exit code on the existing step row (no
      schema fork).
- [ ] Freestyle editor: numeric fields with server-side parsing/validation.
- [ ] `docs/lua-runtime.md` §5.3 and PRD §3.3.5: record the two knobs and the
      clamping rule.

## Dependencies

- F-009 (cancellation propagation, deadline) — accepted

## Test Cases

### TC-001: A hanging step fails fast with step-timeout

Steps:
1. Define one step that sleeps 30s with `timeout = 2`, followed by a normal step,
   and a 60-minute run timeout.

Expected:
- The step fails within ~2s with reason `step-timeout`.
- The following step did not run (default stop); the run's failure reason names the
  step timeout, not the run deadline.

### TC-002: A flaky step is retried and can still succeed

Steps:
1. A step whose script fails the first attempt and succeeds the second, with
   `retry = 2`.

Expected:
- One step record, `Attempts = 2`, final status success, exit code 0.
- The log shows two attempt sections; the run is `Success`.

### TC-003: Cancel is not retried

Steps:
1. Start a step with `retry = 3`; cancel the run during the first attempt.

Expected:
- No further attempt starts; the run is `Canceled`; the step's record shows one
  attempt.

## Notes and Decisions

- Rejected: a per-attempt step record. It would break "step N of the frozen spec"
  identity used by artifacts, conditions and the page's merge logic.
