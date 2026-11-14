---
id: F-019
title: A form post with no bindable fields returns 500 and a stack instead of a validation error
type: feature
priority: P2
size: S
status: done
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

A POST to a form endpoint whose body binds to nothing is refused by that surface's own error vocabulary —
never by a `NullReferenceException` — and no endpoint's failure path can hand a stack trace to a client.

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

- [x] A `POST` to each **form handler that dereferences its bound form** with no bindable fields (and
      with a valid antiforgery token) is **never a 5xx**; it is answered in the vocabulary that surface
      already uses. PM ruling (2026-11-14): HTML form surfaces keep their established
      `302` + `?error=<stable-code>` post-redirect, because F-002/F-003/F-012 pinned that contract and
      tests assert it, and because a bare `400` has no page in this app's status-code handling; only
      the JSON/API-shaped surfaces (the git-credential endpoints without a project id) keep `400` with
      `{field, code, message}`. "Never 5xx with a validation message" is the requirement; `400` is not.
- [x] The same is true for a field name that exists but is empty/invalid — the response names the field,
      it does not crash.
- [x] **Handlers whose bound type is an empty class must not be given a null guard**: binding returns
      `null` for those on the *normal* page submit, so a blanket `null → reject` would break registering
      tokens, enabling/disabling/deleting workers, cancelling runs, webhook rotate/enable/disable/delete,
      deleting secrets and projects, and detaching a credential. The guard applies to the handlers that
      dereference, and the fix is the tolerant shape (`form?.ToInput() ?? empty input`, display fields via
      `form?.X`), not a new early-return.
- [x] **A malformed run-start is refused, not executed.** `StartRunForm` has no required field, so a
      tolerant guard would turn a token-only submission into a real run; PM ruling: a null form there
      redirects with `?runError=field-invalid` and creates nothing.
- [x] No client-visible response body contains a stack trace, exception type name or source path; a
      regression test asserts this for at least the forms endpoints.
- [x] The normal paths still behave: correct fields create/update and redirect as before, and the
      existing suites (`Secrets`, `Projects`, `Workers`, `TenantIsolation`) stay green.
- [x] Each guarded handler is covered by a test that fails if the guard is removed.

## Subtasks

- [x] Enumerate every `[FromForm]` handler and give each a null-form guard with field messages.
- [x] Add the client-visible-leak assertion to the error-handling path.
- [x] Tests: empty form, wrong field names, valid form, and the no-stack-in-body assertion.

## Dependencies

- none (found by Q-008; independent of it)

## Test Cases

### TC-001: Empty and malformed forms are rejected, not crashed — PASS

The 28 `[FromForm]` parameters fall into three groups, and the reviewer verified the split statically:

| Group | Count | Members |
|---|---|---|
| Dereferences its form → tolerant guard added | **11** | Organization switch/rename/invite/change-role/remove; Secrets create/rotate; Projects create/edit; Webhooks create; Runs start |
| Empty class (or reads nothing) → **not touched** | **13** | Secrets delete; Projects delete; Workers mint/revoke/disable/enable/delete; Webhooks rotate/enable/disable/delete; Runs cancel; Git detach |
| Already tolerant since F-013 | **4** | Git credential create/rotate/delete/attach |

The guard shape is the tolerant one (`form?.ToInput() ?? EmptyInput()`, display fields via `form?.X`),
not a new early-return — that distinction is the whole card, because a `null → reject` rule would have
broken the thirteen endpoints whose normal page submit binds `null`. `Projects.ValidationRedirect` now
takes a nullable form, so its echo fields stopped dereferencing. No shared abstraction was introduced:
each file carries a six-line private `EmptyInput()`, mirroring the F-013 precedent; an earlier draft's
problem-document helper was removed.

Result (observed 2026-11-14): PASS. Empty and misnamed bodies now land on the surface's own vocabulary
(`302` → `?error=name-required`, `?webhookError=…`, `?error=member-not-found`, `?runError=field-invalid`),
with no side effects; a manual curl probe against a real process recorded the three shapes (empty body and
`CreateSecretForm.Name` both → `/secrets?error=name-required&name=`, correct fields →
`/secrets?notice=created`) with a `NullReferenceException` count of zero in the server log. The new
`Category=Forms` suite drives every guarded handler through a real form POST (fetch the page, carry its
antiforgery token) and asserts both the stable error code — which proves the handler ran rather than the
framework rejecting the body — and the absence of the side effect. A sweep test enumerates every form POST
from `EndpointDataSource` via `IAntiforgeryMetadata` and asserts all of them stay under 500.

### TC-002: No leak on the failure path — PASS

Result (observed 2026-11-14): PASS, and the test is built so it cannot pass vacuously. It runs in the
**Production** shape (`AuthTestHost` gained an optional environment; the tests pass `Production` plus
`Database:ApplyMigrationsOnStartup=true`), because the Development host intentionally renders the
developer page. Two probes: an `IStartupFilter` appends a throwing middleware **after** `next(app)` so it
sits inside the exception handler and answers only an unrouted path (ordering it the other way would
bypass the handler and produce an empty 500 — which the page assertion would catch), and a real
`POST /secrets/create` whose `SecretProtector` resolves by throwing. Assertions are bidirectional:
`500` + `text/html` + **contains** the error page's `Something went wrong` + **contains none** of five
markers (stack frame, `.cs:line N`, `SomethingException`, framework namespaces, absolute paths), plus the
submitted plaintext is not echoed back, plus `CapturingLoggerProvider` proving the message and type exist
**only** in the log. The reviewer re-measured the five denylist patterns against a real developer-page
stack (5/5 match) and against the real error page (0 false positives).

