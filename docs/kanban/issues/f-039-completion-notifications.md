---
id: F-039
title: Completion notifications — signed outbound webhooks on failure, success and recovery
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
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

- [ ] A project can configure N notification endpoints: URL, secret, enabled, and the
      subscribed events (`failed`, `success`, `recovered`, `all`).
- [ ] Delivery is at-least-once through a persisted outbox row written in the same
      transaction as the terminal status; a retry has a bounded backoff and a dead
      endpoint ends in a recorded `failed` state, never a stuck run.
- [ ] The payload is stable and documented: run id and number (F-036), project name and
      id, branch, commit, status, failure reason, attempt/rebuild origin (F-037), a
      link to the run, and timestamps. It never contains parameters, secret values,
      environment snapshots or credential material.
- [ ] Each request carries an HMAC signature header over the body (same scheme as
      F-017's deliveries) plus a delivery id so a receiver can deduplicate a retry.
- [ ] `recovered` fires only when the previous run of the same project (and branch, in
      multibranch mode) was a failure and this one succeeded.
- [ ] The project page shows per endpoint: enabled, last delivery time, last outcome
      and the last error (truncated), with a "send test" action.
- [ ] Redirects are not followed; the response body is read up to a small bound; a
      timeout is enforced; a `https` endpoint is required unless the project opts into
      plain `http` with a warning.
- [ ] Disabled projects (F-028) still notify about the runs they already had; they just
      do not start new ones.

## Subtasks

- [ ] `NotificationEndpoint` entity (project, url, secret ciphertext, events, enabled,
      last outcome) and its use of the F-026 outbox.
- [ ] Dispatcher: outbox consumer with backoff, signature, timeout, bounded response
      read, and a per-endpoint failure record.
- [ ] Payload serializer in `Shared` (one shape the docs and the tests pin), with a
      source-generated context.
- [ ] Event derivation: terminal status plus the previous same-project/branch status for
      `recovered`.
- [ ] UI: a Notifications card, the test-send button, the delivery table.
- [ ] Docs: `docs/notifications.md` — payload, headers, signature verification example,
      retry policy.
- [ ] PRD §3.16 (new): outbound notification contract.

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
