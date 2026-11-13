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

### v0.9 — Management UI (Phase 5)

Goal:
- The whole product is usable from the browser without touching the API.

Candidate tasks:
- Dashboard: recent runs, running, queued, worker status, recent failures.
- Project detail: overview / pipelines / runs / settings tabs.
- Build run detail: step list, live log viewer (pause auto-scroll, search, download), artifacts.
- Workers page: OS, arch, tags, status, current runs, concurrency, last heartbeat.
- Secrets page.

### v1.0 — Stable release (Phase 6)

Goal:
- Pass all seven acceptance scenarios in PRD §10 on Windows, Linux, and macOS.

Candidate tasks:
- Multi-worker concurrency and no-double-assignment tests.
- Worker disconnect recovery, Server restart recovery.
- Cross-tenant isolation audit (data, logs, artifacts, git credentials).
- Self-contained publish + Windows Service / systemd / launchd packaging.
- Install guide and upgrade notes.

## Parking Lot

Future ideas that are not yet committed:

- License decision: MIT vs Apache-2.0 (PRD says TBD).
- Docker images (explicitly out of v1 scope, but the most requested thing a self-hosted CI gets asked for).
- DAG / parallel pipelines, matrix builds, scheduled triggers.
- Plugin system and third-party marketplace.
- S3-compatible artifact storage backend.
- Build trend analysis / dashboards beyond the current run list.
- Enterprise SSO, fine-grained RBAC, deployment approvals.
- Built-in package registry, code-quality analysis.
- `p4` / Perforce repository support (common in game teams — not in the PRD, worth asking about).
