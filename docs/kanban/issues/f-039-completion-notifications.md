---
id: F-039
title: Completion notifications — signed outbound webhooks on failure, success and recovery
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-19
blocked: none
---

## Goal

A project can notify one or more endpoints when a run ends — failure, success, and
the "back to green" event people actually watch — with a signed JSON payload, a dead
endpoint that never blocks the build, and a visible delivery outcome per endpoint.

## Background

Today the only way to learn that a build broke is to open the UI. Jenkins' equivalent
is a post-build step with a plugin per chat service; this product can do the same job
with one outbound webhook that a chat bridge or a small receiver can consume, using
the same HMAC idea the inbound webhook already uses (F-017) so the receiver can trust
it.

The delivery guarantee is the design crux: the terminal status must not depend on a
third party being up, and a crash between "run ended" and "notification sent" must not
lose the notification. That is exactly the outbox F-026 introduces.

## Acceptance Criteria

- [x] A project can configure N notification endpoints: URL, secret, enabled, and the
      subscribed events (`failed`, `success`, `recovered`, `all`).
- [x] Delivery is at-least-once through a persisted outbox row written in the same
      transaction as the terminal status; a retry has a bounded backoff and a dead
      endpoint ends in a recorded `failed` state, never a stuck run.
- [x] The payload is stable and documented: run id and number (F-036), project name and
      id, branch, commit, status, failure reason, attempt/rebuild origin (F-037), a
      link to the run, and timestamps. It never contains parameters, secret values,
      environment snapshots or credential material.
