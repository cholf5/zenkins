---
id: D-003
title: Project detail as tabs, with git credentials managed in settings
type: feature
priority: P1
size: L
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

A project page that scales past one card stack: overview, pipelines, runs and settings as real routes,
with the settings tab hosting everything that configures the project — including git credentials, which
today have endpoints (F-013) and no way to create them from the browser at all.

## Background

Roadmap v0.9 ("Project detail: overview / pipelines / runs / settings tabs"). `ProjectDetail.razor` is
now 385 lines holding repository, start-run and webhook cards on one page; `ProjectRuns.razor` already
renders a run list on its own route. F-013 recorded, on its own card, that `GitCredential` has form-POST
endpoints but no page, so a project pointing at a private repository cannot be made to work through the
UI — that gap is the reason this card is P1 rather than cosmetic.

Decisions:

- **Tabs are routes, not client state.** `/projects/{id}`, `/projects/{id}/pipelines`,
  `/projects/{id}/runs`, `/projects/{id}/settings`. A tab must be linkable, survive reload, and work
  with back/forward; a tab rendered by toggling a field is none of those.
- **The existing webhook UI moves, its endpoints do not change.** Behaviour F-017 pinned — show-once
  plaintext, enable/disable, delete, last outcome — is preserved and re-pinned from the new location,
  so this card cannot regress F-017 by moving the markup.
- **Git credentials are managed like secrets**: Owner-only writes (F-013's rule), write-only (no
  reveal, no value in any response), same name rules, rotate replaces the material. The page states the
  asymmetry rather than apologising for it.
- **Pipelines tab is read-only in this card.** It shows the pipeline path from settings and, when a run
  exists, the frozen spec of the newest run with its step names — which is the answer to "what does
  this project actually run". Editing pipeline content belongs to the repository, and a pipeline editor
  is not in v0.9.
- **No new server surface beyond git credentials' list**: the tabs read what the existing services
  already expose; the one new endpoint is listing a project's credentials (never their values).
- **Members get read-only settings.** Every write path must keep F-003/F-012/F-013/F-017's
  Owner/Member rules; moving markup must not move a permission check into the page.

## Acceptance Criteria

- [ ] The four routes exist, each renders its own content, each deep link survives a reload, and an
      unknown project id is a 404 on all four.
- [ ] Overview shows repository URL, default branch, pipeline path, the newest run with its status and
      a link to it, and the webhook/credential counts.
- [ ] Pipelines shows the pipeline path and, when a run exists, the frozen spec of the newest run:
      step names in order, branch, commit, and the spec's validated shape.
- [ ] Runs tab reuses the existing run list behaviour (ordering, paging or cap, links) unchanged.
- [ ] Settings: project basics editing still works as before; git credentials can be created,
      rotated and deleted; a created credential is never readable back (list shows name, kind and
      timestamps only); the same actions on another organization's project are 404.
- [ ] The webhook section keeps every F-017 behaviour from its new location, including show-once
      plaintext on create and rotate and no plaintext in any later response or page.
- [ ] A Member sees every tab but no write affordance that the server would refuse; a forged POST as a
      Member is still 403 (asserted, not assumed from hiding the button).
- [ ] Deleting a project still requires the existing confirmation route and still cascades as before.

## Subtasks

- [ ] Routes and a shared tab header component; move the existing cards into their tabs without
      changing behaviour.
- [ ] Git credentials: list endpoint (name/kind/timestamps only), create/rotate/delete form flow,
      Owner-only, in the settings tab.
- [ ] Pipelines tab: read the newest run's frozen spec through the existing run view.
- [ ] Tests (`Category=ProjectUi`): routing matrix, deep links, 404s, permissions per tab, credential
      lifecycle without reveal, webhook show-once after the move, project delete unchanged.

## Dependencies

- F-003 (project CRUD), F-012 (protector + write-only pattern), F-013 (git credential endpoints),
  F-014 (frozen spec), F-017 (webhook management UI)

## Test Cases

### TC-001: Every tab is a page

Steps:
1. Request each of the four routes for an existing project; then reload each; then request each for a
   random project id.

Expected:
- Each renders its own content with no cross-tab bleed; reloads keep the tab; the random id is 404 on
  all four.

### TC-002: A private repository becomes drivable from the browser

Steps:
1. As an Owner, create a git credential of kind HTTPS token in settings; point the project at a
   private fixture repository; trigger a run; rotate the credential; trigger another run.
2. As a Member, attempt to create and to rotate.

Expected:
- Both runs are created; after rotation the second run still authenticates; the list shows name, kind
  and timestamps and never the material; the Member's writes are 403 and change nothing.

### TC-003: Moving the webhook UI did not change it

Steps:
1. From the settings tab, create a webhook, copy the shown plaintext, then reload the tab and open the
   project page; replay F-017's show-once assertions.

Expected:
- Plaintext appears exactly once at creation, never again; enable/disable and delete behave as
  F-017's tests describe.

## Development Log

### 2026-11-13

- Planned from roadmap v0.9. The git-credential page is the reason this is scheduled before the
  remaining UI cards: without it the product's own private-repository path is unreachable from the
  browser, which contradicts v0.9's goal.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Components/Pages/Projects/*`,
  `src/Server/Components/Shared/*` (tab header), `src/Server/Git/GitCredentialEndpoints.cs`,
  `src/Server/Projects/*`, `tests/Zenkins.Tests/ProjectUi/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=ProjectUi"`;
  full suite; PostgreSQL contract gate not required unless the model changes.
- Results: not run (card not started).
