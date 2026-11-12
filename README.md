# Zenkins

**CI, without the complexity.**

A lightweight, cross-platform, self-hosted CI server. Built with C# / .NET 10, Blazor Server, and Lua pipelines — no Java, no Docker required.

> Status: **pre-alpha / scaffolding**. Nothing is runnable yet. Work is tracked in [`docs/kanban/board.md`](docs/kanban/board.md).

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
- [Roadmap](docs/kanban/roadmap.md) — milestones and long-term direction
- [Kanban board](docs/kanban/board.md) — what is being worked on now

## Getting started

Not yet available. The first milestone (`v0.1`) delivers a deployable Server with login, Organizations, and Projects.

## Repository layout

```text
docs/
  prd.md                 # product requirements
  kanban/                # roadmap, board, issue cards, templates
src/                     # .NET projects (created by I-001)
```

## License

TBD — MIT or Apache-2.0 under consideration (see roadmap parking lot).