- [x] Each request carries an HMAC signature header over the body (same scheme as
      F-017's deliveries) plus a delivery id so a receiver can deduplicate a retry.
- [x] `recovered` fires only when the previous run of the same project (and branch, in
      multibranch mode) was a failure and this one succeeded.
- [x] The project page shows per endpoint: enabled, last delivery time, last outcome
      and the last error (truncated), with a "send test" action.
- [x] Redirects are not followed; the response body is read up to a small bound; a
      timeout is enforced; a `https` endpoint is required unless the project opts into
      plain `http` with a warning.
- [x] Disabled projects (F-028) still notify about the runs they already had; they just
      do not start new ones.

## Subtasks

- [x] `NotificationEndpoint` entity (project, url, secret ciphertext, events, enabled,
      last outcome) and its use of the F-026 outbox.
- [x] Dispatcher: outbox consumer with backoff, signature, timeout, bounded response
      read, and a per-endpoint failure record.
- [x] Payload serializer in `Shared` (one shape the docs and the tests pin), with a
      source-generated context.
- [x] Event derivation: terminal status plus the previous same-project/branch status for
      `recovered`.
- [x] UI: a Notifications card, the test-send button, the delivery table.
- [x] Docs: `docs/notifications.md` — payload, headers, signature verification example,
      retry policy.
- [x] PRD §3.16 (new): outbound notification contract.

## Dependencies

- F-026 (the outbox and its dispatcher) — or this card introduces the outbox and F-026
  reuses it; decide once, in the first of the two that ships
- F-036 (numbers in the payload)
- F-037 (rebuild origin in the payload)

## Test Cases

### TC-001: A failed run notifies once, signed

Steps:
1. Configure an endpoint subscribed to `failed`; run a failing pipeline against a
   receiver stub.

Expected:
- Exactly one POST with an HMAC header that verifies against the shared secret.
- The payload's status, number, branch and commit match the run.

### TC-002: A dead endpoint does not block the run or wedge the outbox

Steps:
1. Endpoint points at a closed port; run fails; let the dispatcher retry to its bound.

Expected:
- The run reaches `Failed` immediately (notification is post-commit).
- The endpoint records `failed` with the last error after the retries; the outbox row
  is consumed, not stuck.

### TC-003: Recovery fires only after a failure

Steps:
1. Build fail, fail, success on one project.

Expected:
- Events: `failed`, `failed`, `recovered` (and `success` if subscribed).
- A success after a success is not `recovered`.

### TC-004: No secret material in the payload

Steps:
1. Run a pipeline that uses `${secret:X}` and a password parameter (F-041); inspect the
   delivered JSON.

Expected:
- The payload contains no parameter values, no environment, and no substring of the
  secret's plaintext.

## Notes and Decisions

- Email is out of scope: it needs SMTP configuration, deliverability and abuse stories
  that a webhook receiver does not. A mail bridge can consume this webhook.
- The endpoint URL is Owner-configured and therefore trusted; the card still bans
  redirects and bounds the response, so a mistake cannot become an SSRF pivot.

## Delivered

Schema: `20261118141858_AddCompletionNotifications` (generated with `Database__Provider=Postgres` after the `DbSet`
went in): the `NotificationEndpoints` table, four nullable columns on `OutboxMessages`
(`NotificationEndpointId`, `NotificationEvent`, `NotificationSecretCiphertext`, and `LinkId` relaxed to nullable for
the kind that has no chain edge), two indexes. `has-pending-model-changes` answers "no changes"; `ModelSnapshotTests`
is green.

**One outbox, two kinds.** The obligation rides F-026's table (`OutboxKinds.CompletionNotification`), written in the
same `SaveChanges` as the terminal status in `CompleteAsync`, and inside an explicit transaction around the CAS in
`ReportCanceledAsync` (a timeout is a `Failed` run, so it owes a notification too). Each consumer reads only its own
kind; both read due rows through `SystemQueryAsync`, because a background pass has no request tenant.

**The event is decided where the terminal state is, not at dispatch.** `recovered` depends on the previous
**terminal** run of the same project *and branch* (F-023's semantics, read from the run's own snapshot) - and that run
can be trimmed by F-038's retention before the delivery happens, so the derived event is stored on the row.

**Rotation does not re-sign what is already owed.** The outbox row snapshots the secret ciphertext in force when the
obligation was created, so a pending delivery stays verifiable by a receiver that still holds the old secret. A
delivery staged after the rotation uses the new one.

**Never a stuck run, never an infinite retry.** The dispatcher is a background service; `Notifications:MaxAttempts`
(default 5) with a linear backoff, a per-request timeout, no redirect following, a 4 KiB response read. After the
bound the row settles as `failed` and the endpoint records the last outcome and error.

| Fact | What it pins | Mutation that turns it red |
|---|---|---|
| `A_failed_run_notifies_a_subscribed_endpoint_once_and_signed` | TC-001: one POST, HMAC verifiable with the shared secret over the exact bytes, the wrong secret fails, the payload carries the run's identity/status/branch/commit, a second pass sends nothing, the endpoint records `delivered` | the signature header dropped: `NotificationSignature.Verify` false; the row not settled: a second POST |
| `A_dead_endpoint_ends_in_a_recorded_failure_and_never_retries_forever` | TC-002: a closed port backs off once, then settles `failed` with the transport error on the row and the endpoint; nothing stays pending | the attempt bound not enforced (`>= MaxAttempts` removed): the row retries instead of settling |
| `A_success_after_a_failure_is_recovered_and_a_success_after_a_success_is_success` | TC-003: failed, failed, recovered, success - recovery its own event, one delivery id per terminal state | the recovery derivation forced to `success` (previous status ignored): the third event becomes `success` |
| `A_pending_delivery_is_signed_with_the_secret_it_was_staged_under` | rotation: an owed delivery verifies with the old secret, the next one with the new | signing with the endpoint's current secret instead of the row's snapshot: the first delivery fails to verify with the old secret |
| `The_payload_carries_no_parameter_environment_or_secret_material` | TC-004: no parameter value, no environment value, no shared secret, no field for either | `parameters` added to the payload record: the parameter value appears in the body |
| `An_http_receiver_needs_the_opt_in_and_a_redirect_is_never_followed` | AC-7: plain http refused with `url-insecure` unless the endpoint opts in; a 302 is a failed attempt and the target is never contacted | the http opt-in check removed: the create succeeds; `AllowAutoRedirect` left on: a second request arrives at the target |
| `The_terminal_status_commits_before_any_notification_is_attempted` | the completion report returns while the receiver is still silent, the run is already terminal, the obligation is on disk and still owed; the pass then times out instead of hanging | staging moved into the request path (deliver synchronously): the completion waits on the receiver |
| `A_disabled_project_still_notifies_about_the_run_it_already_had` | AC-8: F-028's brake stops triggers, not the notification of a run it already had | `project.Disabled` honoured in the dispatcher: nothing is delivered |
| `The_settings_card_lists_each_endpoint_its_last_delivery_and_sends_a_test` | AC-6: the create form (Owner + antiforgery + validation), the card's per-endpoint facts including the plain-http warning and no secret, and "send test" delivering an `event: "test"` with `delivered` recorded | the last-delivery column not rendered: `Never delivered` / `delivered` missing |
| `The_hosted_dispatcher_settles_a_row_from_a_scope_with_no_tenant` (F-026's suite) | the production shape of the chain dispatcher: a pass with no request tenant settles its row | the candidate read through the tenant filter: `settled=0`, the row stays `pending` |
| `Notification_endpoints_cannot_reach_another_tenants_project` | Q-006: a foreign project (create) or a foreign endpoint id (all five actions) is one bare 404, the row is untouched, A's page renders no foreign receiver - while a Member of A gets 403 on A's own project and an Owner's own create succeeds | the endpoint resolved without the tenant filter: `Expected NotFound / Actual InternalServerError` |

**Found and fixed while building this card:** F-026's chain dispatcher read its due rows through the tenant filter,
which answers the empty set in a background pass (no request tenant). Its tests passed because they set the tenant in
the same scope; the hosted dispatcher could never settle a row. Fixed the same way as this card's consumer
(`SystemQueryAsync` + the row's own `OrganizationId`), and pinned with
`The_hosted_dispatcher_settles_a_row_from_a_scope_with_no_tenant` so it cannot regress.

**Decisions worth knowing:**
- "The same transaction as the terminal status" is literal on both terminal paths: the completion path stages on the
  same context before its one `SaveChanges`; the worker's canceled/timeout report writes through a CAS
  (`ExecuteUpdate`), so that path opens an explicit transaction around the CAS and the staging.
- Canceled and `Lost` runs stage nothing (the card's vocabulary is failed/success/recovered); `Lost` still counts as
  red for the next run's recovery decision.
- A `success` subscription also matches `recovered` - documented in `docs/notifications.md` and pinned by the
  recovery fact.
- The `test` event exists only for "send test"; it is not subscribable and never staged by a run.
- Endpoint writes are Owner-only (the URL is a place the Server POSTs to); reads are member-level.
- An unknown endpoint id under an owned project answers the same 404 a missing project does, so an id cannot become an
  existence oracle (the audit's usual shape).

Nothing in this card's acceptance criteria is open.

## Close-out verification (2026-11-18)

Notifications arrived on F-026's outbox rather than beside it, which is the decision that makes the delivery
guarantee true instead of aspirational: the terminal status does not depend on a third party being up, and a
crash between "run ended" and "notification sent" loses nothing, because the obligation is written in the same
transaction as the status.

- **Three events, and "recovery" is one of them.** Failure, success, and back-to-green - the last one derived
  from the previous terminal state rather than inferred from a payload field, because the event people watch is
  the transition and not the state.
- **The signature reuses the inbound webhook's HMAC idea**, so a receiver verifies the sender with the same
  mental model it already has, and a rotated signing key does not make deliveries in flight unverifiable.
- **A dead endpoint cannot block a build**: bounded timeout, backoff, and a final settlement as a visible
  failed delivery rather than an unbounded retry.
- **The outbox is now general.** `OutboxMessage.LinkId` became optional because a notification has no chain
  edge - and that surfaced a compile-time coupling in F-026's test, which now selects the links it actually
  means instead of assuming every row has one.
- **Counts.** fast 1643 -> 1655 measured on the merged tree, Postgres stays 9, the slow categories 24,
  everything 1688. One integration red remains at this commit - F-031's `post-failure` endpoint was never
  registered in the Q-006 inventory - and it is fixed in the follow-up commit that closes the series.
