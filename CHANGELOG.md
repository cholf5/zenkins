# Changelog

All notable changes to Zenkins are recorded here, newest first. The style is the repository's: what
shipped, what was verified on which machine, and what is knowingly not there — no marketing. The
product version itself lives in the single `Version` property of `Directory.Build.props` (card
P-001); both binaries print it for `--version`, and the web UI's sidebar footer shows it on every
signed-in page, so a tag below can be checked against the binaries that carry it.

## Unreleased

- **License chosen: Apache-2.0.** The 1.0.0 source distribution shipped with no license granted
  (the PRD said TBD); that entry below stays as the historical record. The decision is now made:
  the Apache License, Version 2.0 text is in `LICENSE` (SPDX: `Apache-2.0`), the PRD and README
  point at it, and the bundled third-party components remain noted in `THIRD-PARTY-NOTICES.md`.

## 1.0.0 — 2026-11-15 (tag `v1.0`)

The first tagged release, closing roadmap Phase 6. A build worker you deploy next to your code: an
ASP.NET Core Server (Blazor Server UI) and a console Worker, one SQLite file by default or a real
PostgreSQL, no Docker anywhere in the product. Everything below shipped across the roadmap's phases
(v0.1 Foundation → v1.0 Stable release) and is documented in `docs/development.md` (developers),
`docs/install.md` (operators) and one card per change in `docs/kanban/issues/`.

**What shipped.**

- Multi-tenant core (Phase 1): Identity sign-in with the first-run Owner bootstrap, organizations
  and members, projects scoped to the active organization, an EF Core data layer (SQLite default,
  PostgreSQL) whose tenant query filters are enforced from the first query (I-001, I-002, F-001,
  F-002, F-003).
- Worker runtime (Phase 2): registration tokens minted by an Owner, heartbeats, an atomic
  claim/lease so a run is executed once, per-run isolated work directories, log streaming
  persisted incrementally, cancellation propagation and per-run timeouts, offline detection turning
  a dead worker's run into `Lost` — never a silent retry (F-004–F-010).
- Pipelines (Phases 3–4): `pipeline.lua` with a frozen step spec, MoonSharp as the embedded engine,
  per-step records with `secret()` redaction, a path-safe local artifact store, git clone/checkout
  with HTTPS and SSH credentials delivered to the worker, and `git push` webhook triggers with
  signature verification, branch filtering and replay protection (F-011–F-017).
- Management UI (Phase 5): dashboard, project tabs, the run detail page with a pause/search log
  viewer and artifacts, the workers page, the secrets page (D-002–D-006).
- Release engineering (Phase 6): the self-contained publish matrix (win-x64 / linux-x64 / osx-arm64)
  with Windows Service / systemd / launchd packaging (I-003), the NativeAOT Worker with the Server's
  recorded stays-JIT verdict (I-004), the install guide (DOC-003), and this release's version stamp,
  `scripts/verify.sh` and CI (P-001).

**The seven PRD §10 acceptance scenarios, each with real-machine evidence.**

| Scenario | Evidence |
|---|---|
| 1. Single-organization build (run, logs, artifact) | Q-002's milestone harness: a real Server process, a real Worker, a real git repository, a real webhook delivery, a real artifact download. |
| 2. Cross-platform builds through worker tags | Q-005 in-suite; Q-007 TC-003 on real Windows and Linux workers (tag filter discriminated with a 14 s queued-while-idle window). |
| 3. Multi-worker parallelism without double assignment | Q-005's concurrency scenarios; the claim is a single-statement compare-and-set with a lease (B-002 below records the one seam it left open — the check-then-act cap, closed after the tag). |
| 4. `git push` triggers the pipeline | Q-002/F-017: signature-verified delivery, branch filter, replay protection, the run pinned to the pushed commit. |
| 5. Two organizations cannot see each other | Q-006's audit: a machine-enforced surface inventory (64 HTTP surfaces, both hubs, the stores) with id-swap probes and byte-level store isolation. |
| 6. Recovery from crashes | Q-004: a queued run survives a server restart; a killed worker's run becomes `Lost` with the offline reason, its late report refused, a restarted worker claims new work. |
| 7. Cross-platform deployment without Docker | I-003's publish matrix plus Q-007: Windows Service (SCM restart proven by the event log) and systemd (restart preserving the database), both running real builds with no SDK on the target; Q-008 proved the published Server on a real PostgreSQL 16.15. |

The credential story that runs through all of it is F-018's (a build clones only from the
credential the Server delivered, with the ambient ones provably unused) and Q-009's verdict: a real
`sshd` serves a private repository on loopback, the delivered-key clone reaches `Success` with the
sshd's own accepted-fingerprint log as the counter, and the no-credential / rejected-key /
wrong-host-key refusals each surface with their named reasons.

**Verification.** The gate is `scripts/verify.sh`: `dotnet build -c Release`, the fast suite
(1045 tests at this revision, 0 skipped), then one filtered run per acceptance harness — Milestone,
Recovery, PublishSmoke, PublishAot, GitSshd (docs/development.md §2). CI (`.github/workflows/ci.yml`)
runs the same gate on Linux, Windows and macOS for every push and pull request, and on the push of a
`v*` tag.

