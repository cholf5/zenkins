# Kanban Board

Two columns only. Card details live in `docs/kanban/issues/<ID>-<slug>.md`.
Moving a card means updating its row here and `status` in the issue file in the same operation.

Workflow per card: a developer implements it and sets `status: review` (the card stays in Todo);
an independent reviewer then checks it against its acceptance criteria. Only after a PASS does the
card move to Done with `status: done`. A FAIL sends the card back to the same developer.

## Todo

| ID | Title | Priority | Size | Blocked | Dependencies |
|---|---|---|---|---|---|
| [F-006](issues/f-006-runs-queue-scheduler.md) | Build runs, queue and scheduler (atomic claim/lease) | P1 | L | none | F-004, F-005 |
| [F-007](issues/f-007-workdir-process-runner.md) | Per-run work directory and process runner (Worker) | P1 | M | none | F-006 |
| [F-008](issues/f-008-log-streaming.md) | Real-time log streaming with incremental persistence | P1 | L | none | F-006, F-007 |
| [F-009](issues/f-009-cancellation-timeout.md) | Cancellation propagation and per-run timeout | P1 | M | none | F-006, F-007, F-008 |
| [F-010](issues/f-010-offline-detection-lost-runs.md) | Offline detection → Lost runs | P1 | M | none | F-004, F-006 |

## Done

| ID | Title | Priority | Done At |
|---|---|---|---|
| [I-001](issues/i-001-solution-scaffold.md) | Solution scaffold: .NET 10, .slnx, CPM, Server/Worker/Shared | P1 | 2026-11-12 |
| [I-002](issues/i-002-efcore-multitenant-data-layer.md) | EF Core data layer with enforced tenant isolation | P1 | 2026-11-12 |
| [D-001](issues/d-001-app-shell-blazorblueprint.md) | Blazor Server app shell on BlazorBlueprint v4 | P1 | 2026-11-12 |
| [Q-001](issues/q-001-tenant-isolation-tests.md) | Tenant isolation test harness (two-org fixture) | P1 | 2026-11-12 |
| [F-001](issues/f-001-identity-auth-bootstrap.md) | Identity auth + first-run Owner bootstrap | P1 | 2026-11-12 |
| [F-002](issues/f-002-organization-membership.md) | Organization CRUD, members, switcher | P1 | 2026-11-12 |
| [F-003](issues/f-003-project-crud.md) | Project CRUD | P1 | 2026-11-12 |
| [F-004](issues/f-004-worker-registration-tokens.md) | Worker registration, tokens and heartbeat (Server) | P1 | 2026-11-13 |
| [F-005](issues/f-005-worker-console-host.md) | Worker console host (register, heartbeat, reconnect) | P1 | 2026-11-13 |
| [DOC-001](issues/doc-001-developer-setup-guide.md) | Developer setup guide | P2 | 2026-11-12 |
