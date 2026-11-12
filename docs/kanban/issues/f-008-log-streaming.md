---
id: F-008
title: Real-time log streaming with incremental persistence
type: feature
priority: P1
size: L
status: done
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

- [x] Worker streams stdout/stderr lines (tagged) to the Server over SignalR with a per-run
      monotonic `Seq`; reconnects use the sequence to avoid duplicates/ordering bugs.
- [x] Server persists lines incrementally in bounded batches; a 10k+ line run completes with every
      line present exactly once, in `Seq` order, in the database — and the server's memory profile
      stays bounded (no whole-log buffering).
- [x] The run detail page shows lines live as they are produced (no manual refresh), stdout and
      stderr distinguishable, and shows the full history when opened for a finished run.
- [x] Full-log download matches the persisted log byte-for-byte modulo encoding, ordered by `Seq`.
- [x] Access control: foreign-org user cannot subscribe or download; unauthenticated hub
      connections are rejected; worker token cannot read another org's runs.
- [x] Dropped-line marker appears when the worker's buffer overflows during a disconnect
      (synthetic line, honestly labelled).

## Subtasks

- [x] `BuildLog` entity + migration + batched append writer (system scope for appends by worker).
- [x] Worker→Server hub (token auth) + worker-side streaming client with bounded buffer, reconnect
      and `Seq`-based resume.
- [x] Browser-side subscription on the run detail page + history rendering + download endpoint.
- [x] Access-control plumbing (org membership checks on hub + download).
- [x] Tests (`Category=LogStreaming`): volume/order/exactly-once, bounded memory assertion
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

**`BuildLog` entity + migration.** `src/Server/Data/Entities/BuildLog.cs`: columns
`(RunId, Seq, Stream, Message, Timestamp)` with **`OrganizationId` as a direct column** and the
**composite primary key `(RunId, Seq)`** — the key *is* the reconnection-deduplication authority,
so the database (not a pre-flight query) absorbs a reconnecting worker's overlapping re-delivery,
and the key also serves every read (`WHERE RunId = x ORDER BY Seq` needs no extra index).
Why a direct org column instead of a filter through `BuildRun`: (a) it keeps the tenant discipline
single-shaped (`ITenantOwned` + column filter, exactly like every other tenant-owned entity — the
Q-001 harness accepted it with one new probe row and two new probes); (b) log reads are the
highest-volume queries in the product (history chunks, the download stream) and a join-based
filter would pay a `BuildRun` join on each; (c) the value is written once, from the run row the
worker hub has already validated, never from a client. `Stream` is a checked string
(`StdOut`/`StdErr`), `Message` deliberately has no max length (a child can emit arbitrarily long
lines, same reasoning as `BuildRun.CommandJson`). Migration `AddBuildLogs` generated against
PostgreSQL per the house rule (uuid/timestamptz/varchar type names), FKs cascade from both
`Organizations` and `BuildRuns` (same double-path shape the worker tables already use).

