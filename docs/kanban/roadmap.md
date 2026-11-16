# Roadmap

This roadmap preserves long-term goals and milestone direction. Executable task cards live in `docs/kanban/issues/` and are tracked in `docs/kanban/board.md`.

Source of record for all requirements: [`docs/prd.md`](../prd.md) (v1.0).

## Planning Policy

- Do not create detailed issue cards for every possible future requirement at once.
- Keep detailed cards focused on the current 1–2 iterations or the next 5–10 actionable items.
- Use this roadmap for long-term themes, epics, and candidate work.
- Convert roadmap items into detailed task cards when they become near-term work.
- Use discovery/spike cards for uncertain work before creating implementation tasks.

## Milestones

### v0.1 — Foundation (Phase 1)

Goal:
- A deployable Server you can log into, where you can create an Organization and a Project. Multi-tenant isolation enforced from the first query, not retrofitted.

Candidate tasks:
- Solution scaffold: .NET 10, `.slnx`, central package management, `src/Server`, `src/Worker`, `src/Shared`. → `I-001`
- Blazor Server + BlazorBlueprint v4 shell, layout, theme. → `D-001`
- EF Core (SQLite + PostgreSQL), core entities, global tenant query filters. → `I-002`
- Tenant isolation test harness (two-org fixture). → `Q-001`
- ASP.NET Core Identity: register / login / first-run Owner bootstrap. → `F-001`
- Organization CRUD, members, Organization switcher. → `F-002`
- Project CRUD (name, repo URL, default branch, pipeline path). → `F-003`
- Developer setup guide. → `DOC-001`

### v0.2 — Worker runtime (Phase 2)

Goal:
- Server dispatches jobs to Local and Remote Workers over HTTP + SignalR; workers execute native processes and stream logs in real time; jobs can be cancelled.

Candidate tasks:
- `Zenkins.Worker` console host; registration token, heartbeat, capability/tag reporting.
- Worker authentication (token issue, revoke, rotate) — communication must be authenticated from the first call.
- Scheduler + job queue; claim/lease so one run is never executed twice.
- Per-run isolated work directory (`work/{run-id}/repository|artifacts|temp`).
- Process runner on `ProcessStartInfo` (no shell string concatenation), streaming stdout/stderr.
- SignalR log streaming; logs persisted incrementally, never fully buffered in server memory.
- Cancellation propagation and per-run timeout.
- Offline detection → `Lost` runs (never silently retry).

### v0.3 — Lua pipelines, Git, Webhooks (Phases 3–4)

Goal:
- A real build runs from `pipeline.lua` in a Git repository, and a `git push` triggers it.

Engine decision (was a spike in the original plan): **not a spike any more** — measured and pinned in
[`docs/lua-runtime.md`](../lua-runtime.md) (MoonSharp 2.0.0; 3.0.0-beta.1 is behaviourally identical
for our surface, so the stable tag wins; switch triggers are recorded there). The card that consumes
it is `F-011`.

Candidate tasks:
- Lua runtime host + sandbox, host-driven coroutine budgets, one `Script` per run. → `F-011`
- Secrets: encrypted at rest, Owner-only management, never re-readable. → `F-012`
- Git CLI integration: clone / fetch / checkout, HTTPS + SSH credentials, LFS/submodule compatibility. → `F-013`
- Pipeline runs: `pipeline.lua` definition phase, strict validation, frozen spec, linear step execution. → `F-014`
- Step records, per-step logs, `continue_on_error`, conditions, pipeline/step env, `secret()` and redaction. → `F-015`
- Artifacts: local filesystem store, path-traversal-safe, downloadable per run. → `F-016`
- Webhook endpoint: signature/secret verification, branch filter, replay protection, commit tracking. → `F-017`
- Milestone acceptance harness (real repo, real worker, real webhook, real artifact). → `Q-002`

Not in this milestone, deliberately: per-step log viewer controls (pause/search) stay v0.9; artifact
retention and git object caching are parking-lot items.

### Carried over from v0.3

- The accepted v0.3 cards each recorded their follow-ups rather than leaving them silent. The
  test-shaped ones are collected in `Q-003` (rate-limiter properties, a snapshot flake, the
  URL-to-clone product path, two loop-level definitions tests, one key-ring branch).

