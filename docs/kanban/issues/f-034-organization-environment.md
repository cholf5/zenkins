---
id: F-034
title: Organization environment variables — one shared env layer for every project
type: feature
priority: P2
size: S
status: done
created: 2026-11-16
updated: 2026-11-17
blocked: none
---

## Goal

An organization defines plain environment variables once and every project's steps
receive them, so a registry host, a tool path or a proxy setting is not copied into
every project.

## Background

The environment layers today are: server-provided `ZENKINS_*` metadata, build
parameters (F-021), pipeline-level `env`, step-level `env`, plus secrets resolved by
reference. There is no organization layer, so shared settings are duplicated per
project or smuggled into a secret (wrong: secrets are Owner-only and write-only, and
these values are not sensitive).

Jenkins' equivalent is a global environment property; the interesting part here is
layering and snapshotting, not the storage.

## Acceptance Criteria

- [x] An organization can define variables (name/value) with the same name rules as
      secret names; Owner-only edit, Member-readable, values never treated as
      secrets (no masking, no write-only form).
- [x] The documented layering is enforced and tested (lowest to highest):
      `ZENKINS_*` metadata → organization vars → parameters → pipeline env → step env.
- [x] `ZENKINS_*` and other reserved prefixes cannot be overridden by an organization
      variable; the save is refused with a field error naming the collision.
- [x] The resolved organization layer is **snapshotted onto the run** at trigger time:
      editing an organization variable never changes a queued or running run.
- [x] The worker injects the snapshot exactly like parameters (same claim field, same
      precedence), and `${env:NAME}` in a step resolves against it.
- [x] Removing a variable does not break history: old runs keep their snapshot, and
      the run page can show the environment it ran with (values included — they are
      configuration, not secrets).
- [x] A cap on count and value length (e.g. 100 vars, 4 KiB each) with a clear error.

## Subtasks

- [x] `OrganizationEnvironment` entity (org, name, value, attribution) + migration and
      a unique (org, name) index.
- [x] Merge function in the run-create path that builds the frozen environment
      snapshot, plus the reserved-name check.
- [x] Claim payload: the snapshot travels with the run (extend the existing
      server-provided environment block rather than adding a second one).
- [x] UI: an Environment card under organization settings (list, add, edit, delete),
      Owner-only mutations, read-only for Members.
- [x] PRD §3.11.3 / §3.1.1: record the layer order and the snapshot rule.

## Dependencies

