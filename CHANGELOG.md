# Changelog

All notable changes to Zenkins are recorded here, newest first. The style is the repository's: what
shipped, what was verified on which machine, and what is knowingly not there — no marketing. The
product version itself lives in the single `Version` property of `Directory.Build.props` (card
P-001); both binaries print it for `--version`, and the web UI's sidebar footer shows it on every
signed-in page, so a tag below can be checked against the binaries that carry it.

## Unreleased

- **A full-page run console, with a log that reads line by line (F-047).** The run's log was a
  384px card at the bottom of the run page, and every line was an inline span whose newline Razor
  trimmed - one step's output rendered as one long wrapped string. The log is now its own page
  (`/runs/{id}/console`, plus the numbered route), shaped like the useful half of Jenkins' console:
  one block per line, leading spaces and blank lines preserved, stderr marked in colour *and* with
  a border, optional UTC timestamps and wrapping. It follows the tail; scrolling up pauses the
  viewport and "Jump to latest" resumes, while an explicit pause survives a scroll back to the
  bottom. Status, step badges and elapsed time update on a 1s clock / 2s read and stop when the run
  ends. A step sidebar and the console's step headers filter by the recorded step id, and the run
  page, the runs list and each step's "View log" link here. The viewer keeps at most 20,000 lines
  and renders at most 2,000; both bounds are stated and the download is still the complete record.
  Verified: RunUi 54/54, Milestone 12/12 (Release, real binaries), Server build 0 warnings. The
  real-browser pass (follow, mobile width, 5,000-line fixture) is still owed - this environment has
  no browser harness.

- **Windows logs decode the bytes the tools actually wrote (B-016).** The Worker decoded child
  output with the host's console encoding and wrote batch files as UTF-8 while `cmd.exe` read them
  in the console code page, so Chinese could be wrong on both sides. The runner now reads bytes and
  decodes each line as strict UTF-8 with the host OEM code page as a fallback; Windows scripts are
  written in the OEM page when the text fits and as UTF-8 with `@chcp 65001 >nul` when it does not.
  Verified on every platform: the mixed-UTF-8/GBK, BOM, CR/LF and no-trailing-newline facts, and the
  existing Worker suite (46 passed). The two real `cmd.exe` facts are Windows-only and are owed on
  CI's windows-latest or a Windows Worker. Historical logs are not rewritten; only new runs change.

- **Rows can be dragged to a new place (F-046).** Up and down buttons were the only way to reorder a
  step - nine presses to move the tenth step to the top - and the parameters list had no ordering at
  all. A row now moves where the pointer drops it: the gesture lives in the frame
  (`js/split-list.js`), so both lists get it, and the handle is the only draggable element, so a
  reader's text selection inside the fields still works. The row is moved with `insertBefore` and
  never re-rendered, which is what keeps the open row's fields in the pane and an unselected row's
  fields where they are. A drag stays inside its own list (the four step lists share one form). The
  parameters list also gained ↑↓, because the handle is hidden where there is no room for the pane and
  a touch screen never fires an HTML5 drag.

- **The parameters editor joins the list and inspector (F-045).** A project's build parameters were
  rendered the way the steps were: one fully expanded card each, so ten parameters were ten blocks of
  five fields. They are now one line per parameter - its number, its name (or *unnamed*, for one that
  has none yet) and its type as a chip - with the selected parameter's fields in the pane, using the
  frame B-014 built for the steps list and needing no new layout CSS. The same rule holds here: the
  block of fields is moved into the pane, never re-rendered, because `js/parameter-rows.js` serializes
  the nodes and a re-created input would save empty. A parameter's fields live in
  `ProjectParameterFields.razor` and render for both the saved row and the row template. The stored
  JSON keeps its field names, its order and its shape.

- **The steps editor is a list and an inspector (B-014).** Ten steps used to be ten fully expanded
  cards, so one pipeline was ten screens of fields and the shape of the run was somewhere in the
  middle of it. The editor is now one line per step on the left - its number, its name (or the script's
  first line, so an unnamed step still says what it does) and a chip per setting that is not the
  default - with the selected step's fields in the pane on the right, the shape Unity's Inspector and
  Jenkins' build-step list both use. The fields of every step stay in the DOM and are *moved* into the
  pane, never re-rendered: `js/freestyle-steps.js` serializes the nodes, so a re-created `<textarea>`
  would save empty and say nothing. Below 60rem the pane is gone and the same markup is an accordion.
  A step's fields now live in `FreestyleStepFields.razor` and render for both the saved row and the row
  template, so the template cannot drift from the saved row again. The same change fixes the `runs-on`
  placeholder, which showed its `&&` escaped because the attribute was escaped a second time.