### v0.9 — Management UI (Phase 5)

Goal:
- The whole product is usable from the browser without touching the API.

Candidate tasks:
- Dashboard: recent runs, running, queued, worker status, recent failures. → `D-002`
- Project detail: overview / pipelines / runs / settings tabs. → `D-003` (also the git-credential page
  that F-013 left without one)
- Build run detail: step list, live log viewer (pause auto-scroll, search, download), artifacts.
  → `D-004`
- Workers page: OS, arch, tags, status, current runs, concurrency, last heartbeat. → `D-005`
- Secrets page. → `F-012` shipped the minimal Owner-only page (list, create, rotate, delete, no reveal);
  the remaining polish (creator column, "which pipelines reference this name") has its own card:
  `D-006`.

### v1.0 — Stable release (Phase 6)

Goal:
- Pass all seven acceptance scenarios in PRD §10 on Windows, Linux, and macOS.

Candidate tasks:
- Multi-worker concurrency and no-double-assignment tests. → `Q-005`
- Worker disconnect recovery, Server restart recovery. → `Q-004`
- Cross-tenant isolation audit (data, logs, artifacts, git credentials). → `Q-006`
- Self-contained publish + Windows Service / systemd / launchd packaging. → `I-003`
- Native AOT: the Worker ships a `PublishAot` artifact (MoonSharp proof included); the Server stays
  JIT with the verdict recorded, because Blazor Server + EF Core + Identity are not .NET 10 AOT
  targets. → `I-004`
- Install guide and upgrade notes. → `DOC-003` (blocked by `I-003`)

PRD §10's seven scenarios now have real-machine evidence (Q-002/Q-004/Q-005/Q-006/Q-007/Q-008 and
F-018's credential proof). Three cards remain between here and the tag, in the user's ledger order —
the two pre-tag gates first, the release work as the closing gate:

- The captured D-002-B2 Dashboard flake (seeded `Running` run swept `Lost` by the startup sweep), so
  CI is trustworthy from day one. → `B-001`
- SSH clone against a real sshd — the keyless-sshd fixture F-018 recorded as its follow-up, because
  the tag must not go out while the SSH half of the credential story has no verdict. → `Q-009`
- Release engineering: version, tag, CHANGELOG, `verify.sh`, CI — the gate that lets the repository
  back a "v1.0" claim. → `P-001` (depends on `B-001` + `Q-009`)

### v1.0.1 — Post-release hardening

Goal:
- Close the seams the v1.0 verification cards recorded but deliberately deferred, so the release
  ships with them known (CHANGELOG known-issues entries via `P-001`) rather than silent.

Candidate tasks:
- The `maxConcurrency` slot check is check-then-act; a token-holding client can exceed the cap. → `B-002`
- Tag validation asymmetry: the trigger accepts a union no single worker can cover — decide
  tighten-vs-document and align both sides. → `Q-010`
- Q-006's "Documented invariants" section is adjudicated prose outside machine validation (recorded
  in that card's development log); turn the invariants into machine-checked assertions. No card yet
  — open one when this becomes near-term work.

### v1.1 — Job flexibility

Goal:
- The Jenkins-shaped job model lands beside the SCM-centric one: a project can be a **Freestyle** job
  (no repository, steps configured in the UI as shell scripts, every run executing the snapshot it
  was triggered with) and a job can declare **build parameters** the trigger form fills in and every
  step's environment receives. The Pipeline kind (repository + `pipeline.lua`) is unchanged; both
  kinds share the frozen-spec execution contract, so scheduling, logs, secrets and artifacts do too.

Candidate tasks:
- Freestyle job kind: kind column + nullable repository fields, server-frozen spec on the run, script
  steps (schema v2) executed from a per-step file by the platform shell, steps editor, workspace
  working directory, webhook/credential guards. → `F-020`
- Parameterized builds: parameter definitions on the project (string/text/boolean/choice), values
  snapshotted onto the run at trigger time, injected as environment variables into every step of both
  kinds; dynamic trigger form; webhook and other headless triggers resolve defaults. → `F-021`

### v1.2 — Job model, triggers and history

Goal:
- The Jenkins-shaped feature set people miss when they migrate lands beside what v1.1 added: a job
  defined on the server (not only in a repository), more than one way to start it, a history that can
  be read and bounded, and a machine-facing API. Still no plugins, no Docker, no DAG: the product
  stays a small CI server with a Lua pipeline.

