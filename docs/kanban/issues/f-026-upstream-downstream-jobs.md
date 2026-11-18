---
id: F-026
title: Upstream and downstream jobs — run a job after another one succeeds, with loop protection
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-19
blocked: none
---

## Goal

A project can declare upstream triggers ("run me after X succeeds") and downstream
targets ("run Y after I succeed"), so a build → test → deploy chain runs in order
without an external orchestrator, and a loop can never spin forever.

## Background

There is no cross-project trigger today. The only way to chain jobs is a step that
calls the Server's own HTTP API — which does not exist either (F-027) — and even
then the chain is invisible in the UI.

Jenkins does this with "Build after other projects are built" plus downstream
post-build actions, and its failure modes are well known: a chain that loops, a
chain that silently stops when the upstream is disabled, and a trigger lost when
the master dies between "build finished" and "queue downstream". This card has to
answer all three.

## Acceptance Criteria

- [x] A project can declare N upstreams (project + "only on success" flag) and M
      downstreams; both are visible on the overview and in the run's "triggered
      by" / "triggered" sections.
      **Delivered**: the data (`ProjectLink` + `ProjectLinkService` save/list/delete, with the cycle check) and
      the run page's chain section ("Triggered by #N" / "Triggered #M ...", each downstream with its outcome and
      reason); the declaring UI - an **Upstream and downstream** card on the settings tab that lists both
      directions, adds an edge (project, direction, "only on success", branch policy) and removes one through
      `POST /projects/{id}/links` and `POST /projects/{id}/links/{linkId}/delete`; and the overview's **Chain**
      summary, which names both ends of this project's place in the chain before a run is started.
- [ ] When an upstream run reaches a terminal state, a downstream run is queued if
      the rule allows it; the downstream run records `TriggerType = Upstream` and
      the triggering run id, and its page links to it.
      **Delivered**: the run records `TriggerType = Upstream`, `TriggeredByRunId` and `UpstreamHops`, and
      the run page links it to the run that caused it.
- [x] Branch policy per link: `sameBranch` (default, meaningful with F-023) or
      `defaultBranch` (the downstream builds its default branch); a branch the
      downstream cannot build is recorded as `skipped-branch`, not a failure.
- [x] Loop protection at two levels: a cycle is refused at save time (graph
      check — and that search does not read branch policies, so `A→B→A` is
      refused whatever branches the two edges build), and a runtime hop counter
      (`MaxUpstreamHops`) bounds how deep a chain may run: it stops an acyclic
      chain that is legal edge by edge yet too long, with a recorded reason on
      the run that stopped, and it is the second brake for an edge written past
      the save-time check.
- [x] The trigger is delivered exactly once even if the Server dies right after the
      upstream completes: an outbox row is written in the same transaction as the
      terminal status and dispatched after commit, retried until delivered.
- [x] A disabled project (F-028) is skipped with a recorded reason; a missing
      credential or an invalid downstream is not retried forever — it is recorded
      as a failed trigger, visible on the upstream run page.
- [x] Downstream parameter values are the definitions' defaults, and the run's
      environment carries the upstream facts (`UPSTREAM_PROJECT`, `UPSTREAM_RUN_ID`,
      `UPSTREAM_STATUS`, `UPSTREAM_BRANCH`, `UPSTREAM_COMMIT`).
      **Delivered**: a downstream run's parameters resolve through the same resolver a schedule uses, the
      five `UPSTREAM_*` facts are frozen onto the run in their **own column** (`UpstreamFactsJson` - not the
      organization layer, not the build parameters, because the page would describe them wrongly in either)
      and read by its steps through the claim, and the run page lists them in their own block inside the
      chain section.
- [x] Deleting a project removes its links in both directions (no dangling
      triggers), and the deletion is confirmed with the affected jobs listed.
      **Delivered**: both links vanish with either project, a pending obligation whose link is gone settles
      as `skipped-missing`, and the delete confirmation names the jobs the chain would lose - the upstreams
      that would stop triggering this project and the downstreams this project would stop triggering - or
      says plainly that the project is in no chain.

## Subtasks

- [x] `ProjectLink` entity (upstream, downstream, onlyOnSuccess, branchPolicy),
      unique per pair, tenant-owned.
- [x] `OutboxMessage` entity plus a dispatcher background service — the shared
      mechanism F-039 also needs (introduce it here, reuse it there).
- [x] Graph validation at save: DFS over the organization's links, refusing cycles
      with the path that would close it.
- [x] Terminal-state hook in the run completion path: write outbox rows for every
      matching downstream, inside the same transaction.
- [x] Dispatch: resolve the target, apply the branch policy, re-validate the
      project (enabled, kind, credential), queue through `RunService.CreateRunAsync`.
- [x] UI: an Upstream/Downstream card on project settings; the run page's chain
      section; the "triggered by #N" and "triggered #M" links.
- [x] PRD §3.14 (the card said §3.13, which the PRD already uses for scheduled
      triggers): the chain contract, the loop rules and the delivery guarantee.

## Dependencies

