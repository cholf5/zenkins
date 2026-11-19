# Worker registration, identity and re-registration

What a Worker's identity actually is, how the first registration works, what every later start does,
and exactly what to do when the Server stops accepting the credential — including the cases the Worker
now recovers from by itself.

The short version: **the name is a label, the token is the identity.** A Worker is authenticated by a
`zwrk_…` token, and the name only selects a row on the Server and a directory on the build machine. So
"the Server does not recognise my worker" is always a statement about the token or about which file was
read — never about the name being spelled differently.

Companion material: [`docs/install.md`](install.md) §3.5 (a worker's first build-machine start) and §6
(the environment knobs); this page is the reference for the credential lifecycle itself.

## Contents

1. [Two credentials, not one](#1-two-credentials-not-one)
2. [What "identity" means](#2-what-identity-means)
3. [Where the credential lives](#3-where-the-credential-lives)
4. [First registration](#4-first-registration)
5. [Every later start](#5-every-later-start)
6. [Recovery the Worker does by itself](#6-recovery-the-worker-does-by-itself)
7. [Re-registering by hand](#7-re-registering-by-hand)
8. [Changing the name, the URL, or the machine](#8-changing-the-name-the-url-or-the-machine)
9. [One machine, several Workers](#9-one-machine-several-workers)
10. [Under a service manager](#10-under-a-service-manager)
11. [Troubleshooting by log line](#11-troubleshooting-by-log-line)
12. [Security notes](#12-security-notes)

## 1. Two credentials, not one

| Credential | Prefix | Minted by | Lifetime | Where it lives |
|---|---|---|---|---|
| Registration token | `zreg_` | an Owner, on **Workers → Registration tokens** (or the setup wizard's Worker step) | **single use** — burned the moment a registration succeeds | only in the operator's hands; never stored |
| Worker token | `zwrk_` | the Server, in the registration response | until revoked, or until that worker registers again | the Worker's own file on the build machine |

The Worker sends one of them per request, in a plain `Authorization: Bearer …` header:

```text
POST /api/workers/register    Authorization: Bearer zreg_…   → { workerId, workerToken, workerName }
POST /api/workers/heartbeat   Authorization: Bearer zwrk_…   → { workerId, lastHeartbeatAt, cancelRequestedRuns }
```

Only the SHA-256 of either token is stored server-side (`WorkerTokens.TokenHash`), so a copy of the
Server's database cannot be replayed as a credential. Every authentication failure is a bare `401`
with no body — there is no "wrong name" answer to get, because the name is not part of authentication.

## 2. What "identity" means

- **The name** (`ZENKINS_WORKER_NAME`, default: the machine name) is the label on the **Workers** page,
  the key a same-name re-registration reuses, and one segment of the local directory path. Registering
  again under the same name **updates that row in place**: the worker id and its run history stay with
  it (claiming is by tags and availability, never by name). A different name is a different worker,
  with a fresh row.
- **The token** is what the Server authenticates. It belongs to exactly one worker row.
- Both are needed to look like "the same worker": the token to be accepted, the name to land on the
  same row.

Two consequences worth internalising before debugging anything:

1. Re-registration under the same name **revokes the previous worker tokens** — the identity is
   replaced, not added to. A second process holding a copy of the old token stops working.
2. A Worker whose token is rejected cannot "wait it out". 401/403 is a definitive answer; only a new
   credential fixes it (see §6 and §7).

## 3. Where the credential lives

```text
<data dir>/worker-tokens/<server-authority>/<worker-name>/worker.token
```

| Part | Value | Notes |
|---|---|---|
| data dir | `~/.zenkins` (`%USERPROFILE%\.zenkins` on Windows), or `ZENKINS_DATA_DIR` | not the install directory and not the current directory — an upgrade or a `work/` cleanup cannot touch it |
| server authority | the `host:port` of `ZENKINS_SERVER_URL` | the **port counts**; so does `localhost` vs `127.0.0.1` |
| worker name | the value of `ZENKINS_WORKER_NAME` (or the machine name) | a changed name is a different directory |
| both segments | sanitised, with a short hash suffix appended | the suffix keeps lookalike values (`a.b` vs `a-b`) apart; it is derived from the value, not a secret |

An example path for a worker named `build-1` talking to `http://127.0.0.1:5070`:

```text
~/.zenkins/worker-tokens/127-0-0-1-5070--<hash>/build-1-<hash>/worker.token
```

The file is written atomically (temp file moved over the target) with mode `0600` inside a `0700`
directory on POSIX. A file that exists but does not contain a `zwrk_…` value is treated as corrupt and
reported, never silently ignored — otherwise the Worker would 401 forever with no explanation.

**Why the path matters more than it looks:** a service manager that runs the Worker as another user
(or with a different `HOME`) resolves a different `~/.zenkins`, so the same Worker "loses" its
credential without anything being wrong. §10 is the fix: pin `ZENKINS_DATA_DIR`.

## 4. First registration

1. On the Server, signed in as an Owner: **Workers → Registration tokens → Create registration token**.
   The plaintext `zreg_…` is shown once, in a standalone page — copy it before leaving; it is not
   stored and cannot be shown again. (The setup wizard can also mint one as its Worker step.)
2. On the build machine, start the Worker with the registration token for this one run:

   ```bash
   ZENKINS_SERVER_URL=http://<server-host>:<port> \
   ZENKINS_REGISTRATION_TOKEN=<the zreg_ token> \
   ZENKINS_WORKER_NAME=build-1 \
   ./Zenkins.Worker
   ```

   ```text
   Zenkins Worker: registering with http://<server-host>:<port>/ as 'build-1' (Linux, X64, 8 CPUs, tags '').
   Zenkins Worker: registered; the server assigned worker id '<worker-id>'.
   Zenkins Worker: worker token persisted to ~/.zenkins/worker-tokens/<authority>/build-1-<hash>/worker.token.
   Zenkins Worker: run execution starts on 1 loop(s); claiming every 2 second(s).
   Zenkins Worker: online as worker '<worker-id>'; heartbeating every 15 second(s).
   Zenkins Worker: log stream connected.
   ```

3. Server-side effect: the registration token row gets `UsedAt` set (a second use is `401`), the
   `Workers` row is created with status **Online**, and one `Worker` token row is issued. Same-name
   re-registration later updates that row and replaces the token.

A rejected registration is a **process exit**, not a retry loop:

| Exit code | Meaning | What to do |
|---|---|---|
| `2` | configuration error (bad URL, no token and no stored credential, unusable token file) | fix the environment; the one stderr line names the variable or the file |
| `3` | the Server gave a definitive `4xx` to `register` — almost always a `zreg_` token that is already burned, revoked or unknown | mint a fresh registration token |

Transport failures (Server slow, unreachable, connection reset) are *not* definitive: they back off and
retry forever, because a machine that boots before its network should still register when the Server
comes up.

## 5. Every later start

Nothing to pass but the URL and the name:

```bash
ZENKINS_SERVER_URL=http://<server-host>:<port> ZENKINS_WORKER_NAME=build-1 ./Zenkins.Worker
```

```text
Zenkins Worker: using the persisted worker token from ~/.zenkins/worker-tokens/<authority>/build-1-<hash>/worker.token.
Zenkins Worker: online as worker '<worker-id>'; heartbeating every 15 second(s).
```

That is the whole restart story in the normal case: same file, same token, same row, same history. The
Worker keeps heartbeating every `ZENKINS_HEARTBEAT_SECONDS` (default 15); if it stops for longer than
`Workers:OfflineAfterSeconds` (default 60) the Server marks it **Offline** and its running runs become
**Lost**.

Everything the credential depends on is outside the install directory, so upgrading the binary, moving
it, or deleting `work/` does not disturb the identity. What *does* disturb it: a different data
directory, a different server URL (`host:port`), a different worker name, or a different user account.

## 6. Recovery the Worker does by itself

A credential can stop being accepted for reasons the Worker cannot see: the Server's database was
replaced or restored, or an Owner revoked the worker. Treating that as "retry until someone notices"
was the old behaviour and it was wrong — the Worker would sit in a 401 loop forever, and the guidance
to "re-register" was unreachable while a token file existed.

Now, when `ZENKINS_REGISTRATION_TOKEN` is present in the environment, the Worker treats it as the
standing instruction to register again:

- **At startup**, if a persisted credential exists, one heartbeat decides. A definitive `401/403`
  means it is dead → the Worker registers again, persists the replacement, and continues. A transport
  failure or a timeout says nothing about the credential and changes nothing.
- **While running**, the same `401/403` on a heartbeat triggers the same re-registration; every loop
  and the log-stream reconnect read the replacement immediately, so no restart is involved.

```text
Zenkins Worker: using the persisted worker token from …/worker.token.
Zenkins Worker: the server no longer accepts the persisted worker token; registering again with the configured ZENKINS_REGISTRATION_TOKEN.
Zenkins Worker: registering with http://<server-host>:<port>/ as 'build-1' (…).
Zenkins Worker: registered; the server assigned worker id '<the same worker-id>'.
Zenkins Worker: worker token persisted to …/worker.token.
Zenkins Worker: re-registered; this worker now presents the new worker token.
```

What this guarantees, and what it deliberately does not:

| Situation | Behaviour |
|---|---|
| credential accepted | kept; a registration token you staged is **not** burned |
| credential rejected, registration token unused | re-registers under the same name → same row, same history |
| credential rejected, no registration token | no registration attempt, file untouched, stderr names the file and the variable (§7) |
| registration token already burned or revoked | reported once (startup: exit `3`; running: one line) and not retried — a single-use token cannot heal twice |
| Server unreachable or timing out | unchanged: back off and retry with the credential it has |

The honest limit: registration tokens are single-use by design, so **one staged token heals one
credential loss**. The common production case — the Server's data was replaced — is one loss, fixed by
one restart with a fresh token. A Worker that keeps losing credentials needs an investigation
(§11), not a second token in the environment.

## 7. Re-registering by hand

Four situations, four moves.

**(a) The Server refuses the credential and no registration token is configured.** The Worker says
exactly this:

```text
Zenkins Worker: the server rejected the worker token with 401 while sending a heartbeat (unknown, used or revoked). Retrying in 1s. Delete '~/.zenkins/worker-tokens/<authority>/<name>/worker.token' and start the worker with a freshly minted ZENKINS_REGISTRATION_TOKEN to register it again.
```

Mint a token on the Workers page and restart with it. Deleting the file first is no longer required
(the startup check replaces it), but it is harmless and makes the intent obvious. Without a token the
old credential cannot be repaired by any amount of waiting.

**(b) Deliberate rotation** (suspected leak, personnel change). Revoke the worker's token — or simply
re-register — and the old credential dies immediately; mint a fresh registration token and restart the
Worker with it. Never copy a `zwrk_…` value between machines: it is one worker's identity.

**(c) Without a reachable Workers page** (lost Owner access). The Server's `WorkerTokens` table is the
source of truth; a row with `RevokedAt` set, or no row for the credential's hash, explains the 401.
The supported repair is still a fresh registration token from an Owner — there is no "reset" that
invents one locally.

**(d) A credential you want to keep while changing where it is read from.** Use the same data
directory (`ZENKINS_DATA_DIR`), the same `host:port` spelling, and the same name. If the name is the
only thing changing, the row changes with it (a new worker), so re-register instead of moving files.

## 8. Changing the name, the URL, or the machine

| Change | Effect | Recommended move |
|---|---|---|
| `ZENKINS_WORKER_NAME` | different store directory **and** different row: the Worker looks like a new machine | keep the name; a rename is a new registration (and the old row becomes permanently Offline) |
| server `host` or `port` | different store directory; the Worker cannot find its credential and needs a registration token | keep the URL stable, or re-register once after the move |
| different user / service account | different `HOME` → different `~/.zenkins` | pin `ZENKINS_DATA_DIR` to an absolute path the service can read |
| new build machine | nothing to move: a token cannot be transplanted safely | register the new machine with its own token, delete the old worker once its history is expendable |
| data directory deleted | identity lost, everything else (work trees) is disposable | re-register; the name keeps the row and history if the Server still has it |

A changed URL or name prints `configuration error: no persisted worker token at '<path>' and
ZENKINS_REGISTRATION_TOKEN is not set …` — a *local* path problem, not the Server rejecting anything.

## 9. One machine, several Workers

Several Workers can run on one machine; give each a distinct `ZENKINS_WORKER_NAME`, and preferably its
own `ZENKINS_DATA_DIR` and `ZENKINS_WORK_ROOT`:

```bash
ZENKINS_SERVER_URL=http://<server-host>:<port> ZENKINS_WORKER_NAME=build-1 \
ZENKINS_DATA_DIR=~/.zenkins/build-1 ZENKINS_WORK_ROOT=~/zenkins/build-1/work ./Zenkins.Worker
```

They are separate identities to the Server (separate rows, separate queues, separate tags) even though
they share the machine. One data directory per Worker is not required — the store already separates by
authority and name — but it makes "which token is whose" obvious when something goes wrong.

## 10. Under a service manager

A Worker under launchd / systemd / Task Scheduler is the case where "it worked in my shell" and "it
cannot find its credential" diverge, because a service's `HOME` and working directory are not a login
shell's. Two rules:

1. **Pin `ZENKINS_DATA_DIR` to an absolute path** the service account can read and write (for example
   `/var/lib/zenkins-worker`), and keep it across upgrades — it holds the identity, not the work.
2. **Put `ZENKINS_SERVER_URL` and `ZENKINS_WORKER_NAME` in the unit itself** (plist
   `EnvironmentVariables`, systemd `Environment=`, or the Windows service `Environment` registry
   value), never in a shell profile the service does not read.

Keeping a registration token in the same unit environment is optional but useful: it is the difference
between a Worker that heals a credential loss on its next start (§6) and one that waits for a human.
Since it is single-use, it heals exactly once; after that the service needs a fresh one.

The install guide's per-OS templates and settings locations are in [`docs/install.md`](install.md) §3.2,
§4.2 and §5.2.

## 11. Troubleshooting by log line

| What the Worker prints | What happened | Fix |
|---|---|---|
| `no persisted worker token at '<path>' and ZENKINS_REGISTRATION_TOKEN is not set` | the store has nothing for this URL + name (first start, changed URL/name, or a different user's `HOME`) | start with a registration token; keep URL and name stable |
| `... does not contain a worker token; delete it and re-register` | the file is not a `zwrk_…` value (truncated, copied wrong, or something else wrote it) | delete that file, start with a registration token |
| `registered; the server assigned worker id …` then `online as worker …` | success — nothing to do | — |
| `the server no longer accepts the persisted worker token; registering again …` then `re-registered` | the old credential was dead and a configured registration token replaced it | none; the row and its history are intact |
| `... rejected the worker token with 401/403 ... Delete '<path>' and start the worker with a freshly minted ZENKINS_REGISTRATION_TOKEN` | dead credential, nothing to heal with | mint a token, restart with it (§7a) |
| `automatic re-registration failed. … Registration tokens are single-use` | the configured `zreg_` token was already burned | mint a fresh token and restart; while it runs the Worker keeps retrying with the credential it has |
| exit `3` at startup with `configuration error: the server rejected the worker token with 401 while registering` | a burned/unknown/revoked registration token | mint a fresh one |
| exit `2` at startup | bad URL, bad number, missing variable, unusable token file | the line names the variable or file |
| the Worker is online in its log but the Workers page shows **Offline** | it is not reaching the same Server, or something else answers on that port | check `ZENKINS_SERVER_URL`; `curl -si -X POST <url>/api/workers/heartbeat` must answer like Zenkins (`401`, empty body), not like another service (`Server: AirTunes` is a macOS AirPlay receiver — see [`docs/install.md`](install.md) §10) |
| the Worker is **Online** but never claims work | **Disabled** on the Workers page, wrong tags, or a full concurrency slot | enable it, compare `ZENKINS_WORKER_TAGS` with the run's required tags |

Server-side confirmation, when the Worker's own log is not enough: a Logging warning on
`Zenkins.Server.Workers.WorkerService` names the reason a credential was refused —
`Reason=unknown-token` (no row for that hash: the database is not the one that issued it) or
`Reason=revoked` (an Owner revoked it, or the worker re-registered elsewhere).

## 12. Security notes

- **Plaintext never leaves the build machine except in a request header.** The Server stores hashes;
  the Worker prints only paths, status codes and its server-assigned id.
- **The token file is the identity**: `0600` in a `0700` directory on POSIX, written atomically. Back it
  up only as carefully as you would a password; delete it to discard an identity.
- **Registration tokens are single-use, Owner-only, and revocable**; the plaintext is shown once and
  never stored. A leaked one is a one-shot enrollment risk, hence the preference for an
  environment/unit setting over a shell history entry: `ZENKINS_REGISTRATION_TOKEN=… ./Zenkins.Worker`
  lands in `~/.bash_history` and in the process list.
- **Revocation is immediate**: a revoked or replaced token is a `401` on the next request, and a
  Worker cannot re-register without a fresh token from an Owner. That is why the automatic recovery of
  §6 requires an explicit `ZENKINS_REGISTRATION_TOKEN` — it can never undo a decision silently.
