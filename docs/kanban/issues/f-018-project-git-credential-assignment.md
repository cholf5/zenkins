---
id: F-018
title: Project git credential assignment, and a real clone served by the server
type: feature
priority: P1
size: M
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

An Owner can attach an organization git credential to a project in the UI, the Server hands that
credential to the Worker for that project's runs, and a private repository clones **from that
credential** rather than from whatever key happens to sit on the worker host — with a missing or
rejected credential visible instead of silent.

## Background

Q-007 verified the cross-platform story on real machines and found, along the way, that the
credential path never participates in it. F-013 built the storage and the delivery endpoint, but
`Project.GitCredentialId` has **no writer anywhere in the product**:

- `GitCredentialService.CreateAsync` takes no project and only inserts an organization-level row; the
  `ProjectId` the settings form posts is used solely to build the post-redirect
  (`GitCredentialEndpoints` passes `form?.ProjectId` to `SuccessOutcome`).
- `ProjectInput` carries name/repository URL/branch/pipeline path only, so the create/edit endpoints
  cannot accept a credential even if a form offered one, and no project page has an attach control.
- `FetchMaterialForRunAsync` returns `NotFound` unless `project.GitCredentialId` is set — reading a
  column nothing writes.

D-003 recorded this as a deliberate v0.9 scope cut ("the assignment has no UI … so the tests seed it"),
which is why the tests have always seeded the column and why nothing failed. Q-007 made the cost
visible: **all six runs in that card cloned with the worker host's ambient key**, and the server logged
`Git credential request resolved no material` for every one of them.

Decisions:

- **Wire the existing pieces, don't rebuild them.** The credential row, encryption, the worker-side
  fetch endpoint and the client already exist and are tested; what is missing is the assignment.
- **Owner-only, tenant-safe, like every other credential operation.** The attach/detach surface follows
  F-012's authorization and Q-001's isolation rules, and the new queries go through the tenant filter.
- **A missing credential is not silent.** Today a project with no credential degrades to ambient keys
  with no operator-visible signal. After this card, the run record (or the run page) must say that no
  credential was used because none is assigned, and a rejected credential must surface as a failure
  with a reason, not as an opaque `git-auth-rejected`.
- **Prove it with a credential, not with ambient keys.** The acceptance evidence is a private
  repository cloned on a machine that has **no** ambient key for it, using only what the server
  delivered.

## Acceptance Criteria

- [ ] An Owner can attach one of the organization's git credentials to a project and detach it again,
      from the UI, with the change auditable on the page (which credential, by whom, when).
- [ ] The assignment is persisted (`Project.GitCredentialId` written), tenant-scoped, Owner-only, and
      survives a restart; a credential belonging to another organization, or a credential id that does
      not exist, is refused.
- [ ] A run of that project fetches its credential from the server and the Worker clones the private
      repository **without any ambient key or SSH agent on the worker host** — proven by a real build
      on a machine whose `~/.ssh` holds nothing for that host, recorded in the card.
- [ ] A project with no credential assigned says so on the run (visible to the owner) instead of
      silently falling back to ambient keys; a credential the remote rejects fails the run with a
      reason that names the rejection rather than a bare `git-auth-rejected`.
- [ ] Deleting a credential (F-012 semantics) that is still assigned is refused or confirmed with the
      projects that use it, the same way D-006 handles referenced secrets.
- [ ] Owner-only and cross-tenant probes for the new surface are permanent tests, and the existing
      secrets/credential suites stay green.

## Subtasks

- [ ] Write path: endpoint or form field to attach/detach a credential on a project (Owner-only,
      validated against the organization's credentials).
- [ ] Project edit / settings page control, showing the current assignment and its provenance.
- [ ] Run-side messaging: "no credential assigned" and credential-rejection reasons reach the run page
      and the log without leaking material.
- [ ] Delete-and-referenced flow for credentials, mirroring D-006's reference awareness.
- [ ] Real proof: private repository, keyless worker host, real build.

## Dependencies

- F-012 (storage and encryption), F-013 (delivery endpoint and worker client), D-003 (project tabs and
  the settings surface this extends), D-006 (the reference-awareness pattern to mirror), Q-007 (the
  evidence that the path is unused today).

## Test Cases

### TC-001: Attach, run, clone — without an ambient key

Steps:
1. Create a project pointing at a private repository; on a worker host whose `~/.ssh` has no key for
   that host, start a Worker.
2. Attach the organization's credential to the project in the UI; trigger a run.

Expected:
- The clone succeeds from the delivered credential; the worker host's ambient keys are provably absent
  and unused; the run log shows the credential source without revealing material.

### TC-002: No credential is visible, not silent

Steps:
1. Trigger a run of a project with no credential assigned against a private repository.

Expected:
- The run fails (or is refused) with a reason naming the missing assignment, and the run page states
   that no credential is assigned — not an opaque clone failure, and not a silent ambient-key success.

### TC-003: Boundary cases

Steps:
1. Attempt to attach another organization's credential; delete an assigned credential; detach and
   re-attach.

Expected:
- Cross-tenant attach is refused without leaking existence; the delete warns with the referencing
  projects; detach and re-attach behave idempotently.

## Development Log

### 2026-11-14

- Opened from Q-007's finding. Deliberately not fixed inside that verification card: the work is a
  feature (UI surface + write path + run-side messaging), and mixing it into an acceptance card would
  have made both impossible to judge independently.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Git/*` (`GitCredentialService`, endpoints), `src/Server/Data/Entities/Project.cs`,
  `src/Server/Components/Pages/Projects/*` (settings/detail), `src/Worker/Git/*` (client and material
  handling), `tests/Zenkins.Tests/Git/*`, `tests/Zenkins.Tests/Secrets/*`,
  `docs/kanban/issues/d-003-project-tabs-git-credentials.md`.
- How to run/verify: `dotnet build -c Release`; `--filter "Category=Git"`; `--filter "Category=Secrets"`;
  `--filter "Category=TenantIsolation"`; the fast suite; plus the real-clone proof on a keyless worker
  host (TC-001), recorded in this card.
- Results: not run (card not started).