- F-036 (numbers) for readable chain links (a guid reference works in the interim)

## Test Cases

### TC-001: A successful upstream queues its downstream once

Steps:
1. Link project A → B with `onlyOnSuccess`.
2. Run A to success; let the dispatcher tick twice.

Expected:
- Exactly one B run exists, `TriggerType = Upstream`, referencing A's run.
- A second tick adds nothing (the outbox row is consumed).

### TC-002: A failed upstream with onlyOnSuccess queues nothing, and says why

Steps:
1. Same link; run A to failure.

Expected:
- No B run; A's page records "downstream B not triggered: upstream failed".

### TC-003: A cycle is refused at save, and a runtime loop stops

Steps:
1. Link A → B, then try to link B → A.
2. Separately, seed a chain of 25 projects linked A→B→…→Y and run A.

Expected:
- Step 1 is refused with the closing path in the error.
- Step 2 stops at the hop cap with a recorded reason; no project runs twice in the
  same chain.

## Notes and Decisions

- Rejected: triggering downstreams by shelling out to the API from a step. It
  cannot be transactional, it hides the chain from the UI, and it needs F-027 to
  exist before the feature does.
- The outbox is deliberately a table and not an in-memory queue: the guarantee is
  "a completed upstream does not lose its downstream".
- Superseded wording: this card, the settings page's description and the code comments used to say a chain
  could "cycle only through branch policies without the graph ever containing a cycle", with the hop counter
  as the answer. That is not true - the save-time search ignores branch policies and refuses that edge - so
  all three now say what does hold: the hop limit bounds depth, and it is the second brake for an edge
  written past the save-time check. The message of the `feat(F-026)` commit on `main` still carries the old
  sentence; it is history and is left as written.

## Delivered / remaining

**Delivered (server side), 8 facts, each proved live by a mutation:**

| Fact | What it pins | Mutation that turns it red |
|---|---|---|
| `A_committed_obligation_is_dispatched_after_the_fact_and_only_once` | a row staged the way the terminal hook stages it is dispatched by a later pass, and the second pass adds nothing | `SettleAsync` leaving the row `pending`: the second pass queues a second run (`Expected 0 / Actual 1`) |
| `An_only_on_success_link_records_why_a_failed_upstream_triggered_nothing` | the rule not holding still leaves a settled row with `skipped-upstream-failed` | the skip branch disabled: the row stays `pending` (`Expected "settled" / Actual "pending"`) |
| `A_link_without_only_on_success_triggers_on_a_failed_upstream` | `OnlyOnSuccess = false` really triggers on a failure | the skip made unconditional: `Expected "pending" / Actual "settled"` |
| `A_branch_policy_the_downstream_cannot_build_is_a_skip_not_a_failure` | `sameBranch` on a branchless upstream skips; `defaultBranch` queues the downstream's default | the branch check disabled: `Expected "skipped-branch" / Actual "queued"` |
| `A_cycle_is_refused_at_save_with_the_path_that_would_close_it` | A->B->C then C->A is refused, with the path in the message; a self-link is refused | the graph walk disabled: `Expected InvalidInput / Actual Success` |
| `A_chain_at_the_hop_limit_stops_with_its_own_reason` | the runtime counter bounds depth: a chain that is legal edge by edge still stops at the limit | the cap loosened by 100: `Expected "skipped-hop-limit" / Actual "queued"` |
| `A_disabled_downstream_is_skipped_with_its_reason_and_not_retried` | a disabled project is skipped (not failed, not retried) | the disabled check removed: the queue gate answers instead, `Expected 1 / Actual 0` settled rows |
| `A_downstream_run_carries_the_upstream_facts_and_its_definitions_defaults` | the downstream's parameters are the definitions' defaults and its claim carries the five `UPSTREAM_*` facts | dropping the facts from the claim merge (`UPSTREAM_PROJECT` assertion) and passing no parameters (`GREETING` assertion) - two mutations, one per half |
| `The_run_page_shows_the_chain_on_both_ends` | the downstream page names and links its upstream; the upstream page lists every downstream with its outcome and reason (including a skipped one) | dropping the chain links from `RunView` |
| `Deleting_a_project_removes_its_links_in_both_directions` | both edges vanish with the project | the downstream foreign key `Cascade` -> `Restrict` (with the migration regenerated): `SQLite Error 19: 'FOREIGN KEY constraint failed'` on the delete |
| `The_project_settings_card_declares_and_removes_a_chain_edge` | the settings card lists both directions, the save endpoint declares the edge the form posts, and the remove endpoint deletes exactly that edge | the downstream list not rendered: `data-chain-downstream-id` is not on the page |
| `The_project_overview_summarises_the_chain_on_both_ends` | a middle project's overview names its upstream and its downstream; a project in no chain renders no summary | the overview card removed: `project-chain-summary` is not on the page |
| `The_delete_confirmation_lists_the_jobs_the_chain_would_lose` | the confirmation lists the upstream that stops triggering it and the downstream it stops triggering, and says "none" for a project in no chain | the affected list not rendered: `delete-chain-affected` is not on the page |
| `Link_endpoints_cannot_reach_another_tenants_project` | a foreign project in the route, in the body's target or as the link id is one bare 404, no row is written in either tenant, and the settings page renders no foreign edge - while the same requests succeed for the caller's own organization | the `NotFound` answer mapped to the success redirect: `Expected NotFound / Actual Found`; the project lookup without the tenant filter answers 500 (the write guard) instead of 404 |
| `A_cross_tenant_project_link_write_is_rejected` / `A_cross_tenant_outbox_write_is_rejected` | a row that names another organization is refused by the data layer, and neither tenant's count changes | the tenant-mismatch throw removed from `ZenkinsDbContext`: `Assert.Throws() Failure: No exception was thrown` |

