---
id: B-010
title: The project form's kind switch only works when the page's script happened to run — Freestyle still demands a repository URL, and the inline script box does not exist
type: bug
priority: P1
size: M
status: review
created: 2026-11-19
updated: 2026-11-19
blocked: none
---

## Goal

Picking **Freestyle** or **Pipeline script** on the project create form leaves a form that matches the
choice: no repository URL is demanded, no definition source is offered for a kind that has none, and
the script box a reader picks is actually there to type in - whatever route brought them to the page.

## Background

Reported against the running UI: with **Project Kind = Freestyle** selected, the form still required
**Repository URL** and still offered **Pipeline script from SCM**.

Nothing in the product asks a Freestyle job for a repository. The server is kind-aware
(`ProjectValidation.ValidateRepositoryFields` requires the fields only for
`Pipeline + repository`, and a Freestyle project's repository columns stay null). The demand was the
**browser's own `required` attribute** on a field the form never took away, and the definition-source
choice was still on screen for the same reason.

Two separate truths decided the form's shape, and they only agreed by luck:

| Truth | Decided by | Where |
|---|---|---|
| which halves **exist** in the DOM | the Server, from the posted/query `Kind` | `ProjectFormCard.ShowRepositoryFields` / `ShowInlineFields` |
| which halves are **visible** | a script, `hidden` + `disabled` | `wwwroot/js/project-kind.js` |

A fresh create page posts no kind, so the Server rendered the repository half - `required` and all -
and left the rest to the script. `project-kind.js` at the time bound its `change` handlers to the
radios it found at `DOMContentLoaded`: the file is a plain `<script>` in `App.razor`, loaded **once per
document**, while an in-app navigation (this app routes interactively: `App.razor` passes
`PageRenderMode` down to `Routes`) replaces the page without re-running it. So the radios the reader
clicks had no handler, nothing hid, and the browser refused the submit with "fill out this field".

The same split produced a worse, quieter bug in the other half: `ShowInlineFields` was only true once
the *posted* source was inline, so a first-time reader who chose "Pipeline script" got the repository
fields withdrawn and **no script box at all** - the box only appeared after a refused create echoed
`pipelineSource=inline`. Switching back to Pipeline from such a re-render had the mirror problem: the
repository fields were not in the DOM to come back.

`js/freestyle-steps.js` and `js/parameter-rows.js` had the same load-time binding, on forms whose row
buttons a replaced DOM would have left dead. `js/snippets.js` was already listening on `document`, and
that is the shape the fix generalises.

## Acceptance Criteria

- [x] The create form renders **all three** field groups in every state, and the Server renders
      `hidden` + `disabled` for the groups that do not apply to the posted/query state, so a page whose
      script never ran is already honest and no `required` field can block a Freestyle submit.
- [x] `js/project-kind.js` installs a `change` listener on `document` (never on the radios), and
      re-applies its state on arrival and on `enhancedload`.
- [x] Choosing "Pipeline script" on a first visit reveals the script box (it is in the DOM, hidden and
      disabled, not missing).
- [x] Switching back and forth any number of times works, because no group is ever absent.
- [x] A kind-locked edit form still renders nothing for a half the project does not own: a Freestyle
      project's edit form carries no repository field and no script box.
- [x] `js/freestyle-steps.js`, `js/parameter-rows.js` and `js/snippets.js` install every listener on
      `document`, so a form Blazor replaced keeps working (the row buttons, the serialise-on-submit).
- [x] Facts pin both halves: the rendered groups and their state (4 facts), and the "listens on the
      document" shape of the four form scripts (1 theory, 6 rows).

## Subtasks

- [x] `ProjectFormCard`: `RenderRepositoryFields` / `InlineFieldsEnabled` / `DefinitionFieldsEnabled`
      replace `ShowRepositoryFields` / `ShowInlineFields`; every control carries `disabled` and every
      group carries `hidden` for the state it is not in.
- [x] `project-kind.js`: `document`-level `change`, `apply()` on load and on `enhancedload`,
      `setEnabled` instead of `disableAll` (same effect, named for what it means).
- [x] `freestyle-steps.js`, `parameter-rows.js`: capture-phase `submit` + `click` on `document`,
      deriving the form from the event target; `snippets.js`: the same listener without the
      `DOMContentLoaded` wrapper it did not need.
- [x] `tests/Zenkins.Tests/ProjectUi/ProjectFormFieldGroupsTests.cs`.

## Dependencies

- F-020 (Freestyle kind), F-022 (inline source) — the features whose surface this repairs.
- F-044 — its harness seeded owners through the wizard while this card was written; two of its
  test-side defects blocked every host-creating fact and are fixed here (see the log).

## Test Cases

### TC-001: A Freestyle choice on the create form

Steps:
1. Open `/projects/new` (from an in-app link, not a reload) and pick **Freestyle**.
2. Fill in a name and submit.

Expected:
- The repository fields and the definition-source choice are gone from view, the script box and the
  repository fields still exist in the DOM but are `hidden` and `disabled`.
- The create succeeds; the project's repository columns are null.

### TC-002: An inline pipeline on the create form

Steps:
1. Open `/projects/new` and pick **Pipeline script**.

Expected:
- The repository fields hide, the script box appears immediately, and the script is required only by
  the server's own `inline-script-required` answer when it is left empty.

### TC-003: The two re-rendered states

Steps:
1. Open `/projects/new?kind=freestyle`; then `/projects/new?pipelineSource=inline`.

Expected:
- Both pages carry every group; the first disables the repository half and hides the definition
  source, the second enables the script box and hides the repository half.

## Development Log

### 2026-11-19

- **The form.** `src/Server/Components/Shared/ProjectFormCard.razor` now renders the three groups as
  three *states of one form* rather than three conditional fragments. `RenderRepositoryFields` is
  always true on the unlocked create form and stays the old shape test on a kind-locked edit form
  (`Pipeline + repository` only), so a Freestyle edit page still has no repository input to inherit.
  `RenderInlineFields` is `!KindLocked`: the box exists on the create form in every state - that is
  what makes "Pipeline script" revealable on the first visit - and never on an edit form, where the
  inline script is edited on the Pipelines tab.
- **Why `hidden` and `disabled` and not just one.** `hidden` is presentation; `disabled` is what keeps
  a `required` input from blocking the submit and keeps a non-applicable group out of the POST. The
  Server renders both from the state it was given, so the no-script rendering is already correct and
  the script is only ever *keeping* it correct.
- **The script.** `js/project-kind.js` is now one `change` listener on `document` plus `apply()` on
  arrival and on `enhancedload`. `disableAll` became `setEnabled` (`root.hidden = !enabled`,
  `inputs.disabled = !enabled`) which is the same DOM effect with a name that says which state is
  being asked for. Nothing queries a control at load time any more, so *when* the script ran stopped
  mattering.
- **The siblings.** `js/freestyle-steps.js` and `js/parameter-rows.js` bind `submit` (capture phase,
  before `js/antiforgery.js` fills its token) and `click` on `document` and derive the form from
  `event.target.closest(...)`; the add / remove / move / insert-snippet and add / remove paths are
  unchanged, only the entry point moved. `js/snippets.js` was already delegating - its
  `DOMContentLoaded` wrapper is gone because it only wrapped one `document` listener.
- **The facts.** `ProjectFormFieldGroupsTests` renders the real pages and reads the real tags: every
  group present in all three states, `hidden`/`disabled` saying which applies, and a Freestyle edit
  page carrying neither repository nor inline fields. The script half is a source-shape fact - every
  `.addEventListener(` in those four files must be a `document.addEventListener(` - because a browser
  pass is not in the gate (no Node.js, no browser download; the same trade B-007 recorded) and this
  check fails closed: the pre-fix files bind 1-2 handlers to forms and go red.
- **Two harness defects found while verifying, fixed here.** The tree at `d09bce1` was red for every
  fact that creates a host, and neither defect was this card's:
  1. `AuthTestHost.RegisterAsync` posted the wizard's Owner form with the antiforgery token read from
     `/setup` - the welcome page, which has no form. It now reads `/setup/owner`, where the form (and
     its token) is.
  2. `SetupWizardTests` compared `Location?.PathAndQuery` on redirects: TestServer answers some with a
     relative URI, where that property throws, and others absolutely. A local `PathOf` reduces both
     shapes to the path a browser would follow, which is what those assertions are about.
  F-044's owner should confirm both when that card is reviewed; they are test-harness edits, no product
  behaviour changed. They are committed on their own (`c57f3c9`) so that card can take or drop them
  without this one.

## Verification

- `dotnet build Zenkins.slnx` - clean (`src/Server`, `tests/Zenkins.Tests`, both 0 warnings / 0 errors).
- `dotnet test --filter "FullyQualifiedName~ProjectFormFieldGroupsTests"` - **10 passed / 0 failed**:
  the 4 group facts (create default, `?kind=freestyle`, `?pipelineSource=inline`, Freestyle edit) and
  the 6 theory rows for the four scripts.
- `dotnet test --filter "FullyQualifiedName~ProjectFormFieldGroupsTests|FullyQualifiedName~SetupWizardTests"`
  - **17 passed / 1 failed** before the redirect comparison was fixed; the failing fact
  (`The_owner_form_creates_the_named_organization_and_signs_the_browser_in`) then passed on its own
  (**1 passed / 0 failed**).
- **Not run: the whole gate.** The full suite was deliberately not executed on this change (it is heavy
  on this machine, and the operator asked for code-only). The facts above were verified; the
  Milestone/Recovery/Publish/Postgres suites were not, so nothing here speaks for them.
- **Not verified: a real browser.** By construction: the gate is `dotnet test`. The rendered markup and
  the script's binding shape are pinned; the click-through is TC-001..TC-003 and belongs to whoever
  reviews this in a browser.

## Review

Pending.