- F-021 (the parameter layer and the claim's environment block) — in review

## Test Cases

### TC-001: A shared variable reaches every project's steps

Steps:
1. Define `REGISTRY_HOST=registry.internal` at organization level.
2. Run two different projects that echo `${env:REGISTRY_HOST}`.

Expected:
- Both logs show `registry.internal`; neither project defines the variable itself.

### TC-002: Precedence is the documented one

Steps:
1. Organization `TARGET=org`, project env `TARGET=project`, step env `TARGET=step`;
   trigger with parameter `TARGET=param` for a second project.

Expected:
- The step sees `step`; a project without a step override sees `project`; a project
  with only a parameter sees `param`; a project with nothing sees `org`.

### TC-003: The snapshot is frozen

Steps:
1. Trigger a run (no worker), change the organization variable, then let it execute.

Expected:
- The queued run's steps see the old value and the run page shows the old snapshot.

## Notes and Decisions

- Values are plain configuration: they appear in the run's environment view and in
  logs if a step echoes them. Anything sensitive belongs in Secrets (F-012), and the
  UI says so next to the form.

## Implementation Notes

### 1. Data model, and where the name rule comes from

- `OrganizationEnvironmentVariable` (server entity, table `OrganizationEnvironmentVariables`):
  `(OrganizationId, Name, Value, CreatedByUserId, UpdatedByUserId, CreatedAt, UpdatedAt)`, unique
  `(OrganizationId, Name)`, `ITenantOwned`, tenant query filter, cascade from `Organization`.
  Migration `20261117120221_AddOrganizationEnvironment`: a **new table** (zero rows in every existing
  installation, nothing to backfill) plus a **new nullable column** `BuildRuns.EnvironmentJson` (every
  historical run starts `null` = "queued before this layer existed" - no default, no rewrite; the
  migration's own doc comment records this).
- **The name rule is reused, not rewritten.** `OrganizationEnvironmentValidation.ValidateName`
  delegates to `SecretValidation.ValidateName` (same trim, same `^[A-Za-z_][A-Za-z0-9_]{0,63}$`,
  same 64-character bound) and its three shape error codes *are* the secret codes
  (`OrganizationEnvironmentErrorCodes.NameRequired = SecretErrorCodes.NameRequired`, …), so the two
  vocabularies cannot drift. On top of the shape it adds `name-reserved`.
- **The page permission is deliberately NOT the secret page's.** `GetVariablesAsync` checks
  *membership* (a Member reads the list, values included); create/update/delete check
  *ownership*. The Secrets page is whole-page Owner-only; copying that guard would have violated AC1.

### 2. Where the layering lives, and the facts that pin it

One function: `StepEnvironment.Compose` (`src/Worker/Pipelines/StepEnvironment.cs`), one call site
(`PipelineRunner.cs`, step loop). Lowest → highest:

```
worker process env → ZENKINS_* metadata → server-resolved map (org, then parameters) → pipeline env → step env
```

The server **resolves organization → parameters once**, when it composes the claim
(`RunEnvironmentSnapshot.Merge`: parameters win a shared name), so exactly one name→value map crosses
the wire and the worker is a dumb injector. Two server dictionaries in the claim would mean two copies
of the order.

The two snapshots stay **separate columns on the run** (`EnvironmentJson`, `ParametersJson`) because
the run page must say which layer a value came from and because a rebuild re-resolves only the
parameter snapshot against today's definitions.

Facts (deliberately split, per the review: a same-name collision between `ZENKINS_*` and the
organization layer is **not representable** after AC3, so it cannot be part of a five-value chain):

- **TC-002a** `TC_002a_the_four_adjacent_edges_resolve_low_to_high` - the four value edges, one
  fact, three real runs, child stdout compared to *distinctive* values (`value-from-step`,
  `value-from-pipeline`, `value-from-param`, `value-from-org`): organization→parameters,
  parameters→pipeline env, pipeline env→step env, and the inherited environment losing every edge.
- **TC-002b** `TC_002b_the_metadata_edge_is_proven_by_refusal_and_filtering` - the metadata edge by
  refusal + filtering (below).
- `The_inherited_environment_is_the_lowest_layer_and_the_server_map_is_next` (WorkerRuntime) - the
  bottom two edges in isolation.

### 3. Reserved prefix: three places, one rule

1. **Organization variable, at save** - `OrganizationEnvironmentErrorCodes.NameReserved`, a field
   error on `Name` naming the prefix (`?error=name-reserved&name=…`), nothing stored.
2. **Build parameter, at save** - `ProjectParameters.Validate` refuses the same prefix. This is new:
   before the reorder the parameter merely *lost by sort order*, which was correct by accident and
   silent; with the metadata on the bottom of the chain a parameter would now win, so it must be
   refused.
3. **Pipeline / step `env` (Lua has no save step)** - `StepEnvironment.Compose` drops the name and
   reports it through `onReservedDropped`; `PipelineRunner` writes that line into the run log
   (`… the step env sets 'ZENKINS_RUN_ID', which is reserved for the run's own metadata; the declared
   value was ignored`). Not silently dropped, not silently winning.

**One rule, two callers.** The predicate and the prefix list are
`Zenkins.Shared.Pipelines.ServerEnvironmentNames` - the server refuses with them at save time and the
worker's `RunEnvironment.IsReserved` / `StepEnvironment.Compose` delegate to them, so the two sides of
the wire cannot answer "is this name reserved?" differently. (Before the F-034 review fix the worker
carried its own copy compared with `StringComparison.Ordinal` while the server's compared
`OrdinalIgnoreCase`; on Windows that let a step env's `zenkins_run_id` overwrite the run's own
`ZENKINS_RUN_ID` in the child's case-insensitive environment block - the spoof this card exists to
close. The shared predicate is the fix, and
`A_lower_case_reserved_name_is_dropped_too_because_a_windows_environment_is_case_insensitive` is the
fact that pins it.)

**Mechanism change, recorded as the review asked.** F-036's
`A_parameter_or_organization_variable_cannot_overwrite_the_build_metadata` and F-015's
`StepConditionTests` TC_003 (`RunEnvironmentWins`) are **still green and their intent is unchanged** -
a parameter / step env still cannot spoof the run metadata - but the mechanism behind them changed
from *ordering* to *filtering*. Both are now expressed through `StepEnvironment.Compose` (the
RunEnvironmentTests fact also asserts the diagnostic fires once per dropped name).

### 4. Snapshot → claim → worker

- Frozen in `RunService.StageQueuedRunAsync`, the one insert path of every trigger source.
- `ReadForOrganizationAsync(context, systemDataScope, run.OrganizationId, ct)`: the run's own
  organization named in the predicate, read inside `ISystemDataScope`. Both headless paths in this
  tree happen to set the tenant before staging (`ScheduleService` sets it per organization), so the
  "silently empty layer" was not reachable today - but the read no longer depends on that happening.
- Claim: `RunClaimResponse.Parameters` is the single resolved map (organization ← parameters). No
  second claim field. Worker: `WorkerRunLoop.BuildRunEnvironment` → `RunEnvironment { Metadata, Values }`.
- `${env:NAME}` resolves against the composed map at the step boundary, so it sees the layer that won.

### 5. History and the run page

Deleting or editing a variable never touches a run: `BuildRuns.EnvironmentJson` is that run's own
copy. The run page renders an "Organization environment" card (`#run-environment`,
`#run-environment-{NAME}`) from that column only - values included, because they are configuration,
not secrets. Facts: TC-003 (frozen + page shows the old value and not the new one), `Deleting_a_
variable_leaves_the_runs_snapshot_and_its_page_intact`.

### 6. Caps

`OrganizationEnvironmentValidation`: 100 variables per organization, 4096 characters per value, the
secret name bound (64). Errors: `value-too-long` (field), `limit-reached` (form-level, message names
the cap and the way out). Facts: `The_value_length_and_the_variable_count_are_capped_with_clear_errors`
(the boundary value is accepted, one character more is refused; the 100th row is accepted, the 101st
is refused with the list unchanged).

### 7. Facts added (27)

- `OrganizationEnvironmentTests` (20, `Category=Runs`): the store/name/role/cap facts (Owner view,
  Member read-only + 403, name-rule theory ×5, organization reserved prefix ×4, parameter reserved
  prefix, caps), TC_001 (two projects, real steps), TC-002a, TC-002b, TC-003 (frozen + page),
  delete-keeps-snapshot, headless webhook trigger, and the secret boundary
  (`A_variable_that_shares_a_secret_s_name_stays_configuration_and_the_secret_stays_masked`).
- `RunEnvironmentTests` (+2): the reserved-name drop + diagnostic, the lower-case reserved name
  (the case-insensitivity fact), and the inherited-metadata-server-map order.
- Tenancy (+5): organization variables in the entity probe registry (read isolation, cross-tenant
  write refusal, Find-after-switch), `The_run_snapshot_reader_reads_only_the_run_s_own_organization_
  and_needs_no_request_tenant` (both directions + no request tenant), and
  `Environment_variable_mutations_cannot_reach_another_tenants_variable` (foreign id = bodyless 404,
  row untouched); the page scan also covers `/organizations/environment` and the foreign value.

### 8. Mutation record

Each fact was proven red under one mutation; every patch was reverted and the six files' SHA-256
verified identical afterwards (`artifacts/f-034/mutations.log`).

| # | Mutation | Fact | Result |
|---|---|---|---|
| M1 | parameters and pipeline env swapped in `Compose` | TC-002a | red (`value-from-pipeline` → `value-from-param`) |
| M2 | pipeline env and step env swapped in `Compose` | TC-002a | red (`value-from-step` → `value-from-pipeline`) |
| M3 | claim merge lets the organization layer win a shared name | TC-002a | red (`value-from-param` → `value-from-org`) |
| M4 | declared reserved names are no longer dropped | TC-002b + RunEnvironmentTests | red |
| M5 | organization name prefix no longer refused | `An_organization_name_inside_the_server_prefix…` | red |
| M6 | parameter name prefix no longer refused | `A_parameter_name_inside_the_server_prefix…` | red |
| M7 | snapshot read goes back to the request tenant | tenancy snapshot fact | red |
| M8 | snapshot read loses its organization predicate | tenancy snapshot fact | red |
| M9 | no snapshot frozen onto the run | TC-003 + delete fact | red |
| M10 | run page hides the frozen environment | TC_001 | red |
| M11 | the count cap disappears | caps fact | red |
| M12 | the list becomes Owner-only (copying the Secrets guard) | Member fact | red |

12/12 (M2 and M7 were re-targeted once: the first M2 attempt matched a value that appeared in the
worker's own log lines, which is why TC-002a now compares the child's stdout against distinctive values;
the first M7 attempt used the webhook path, which does set a tenant, so the no-request-tenant tenancy
fact is the mutation's target).

### 9. Gate (one line per command)

| Command | Result |
|---|---|
| `dotnet build -c Release` | **PASS** - `Build succeeded`, 0 warnings, 0 errors (warnings are errors) |
| fast suite (sextuple filter, unchanged) | count **1341 → 1368** discovered (`--list-tests`, before/after; the review fix added two facts: the case-insensitivity fact and the secret-boundary fact). Full-run status: **not green on this machine at HEAD either** - see the environment note below. The 184 tests of every class this card touches passed in one run before the review fix: `Total: 184, Failed: 0, Skipped: 0, Time: 158s` |
| review-fix classes (one filtered run each) | **PASS** - `OrganizationEnvironmentTests` 20/20, `RunEnvironmentTests` 5/5, `StepConditionTests` 7/7, `ProjectParametersTests` 20/20, `WorkerAotGuardTests` 4/4, and the F-022 inline fact (`An_inline_definition_carries_its_triggers_pipeline_env_and_secret_references`) 1/1 |
| `Category=Postgres` | **PASS** - `Total: 6, Failed: 0, Skipped: 0, Time: 10.4s` against a disposable PostgreSQL **16.15** (`initdb --locale=C.UTF-8`, random loopback port, `pg_ctl`; every fact drops and recreates `public`, so the whole migration chain - including this card's new table and column - runs on a real PostgreSQL) |
| mutation | **12/12** - every new fact proven red under one mutation, files restored and SHA-256 verified (`artifacts/f-034/mutations.log`) |

**Environment note (important for reading the "fast" line).** This machine cannot run the whole fast
suite green even at HEAD: a baseline run of the unchanged tree (`artifacts/f-034/baseline-fast.log`,
`git stash`ed working tree) hits five-minute host-creation timeouts in classes this card never touches
(`GitProbeTests`, `ProjectCrudTests`, `TenantIsolation*ProbeTests`). With this card applied the suite
shows the same class of environment timeouts (a git-timeout timing fact, a six-minute concurrency
harness) and **no functional failure in any surface this card changes**. Every class this card touches
passes together (184 tests, above), and each new fact passes in isolation. Nothing here pretends the
whole suite is green on this machine; the claim is that the change is not what is red.

### 10. Doc discrepancy (resolved)

`docs/lua-runtime.md` §6.0.1 used to describe the pre-F-034 chain ("… step `env` < the
server-provided run environment", "build parameters join this same server-provided layer"). It now
states the documented chain (inherited < `ZENKINS_*` < organization < parameters < pipeline < step),
says the metadata edge is protected by refusal/filtering rather than by ordering, and keeps the
organization and parameter snapshots separate - so no follow-up is outstanding.

### 11. Known limitations (recorded, deliberately not fixed)

- **The count cap is a `COUNT(*)` pre-check, not a constraint.** Two concurrent creates can both read
  99 and both insert, so an organization can briefly hold more than 100 variables. AC-7 asks for a cap
  and a clear error, which is what the pre-check gives (a single user's 101st save is refused, and the
  message names the cap and the way out); closing the window needs a constraint or a serializable
  transaction. F-029's precedent records the same shape - a check-then-act cap with a `Category=Postgres`
  race fact - and this card deliberately does not claim more than it does.
- **The value cap counts UTF-16 code units; the column stores `varchar(4096)` characters.**
  `OrganizationEnvironmentValidation.MaxValueLength` measures `string.Length`, the column counts code
  points. `Length >= code-point count` always holds, so a value that passes validation can never be
  truncated or refused by the column: the check is at least as strict as storage. A value rejected at
  4096 code units of astral-plane text is a stricter answer than a data-loss one, which is the safe
  direction.

### 12. Review fixes (terminal review, 2026-11-17)

Five findings, all applied; nothing else in the card's substance changed.

1. **Reserved-name rule was written twice, with two comparers (Major).** The server's predicate compared
   `OrdinalIgnoreCase`, the worker's own copy `Ordinal`. On Windows (case-insensitive environment block)
   a step `env`'s `zenkins_run_id` was therefore not recognised as reserved, survived `Compose`, and
   overwrote the run's own `ZENKINS_RUN_ID` when the child was started - a spoof this card exists to
   close. Fix: the rule and its prefix list moved to
   `src/Shared/Pipelines/ServerEnvironmentNames.cs` (the one implementation both sides use);
   `RunEnvironment.IsReserved` delegates to it and `RunEnvironment.ReservedPrefix` was deleted rather
   than kept as a second copy of the literal. New fact
   `A_lower_case_reserved_name_is_dropped_too_because_a_windows_environment_is_case_insensitive`
   (WorkerRuntime): a lower-case reserved name from the pipeline env and a mixed-case one from the step
   env are both dropped, both are named in the diagnostics, and no second spelling of either name is in
   the composed map. Proven red before the fix (`Expected: 2, Actual: 0` - nothing was dropped) and
   green after.
2. **TC ids and test names disagreed.** `TC-002a`/`TC-002b` now carry the precedence facts (formerly
   `C1a`/`C1b`) and `TC_003_the_layer_is_frozen_…` carries TC-003 (formerly named `TC_002…`, which left
   TC-002 unused and TC-003 dangling). The card's own references were updated with them.
3. **The secret boundary was asserted nowhere.** New fact
   `A_variable_that_shares_a_secret_s_name_stays_configuration_and_the_secret_stays_masked`: with one
   name in both storages, `secret(name)` yields the secret's value (the child's line is `***`, and the
   value reaches no log row, page or download), the environment reader yields the organization's value
   in plaintext (log and run page included), the claim's resolved map carries the configuration value,
   the composed environment contains no secret value, and the Secrets page still shows the name only.
4. **The doc-discrepancy note was stale.** §10 is resolved (see above).
5. **The dropped-name diagnostic carried two prefixes.** `CollisionDiagnostic` no longer starts with
   `zenkins: `; `PipelineRunner` already prefixes the line with its own location, so the run log reads
   `zenkins: step 1/2 "X": the step env sets 'ZENKINS_RUN_ID', …` once. `TC-002b` now pins the rendered
   line's shape and asserts the `zenkins: ` prefix appears exactly once.

- **The composed environment is an `Ordinal` dictionary; a name-collision on Windows is decided by
  enumeration order.** `StepEnvironment.Compose` keeps names Ordinal, so if the *worker host's own*
  process environment happened to define `zenkins_run_id`, the composed map would hold both spellings
  and `ProcessStartInfo.Environment` — a case-insensitive dictionary on Windows — would let whichever is
  written last win. Every declared layer (organization, parameters, pipeline `env`, step `env`) now
  refuses or drops a reserved name regardless of case, so the only remaining source is the operating
  system's own environment on the build machine — the operator's own configuration, not a tenant's.
  Making the map itself `OrdinalIgnoreCase` would collapse two differently-cased Lua keys into one and
  change merge semantics for every layer, which is a larger question than this card; recorded here
  rather than fixed.

## Close-out verification (2026-11-17)

Two review rounds. The card's own layering change turned out to be a **reorder of the base**, not an
insertion: the shipped order was `inherited < pipeline < step < (parameters ∪ ZENKINS_*)`, and the
acceptance criteria ask for `ZENKINS_* < organization < parameters < pipeline < step`. The review found
that and said so before any implementation was read, which is why the work was rebuilt rather than
patched.

- **The metadata guarantee moved from ordering to refusal.** `ZENKINS_*` was protected only by being
  overwritten last; under the new order a parameter or a step `env` naming it would have won. It is now
  refused at save for organization variables and for parameters, and dropped with a logged diagnostic for
  pipeline and step `env` — the two existing facts (`A_parameter_or_organization_variable_cannot_overwrite_the_build_metadata`
  and `StepConditionTests`' `RunEnvironmentWins`) still assert the same thing, but they are now true by
  filtering rather than by order.
- **The one major defect was Windows-only, and the Windows host is what found it.** The reserved-name
  predicate existed twice: the server's compared `OrdinalIgnoreCase`, the worker's `Ordinal`. A Lua
  `env = { zenkins_run_id = "x" }` therefore survived `Compose` and, on Windows, overwrote the real
  `ZENKINS_RUN_ID` because `ProcessStartInfo.Environment` is case-insensitive there. The rule now lives
  once in `Shared/Pipelines/ServerEnvironmentNames.cs`, the worker's copy is a one-line delegation with
  its constant deleted, and a `Compose` fact with a lower-case reserved name pins it — that fact failed
  1/1 before the predicate changed (expected 2 dropped, actual 0) and passed after.
- **Nothing was taken on trust.** The reviewer re-applied thirteen mutations of its own (13 red, 0
  vacuous) and closed the major defect in the *revert* direction — restoring the worker's `Ordinal`
  comparison reddens the new fact. It also split the headless question into three mutations to show
  which half of the snapshot fact actually carries it: M11/M11b die on the foreign-tenant half, and only
  M11c (silently returning an empty layer when there is no request tenant) reaches the headless
  assertion. Both properties hold; that one fact has a single failure point is recorded as a
  nice-to-have, not a gap.
- **The secret boundary is now proven rather than argued.** One name is simultaneously organization
  configuration and a secret with a different value; the configuration step prints it, the secret step
  prints `***`, and the secret's plaintext appears in no log line, page or log download — which is what
  distinguishes "two stores" from "one store that happens to look like two".
- **The full suite could not run here.** 1368 tests in one process, each building a Blazor host, turn
  `WebApplicationFactory`'s five-minute host-build timeout into spurious failures on this 8 GB machine
  (post-reboot the same class that had timed out passes in 6 s). It ran on a 20-core/64 GB Windows host:
  **1368 total, 1362 passed, 3 failed, 3 skipped, 9m47s**, and the three failures are exactly main's own
  Windows baseline (`GitLfsTests` ×2 against this machine's git-lfs, and `ProcessRunnerTests`' process-
  handle race) — set difference against main is empty. The three skips are the POSIX-only facts.
- **The Windows run also caught a defect in F-022**, not in this card: that card's inline-definition fact
  hand-wrote `run = { "echo", "hi" }`, and `echo` is not an executable on Windows, so it ended
  `step-failed`. Fixed separately in `b31f84e` by using the file's existing `PipelineTestData.Step`
  helper; it is green on both platforms now.
- **`Category=Postgres` ran here on the final tree: 6/6, 0 skipped, 7.9 s** against a disposable PG 16
  instance.
- Recorded rather than fixed, in §11: the count cap is a `COUNT(*)` pre-check; the value cap counts
  UTF-16 code units against a `varchar(4096)` column; and the composed map is `Ordinal`, so a
  case-collision coming from the worker host's own environment on Windows would be decided by
  enumeration order.
