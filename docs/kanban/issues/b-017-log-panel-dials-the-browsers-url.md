---
id: B-017
title: The live log dies behind a forwarded port — the panel dialled the browser's URL and the hop answered 401
type: bug
priority: P1
size: M
status: review
created: 2026-11-20
updated: 2026-11-20
blocked: none
---

## Goal

A run console opened through a forwarded port or an authenticating reverse proxy streams its log.
The panel's connection is made by the Server process itself, so it goes to the listener that process
owns — never back out through whatever front door the browser used.

## Background

Reported from a remote-desktop session: opening **Console · Run #2** showed the log card with

```text
无法连接日志流 (Response status code does not indicate success: 401 (Unauthorized).)
本次运行没有捕获到任何输出。
```

while the run was **Running** and the Worker was streaming. The browser's address bar was
`http://<forwarded-host>:<forwarded-port>/runs/<id>/console?step=<step-id>` — a forwarded address on
a network the Server is not on, not a listener of the Server itself.

The panel was built (F-008) with a **server-side** `HubConnection` in the same process as the hub,
authenticated by a subscription ticket the page mints for its own session. Its `WithUrl` used
`NavigationManager.ToAbsoluteUri("hubs/run-logs")` — the **browser's** base URI. So the negotiate POST
left the Server, went back through the forwarded port, and was answered by the hop in front of it: a
plain HTTP request from the Server is not the authenticated browser, and the hop returned 401. The
ticket was never wrong; it never reached the hub.

Evidence gathered on the reported deployment's own build:

- The hub authenticates its own tickets correctly: on a local instance of the same build, a ticket
  minted with that instance's key ring negotiates **200**, an absent or forged one is a bare **401**
  (also pinned by `RunLogAccessTests`).
- The reported browser URL's host is not reachable from the Server's machine at all (the address is
  in the remote-access network, not the LAN), and its port is not a port this product binds — the
  request is answered by the forwarding hop, not by Kestrel.
- Therefore a 401 seen by a client that mints its own valid ticket in-process can only mean the
  request never arrived.

The defect is not specific to remote desktops: `docs/install.md` §7 tells operators to put a
TLS-terminating (possibly authenticating) proxy in front of a loopback-bound Server, and F-047 moved
the log onto its own page — the most-used place for that panel.

## Acceptance Criteria

- [x] The console's hub connection dials an address this Server process is listening on, resolved
      from the server's own bound endpoints, so a forwarded or proxying front door cannot answer for
      the hub.
- [x] A wildcard bind (`0.0.0.0`, `[::]`, `*`) resolves to loopback on the same port; plain HTTP is
      preferred over HTTPS for the loopback hop (a loopback TLS hop would only add certificate trust);
      an endpoint bound to one concrete address is used as it is.
- [x] A host that reports no listener addresses (the in-memory test server, IIS/HTTP.sys in-process
      hosting) keeps the previous browser-base behaviour instead of failing.
- [x] The chosen address is proven to answer, not merely computed: a fact starts a real Kestrel
      listener on a wildcard dynamic port, resolves the endpoint, and POSTs to the resolved URL.
- [x] A failed connection logs the attempted address (never the ticket), so an operator can tell
      "the hub refused me" from "something in front of it did".
- [x] Everything else about the panel is unchanged: the ticket scheme, the join authorization, the
      Seq-deduplicating view model, the reconnect button.

## Subtasks

- [x] `src/Server/LogStreaming/RunLogSelfEndpoint.cs` (new): the resolver — `IServerAddressesFeature`
      → a root URI (loopback > concrete, HTTP > HTTPS), `Choose` pure and separately testable.
- [x] `src/Server/LogStreaming/LogsServiceCollectionExtensions.cs`: register it as a singleton.
- [x] `src/Server/Components/Pages/Runs/RunLogPanel.razor`: dial `RunLogSelfEndpoint.RunLogHubUrl`
      with the browser base URI as the documented fallback; log the address on both failure paths.
- [x] `tests/Zenkins.Tests/LogStreaming/RunLogSelfEndpointTests.cs` (new): the choice facts, the real
      wildcard listener, and the DI registration inside the real pipeline.
- [x] `docs/install.md` §7: the proxy serves the browser hop; the console finds its own listener.
- [x] `CHANGELOG.md` (Unreleased).

## Dependencies

- F-008 (the ticket scheme and the browser hub), F-047 (the console page is where the panel is used),
  DOC-003 (`docs/install.md` §7 — the reverse-proxy deployment shape this bug broke).

## Test Cases

### TC-001: A console behind a forwarded port (the reported shape)

Steps:
1. Run the Server on machine A, reached from machine B through a forwarded port (a remote-desktop
   tunnel, an SSH `-L`, an authenticating reverse proxy).
2. Start a run on a Worker and open its console page from B.

Expected: **Following live** and the Worker's lines arrive. The Server log records no 401 from the
log address. (Pre-fix: the red `401 (Unauthorized)` line, no lines, on every console page.)

### TC-002: The plain localhost deployment still works

Steps:
1. Open `http://localhost:<port>/runs/<id>/console` on the Server's own machine.

Expected: unchanged — the panel streams; the resolved address is loopback with the same port.

### TC-003: The resolution facts

`RunLogSelfEndpointTests`: wildcard → loopback (IPv4 and IPv6 forms), loopback beats a wider or
proxied address, HTTP beats HTTPS on loopback, a concrete-only bind is kept, unusable entries
(pipe, unix socket, port 0, blank) are ignored, no addresses → null, and a real Kestrel listener on
`http://0.0.0.0:0` resolves to a `127.0.0.1` URL that answers `200`.

### TC-004: A failure names the address

Steps:
1. Make the hub unreachable (block the port) and open a console page.

Expected: the panel's error copy is unchanged, and the Server log has one warning naming the run and
the attempted URL — never the ticket.

## Development Log

### 2026-11-20

- **Diagnosed from the report without touching the machine.** The reported build's authentication was
  proved healthy by minting a ticket with its key ring and negotiating against it (200), which rules
  out the ticket, the scheme, the claims and the key ring; a bare negotiate is a 401 (by design). What
  was left is the destination, and the code dialled the browser's URL — a forwarded address the Server
  does not own.
- **Not "make the ticket the cookie's":** the ticket exists because a circuit cannot replay the
  browser's cookie; that design is right and unchanged.
- **Not "widen the ticket lifetime" or "retry":** neither changes who answers the request.
- **The resolver returns null rather than guessing** when the host reports no listener: the fallback
  path is explicit, so a host that cannot answer this question degrades to the old behaviour instead
  of dialling something invented.

## Verification

- `dotnet test --filter "FullyQualifiedName~RunLogSelfEndpointTests"` — **9 passed / 0 failed**, on
  macOS (arm64), Debug. Includes the real Kestrel wildcard-bind fact (resolved URL answered `200`).
- `dotnet test --filter "FullyQualifiedName~LogStreaming|FullyQualifiedName~RunUi"` — **88 passed /
  0 failed** (the ticket/access/live/reconnect suites and the console page suite unchanged).
- `dotnet build src/Server/Zenkins.Server.csproj` and the test project — 0 warnings / 0 errors.
- **Owed: TC-001 on the reporting deployment.** That machine is on a remote-access network this
  environment cannot reach, so the forwarded-port pass was not executed here; the mechanism is pinned
  by the facts above, and the operator's re-check is one page load.
- Not run: the whole gate (`scripts/verify.py`), the Milestone/Recovery/Postgres/publish suites — the
  change is one new service, one DI line and one call site.

## Review

Pending.
