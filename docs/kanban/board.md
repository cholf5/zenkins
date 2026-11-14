# Kanban Board

Two columns only. Card details live in `docs/kanban/issues/<ID>-<slug>.md`.
Moving a card means updating its row here and `status` in the issue file in the same operation.

Workflow per card: a developer implements it and sets `status: review` (the card stays in Todo);
an independent reviewer then checks it against its acceptance criteria. Only after a PASS does the
card move to Done with `status: done`. A FAIL sends the card back to the same developer.

## Todo

| ID | Title | Priority | Size | Blocked | Dependencies |
|---|---|---|---|---|---|
| [P-001](issues/p-001-release-engineering.md) | Release engineering: version, tag, CHANGELOG, verify.sh, CI (the v1.0 gate) | P1 | M | user: push → CI green → tag | B-001, Q-009 |
| [Q-011](issues/q-011-pipelines-test-temp-leak.md) | Pipelines tests leak per-run temp roots — Dispose swallows only IOException (found by P-001) | P3 | S | — | — |

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
| [D-005](issues/d-005-workers-page.md) | Workers page: detail, current runs and safe controls | P2 | 2026-11-14 |
| [Q-003](issues/q-003-v03-test-debt.md) | Close the v0.3 test debt recorded on accepted cards | P2 | 2026-11-14 |
| [Q-005](issues/q-005-multi-worker-concurrency.md) | Multi-worker concurrency and tag assignment, end to end | P1 | 2026-11-14 |
| [Q-006](issues/q-006-tenant-isolation-audit.md) | Cross-tenant isolation audit (data, logs, artifacts, credentials) | P1 | 2026-11-14 |
| [I-003](issues/i-003-release-packaging.md) | Self-contained publish and OS service packaging (Windows Service / systemd / launchd) | P1 | 2026-11-14 |
| [I-004](issues/i-004-native-aot.md) | NativeAOT: the Worker publishes AOT, the Server gets a recorded verdict | P2 | 2026-11-14 |
| [DOC-003](issues/doc-003-install-guide-upgrade-notes.md) | Install guide and upgrade notes | P2 | 2026-11-14 |
| [D-006](issues/d-006-secrets-page-polish.md) | Secrets page polish: creator column and pipeline references | P2 | 2026-11-14 |
| [Q-007](issues/q-007-cross-platform-verification.md) | Cross-platform verification on real Windows and Linux machines | P1 | 2026-11-14 |
| [Q-008](issues/q-008-postgresql-published-form.md) | PostgreSQL in published form: real instance, migrations, full build | P1 | 2026-11-14 |
| [F-018](issues/f-018-project-git-credential-assignment.md) | Project git credential assignment, and a real clone served by the server | P1 | 2026-11-14 |
| [F-019](issues/f-019-form-binding-500-and-stack-leak.md) | A form post with no bindable fields returns 500 and a stack instead of a validation error | P2 | 2026-11-14 |
| [B-001](issues/b-001-dashboard-org-switch-sweep-flake.md) | Dashboard org-switch flake: seeded Running run swept Lost before the page reads it (D-002-B2) | P1 | 2026-11-15 |
| [Q-009](issues/q-009-ssh-clone-real-sshd.md) | SSH clone verified against a real sshd — the keyless-sshd fixture (F-018 follow-up) | P1 | 2026-11-15 |
| [B-002](issues/b-002-maxconcurrency-check-then-act.md) | maxConcurrency slot check is check-then-act; a token-holding client can exceed the cap (Q-005 hardening) | P2 | 2026-11-15 |
| [Q-010](issues/q-010-tag-validation-asymmetry.md) | Tag validation asymmetry: the trigger accepts a union no single worker can cover (Q-005 hardening) | P2 | 2026-11-15 |