This milestone exists because the v1.0/v1.1 surface had a clear, enumerated gap list against Jenkins
(two job definitions, branch discovery, scheduled and polled triggers, chains, an API, numbered
builds, rebuilds, retention, notifications, credential and parameter breadth). Every item judged
near-term work is a card; the ones that would change the product's shape stayed in the Parking Lot,
named so nothing is lost.

Job model:
- Inline pipeline script: a `pipeline` project whose `pipeline.lua` lives on the Server, with the
  definition phase intact. → `F-022`
- Multibranch jobs: one branch job per branch, its own history and last status, per-branch retention.
  → `F-023` (needs `I-005`)
- Project folders: nested grouping per organization, names unique inside a folder. → `F-042`
- Stage grouping: named stages in the frozen spec, rendered with status and duration. → `F-030`
- Post actions: always / on-success / on-failure steps, still running on cancellation. → `F-031`
- Step-level timeout and retry, with typed failure reasons. → `F-032`
- Per-step worker requirements and label expressions — the `runs_on` PRD §3.4.4 already shows. → `F-033`
- Organization environment variables and insertable step snippets. → `F-034`, `F-035`

Triggers and scheduling:
- Scheduled (cron) triggers with an honest overlap, miss and restart policy. → `F-024`
- SCM polling for hosts that cannot deliver webhooks, deduped against a webhook on the same commit.
  → `F-025` (needs `I-005`)
- Upstream/downstream chains with save-time cycle refusal and an outbox delivery guarantee. → `F-026`
- API v1 with scoped organization tokens: trigger, read, tail, cancel. → `F-027`
- Project enable/disable and queue controls (concurrency, quiet period, queue cap). → `F-028`, `F-029`
- The trigger primitive both discovery paths need: a worker-side git remote probe. → `I-005`

Results:
- Build numbers and permalinks; rebuild and replay; retention that really deletes; signed completion
  notifications. → `F-036`, `F-037`, `F-038`, `F-039`

Credentials and parameters:
- Username/password and secret-file credentials bindable per step; password/file/run/git-ref
  parameters. → `F-040`, `F-041`

Suggested landing order (each step leaves the product coherent):
1. `F-036` (numbers) → `F-028` (disable) → `F-029` (queue controls) — small, and everything else
   references them.
2. `F-022` (inline script) and `I-005` (probe) — the two enablers.
3. `F-024` (cron) → `F-025` (polling) → `F-023` (multibranch) → `F-026` (chains).
4. `F-027` (API) — independent, but its docs land better after numbers and chains exist.
5. `F-030` → `F-031` → `F-032` → `F-033` (the spec v3 cluster: one bump, one migration story).
6. `F-037`, `F-038`, `F-039`, `F-040`, `F-041`, `F-034`, `F-035`, `F-042`.

Scope note: PRD §7.2 lists 定时任务 (scheduled triggers) as a v1.0 non-goal. `F-024` reverses that and
owns the PRD edit; the other §7.2 non-goals below are deliberately still out of scope.

## Parking Lot

Future ideas that are not yet committed:

- Docker images (explicitly out of v1 scope, but the most requested thing a self-hosted CI gets asked for).
- DAG / parallel pipelines, matrix builds, and a run that spans more than one worker (the prerequisite
  of both: workspace hand-off and per-step leases).
- Plugin system and third-party marketplace; Jenkins shared libraries as a runtime mechanism (v1.2's
  `F-035` snippets are the frozen-at-save substitute).
- Tool installations (a managed JDK / MSBuild / Unity per tool version) and lockable shared resources.
- Global build trend charts, artifact fingerprints and `last*Build` badges beyond `F-036`'s permalinks.
- Test-report ingestion (JUnit), coverage and code-quality dashboards.
- S3-compatible artifact storage backend.
- Email notifications (v1.2 ships signed outbound webhooks; a mail bridge can consume them).
- Enterprise SSO, fine-grained RBAC, per-folder permissions, deployment approvals / manual gates.
- Built-in package registry.
- Multi-server deployment (the probe queue and the lease sweeper assume one Server).
- `p4` / Perforce repository support (common in game teams — not in the PRD, worth asking about).
