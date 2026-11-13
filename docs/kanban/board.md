# Kanban Board

Two columns only. Card details live in `docs/kanban/issues/<ID>-<slug>.md`.
Moving a card means updating its row here and `status` in the issue file in the same operation.

Workflow per card: a developer implements it and sets `status: review` (the card stays in Todo);
an independent reviewer then checks it against its acceptance criteria. Only after a PASS does the
card move to Done with `status: done`. A FAIL sends the card back to the same developer.

## Todo

| ID | Title | Priority | Size | Blocked | Dependencies |
|---|---|---|---|---|---|
| [D-005](issues/d-005-workers-page.md) | Workers page: detail, current runs and safe controls | P2 | M | none | F-004, F-006, F-010, D-002 |
| [Q-003](issues/q-003-v03-test-debt.md) | Close the v0.3 test debt recorded on accepted cards | P2 | M | none | F-012, F-014, F-016, F-017 |

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
| [F-010](issues/f-010-offline-detection-lost-runs.md) | Offline detection → Lost runs | P1 | 2026-11-13 |
| [F-009](issues/f-009-cancellation-timeout.md) | Cancellation propagation and per-run timeout | P1 | 2026-11-13 |
| [F-008](issues/f-008-log-streaming.md) | Real-time log streaming with incremental persistence | P1 | 2026-11-13 |
| [F-007](issues/f-007-workdir-process-runner.md) | Per-run work directory and process runner (Worker) | P1 | 2026-11-13 |
| [F-006](issues/f-006-runs-queue-scheduler.md) | Build runs, queue and scheduler (atomic claim/lease) | P1 | 2026-11-13 |
| [F-004](issues/f-004-worker-registration-tokens.md) | Worker registration, tokens and heartbeat (Server) | P1 | 2026-11-13 |
| [F-005](issues/f-005-worker-console-host.md) | Worker console host (register, heartbeat, reconnect) | P1 | 2026-11-13 |
| [DOC-001](issues/doc-001-developer-setup-guide.md) | Developer setup guide | P2 | 2026-11-12 |
| [DOC-002](issues/doc-002-lua-runtime-contract.md) | Lua runtime contract (engine, sandbox, execution model) | P2 | 2026-11-13 |
| [F-011](issues/f-011-lua-runtime-host.md) | Lua runtime host and sandbox (Worker) | P1 | 2026-11-13 |
| [F-012](issues/f-012-secrets-encrypted-storage.md) | Secrets: encrypted storage and Owner-only management | P1 | 2026-11-13 |
| [F-013](issues/f-013-git-clone-checkout.md) | Git: clone, fetch, checkout with HTTPS/SSH credentials | P1 | 2026-11-13 |
| [F-014](issues/f-014-pipeline-runs-definition.md) | Pipeline runs: definition phase, frozen spec, sequential steps | P1 | 2026-11-13 |
| [F-015](issues/f-015-step-records-env-secrets.md) | Step records, per-step logs, env, continue_on_error, secrets and redaction | P1 | 2026-11-13 |
| [F-016](issues/f-016-artifacts.md) | Artifacts: safe local store, per-run download | P1 | 2026-11-13 |
| [F-017](issues/f-017-webhook-trigger.md) | Webhook trigger: signature, branch filter, replay protection | P1 | 2026-11-13 |
| [Q-002](issues/q-002-milestone-acceptance.md) | Milestone acceptance harness for v0.3 (real repo, real worker, real webhook) | P1 | 2026-11-13 |
| [D-002](issues/d-002-dashboard.md) | Dashboard: running, queued, workers and recent failures | P1 | 2026-11-14 |
| [D-003](issues/d-003-project-tabs-git-credentials.md) | Project detail as tabs, with git credentials in settings | P1 | 2026-11-14 |
| [D-004](issues/d-004-run-log-viewer.md) | Run detail: pause, search and per-step log filtering | P1 | 2026-11-14 |
| [Q-004](issues/q-004-recovery-acceptance.md) | Recovery acceptance: server restart, worker crash, honest Lost | P1 | 2026-11-14 |
