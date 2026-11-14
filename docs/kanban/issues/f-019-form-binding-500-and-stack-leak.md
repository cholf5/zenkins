---
id: F-019
title: A form post with no bindable fields returns 500 and a stack instead of a validation error
type: feature
priority: P2
size: S
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

A POST to a form endpoint whose body binds to nothing returns a 400-family validation response, not a
`NullReferenceException`, and no endpoint's failure path can hand a stack trace to a client.

## Background

Found by Q-008 while exercising the published Server against PostgreSQL, then reproduced independently
during that card's reconciliation:

- `POST /secrets/create` with field names that match no property (e.g. `CreateSecretForm.Name`) → **500**,
  server log `System.NullReferenceException`, stack top `SecretEndpoints.CreateAsync`
  (`src/Server/Secrets/SecretEndpoints.cs:101`, `form.ToInput()`); the response body is the generic
  "Something went wrong" page (the stack stays server-side, which is the one thing already right).
- **A completely empty form with only a valid antiforgery token fails the same way**, so the trigger is
  wider than a malformed client: any POST whose fields are all lost (proxy rewrite, client bug, fuzzer)
  reaches the null path.
- The correct shape (`Name` / `Value`) still works: 302 → `/secrets?notice=created`.
- `application/json` bodies are refused with 415 and do not trigger it.

Root cause: minimal-API `[FromForm]` binding yields `null` when no property can be bound, and this
handler dereferences it without a guard. Other form endpoints in the product happened not to exhibit it
in the probe, but they share the same shape and deserve the same guard.

## Decisions

- **Guard at the handler, not only globally.** Each form handler should treat a null form as a client
  error with a field-level message, so the UI can show something useful.
- **The global handler is the second line.** A 500 is still a 500 for genuinely unexpected faults, but
  the response must never contain a stack trace or an internal type name (this card verifies that,
  rather than assuming it).
- **Sweep, don't spot-fix.** Every `[FromForm]` handler gets the same treatment in one pass; the defect
  was found in one place but the shape is shared.

## Acceptance Criteria

- [ ] A `POST` to each form endpoint with no bindable fields (and with a valid antiforgery token)
      returns a 400-family response with a validation message, never a 500.
- [ ] The same is true for a field name that exists but is empty/invalid — the response names the field,
      it does not crash.
- [ ] No client-visible response body contains a stack trace, exception type name or source path; a
      regression test asserts this for at least the forms endpoints.
- [ ] The normal paths still behave: correct fields create/update and redirect as before, and the
      existing suites (`Secrets`, `Projects`, `Workers`, `TenantIsolation`) stay green.
- [ ] Each guarded handler is covered by a test that fails if the guard is removed.

## Subtasks

- [ ] Enumerate every `[FromForm]` handler and give each a null-form guard with field messages.
- [ ] Add the client-visible-leak assertion to the error-handling path.
- [ ] Tests: empty form, wrong field names, valid form, and the no-stack-in-body assertion.

## Dependencies

- none (found by Q-008; independent of it)

## Test Cases

### TC-001: Empty and malformed forms are rejected, not crashed

Steps:
1. With an Owner session and a valid antiforgery token, POST `/secrets/create` with no fields; then with
   `CreateSecretForm.Name` / `CreateSecretForm.Value`; then with valid `Name` / `Value`.

Expected:
- First two: 400-family with a validation message, no 500, no stack. Third: 302 and a stored row.

### TC-002: No leak on the failure path

Steps:
1. Force a handler exception (test-only) and read the response body.

Expected:
- Generic error page, no exception type, no stack, no source path; the detail exists only in the server log.

## Development Log

### 2026-11-14

- Opened from Q-008's finding (reproduced independently by its reviewer, who also widened the trigger to
  a completely empty form). Deliberately not fixed inside a verification card.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Secrets/SecretEndpoints.cs` (the observed site), every other `[FromForm]`
  endpoint, the global exception handling and error page (`src/Server/Components/Pages/ErrorPage.razor`),
  `tests/Zenkins.Tests/Secrets/*`, `tests/Zenkins.Tests/Projects/*`.
- How to run/verify: `dotnet build -c Release`; the affected suites by category; a manual POST probe with
  curl as described in TC-001; the fast suite.
- Results: not run (card not started).
