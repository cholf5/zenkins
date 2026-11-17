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
| [F-022](issues/f-022-inline-pipeline-script.md) | Inline pipeline script: a Pipeline project whose pipeline.lua lives on the Server | P1 | M | | F-020 |
| [F-023](issues/f-023-multibranch-jobs.md) | Multibranch jobs: one branch job per branch, with its own history and last status | P1 | L | | I-005, F-017, F-036 |
| [F-025](issues/f-025-scm-polling-trigger.md) | SCM polling: queue a run when the remote head moves, for hosts without webhooks | P2 | M | | I-005, F-017 |
| [F-026](issues/f-026-upstream-downstream-jobs.md) | Upstream and downstream jobs: run a job after another one succeeds | P2 | M | | F-036 |
| [F-030](issues/f-030-stage-grouping.md) | Stage grouping: named stages in the frozen spec, with status and duration | P2 | L | | F-014, F-015, F-020 |
| [F-031](issues/f-031-post-actions.md) | Post actions: always / on-success / on-failure steps after the last step | P2 | M | | F-030, F-009 |
| [F-032](issues/f-032-step-timeout-retry.md) | Step-level timeout and retry: a hung or flaky step is the pipeline's problem | P2 | M | | F-009 |
| [F-033](issues/f-033-step-worker-requirements.md) | Per-step worker requirements and label expressions (the runs_on the PRD promises) | P2 | M | | Q-010, Q-005 |
| [F-034](issues/f-034-organization-environment.md) | Organization environment variables: one shared env layer for every project | P2 | S | | F-021 |
| [F-038](issues/f-038-build-retention.md) | Build retention: keep N builds and/or D days, delete the rest for real | P2 | M | | F-016, F-036 |
| [F-039](issues/f-039-completion-notifications.md) | Completion notifications: signed outbound webhooks on failure, success and recovery | P2 | M | | F-026, F-036, F-037 |
| [F-040](issues/f-040-credential-types.md) | More credential types: username/password pairs and secret files, bindable per step | P2 | M | | F-012, D-006 |
| [F-041](issues/f-041-parameter-types.md) | More parameter types: password, file, run and git ref | P2 | M | | F-021 |
| [F-035](issues/f-035-step-snippets.md) | Step snippets: reusable, insertable step definitions shared across an organization | P3 | M | | F-020, F-022 |
| [F-042](issues/f-042-project-folders.md) | Project folders: nested grouping per organization | P3 | M | | F-023 |
| [Q-018](issues/q-018-schedule-race-catch-path.md) | The schedule's lost-race catch path has no committed fact, and it drops the candidate it caught | P3 | S | | none |
| [B-006](issues/b-006-choice-duplication-and-freestyle-pipeline-path.md) | Duplicate choice entries accepted; a Freestyle row's inherited pipeline path | P3 | S | | none |

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
| [B-005](issues/b-005-first-three-os-gate-defects.md) | Two product defects the first three-OS CI gate caught — cancel race and key-ring path shape | P1 | 2026-11-15 |
| [Q-010](issues/q-010-tag-validation-asymmetry.md) | Tag validation asymmetry: the trigger accepts a union no single worker can cover (Q-005 hardening) | P2 | 2026-11-15 |
| [Q-011](issues/q-011-pipelines-test-temp-leak.md) | Pipelines tests leak per-run temp roots — Dispose swallows only IOException (found by P-001) | P3 | 2026-11-15 |
| [B-003](issues/b-003-dashboard-ceiling-count-interference.md) | Dashboard ceiling tests share one fixture and both seed 200 runs — the count assertion sees 401 | P2 | 2026-11-16 |
| [F-020](issues/f-020-freestyle-job-kind.md) | Freestyle job kind: repository-free projects with server-configured script steps | P1 | 2026-11-16 |
| [F-021](issues/f-021-parameterized-builds.md) | Parameterized builds: project definitions, trigger values and a frozen run snapshot (both kinds) | P1 | 2026-11-16 |
| [F-028](issues/f-028-project-enable-disable.md) | Project enable/disable: stop every trigger without deleting history | P2 | 2026-11-16 |
| [F-029](issues/f-029-queue-controls.md) | Queue controls: per-project concurrency, quiet period and a bounded queue | P2 | 2026-11-16 |
| [F-027](issues/f-027-api-v1.md) | API v1: organization API tokens, trigger/cancel/read runs without a browser | P1 | 2026-11-16 |
| [F-036](issues/f-036-build-numbers.md) | Build numbers and permalinks: #N per project, /runs/last, /lastSuccessful | P2 | 2026-11-16 |
| [F-037](issues/f-037-rebuild-replay.md) | Rebuild and replay: re-run a finished run with the same inputs, or the current definition | P2 | 2026-11-16 |
| [Q-016](issues/q-016-postgres-trigger-cap-fact.md) | The trigger-side queue cap has no provider-discriminating fact — SQLite cannot see its lock either | P2 | 2026-11-16 |
| [B-008](issues/b-008-idempotency-key-scope.md) | Idempotency-Key has no project and no payload dimension, so a reused key answers with another project's run | P1 | 2026-11-16 |
| [Q-015](issues/q-015-api-token-stamp-and-test-gaps.md) | F-027's review leftovers: the last-used stamp still fails loud and wide, and two fixes have no regression net | P3 | 2026-11-16 |
| [F-024](issues/f-024-scheduled-triggers.md) | Scheduled triggers: cron per project, with an honest overlap and miss policy | P1 | 2026-11-16 |
| [I-005](issues/i-005-worker-git-remote-probe.md) | Worker git remote probe: branches and heads without a clone, over the pull model | P1 | 2026-11-16 |
| [B-007](issues/b-007-missing-base-token-palette.md) | The app defines no base token palette — dark/light changes nothing until a base color is picked | P1 | 2026-11-16 |
| [Q-013](issues/q-013-theme-shell-test-strength.md) | The theme shell tests do not pin the load order, the trigger wiring or the cookie's negative cases | P2 | 2026-11-16 |
| [B-004](issues/b-004-windows-shim-facts-skip.md) | Nine shim-observed Git facts skip on Windows — a compiled shim TestAsset would run them there | P3 | 2026-11-16 |
| [Q-020](issues/q-020-concurrency-offline-window-margin.md) | The concurrency mass trigger gives the offline window no margin, so a stalled heartbeat reads as a product failure | P2 | 2026-11-16 |
| [B-009](issues/b-009-ilcompiler-audit-blocks-fresh-build.md) | A fresh Windows machine cannot restore — NU1903 on ILCompiler, and warnings are errors | P2 | 2026-11-16 |
| [D-007](issues/d-007-theme-switcher.md) | Multi-theme switcher in the app bar (dark/light, base gray scale, accent, radius) | P2 | 2026-11-16 |
| [Q-014](issues/q-014-artifact-zip-temp-snapshot-flake.md) | The artifact zip fact snapshots the OS temp directory and fails whenever anything else writes there | P2 | 2026-11-17 |
| [Q-019](issues/q-019-tenant-scan-escapde-base64-collision.md) | A short organization name collides with machine-generated base64 in the tenant page scan | P2 | 2026-11-17 |
| [Q-021](issues/q-021-theme-cascade-model-blind-spots.md) | The theme cascade model counts element selectors as zero and ignores @layer, so it can pass what a browser rejects | P2 | 2026-11-17 |
| [Q-012](issues/q-012-f020-f021-review-test-debt.md) | The F-020/F-021 review proved four behaviours with probes that the suite does not carry | P2 | 2026-11-17 |
| [Q-017](issues/q-017-idempotency-fingerprint-and-cap-replay.md) | The idempotency fingerprint drops invalid tags before hashing, and replay-vs-queue-cap has no fact | P3 | 2026-11-17 |