**Known issues** — recorded, not silent. Each entry names its card; the card updates or removes its
entry when it lands.

- **B-002 — `maxConcurrency` enforcement was check-then-act; closed by card B-002.** At release
  time the slot check sat outside the claim's atomic compare-and-set, so a hostile-but-
  authenticated client holding a valid worker token could race the claim endpoint and hold more
  running runs than the `maxConcurrency` it reported (real worker processes were structurally
  bounded — claim loops of at most one run each — and executed-once held, Q-005; the consequence
  was over-admission beyond the reported cap, never double execution). Fixed after the `v1.0` tag,
  2026-11-15: the claim now takes the slot count and the lease inside one transaction serialized on
  the worker's own row, under both providers — pinned by the `ConcurrencyClaimCapTests` regression
  (red before the fix, three consecutive runs admitting 4 of 8 racing claims past a cap of 1) and a
  real-PostgreSQL claim-burst probe (evidence on card B-002). Ships with the next release.
- **Q-010 — tag validation is asymmetric between the trigger and the claim; resolved by card
  Q-010: the asymmetry is the documented semantics.** Trigger-side validation checks the union of
  all workers' tags while claiming requires one worker to cover all required tags, so a run whose
  tags split across two workers passes the form and then starves honestly: it stays `Queued`, is
  never mis-executed and never becomes `Lost` or `Failed` for lack of a worker. Decided on card
  Q-010 (2026-11-15): the union-accept + honest-starvation semantics stay — a trigger-time
  single-worker check cannot tell "no worker covers it today" from "no worker ever will", so it
  would reject exactly the queue-first/worker-registers-later workflow the starvation serves (and
  the webhook path already accepts uncoverable tags deliberately, F-017) — while the claim side's
  per-poll coverage check is the one place the rule can be exact. The semantics are now written
  where the operator meets it (the trigger form's tags-field help text, the install guide's
  worker-tags section) and pinned end to end by a Concurrency-suite test (split set accepted →
  queued while uncovered → executes when a covering worker registers; the matched case unchanged).
  No behaviour change; ships with the next release.
- **A wrong pinned SSH host key reports `git-failed`, not a host-key-specific reason** (Q-009,
  observed and accepted). The refusal is honest and named in the run log — ssh's own
  `REMOTE HOST IDENTIFICATION HAS CHANGED` and `Host key verification failed` — and the page's
  explanation row points at it; the reason vocabulary is closed by design (F-014), so adding a
  `git-host-key-refused` reason would be a deliberate product change, not a bug fix.
- **The Server is not a NativeAOT target** (I-004's recorded verdict). Blazor Server, EF Core 10 and
  ASP.NET Identity are not .NET 10 AOT targets; the Server ships as a JIT self-contained publish
  with revisit triggers written down in `docs/native-aot.md`. The Worker is the AOT artifact; the
  win-x64 and linux-x64 AOT variants carry analyzer-build evidence only — each OS publishes its own
  RID, and osx-arm64 is the one proven by a real AOT publish and smoke run (CI now builds each OS's
  own RID).
- **Q-006 residuals, adjudicated in the audit.** The webhook delivery endpoint answers 401 (not
  404) for a valid id with a bad signature, so its existence is inferable — mitigated by 128-bit
  identifier entropy plus per-identifier budget limiting that rate-limits before any lookup. The
  audit's "Documented invariants" section is prose outside the machine-enforced parser; turning it
  into assertions is a named v1.0.1 item on the roadmap.
- **Start-on-boot is configuration evidence, not a reboot test** (Q-007). The Windows Service is
  `AUTO_START` and the systemd unit is `enabled`, but neither machine was rebooted during the
  verification, so "comes back after a host reboot" rests on the service manager configuration.
  Version-to-version upgrades are likewise not yet exercised (Q-007, Q-008): the recorded upgrade
  rehearsal is same-version stop → replace → start with data intact.
- **No worker memory cap on macOS** (development.md §7). MoonSharp cannot bound memory from inside
  the interpreter and macOS has no verified mechanism (the `ulimit -v` attempt fails); Linux
  (cgroups/`ulimit`) and Windows (Job Objects) have documented, recommended mechanisms. Size the
  host accordingly.
- **No artifact retention policy** (PRD v1 scope). An artifact lives until its run is deleted; disk
  usage is an operator concern.
- **Webhook trigger handling beyond `push` is unimplemented**: provider-specific event types, and
  unknown or duplicate triggers, have no handling beyond the `push` events F-017 verifies.
- **Deleting a credential leaves its attribution columns populated** (F-018, accepted, invisible):
  `ON DELETE SET NULL` clears `GitCredentialId` while `GitCredentialAssignedAt`/`AssignedByUserId`
  keep their old values; asserted explicitly so it cannot change silently.
- **License not chosen** (PRD says TBD): no license is granted with the source distribution until
  the MIT-vs-Apache-2.0 decision is made. Third-party notices ship in `THIRD-PARTY-NOTICES.md`.
