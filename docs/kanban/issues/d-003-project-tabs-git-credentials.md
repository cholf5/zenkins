---
id: D-003
title: Project detail as tabs, with git credentials managed in settings
type: feature
priority: P1
size: L
status: done
created: 2026-11-13
updated: 2026-11-14
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

- [x] The four routes exist, each renders its own content, each deep link survives a reload, and an
      unknown project id is a 404 on all four.
- [x] Overview shows repository URL, default branch, pipeline path, the newest run with its status and
      a link to it, and the webhook/credential counts.
- [x] Pipelines shows the pipeline path and, when a run exists, the frozen spec of the newest run:
      step names in order, branch, commit, and the spec's validated shape.
- [x] Runs tab reuses the existing run list behaviour (ordering, paging or cap, links) unchanged.
- [x] Settings: project basics editing still works as before; git credentials can be created,
      rotated and deleted; a created credential is never readable back (list shows name, kind and
      timestamps only); the same actions on another organization's project are 404.
- [x] The webhook section keeps every F-017 behaviour from its new location, including show-once
      plaintext on create and rotate and no plaintext in any later response or page.
- [x] A Member sees every tab but no write affordance that the server would refuse; a forged POST as a
      Member is still 403 (asserted, not assumed from hiding the button).
- [x] Deleting a project still requires the existing confirmation route and still cascades as before.

## Subtasks

- [x] Routes and a shared tab header component; move the existing cards into their tabs without
      changing behaviour.
- [x] Git credentials: list endpoint (name/kind/timestamps only), create/rotate/delete form flow,
      Owner-only, in the settings tab.
- [x] Pipelines tab: read the newest run's frozen spec through the existing run view.
- [x] Tests (`Category=ProjectUi`): routing matrix, deep links, 404s, permissions per tab, credential
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

Result: PASS (`ProjectUiRoutingTests.Every_tab_is_a_page_reloads_keep_the_tab_and_unknown_ids_are_404`).
Each tab asserts its own content marker and the absence of the other three tabs' markers; the reload
is a second GET of the deep link; a random id is 404 on all four. Two further tests pin the rest of
the matrix: a foreign organization's project id is 404 on all four routes AND on the credential list
(`A_foreign_organizations_project_id_is_404_on_all_four_tabs`), and a Member gets 200 on every tab
(`A_member_sees_every_tab_but_their_forged_credential_writes_are_403`).

### TC-002: A private repository becomes drivable from the browser

Steps:
1. As an Owner, create a git credential of kind HTTPS token in settings; point the project at a
   private fixture repository; trigger a run; rotate the credential; trigger another run.
2. As a Member, attempt to create and to rotate.

Expected:
- Both runs are created; after rotation the second run still authenticates; the list shows name, kind
  and timestamps and never the material; the Member's writes are 403 and change nothing.

Result: PASS, at the fetch level (see note). `ProjectUiCredentialTests.After_a_rotation_the_next_runs_material_is_the_new_one`
creates the credential through the settings tab, assigns it to the project, triggers run 1 through the
real start-run form, and the assigned worker's `GET /api/workers/runs/{id}/git-credential` returns the
created material; the Owner rotates through the settings tab; run 2 is triggered, claimed, accepted
and fetches the rotated material (old value gone). A full private-repository clone E2E is not
repeated here because F-013 already pins the clone-with-token path (`GitCredentialSecrecyTests`, real
HTTP git server); this card pins the settings-tab lifecycle and the fetch each clone depends on.
`The_settings_tab_lifecycle_never_returns_a_value_and_member_writes_are_403` covers the list shape
(exactly the six metadata fields per item, no value raw or URL-encoded, page shows no value), the
PRG flow through the tab, and the Member's forged create/rotate/delete as 403 with the list
afterwards still showing one never-rotated credential.

### TC-003: Moving the webhook UI did not change it

Steps:
1. From the settings tab, create a webhook, copy the shown plaintext, then reload the tab and open the
   project page; replay F-017's show-once assertions.

Expected:
- Plaintext appears exactly once at creation, never again; enable/disable and delete behave as
  F-017's tests describe.