### TC-003: The guarantees are load-bearing — PASS (mutation evidence)

The reviewer re-ran the mutations independently and restored each file byte-for-byte (sha256 compared
before and after):

| Mutation | Result |
|---|---|
| `Secrets.Create`: `form?.ToInput() ?? EmptyInput()` → `form.ToInput()` | `Forms` **3 failed / 9 passed** |
| Delete the `StartRun` null early-return | `Forms` **2 failed / 10 passed** |
| `Errors` tests: `Production` → `Development` | `Errors` **2 failed / 0 passed** |

The sweep caught both guard mutations on its own, which is what makes it worth keeping.

## Development Log

### 2026-11-14

- Opened from Q-008's finding (reproduced independently by its reviewer, who also widened the trigger to
  a completely empty form). Deliberately not fixed inside a verification card.
- Implemented in three groups rather than as a blanket sweep, after the reviewer's preread measured the
  framework behaviour: `[FromForm]` binding yields `null` when nothing binds — which is the **normal**
  case for the thirteen endpoints whose form type is an empty class, so a uniform `null → reject` would
  have broken minting registration tokens, enabling/disabling/deleting workers, cancelling runs, webhook
  rotate/enable/disable/delete, deleting secrets and projects, and detaching a credential. Only the
  eleven handlers that dereference got a guard, and they got the tolerant shape rather than an early
  return.
- PM rulings that shaped it: the AC's "400-family" was rewritten to **"never 5xx, in the surface's own
  vocabulary"**, because F-002/F-003/F-012 pinned `302` + `?error=<code>` for HTML forms and the app has
  no page for a bare `400`; the JSON/API-shaped git endpoints keep `400 {field, code, message}`. And
  `StartRun` is refused explicitly (`?runError=field-invalid`, nothing created) because all three of its
  fields are optional — a tolerant guard there would have turned a body the server could not read into a
  real run.
- The run-start refusal would have been invisible in the UI, so `ProjectDetail.razor` gained a
  form-level alert (`#run-form-error`) with a single mapping for `RunRequestCodes.FieldInvalid`; the test
  that covers it fails if the alert is deleted.
- No-leak coverage is a **Production** property: the Development host renders the developer page by
  design, so the two new `Category=Errors` tests take an explicit environment, force a fault through an
  `IStartupFilter` appended *inside* the exception handler (ordering matters: outside it would produce an
  empty 500) and through a real form POST whose protector throws, and assert both halves — the generic
  page text is present, and none of five leak markers are.
- Verification (developer runs and independent review runs): `dotnet build -c Release` 0 warnings;
  `Category=Forms` 12, `Errors` 2, `Secrets` 81, `Projects` 69, `Workers` 76, `Organizations` 11,
  `Webhooks` 74, `Runs` 44, `RunUi` 25, `RunLifecycle` 26, `TenantIsolation` 115, `Git` 58,
  `SecretRouteShape` 2, fast suite 1044 passed / 0 failed / 0 skipped. The reviewer re-ran three
  mutations (guard removal in two shapes, environment flip) and restored each file byte-identically.
- Known and intentionally unchanged: the `StartRun` refusal runs before project authorization resolves,
  so an unauthorized project receives the same `302` instead of `403`/`404` — no leak (the location only
  echoes the caller's own id and the following GET is still forbidden/missing), unreachable from the UI,
  and now commented as "the body could not be read before the target was resolved". The sweep genuinely
  mints one registration token as its only persistent side effect (asserted, not a problem). The stack
  frame pattern does not match frames containing `$`, which is fine because it is one of five markers and
  the exception-type marker covers that case.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: the five endpoint files that changed (`Organizations/OrganizationEndpoints.cs`,
  `Projects/ProjectEndpoints.cs`, `Runs/RunEndpoints.cs`, `Secrets/SecretEndpoints.cs`,
  `Webhooks/WebhookEndpoints.cs`), `Components/Pages/Projects/ProjectDetail.razor` (the run-start alert),
  the global exception handling (`Program.cs`) and `Components/Pages/ErrorPage.razor`,
  `tests/Zenkins.Tests/Auth/AuthTestHost.cs` (optional environment), and the two new suites
  `tests/Zenkins.Tests/Forms/FormBindingGuardTests.cs` and
  `tests/Zenkins.Tests/Errors/ClientVisibleFailureTests.cs`.
- How to run/verify: `dotnet build -c Release`; `--filter "Category=Forms"`; `Category=Errors`; the
  affected suites (`Secrets`, `Projects`, `Workers`, `Organizations`, `Webhooks`, `Runs`, `RunUi`,
  `RunLifecycle`, `TenantIsolation`, `Git`); the fast suite; `FullyQualifiedName~SecretRouteShape`. A
  manual curl probe of the three request shapes is described in TC-001.
- Results (observed 2026-11-14; developer runs plus independent review runs and three mutations): build
  0 warnings / 0 errors; Forms 12/12; Errors 2/2; Secrets 81/81; Projects 69/69; Workers 76/76;
  Organizations 11/11; Webhooks 74/74; Runs 44/44; RunUi 25/25; RunLifecycle 26/26; TenantIsolation
  115/115; Git 58/58; SecretRouteShape 2/2; fast 1044/1044 with 0 skipped. `git diff --stat` for the
  delivery is 8 files, ~140 insertions, ~32 deletions; no product route was added by the test probes.
