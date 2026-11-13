# Zenkins

**CI, without the complexity.**

A lightweight, cross-platform, self-hosted CI server. Built with C# / .NET 10, Blazor Server, and Lua pipelines — no Java, no Docker required.

> Status: **pre-alpha**. The Server runs: first-run Owner bootstrap, cookie sign-in, Organizations
> and members, organization switching, and projects scoped to the active organization (create/edit as
> a Member, delete reserved to an Owner). Workers can register with an Owner-minted registration
> token and keep heartbeats (F-004/F-005). Runs can be triggered from a project page, queued,
> claimed by workers, executed in isolated per-run work directories (F-006/F-007) and watched live
> with incrementally persisted logs (F-008); organization secrets are stored encrypted and managed by
> Owners only, never readable back (F-012). Work is tracked in
> [`docs/kanban/board.md`](docs/kanban/board.md).

## Why another CI server

| | Jenkins | Zenkins |
|---|---|---|
| Stack | Java | C# / .NET |
| Pipeline | Groovy / Jenkinsfile | Lua (`pipeline.lua`) |
| Worker | Agent (JVM) | .NET Worker (self-contained capable) |
| Docker | Optional | Not required |
| Multi-tenancy | Plugin-dependent | Native |

## Architecture

```text
Browser → Blazor Server UI → ASP.NET Core Server → (Auth · Scheduler · SignalR) → DB
                                                                ↑
                                              Worker 1 (Windows) │
                                              Worker 2 (Linux)   │ HTTP + SignalR
                                              Worker 3 (macOS)   │
```

The Server never executes builds. Workers connect outbound to the Server, pull jobs, execute native processes, and stream logs back.

## Documentation

- [Product requirements (v1.0)](docs/prd.md) — the spec of record
- [Development setup](docs/development.md) — clone, build, run, configure, troubleshoot
- [Lua runtime contract](docs/lua-runtime.md) — engine, sandbox surface, execution model, fixtures
- [Roadmap](docs/kanban/roadmap.md) — milestones and long-term direction
- [Kanban board](docs/kanban/board.md) — what is being worked on now

## Getting started

The only prerequisite is the .NET 10 SDK: no Java, no Docker, no Node.js and no database server for
the default (SQLite) path.

```bash
git clone https://github.com/your-org/zenkins.git zenkins   # replace with the real clone URL
cd zenkins
dotnet build -c Release
dotnet run --project src/Server
```

Then open <http://localhost:5067> and register: the first account becomes the Owner of a starter
organization and lands on the dashboard. Later accounts start without an organization until an Owner
adds them as a member.

Full instructions — verified prerequisites, PostgreSQL, configuration, repository layout and
troubleshooting — are in [`docs/development.md`](docs/development.md).

## Repository layout

```text
docs/
  prd.md                 # product requirements
  development.md         # developer setup guide
  lua-runtime.md         # Lua engine, sandbox and execution contract
  kanban/                # roadmap, board, issue cards, templates
src/                     # .NET projects (Server, Shared, Worker)
```

## License

TBD — MIT or Apache-2.0 under consideration (see roadmap parking lot).

Zenkins bundles third-party components; their licenses and attributions are recorded in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) — currently the Lua engine
(MoonSharp 2.0.0, BSD-3-Clause, with the MIT-licensed parts it is built from).
