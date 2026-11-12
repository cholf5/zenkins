---
id: F-008
title: Real-time log streaming with incremental persistence
type: feature
priority: P1
size: L
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

While a run executes, its stdout/stderr lines flow Worker → Server over SignalR, are persisted
incrementally (never buffered whole in server memory), and reach a browser watching the run page
live; after completion the full log is viewable and downloadable.

## Background

PRD §3.6: Worker collects process output, Server persists and forwards; logs go to browsers over
SignalR; must support large output volume ("不能将整个日志文件长期保存在 Server 内存中"). The Worker
already needs a SignalR connection for cancellation (F-009), so the upstream transport is SignalR
(Microsoft.AspNetCore.SignalR.Client for the Worker — add to `Directory.Packages.props` per CPM).

Decisions:

- Persistence: `BuildLog` rows `(RunId, Seq, Stream, Message, TimestampUtc)` appended in batches
  (size/time based flush). `Seq` is a per-run monotonic sequence assigned by the Worker, so
  reconnect/duplicate delivery can be ordered and deduplicated server-side.
- Worker-side buffer is bounded (drop-oldest beyond a cap, e.g. 10k unsent lines) and a synthetic
  `"<N lines dropped while reconnecting>"` marker line records the loss honestly.
- Browser view: the run detail page subscribes to the run's SignalR group and appends lines as they
  arrive (stdout/stderr tagged). Fancy viewer affordances (pause, search, per-step) are Phase 5.
- Download: `GET /runs/{id}/log/download` streams the persisted log ordered by `Seq`.
- Access: only members of the run's Organization may subscribe to a run group or download; the
  Worker's upstream hub authentication uses its worker token. Cross-org access is 403/404 and never
  leaks lines.

## Acceptance Criteria

- [ ] Worker streams stdout/stderr lines (tagged) to the Server over SignalR with a per-run
      monotonic `Seq`; reconnects use the sequence to avoid duplicates/ordering bugs.
- [ ] Server persists lines incrementally in bounded batches; a 10k+ line run completes with every
      line present exactly once, in `Seq` order, in the database — and the server's memory profile
      stays bounded (no whole-log buffering).
- [ ] The run detail page shows lines live as they are produced (no manual refresh), stdout and
      stderr distinguishable, and shows the full history when opened for a finished run.
- [ ] Full-log download matches the persisted log byte-for-byte modulo encoding, ordered by `Seq`.
- [ ] Access control: foreign-org user cannot subscribe or download; unauthenticated hub
      connections are rejected; worker token cannot read another org's runs.
- [ ] Dropped-line marker appears when the worker's buffer overflows during a disconnect
      (synthetic line, honestly labelled).

## Subtasks

- [ ] `BuildLog` entity + migration + batched append writer (system scope for appends by worker).
- [ ] Worker→Server hub (token auth) + worker-side streaming client with bounded buffer, reconnect
      and `Seq`-based resume.
- [ ] Browser-side subscription on the run detail page + history rendering + download endpoint.
- [ ] Access-control plumbing (org membership checks on hub + download).
- [ ] Tests (`Category=LogStreaming`): volume/order/exactly-once, bounded memory assertion
      (batch flush), reconnect duplicate handling, access control, download fidelity, live-delivery
      integration test.

## Dependencies

- F-006
- F-007 (line callbacks to stream)

## Test Cases

### TC-001: 10k lines, exactly once, in order

Steps:
1. Execute a run whose child emits 10,000 numbered lines split across stdout/stderr.

Expected:
- Database holds 10,000 rows with `Seq` 1..10000 in order; download endpoint returns all lines in
  order; run page history renders them.

### TC-002: Reconnect without duplication or loss (within buffer)

Steps:
1. Drop the worker's connection mid-run below the buffer cap, then reconnect.

Expected:
- No duplicate rows (Seq dedupe/order), no gap within the buffer cap; log complete.

### TC-003: Buffer overflow is honest

Steps:
1. Disconnect the worker and force more lines than the buffer cap.

Expected:
- Overflowing lines are dropped, a `"<N lines dropped…>"` marker row exists, memory stays bounded.

### TC-004: Access control

Steps:
1. As a user of Organization B, request A's run log download and hub group.
2. Connect a worker token of B to A's run stream.

Expected:
- 403/404 and rejected hub join; no log bytes transferred; A's data unchanged.

## Development Log

### 2026-11-13

- (pending implementation)

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: (pending)
- How to run/verify: `dotnet build -c Release` && `dotnet test -c Release --filter "Category=LogStreaming"`
- Results: (pending)