- **A refused steps save gives the draft back (B-015).** When the steps editor's POST was refused -
  an empty step name, a non-numeric timeout, a broken env line - the refusal reason went to the
  server log only, the PRG redirect carried the stable `steps-invalid` code, and the page re-rendered
  the editor from the last *saved* spec: every keystroke since the last successful save was gone,
  behind one generic sentence. The refused submission is now stashed once in memory, keyed to the
  user and project (bounded, expiring, read exactly once), and the redirected page renders the
  editor from what was submitted - values verbatim, including text the frozen spec shape cannot hold
  (an env comment, a non-numeric timeout). The refusal's own sentence (`pipeline.steps[2].name must
  be a non-empty string.`) is on the page instead of only in the log, and the row it names is marked
  and opened first. Another member of the same organization never receives someone else's draft, a
  refresh shows the saved spec again, and a save that lands retires the stash.

- **A run page renders its log panel again (B-013).** Starting a run landed the browser on the error
  boundary - **出错 / 页面渲染失败。** (*Something went wrong / The page failed to render.*) - a moment
  after the run was queued, so a fresh Freestyle project could not be used at all. The run page's log
  panel rendered `_plan.Indicator` while that plan was still null: the plan was filled in
  `OnParametersSet`, but on an interactive circuit Blazor renders a component once while its
  `OnInitializedAsync` is awaiting the log hub - before that hook runs. Prerendering never showed it,
  which is why the server-rendered pages and their facts stayed green. The panel now builds the plan
  on demand (`Plan => _plan ??= _viewModel.Plan(...)`), so the intermediate render draws the same
  empty shell the panel always shows when no line has arrived, in the page's language. One fact
  (`LogPanelFirstRenderTests`) constructs the panel with no lifecycle call and holds the plan
  non-null; it fails on the pre-fix component. Found through a real browser run of a queued Freestyle
  build, with the server's `RunLogPanel` `NullReferenceException` as the trace.

- **A Worker that the Server no longer recognises gets its identity back (B-012).** The Worker's
  identity is a token, not its name: the Server authenticates the hash of a `zwrk_…` credential, and
  the name only picks a row (Server) and a directory (Worker). So a restart could end in two failures
  nobody could see from the outside. If the store looked elsewhere - a different host, port, worker
  name, or service account changes the path - the Worker printed a configuration error that read like
  a rejected name. If the stored credential was found but the Server had replaced its data, or the
  credential had been revoked, every loop retried the same dead token forever: the message said
  "mint a new registration token and re-register", but re-registration was unreachable because a
  registration token in the environment was ignored while a token *file* existed. Now the Worker
  checks its persisted credential once at startup when a registration token is configured, and again
  on every heartbeat: a definitive 401/403 with a registration token in the environment re-registers
  (same name, so the same row and history) and rotates one shared credential that every loop
  and the log-stream reconnect read, so recovery needs no restart. A healthy credential is never
  replaced and a staged registration token is never burned; a transport failure or a request timeout
  changes nothing; and with no registration token the stderr line names the exact file to delete and
  the variable to set. A registration token the Server has already burned is reported once instead of
  retried.

- **"Add step" and "Add parameter" add a row again (B-011).** On a live page every **Add step**
  button on a Freestyle project's Steps tab - the main list and the three post lists - and **Add
  parameter** on the Parameters tab did nothing at all. Both editors clone the new row from a
  `<template>`, and they read it from `template.content`, which holds the row only for HTML the parser
  built; Blazor's renderer inserts a component's children with `appendChild`, so once the interactive
  circuit took over the prerendered page the row was a plain child of the template and the clone threw
  `TypeError: Cannot read properties of null (reading 'cloneNode')`. The row is now resolved from
  `content` or from the template element, so the editor works before and after activation; the
  insert-snippet path shares that resolution, and a missing row returns instead of throwing.

- **The project form's kind switch works wherever the reader came from (B-010).** Choosing
  **Freestyle** on the create form used to leave the repository fields (and their `required`) on
  screen, so the browser refused the submit with "fill out this field"; the definition-source choice
  stayed visible for a kind that has none, and choosing **Pipeline script** revealed no script box at
  all until a refused create echoed the choice back. The shape of the form was decided in two places -
  the Server decided which fields *existed* (always the repository half on a fresh page) and one
  script decided which were *visible* - and that script bound its handlers to radios it found when the
  document loaded, which an in-app navigation replaces without re-running it. Now every field group is
  rendered in every state and `hidden` + `disabled` say whether it applies (so a page whose script
  never ran is already right), the script keeps that state on a `change` delegated to the document and
  re-applies it on `enhancedload`, and the Freestyle steps editor, the parameters editor and the
  snippet buttons - which had the same load-time binding - listen on the document too. A kind-locked
  edit form still shows nothing for a half the project does not own.

- **First-run setup wizard (F-044).** An empty deployment now answers its first browser with a
  three-step wizard instead of a registration form. The welcome page shows this deployment's facts -
  version, database target, key-ring and artifact directories - and a real schema check: a database
  behind the binaries is reported with the `dotnet ef database update` remedy (and an unreachable one
  with its error) rather than as a 500 on the first request. The Owner form collects the
  organization's name (no more forced `Starter Organization`) and writes the account, the
  organization, the Owner membership and the bootstrap marker in the same transaction as before; the
  optional Worker step mints a single-use registration token and prints the two environment variables
  to start a worker with. While the marker is absent `/account/login` and `/account/register`
  redirect to `/setup`; once it exists the wizard redirects out and ordinary registration resumes.
  The wizard path refuses a late submission after another browser finished setup instead of creating
  an account without an organization. All three pages are static SSR and ship in English and
  Simplified Chinese like the rest of the UI.

- **UI localization (F-043).** The browser UI ships in English and Simplified Chinese. A first visit
  follows the browser's `Accept-Language` (`zh*` → 简体中文, `en*` → English, anything else English),
  and the switcher in the shell and on the sign-in pages writes an HttpOnly `zenkins-lang` cookie that
  wins from then on; a switch is a real form POST followed by a document reload, because every rendered
  string has to change at once. Copy goes through one localizer keyed by the English source text, so an
  untranslated key renders readable English rather than a symbol name; server-produced messages the UI
  displays (field validation, schedule outcomes, log-panel state, the trigger guard's refusals) are
  localized too, while API responses, log lines and exception text stay English. `<html lang>` follows
  the choice, and `TranslationCatalogTests` scans the source for keys and fails on a missing
  translation, an orphaned entry or a placeholder mismatch instead of letting a page silently fall back.

- **The repository scripts are Python (no card; supersedes the bash trio).** `scripts/verify.sh`,
  `scripts/publish.sh` and `scripts/suite-duration.sh` became `scripts/verify.py`, `scripts/publish.py`
  and `scripts/suite_duration.py` over a small stdlib-only package (`scripts/zt/`), one module per
  concern: the `dotnet` command builders, the gate's suite table, the publish matrix as data, the
  interactive prompts, the test-log reader and its report. Nothing about the gate changed - same
  commands, same order, byte-identical filter string, fail-fast on the first red suite - and nothing about the
  matrix changed either: the Server still publishes as a folder, the Worker as a folder plus its
  single-file drop, and the JIT rows still pass `-p:PublishAot=false` explicitly (I-004). What changed
  is who can run it and what it can ask: the scripts run natively on Windows instead of needing Git
  Bash, `scripts/publish.py` with no arguments asks which platform(s) and architecture(s), whether the
  publish is self-contained, and whether the Worker's single-file and NativeAOT variants are wanted -
  printing the exact command lines before running any of them (`--dry-run` stops there) - and
  `scripts/verify.py --list` prints §2's commands without running them. The AOT row is refused for a
  RID that is not this machine's, with the reason stated, instead of failing inside ILC. The tooling
  carries its own tests (`python -m unittest discover -s scripts/tests`, 115 cases, no SDK and no
  network), run before the gate in CI; one of them reads docs/development.md §2 and docs/native-aot.md §8
  and fails if any filter they print differs from the one the gate runs; a second, path-filter-free
  workflow (`.github/workflows/tooling.yml`) runs those tests for docs-only changes, which the gate's
  own `paths-ignore` would skip.
- **Step snippets (F-035).** An organization can keep named, reusable text snippets and insert them into a
  project's definition: a `script-step` becomes a fully formed Freestyle step row, a `lua-fragment` is
  inserted at the caret in an inline pipeline (or copied to the clipboard for a repository pipeline - the
  product never rewrites the repository). Inserting **copies the text**; the normal save freezes it, so an
  edited or deleted snippet cannot change a saved project, a frozen spec or a run - there is no reference to
  dangle, and the pages say so. Bodies are bounded like the text they feed (32 KiB / 64 KiB), names are unique
  per organization, and `${secret:…}` references are captured by the same scanner as hand-typed text. Members
  and Owners can both manage snippets; the page previews the resulting step and its secret references.
- **Post actions (F-031).** A pipeline can declare `post = { success, failure, always }` - steps that run
  after the main list, so cleanup no longer needs `continue_on_error` on a real step. The lists are the
  same `PipelineStep` shape as the main one (run/script, env, conditions, artifacts, timeout, retry) and
  their frozen names carry a `post/` prefix, so records and logs cannot be confused with main steps.
  After the main list ends exactly one outcome set runs (success or failure, never both) and then
  `always` runs last. A post step failure is recorded as its own failed step and does not change a
  successful run's result unless the project enables "a failed post step fails the run" (new switch on
  the settings tab; the run then reports `post-failed`). A canceled run still runs `always` inside a
  bounded grace window (30 s, clamped to the run's remaining deadline), so a cancel no longer skips
  cleanup; a step that overruns is killed and recorded (`post-grace-exceeded` vs `timed-out`), and a
  successful cleanup does not change `Canceled`. Freestyle projects get the three lists in the steps
  editor, frozen into the spec the run carries. Schema stays v3 (additive): specs without `post` read,
  render and execute exactly as before.
- **Stage grouping (F-030).** A pipeline can group its steps into named stages, and the run page
  reads a 40-step build as Build → Test → Package instead of one flat list. The name lives **inside the
  frozen spec** (`PipelineStep.Stage`, schema v3): it is not derived from step names, so a later rename
  cannot rewrite what an old run shows, and v1/v2 specs still read, render and execute exactly as
  before (a v1/v2 document carrying a stage is refused as a contradiction; a spec with no stage renders
  as the flat list it always was). Lua has both spellings - `step { stage = "Build", … }` and the
  `pipeline { stages = { { name = "Build", steps = { … } }, … } }` sugar, which assigns the name to the
  steps it contains and flattens them back into the one ordered list. Adjacent steps of one name form
  one collapsible section, with the worst state as its badge and its duration measured as last end
  minus first start; an unstaged step renders outside every section. A failed stage is open and the
  page jumps to its first failed step; successful stages are collapsed. Stages change presentation
  only - execution order, skipping and cancellation are untouched, and Freestyle steps carry the same
  field through the steps editor. No migration: the stage is spec JSON.
- **Worker Git remote probe (I-005).** The Server can now ask a Worker "what heads does this repository
  have right now?" and get `refs/heads/* → sha` plus the remote HEAD back, without cloning and without
  the Server running git or dialing a Worker: a probe is a work item claimed through the existing pull
  channel (`POST /api/workers/probes/claim` / `…/report`), run as one `git ls-remote` with the project's
  delivered credential, and answered as refs or a typed failure (`git-auth-rejected`,
  `git-unreachable`, `git-timeout`, plus `git-failed` for anything else). A probe takes no build slot,
  creates no run and appears on no dashboard or history page; its in-flight queue is in server memory
  (single-Server, a lost probe is re-requested) while its last outcome is persisted on the project and
  stated on its overview. Probes are deduped per (project, repository), non-probe-capable Workers
  (including every pre-I-005 binary) are never handed one, and no probe-capable Worker means a bounded
  `no-worker` answer rather than an endless wait. This is the primitive F-023 (multibranch) and F-025
  (SCM polling) build on; no scheduling ships with it.
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
- **F-009 — a user cancel racing the accept edge could be dropped; fixed.** When the queued-cancel
  compare-and-set lost the race to a worker's accept, the follow-up re-read ran on the same
  tracked context and EF's identity resolution handed back the stale in-memory row (its status
  still `Queued`): the Running branch never ran, the cancel was dropped behind a 302, and the run
  executed on with no recorded cancel request. The claim-storm cancellation test
  (`RunCancellationTests`) caught it; the CI runners' load widened the window until it failed
  every run. Fixed after the `v1.0` tag, 2026-11-15: the re-read is `AsNoTracking`, so the race
  window takes the cancel-request path it was designed to take (evidence on card B-005). Ships
  with the next release.
- **The postgres-fallback key-ring directory carried a forward slash inside Windows paths;
  fixed.** The documented default (`App_Data/keys`) was joined verbatim, and on Windows
  `Path.Combine` embeds the slash — a server-database deployment got a mixed-separator key-ring
  directory that no other surface reports the same way. Fixed after the `v1.0` tag, 2026-11-15:
  joined segment-wise, so the runtime path uses the platform's own separators (evidence on card
  B-005). Ships with the next release.
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