**Worker → Server hub with token authentication (`/hubs/worker-logs`).**
`WorkerTokenAuthentication.cs`: a small custom authentication scheme (`Zenkins.WorkerToken`) whose
handler calls the exact credential machinery of the F-004/F-006 HTTP API
(`WorkerService.AuthenticateWorkerAsync` — hash lookup through the system scope, token row selects
the tenant, revocation checked). Reading both `Authorization: Bearer …` (negotiate, long polling)
and the `access_token` query string (WebSocket handshake) mirrors what JwtBearer does for the same
reason. With `[Authorize(AuthenticationSchemes = …)]` on `WorkerLogHub`, an unauthenticated or
invalid negotiate is a **bare 401** — the same no-body, no-tenant-detail answer as every worker
HTTP endpoint — before any hub method can exist. The claims carry worker id + organization id
(never the plaintext token). `WorkerLogHub.AppendLinesAsync(runId, lines)` re-authenticates the
per-connection token (revocation recheck — connections outlive heartbeats) and re-reads the run
through the token-selected tenant: a foreign run is indistinguishable from a missing one (same
generic rejection), and a run assigned to a *different* worker of the same organization is refused
as well — only the executing worker streams a run's log. Two wire-contract discoveries worth
recording: hub methods must NOT take a `CancellationToken` parameter (invocation binding counts
every declared parameter against the client's argument list — `InvalidDataException: Invocation
provides 2 argument(s) but target expects 3`; `Hub.ConnectionAborted` carries the same signal),
and the client's `SendAsync` is fire-and-forget (`InvokeAsync` is what awaits the server's
completion — the difference is both the backpressure story and whether a server rejection is even
observable).

**Worker side: dual-path output + bounded buffer + Seq resume.** `src/Worker/LogStreamClient.cs`:
one shared `HubConnection` for the whole worker process (created after token acquisition in
`WorkerRunner.RunAsync`, its reconnect loop running next to the heartbeat loop with the shared
capped-exponential backoff). `SendLinesAsync` uses `InvokeAsync` — it completes only when the
server finished the append, so a persisting server paces the worker (natural backpressure keeps
server memory bounded) and a definitive rejection surfaces as a `HubException`. Raises
`Connected` after every (re)connect and exposes `HasEverConnected`.
`src/Worker/RunLogBuffer.cs`: one per-run buffer. `Emit` (called from both process reader threads
under a lock) assigns the per-run monotonic `Seq` **at emit time starting at 1** and appends;
beyond `ZENKINS_LOG_BUFFER_LINES` (default 10 000, 1..1 000 000, pinned in
`WorkerConfigurationTests`) the OLDEST unsent line is dropped and counted. Drops are reported by
the synthetic marker line `"<N lines dropped while reconnecting>"`, synthesized on the next send
after drops occurred with the next sequence number (it was written at reconnect time, after the
survivors were captured) — and the marker never displaces a buffered line, so every real line
survives and the marker's N says exactly how many were not sent. A lost response (send threw) puts
the batch back at the front; the resend after the reconnect delivers the *same* sequence numbers
and the server's key absorbs the overlap — at-least-once on the wire, exactly-once in the table. A
definitive server rejection (the "due to an error on the server" completion: unknown/foreign run,
run assigned elsewhere, dead token) can never succeed on retry: the batch and everything buffered
behind it is dropped with one clear stderr line and the drain ends. `ZENKINS_LOG_BUFFER_LINES` is
the card's bounded buffer, configurable because the tests (and small devices) want smaller values.
`WorkerRunLoop` now feeds each line to **both** sinks: the F-007 temp files (kept, transition-period
debugging aid) and the buffer; a drain task ships flush-sized batches (100, matching the server's
default flush size) concurrently with process execution, and the completed report is sent only
after the drain finishes — the same cleanup-before-report discipline F-007 established — with one
guard: when the upstream never connected during this worker's lifetime the report proceeds anyway
(a connection that may not exist must not starve the run's terminal transition; the pump keeps
retrying in the background).

**Server-side incremental persistence (`RunLogIngest`).** One singleton writer: per-run pending
lists capped at `Logs:FlushLineCount` (default 100), flushed inline when a full batch accumulates
or by a one-shot timer after `Logs:FlushIntervalMilliseconds` (default 250 ms; the timer disposes
itself when the buffer is empty, so an idle run costs nothing). A burst is split into flush-sized
chunks *before* buffering, so even a 10k-line single call costs 100 bounded batches and no run's
pending state ever exceeds the flush size. Each flush: one SaveChanges for the batch (system scope,
`OrganizationId` stamped from the validated run row — the LeaseSweeper's cross-tenant discipline;
the ingest opens its own DI scope per flush since it is a singleton); if the database rejects a row
because `(RunId, Seq)` already exists, the violation falls back to a per-batch "which Seqs does the
database already hold" read and only the missing lines are inserted. Persisted **first**, then
broadcast to the run's browser group (`IRunLogClient.ReceiveLines`) — a browser only ever sees
durable lines, and a later join's history read cannot miss anything already broadcast. A failed
broadcast is logged, never un-persists. Statistics (`TotalAppended` / `FlushCount` /
`MaxBatchSize`) are the memory-boundedness observability seam the test asserts on.
`RunService.CompleteAsync` calls `CompleteRunAsync(runId)` after recording the terminal state:
final tail flush + buffer eviction, so a completed run leaves neither lines behind nor memory
behind. Also within-batch `DistinctBy(Seq)` (a misbehaving sender must not poison one SaveChanges).

**Browser subscription (`/hubs/run-logs` + run detail page).** The panel runs in the interactive
circuit and connects a server-side `HubConnection` to the same-process hub — a circuit cannot
replay the auth cookie, so `RunLogAccess` mints a **subscription ticket**: a data-protected,
self-expiring (8 h, matched to the cookie window) value binding the user id and their active
organization, minted by the run detail page from the resolved session (the page is the only
minter) and presented as a bearer token. The `Zenkins.RunLogTicket` scheme authenticates the
connection (forged/garbled/expired → bare 401); `JoinRunAsync(runId)` then re-validates against the
database — the caller's membership of the ticket's organization (a revoked member cannot join with
an old ticket) and that the run belongs to it (foreign run id indistinguishable from missing,
generic rejection, no existence oracle) — before `Groups.AddToGroupAsync`. Groups are per run id
(`run:{runId:N}`); org membership is a join-time check, not group naming. Joining streams the
persisted history in `HistoryLines` chunks (query projected straight into the wire record, ordered
by `Seq`), **after** the group join, so lines flushed while the history streams arrive as live
messages instead of being lost. The panel (`RunLogPanel.razor` + `RunLogViewModel`) stores lines
per Seq (SortedDictionary) — history chunks and live batches can interleave and overlap, the Seq
merge makes arrival order irrelevant and duplicates collapse — and re-joins on SignalR's automatic
reconnect (groups are per connection). stdout lines render plain, stderr in red
(`bb:text-red-500`, verified present in the compiled BlazorBlueprint CSS); a download link points
at the endpoint below. Pause/search/per-step stay Phase 5.

**Download endpoint.** `GET /runs/{runId:guid}/log/download` (cookie-authenticated in
`RunEndpoints`): tenant from the session (never the URL), foreign/unknown id → plain 404 with an
empty body (the status-pages re-execute does not apply — not an HTML navigation, so no leak-shaped
HTML body either). Streams the persisted log ordered by `Seq` through
`RunService.StreamRunLogAsync` (async-enumerator from the context, `Results.Stream` writer with
`NewLine = "\n"` pinned so the file is identical on every platform),
`Content-Disposition: attachment; filename=zenkins-run-{runId}-log.txt`, content type
`text/plain; charset=utf-8`.

**Tests (`Category=LogStreaming`, 17) + 4 config pins + 3 tenancy probes.**
`RunLogBufferTests` (worker unit, fake sink): monotonic Seq from 1, drop-oldest with honest
counting, the marker text/Seq/stream, every-loss-marked, lost-batch resend with identical Seqs,
and a 4 000-line two-thread emit race proving unique gapless sequences under the buffer lock.
`RunLogStreamingTests`: 10 000 numbered lines (stdout/stderr alternating) through the real hub
endpoint land exactly once in Seq order; the ingest statistics prove the flush stayed bounded
(`FlushCount ≥ 100`, `MaxBatchSize ≤ 100`, never a 10k batch); the download is byte-for-byte the
expected 10 000 lines with the run id in the file name; and a foreign user's download is a 404
with zero bytes. `RunLogReconnectTests`: real disconnect (connection disposed), reconnect with an
overlapping re-delivery (45..60 re-sent after 1..50) plus continuation (61..100) → exactly 100
distinct rows. `RunLogLiveTests`: a subscriber that joins before the first line receives them
while the run is **Running** (asserted between batches), then the full 50 exactly once in order;
a finished run delivers its full history on join (30 mixed-stream lines, ordered, on the history
channel). `RunLogAccessTests`: unauthenticated negotiate is a bare 401 on both hubs; connection
without a credential is rejected; a foreign organization's member with a perfectly valid B-ticket
cannot join A's run group and receives zero lines while A streams (positive control: B can join
B's own run); a worker token of organization B cannot stream into A's run (and not into an
unassigned B-run either — only the executing worker streams), while A's worker streams its own run
without friction; a garbage ticket is rejected; and `RunLogAccess` rejects forgery, expiry
(zero-lifetime ticket) and cross-protector tickets. Config pins extend `WorkerConfigurationTests`
(default 10 000, parsing, out-of-range errors). The tenant harness (`TenantIsolationProbeTests`)
gains the `BuildLog` read probe, the cross-tenant write rejection probe, and a composite-key
Find-after-tenant-switch probe; `TwoOrgsFixture` seeds one log row per organization.

**Process-level E2E extension (`WorkerRunProcessE2ETests`).** After run 1 reaches Success, the test
opens the server's SQLite file and asserts the three `echo-each` lines were streamed upstream and
persisted in Seq order (`StdOut`, `first`/`second`/`third`) — the real worker → real server → real
SignalR → database path, end to end.

**Red proof (restored immediately afterwards, re-verified green).** The `(RunId, Seq)` primary key
was replaced by a surrogate `Id` key plus a *non-unique* `(RunId, Seq)` index (model + a temporary
migration, so the test databases actually carried the weakened schema), and the ingest's
Seq-deduplication (`DistinctBy`) was removed. `Reconnecting_worker_resending_overlapping_sequences
_creates_no_duplicates` went red: **`Assert.Equal() Failure: Values differ. Expected: 100, Actual:
106`** — the overlapping window (45..60) persisted as six duplicate rows, exactly the failure the
unique key exists to prevent. (The first red attempt removed only the index and kept
`DistinctBy`: the test stayed green because the test's sub-flush-size batches coalesced into one
flush, whose within-batch dedupe absorbed the overlap — recorded because it demonstrates precisely
why *both* layers exist: the key is the authority for distinct flushes, `DistinctBy` covers one
SaveChanges.) Everything restored (composite PK, `DistinctBy`, temp migration deleted, snapshot
regenerated), the reconnect test and the whole suite green again, and the PostgreSQL contract gate
reports no pending model changes.

**Known gaps.**
- The SQLite provider materializes one history/download query's result set (it has no true async
  streaming); PostgreSQL (production) streams natively. The *live* ingest path is chunk-bounded on
  both providers; the history read is a bounded read of persisted rows, not a growing buffer.
- The run detail panel's DOM wiring is not browser-tested (no browser in the test suite); the hub
  transport is integration-tested end to end and the panel's merge/dedupe logic is unit-tested via
  `RunLogViewModel`.
- Lines still unsent when the worker process shuts down mid-run are lost with the run (F-010's
  Lost territory, same as F-007's killed-process story); the honest markers cover buffer overflow
  while the connection is down ("while reconnecting", the card's scenario) and while the
  connection is up under backpressure (F4 review fix), which is what the card specifies.
- A run that ends `Canceled` (F-009) does not yet call `CompleteRunAsync` — its tail still flushes
  within 250 ms, but F-009 should call it for the eviction symmetry the completed path has.
- The browser hub re-validates membership at join time only; a membership revoked while a tab
  stays subscribed keeps receiving until the page is reloaded (same lifetime rule as the circuit's
  other read paths).
- The E2E (like F-005/F-007's) needs `dotnet build -c Release` before running; it locates the
  newest built Server/Worker/ArgvEcho binaries.

### 2026-11-13 — review fixes

**F1 (major): the definitive-rejection discriminator never matched.** Before: the worker's
`RunLogBuffer.IsDefinitiveRejection` recognized the server's refusal by the substring
"due to an error on the server" — believed to be SignalR's stable completion text for a thrown
hub method. Probe against the real endpoint: ASP.NET Core 10's hub dispatcher (detailed errors
off) completes a hub-method failure with
`An unexpected error occurred invoking 'AppendLinesAsync' on the server. HubException: <message>`
— and for the rejection cases the `<message>` is our own HubException text ("The run does not
exist in this worker's organization."). Neither half contains "due to an error on the server"
(that string exists in the framework only as the dispatcher's *log* template, "Failed to invoke
'X' due to an error on the server."), so the drain classified every definitive rejection as
retryable: it re-sent the same batch forever, the drain never ended, `FinishDrainAsync` waited
on it forever (HasEverConnected true), and the run stayed Running permanently.
After: **the rejection protocol is a machine prefix** (`LogStreamRejection.Prefix =
"zenkins:rejected:"`, new shared file so Worker and Server compile against one contract). Every
definitive refusal in `WorkerLogHub` — unknown/foreign run, run assigned to another worker, dead
worker token, connection without a worker credential — throws `HubException(LogStreamRejection
.For(reason))`; the client matches by **containment**, not start-of-message, because the 10.0
wrapper puts the prefix in the middle of the completion text. The prefix is machine-namespaced
and the framework's wrapper text can never contain it, so containment has no false positives and
survives wrapper changes in either direction (verbatim forwarding, wrapping, detailed errors
on). A completion without the prefix — transport failure, connection loss, a non-hub server
fault in the generic wrapper — keeps the buffered lines and retries. Red proof: the old substring
discriminator restored → the new integration test (`A_foreign_run_rejection_ends_the_drain
_bounded_with_a_clear_stderr_line`, real server + real hub + org-B worker token appending to
org-A's run through the real `RunLogBuffer`/sink) failed on its bounded 30 s await (the infinite
resend loop) → fix restored → green, with the stderr line naming the prefixed rejection reason
and the buffer terminated (0 pending, no renumbering, zero rows in the foreign table).

**F2 (minor): dead token — no `Context.Abort()`, the prefixed answer is the fix.** The hub
aborted the connection *before* throwing, so over the production WebSocket transport the client
saw a transport loss instead of the answer, the drain retried forever behind a credential that
can never work again. Chosen fix (of the two offered): do not abort — throw the prefixed
rejection; the drain ends on it (the F1 mechanism), and the authentication middleware guarantees
a *next* connection with the dead token is a bare 401 at negotiate time, so the dead credential
cannot accumulate connections. Test pin (`A_dead_token_fails_observably_and_cannot_reconnect`):
a transport note is due — the TestServer long-polls, and long polling re-authenticates *every*
request, so the middleware's bare 401 on the next poll/send is what the test observes (the
prefixed per-method answer is the WebSocket path, covered by the same forwarding evidence the
foreign-run test pins); the test also pins the fresh-negotiate 401 and that exactly the
pre-revocation batch landed.

**F3 (minor): post-eviction appends were a silent-loss window plus a PendingRun leak.** The
windows, all real in the old code: (a) the flush timer's callback looked the run up in the
dictionary — a callback firing after `CompleteRunAsync`'s eviction found nothing and silently
dropped the pending lines it had just swapped out; (b) a multi-chunk `AppendAsync` could be
evicted mid-loop by a concurrent completion, leaving later chunks on an orphaned object whose
timer would never fire for the same reason; (c) a failed timer flush re-inserted its batch into
a run that had since been evicted (same lookup, same silent loss); (d) a late append *after*
eviction (`WorkerRunLoop`'s HasEverConnected escape hatch lets the completed report precede a
drain still retrying) recreated dictionary-rooted `PendingRun` state that nothing would ever
evict again — the leak. The fix: `CompleteRunAsync` raises an eviction flag (capped at 100 000
entries; it is a leak guard, not a correctness authority) **before** removing the state, so an
append racing the eviction either sees the flag or has its lines inside the state the eviction
takes — never in between; a flagged append flushes its lines inline through a global gate
(seeded with the hub-validated organizationId, logged at Information — nothing is silently
dropped, no state is created); `TimerFlushAsync` now receives the `PendingRun` object instead
of looking the run up, so orphaned chunks still flush through their own timer; and the
`FlushGate.Dispose` is gone (disposal racing mid-append/orphan flush paths turned races into
`ObjectDisposedException`s; a gate that never creates its WaitHandle needs no Dispose). Red
proof: the flag check disabled → the late-append test failed on `PendingRunCount == 0` (the
leak) while the lines still persisted → restored → green. The Canceled-run state-linger known
gap (F-009 not calling `CompleteRunAsync`) is unchanged.

**F4 (minor): the drop marker now says where the drops happened.** Overflow drops also happen
while the connection is perfectly fine (the server persists slower than the child produces);
labelling those "while reconnecting" was wrong. The buffer takes an `isConnected` probe
(`WorkerRunLoop` passes `() => logStreams.IsConnected`; called under the buffer lock — safe, the
buffer gate is never nested inside the client gate) and accounts drops separately: reconnect-gap
drops keep the card's `<N lines dropped while reconnecting>`, connected drops become `<N lines
dropped under backpressure>`, and a run that did both emits both markers in a fixed order
(reconnecting first), each with the next Seq, neither displacing a buffered line. Without a
probe (existing unit tests) every drop counts as a reconnect gap — the card's original wording
stays the default.

**F5 (backfill, no code change): the panel's memory/render profile.** The interactive circuit
holds one `RunLogViewModel` entry per distinct Seq — i.e. the whole log in server memory per
subscribed circuit — and every arrival re-snapshots and re-renders the entire `<pre>` block
(O(n) per batch; a 10k-line history read streams in 500-line chunks, each triggering a full
re-render). Bounded at the card's scale and honest about it; virtualization, pause, paging and
per-step views are Phase 5 territory and were deliberately deferred when the card was scoped.

**Review leftover 1 (worker crash/restart resets Seq to 1 — why it cannot collide today).** Seq
is assigned per run at emit time by the *executing* worker, and lines are only ever emitted by a
worker after its `accepted` report. The only server path that hands a claimed run back to the
queue is the lease sweeper's requeue of a claimed-but-never-accepted lease — a run that never
executed and never assigned a Seq. An accepted run has no lease anymore and is unreachable for
the sweeper's statement no matter how late a tick fires; every exit from `Running` is terminal
(Success/Failed/Canceled/Lost) and none re-queues. So a second execution of a run whose Seqs are
already persisted is unreachable today — the (RunId, Seq) key never has to arbitrate between two
attempts. The warning for the future: if any later card introduces re-execution (retry, resume,
requeue-after-Lost), restarting Seq at 1 would make the database *silently drop* the second
attempt's early lines as duplicates (the key is the dedupe authority) and interleave two
attempts' lines by Seq — corrupting the log in the worst possible way. Any re-execution design
must first continue the sequence from the persisted maximum (surface the run's current
`max(Seq)` to the worker at claim/accept time, or a resume handshake) before emitting.

**Review leftover 2 (the 8 h, reusable subscription ticket — residual risk ruling).** The
ticket is not single-use and lives 8 h (matched to the cookie window), so a ticket captured from
a circuit would allow joining that user's active organization's run-log groups for up to 8 h.
Ruling: accepted residual risk. The ticket is strictly weaker than the session cookie it was
minted from (the cookie grants the whole application; the ticket only run-log reads of one
organization), carries the same lifetime, is data-protected with a purpose string, is never
persisted server-side, and lives only in the minting circuit's memory and the SignalR
negotiations of the same process. Making it single-use would break the automatic-reconnect
re-join (each re-join re-presents the same ticket) — the failure F6 fixes — and per-reconnect
rotation would buy almost nothing against an attacker who already reads the circuit's memory.
Revocation paths remain: revoke the member or the organization (join-time re-validation), or
wait out the lifetime.

**F6 (minor): the panel now handles the closed connection and awaits its re-join.**
`WithAutomaticReconnect` exhausts its bounded retry budget and then raises `Closed` — previously
unhandled, so the panel froze silently with whatever it had. The panel now subscribes a `Closed`
handler (error surfaced in the panel's error line plus a **Reconnect** button that restarts the
connection and re-joins the run's group), the initial start failure also offers the button, and
the `Reconnected` re-join is awaited with a caught-and-logged failure (`ILogger<RunLogPanel>`)
instead of fire-and-forget — a failed re-join is visible, not a silent freeze on a group the
connection is no longer in. Handlers are unsubscribed before disposal so the teardown cannot
touch UI state. Like the rest of the panel's DOM wiring this is not browser-tested (no browser
in the suite); the transport and merge logic it feeds are.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files:
  - `src/Shared/LogStreaming/LogLineMessage.cs` (wire contract + `LogStream` constants, new)
  - `src/Shared/LogStreaming/RejectionProtocol.cs` (`LogStreamRejection` machine prefix, review
    fix F1, new)
  - `src/Server/Data/Entities/BuildLog.cs` (new),
    `src/Server/Data/ZenkinsDbContext.cs` (DbSet + model config),
    `src/Server/Data/Migrations/{20261112214228_AddBuildLogs.cs,.Designer.cs}` + snapshot (new)
  - `src/Server/LogStreaming/{RunLogOptions,RunLogAccess,WorkerTokenAuthentication,RunLogTicketAuthentication,RunLogIngest,RunLogHub,WorkerLogHub,LogsServiceCollectionExtensions}.cs` (new)
  - `src/Server/Runs/{RunService,RunEndpoints}.cs` (download + streaming read; completed-path ingest flush)
  - `src/Server/Program.cs` (`AddZenkinsLogs`, `MapHub<WorkerLogHub>("/hubs/worker-logs")`,
    `MapHub<RunLogHub>("/hubs/run-logs")`), `src/Server/Components/_Imports.razor`
  - `src/Server/Components/Pages/Runs/{RunDetail.razor (ticket mint + panel),RunLogPanel.razor (new)}`,
    `src/Server/Components/Shared/RunLogViewModel.cs` (new)
  - `src/Worker/{LogStreamClient,RunLogBuffer}.cs` (new),
    `src/Worker/{WorkerRunLoop,WorkerRunner,WorkerConfiguration}.cs` (dual-path output, shared
    connection + reconnect loop, `ZENKINS_LOG_BUFFER_LINES`),
    `src/Worker/Zenkins.Worker.csproj` + `Directory.Packages.props`
    (`Microsoft.AspNetCore.SignalR.Client` 10.0.15, CPM)
  - `tests/Zenkins.Tests/LogStreaming/{LogStreamingHost,RunLogBufferTests,RunLogStreamingTests,RunLogReconnectTests,RunLogLiveTests,RunLogAccessTests}.cs` (new)
  - `tests/Zenkins.Tests/LogStreaming/{WorkerLogRejectionTests (F1/F2),RunLogLateAppendTests (F3)}.cs (review fixes, new)
  - `tests/Zenkins.Tests/{Auth/AuthTestHost.cs (TestServer accessor),Tenancy/{TenantIsolationProbes,TenantIsolationProbeTests,TwoOrgsFixture}.cs (BuildLog probes),
    Workers/WorkerConfigurationTests.cs (log-buffer pins),
    WorkerRuntime/{WorkerRunLoopTests,WorkerApiDefinitiveReportingTests,WorkerRunProcessE2ETests}.cs}`
- How to run/verify:
  1. `dotnet build -c Release`
  2. `dotnet test -c Release --filter "Category=LogStreaming"`
  3. `dotnet test -c Release` (the whole suite must stay green; the E2E carries `Category=Workers`)
  4. PostgreSQL contract gate:
     `Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins" dotnet ef migrations has-pending-model-changes --project src/Server`
- Results (2026-11-13):
  - Build: **PASS** — `dotnet build -c Release`, 0 warnings / 0 errors (`TreatWarningsAsErrors` active).
  - Tests: **PASS** — **316/316** across the suite (292 pre-card + 17 `Category=LogStreaming` +
    4 `ZENKINS_LOG_BUFFER_LINES` config pins + 3 `BuildLog` tenancy probes), re-run 14+
    consecutive times with zero failures (one subscriber-side flake found during stabilization —
    partial batches flush on the 250 ms ingest timer, so the live/reconnect tests now wait for the
    expected counts instead of asserting immediately; documented above).
  - `Category=LogStreaming`: **PASS** — 17/17.
  - WorkerRuntimeConfigTests (ASP.NET-free worker): **PASS** — the Worker references only the
    SignalR *client* package, which does not reference the ASP.NET Core shared framework; the
    runtimeconfig guard stays green.
  - PostgreSQL contract gate: **PASS** — "No changes have been made to the model since the last
    migration."
  - Red proof: **PASS** — surrogate key + non-unique `(RunId, Seq)` index + Seq-dedup removed →
    `Reconnecting_worker_resending_overlapping_sequences_creates_no_duplicates` red (`Expected:
    100, Actual: 106` — six duplicate overlap rows) → restored → green.
  - TC-001…TC-004 all covered as listed above (10k exactly-once + bounded flush + download
    fidelity; reconnect dedupe; honest overflow marker; access control incl. worker-token
    cross-tenant rejection).
- Results (2026-11-13 — review fixes):
  - Build: **PASS** — `dotnet build -c Release`, 0 warnings / 0 errors.
  - Tests: **PASS** — **322/322** (316 baseline + 6 new: 3 F4 buffer marker tests, 1 F1 drain
    rejection integration, 1 F2 dead-token observability, 1 F3 late-append/no-leak).
  - `Category=LogStreaming`: **PASS** — 23/23 (17 + 6).
  - PostgreSQL contract gate: **PASS** — "No changes have been made to the model since the last
    migration." (no schema change in this round).
  - Red proofs: **PASS** — (1) F1: old "due to an error on the server" discriminator restored →
    `A_foreign_run_rejection_ends_the_drain_bounded_with_a_clear_stderr_line` red (the bounded
    30 s await times out on the infinite resend loop) → fix restored → green. (2) F3: the
    eviction-flag check disabled → `A_late_append_to_a_completed_run_is_persisted_and_leaves
    _no_ingest_state` red (`PendingRunCount` stays 1 — the leak; the lines still persisted via
    the timer) → restored → green.