Schema: `20261118134029_AddUpstreamDownstream` and `20261118135424_AddRunUpstreamFacts`, generated with `Database__Provider=Postgres`: the
`ProjectLinks` and `OutboxMessages` tables plus `BuildRuns.TriggeredByRunId` / `UpstreamHops`.
`ModelSnapshotTests` is green. Q-006's surface inventory carries the two new write endpoints (each row names
`Link_endpoints_cannot_reach_another_tenants_project`), and `ProjectLink`/`OutboxMessage` are registered in the
harness's `CoveredEntityTypes` with the generic cross-tenant write probe, so the reflection guard is satisfied
by a probe that really runs.

**Nothing in this card's acceptance criteria is open.** The two surfaces the earlier hand-off left are
delivered: declaring an edge (settings tab, two endpoints, Q-006 rows) and the delete confirmation's list of
affected jobs. The overview intentionally carries a *summary* (both ends, by name) and no form - the lists and
the declaring form have exactly one home, the settings tab, so there is one place where a link is created and
one place where it is removed.


## Close-out verification (2026-11-18)

Two developers, a handover at three-fifths, and a pre-existing red found on the branch rather than in the
batch run at the end.

- **The trigger is written in the terminal transaction, and dispatched after it.** An outbox row is staged
  inside the same `SaveChanges` that records the upstream's terminal status, the dispatcher claims it,
  dispatches, and only then settles it - so a Server that dies between commit and dispatch loses nothing,
  and a restart finishes the work. The facts pin the three properties separately: a committed row is
  dispatched afterwards, an unsettled row is dispatched again on the next pass (which is what makes the
  restart case real), and settling before dispatch would queue twice.
- **Loop protection is two mechanisms, and only the first one is about cycles.** A cycle is refused when the
  link is saved, naming the path that would close it - and that search does not read branch policies, so two
  projects pointing at each other with `defaultBranch` is the graph cycle and is refused like any other. The
  hop counter bounds how deep a chain may go: it stops a chain that is legal edge by edge and still too long,
  with its own recorded reason, and it is the only stop left for an edge that reached the table without
  passing the save-time check. Each has a mutation that reddens only its own fact.
- **Branch policy is a policy, not a failure.** `sameBranch` carries the upstream branch, `defaultBranch`
  builds the downstream's own; a branch the downstream cannot build is `skipped-branch` and the upstream
  run stays successful. Pinned by a mutation that turns the skip into a queue.
- **Skips are recorded, and one of them got better while being tested.** A disabled downstream records
  `skipped-disabled` and is not retried; a missing credential, an invalid target, the hop limit and a
  deleted project or run are permanent refusals settled on the first pass, while queue refusals retry with
  backoff and settle as `failed-trigger` after five attempts. The disabled reason now quotes the operator's
  own note (`X is disabled: maintenance window`) because a test asked what an operator would want to read.
- **The chain is visible from both ends.** Downstream runs record `TriggerType = Upstream` and the
  triggering run id and link back; the upstream run page lists each downstream with its outcome and
  reason, including the operator's sentence. `UPSTREAM_PROJECT`, `_RUN_ID`, `_STATUS`, `_BRANCH` and
  `_COMMIT` live in their own column and their own page section rather than in the environment or the
  build parameters, where they would have claimed a layer they do not belong to.
- **Declaring a link is a form and two endpoints, and both are gated.** `POST /projects/{id}/links` and
  `.../{linkId}/delete` refuse a foreign project as a body-less 404, a cross-project link id as the same
  404, and a cycle as a PRG refusal that quotes the service's own sentence; both are registered in Q-006
  with a probe name that resolves to a real test, and the delete confirmation now lists the jobs the chain
  would lose.
- **The branch was red before review, and it was the tenant gate.** F-026's `ProjectLink` and
  `OutboxMessage` were missing from `TenantIsolationProbes.CoveredEntityTypes`, so the harness no longer
  covered every tenant-owned entity - the same gate that caught F-025's two new tables. Registering them
  brought two real cross-tenant probes with it (a write from the wrong tenant throws), and
  `Category=TenantIsolation` went from 164/165 to 166/166.
- **Counts.** fast 1510 -> 1526 measured on the merged tree, Postgres stays 9, the slow categories 24
  including `Category=Load`, so everything is 1559. The single batch run at the end of the series is where
  the full suite and Postgres are verified.