Result: PASS (`ProjectUiWebhookRepinTests`). Creating posts from the settings tab and answers the
uncacheable show-once document; the reload of the tab and the overview page carry the configuration
and never the secret, nor does any log line. `Rotate_enable_disable_and_delete_behave_as_F017_from_the_settings_tab`
replays rotation (new plaintext once, old secret 401, new accepted), the last-outcome display
(`ignored:branch`), disable → the same bodyless 404 as an unknown id with the PRG back to
`…/settings?notice=webhook-disabled`, enable restoring deliveries, and delete removing the webhook
and its delivery records. F-017's own suites (Webhooks category, 69 tests) were re-pinned to fetch
and post from the settings tab and stay green.

## Development Log

### 2026-11-13

- Planned from roadmap v0.9. The git-credential page is the reason this is scheduled before the
  remaining UI cards: without it the product's own private-repository path is unreachable from the
  browser, which contradicts v0.9's goal.

### 2026-11-14

- Split `ProjectDetail.razor` into four routed tabs. `ProjectTabs.razor` (Shared) is the one tab
  strip: links, never client state, `NavLinkMatch.All` so the Overview href (a prefix of every other
  tab) cannot light up on all four. Overview (`ProjectDetail.razor`) keeps the repository facts, the
  start-run form and the member-delete note, and adds the Latest-run card (D-002's `RunStatusBadge` +
  `RunDisplay.DurationOrElapsed`, newest first through the same `GetProjectRunsAsync` read the Runs
  tab uses) and the webhook/credential counts. `ProjectRuns.razor` gains the strip and loses only the
  redundant "Project" button (the Overview tab replaces it; the "Start run" button stays —
  `RunEndpointTests` pins it next to "No runs yet"). `ProjectPipelines.razor` is new and read-only.
  `ProjectSettings.razor` is new: project basics (the edit form keeps its own route, so editing works
  exactly as before), the webhook cards moved verbatim, the git-credential store, and an Owner-only
  danger zone to the delete-confirmation route.
- Git credentials: `GitCredentialService.ListForProjectAsync` + `GET /projects/{projectId}/git-credentials`
  are the card's one new server surface — Owner-only, project resolved through the tenant filter
  (foreign id = 404 for everyone), items are `GitCredentialListItem` (id, name, kind, createdAt,
  updatedAt, rotatedAt — no value, no ciphertext, no user name). The settings forms POST to F-013's
  existing `/git-credentials/*` endpoints; when the form carries the tab's hidden `ProjectId` field,
  success and validation answers become PRG redirects to `/projects/{id}/settings` (notices
  `credential-created/rotated/deleted`, errors `credentialError=` with the non-secret name/kind
  echo), and without the field the responses are byte-for-byte the F-013 contract — its tests were
  not touched and stay green. Key decisions recorded here because the card says "no new server
  surface beyond the list" and "keep using them from the settings tab": the optional PRG return
  address is a response-shape negotiation for the tab, not a new endpoint, and it was the only way to
  keep F-013's pinned JSON contract for its own callers.
- Credential visibility follows the secrets asymmetry: the list (and the Overview's credential count)
  is Owner-visible only; a Member's settings tab shows an "Owner access required" card and their
  forged writes are 403 (asserted, not assumed). The one decrypt path is still the worker's
  run-scoped fetch.
- Webhook endpoints: only the PRG redirect targets moved (enable/disable/delete/validation now return
  to the settings tab, where the form lives, and the show-once document's back link points there).
  Routes, status codes, the show-once document and all F-017-pinned behaviours are unchanged; the
  F-017 tests were re-pinned to drive and assert from the settings tab.
- TC-002 interpretation: the "second run still authenticates" is pinned at the worker's credential
  fetch (the step every clone passes through), not with a full private-repository clone — the clone
  path with a token over real HTTP is F-013's `GitCredentialSecrecyTests` and belongs to the
  milestone-scale work; this card's tests live in the fast suite. The project's `GitCredentialId`
  assignment has no UI in v0.9 (no endpoint for it, per the card's no-new-surface rule), so the tests
  seed it the way F-013's own endpoint tests do.
- Surprise: Razor's email-style rule bit the Pipelines tab — `schema v@pipeline.Version` after a
  letter renders the expression literally; the explicit `@(pipeline.Version)` form is required.
- Surprise 2: adding a field to F-013's form models changed MVC's binding of an empty POST — a delete
  with only the antiforgery token now binds the form model as null (previously an instance), which
  the F-013 delete test caught as a 500 (bug G-1 below, fixed same day).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| G-1 | High | Giving the credential forms a `ProjectId` return-address field changed MVC's form binding: a POST whose body carries no matching field (F-013's delete posts only the antiforgery token) binds the form model as null, and the endpoint's `form.ProjectId` access threw a 500 `NullReferenceException`. Caught by `GitCredentialEndpointsTests.The_owner_creates_rotates_and_deletes_a_credential` in the fast suite. | Fixed | All three handlers read the form null-safely (`form?.ProjectId`); the F-013 contract for empty bodies is unchanged and its tests pass. |
| G-2 | Low | Pre-existing stale copy on `/projects/{id}/delete/confirm`: the card said "Build runs and artifacts do not exist yet; when they do, this deletion will have to define its cascade semantics" — false since F-006/F-016, and the delete does cascade both. | Fixed | Copy now states the real behaviour: the delete removes the project "together with its runs and its webhooks", which `ProjectUiDeleteTests` pins. |
| D-003-B3 | Low | Review polish round: four P2 hygiene fixes. (1) Create/rotate called `form.ToInput()` unsafely, so a token-only POST (form bound null, the G-1 shape) would 500 — now an empty input fails validation as the ordinary 400 field error, and the G-1 comment no longer overstates the null-safety. (2) The settings tab bound `credentialKind` from the query but never used it — the echo now pre-selects the submitted kind on a validation redirect. (3) The Overview's organization-wide credential count read as project-scoped — the label now says "Credentials (organization)". (4) The credential list response lacked `Cache-Control: no-store` — added, matching the hygiene of every other credential-bearing response. | Fixed | All four applied; `dotnet build -c Release` 0/0 and the ProjectUi (12), Git (49, including the new `A_token_only_create_or_rotate_is_a_field_error_not_a_500` pin for fix 1) and Webhooks (69) suites green after the fixes. |

## Verification

- Related files: `src/Server/Components/Pages/Projects/ProjectDetail.razor` (Overview tab),
  `src/Server/Components/Pages/Projects/ProjectPipelines.razor` (new),
  `src/Server/Components/Pages/Projects/ProjectSettings.razor` (new),
  `src/Server/Components/Pages/Projects/DeleteProject.razor` (copy fix),
  `src/Server/Components/Pages/Runs/ProjectRuns.razor` (Runs tab + strip),
  `src/Server/Components/Shared/ProjectTabs.razor` (new tab header),
  `src/Server/Git/GitCredentialService.cs` + `GitCredentialEndpoints.cs` (list + tab PRG),
  `src/Server/Webhooks/WebhookEndpoints.cs` (PRG targets to settings),
  `tests/Zenkins.Tests/ProjectUi/*` (new), `tests/Zenkins.Tests/Webhooks/*` (re-pinned)
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=ProjectUi"`;
  `dotnet test -c Release --filter "Category!=Milestone"`;
  `dotnet test -c Release --filter "Category=Milestone"`. PostgreSQL contract gate not required: no
  model or migration change (GitCredential already exists from F-013).
- Results (2026-11-14):
  - `dotnet build -c Release` → Build succeeded, 0 Warning(s), 0 Error(s).
  - `Category=ProjectUi` → 12/12 passed, 0 failed, 0 skipped (routing matrix + deep links + 404s,
    member visibility + forged writes, credential lifecycle without reveal, runs-still-authenticate
    after rotation, webhook show-once re-pin, delete confirmation + cascade, pipelines frozen spec).
  - Fast suite `Category!=Milestone` → 908/908 passed, 0 failed, 0 skipped, 1m 43s. Includes the
    re-pinned F-017 webhooks suite (69 tests) and F-013's git credential endpoints/secrecy suites,
    all green from the moved UI.
  - `Category=Milestone` → 6/6 passed, 0 skipped, ~33s, twice consecutively. One earlier invocation
    failed once (1 of 6, real-process timing flake at 18s) and passed on both immediate reruns.
