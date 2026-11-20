---
id: B-015
title: A save the builder refuses swallows the whole draft — the PRG page renders the last saved spec, and with the collapsed rows the refused step is hard to find
type: bug
priority: P1
size: M
status: review
created: 2026-11-20
updated: 2026-11-20
blocked: none
---

## Goal

A reader whose steps submission is refused gets their own draft back on the page, byte for byte,
with the row the refusal names marked and opened. Nothing they typed is lost to a stable error code.

## Background

The steps editor saves through a PRG form: the POST freezes the spec (F-020) and the browser lands
on `/projects/{id}/pipelines?error=steps-invalid`. When `FreestyleSteps.BuildSpecJson` refuses the
submission — an empty step name, a non-numeric timeout, a broken env line — the refusal reason went
to the log only, the redirect carried the stable code, and the page re-rendered the editor from the
**saved** spec. The draft existed nowhere else: the POST body was gone, so every keystroke the
reader had made since the last successful save vanished, leaving one generic sentence ("The step
list was refused - check every step's name and script, then try again.").

The list + inspector layout makes this worse, not better: a collapsed row hides the field that
failed, and the one sentence names nothing a person can search for. Before the layout change the
same defect ate the same work, but every field was on screen to re-type; now the reader must also
find *which* of their rows is the broken one.

The card's root-cause chain (all three links re-read before the fix):

1. `src/Server/Projects/ProjectService.cs` (`SaveFreestyleStepsAsync`): `BuildSpecJson` returns
   null and the specific reason went to the log; the result carried only
   `ProjectFieldError(Steps, "steps-invalid")`.
2. `src/Server/Projects/ProjectEndpoints.cs` (the steps POST handler): the refusal PRG-302s to
   `/projects/{id}/pipelines?error=steps-invalid`. The POST body survives only this redirect.
3. `src/Server/Components/Pages/Projects/ProjectPipelines.razor`: `OnInitializedAsync` renders the
   editor from `_savedSteps`, parsed from the stored spec.

The fix's shape follows the page's own precedent: F-022's validation result is already "a GET-time
read of an in-memory result, not a run or a stored row". A refused submission is stashed in memory
under the same discipline — one-shot, bounded, not durable — and the PRG GET renders the editor
from what was submitted.

## Acceptance Criteria

- [x] A refused save renders the submitted draft, not the saved spec: every row and every field's
      text the reader submitted is on the page again, including text the frozen shape cannot hold
      (an env comment, a non-numeric timeout) — the values pass through verbatim, valid or not.
- [x] The refusal's own sentence (the envelope's phrasing, `pipeline.steps[2].name must be a
      non-empty string.`) is on the page, not only in the log.
- [x] The row that sentence names is marked server-side (`data-split-error`) — the main list and
      all three post lists, by their own paths.
- [x] The stash is read once: the PRG GET consumes it, and a refresh of the same URL answers the
      saved spec again; a save that lands retires whatever the stash still holds.
- [x] Another member of the same organization visiting the same editor never receives the draft.
- [ ] In a real browser: the marked row is visually distinct and is the row the editor opens first
      (js/split-list.js prefers it), the reason reads in both languages, and nothing of the draft is
      lost after the reader fixes and saves. (Server render and the marking are pinned by facts; the
      open-first behavior and the pixels are the browser pass.)

## Subtasks

- [x] `src/Server/Projects/FreestyleDraftStash.cs` (new): the singleton stash — keyed (user,
      project), read-once `Take`, `Refuse` write, `Clear` on a save that lands, 128-entry cap,
      256 KiB per-entry bound, 30-minute expiry. A submission beyond the bound is not stashed and
      the page falls back to the pre-card behavior.
- [x] `ProjectMutationResult` carries `Detail` (the builder's own sentence) behind the stable field
      code; `SaveFreestyleStepsAsync` fills it from `BuildSpecJson`'s `error` (the log line stays).
- [x] The steps POST handler stashes the refused `FreestyleStepsInput` verbatim and clears the
      stash on success; DI registers the stash as a singleton (it bridges two requests).
- [x] `FreestyleSteps.ReadDrafts` + the `FreestyleStepsDrafts` record: the display-side reader —
      null for a list the submission did not carry (that section renders the saved rows), drafts
      exactly as submitted otherwise.
- [x] The page consumes the stash when the error code is `steps-invalid` and hands the drafts plus
      the sentence to the editor; the editor shows the sentence under the stable code and parses the
      reason's path (`pipeline.steps[n]` / `pipeline.post.<outcome>[n]`, 1-based) into a row index
      per section.
- [x] `FreestyleStepsSection` renders a section from drafts when it is given some (one loop, the
      row shell shared), marks `data-split-error` on the named row; `FreestyleStepFields` gains a
      `Draft` parameter whose editor-shaped text wins verbatim over the frozen step's.
- [x] `js/split-list.js` opens a marked row first; `app.css` marks the row with an inset marker
      (no layout shift) and a destructive index numeral.
- [x] Two localized strings for the alert's new lines (both sides of the catalog, as the
      two-way-checked directory requires).
- [x] Four facts in `FreestyleRunTests`: the main-list render-back (with the env comment), the
      post-list render-back, another member's isolation, and the successful save retiring the stash.
- [x] `CHANGELOG.md` (Unreleased).

## Dependencies

- F-020 (the steps editor and its PRG save), F-031 (the three post lists and their path shape),
  F-022 (the in-memory-result precedent this follows), B-011/the split editor (the collapsed rows
  that make the loss harder to notice).

## Test Cases

### TC-001: One empty name in a three-step draft

Steps:
1. Save a valid two-step list on a Freestyle project.
2. Post a three-step list whose middle step has no name (the real editor's POST).
3. On the redirected page, read the editor.

Expected: the page shows the three submitted rows with their scripts (including the refused one),
the sentence `pipeline.steps[2].name must be a non-empty string.` under the stable code, and the
second row marked. A refresh of the same URL shows the saved two-step list again.

### TC-002: The same reader's draft against another member

Steps:
1. Repeat TC-001's refusal.
2. Have another member of the same organization open the same editor with the same error code.

Expected: the member sees the saved list, never the refused draft; the owner's own visit still
receives it.

### TC-003: Fix and save

Steps:
1. After TC-001's refusal, POST a valid list without re-opening the editor first.
2. Visit `/projects/{id}/pipelines?error=steps-invalid` once more.

Expected: the old draft does not resurrect; the page renders the newly saved list.

### TC-004: A refused post step

Steps:
1. Post a valid main list with an `always` post step that has no name.
2. Read the redirected page.

Expected: the always section shows the submitted post draft, the sentence names
`pipeline.post.always[1]`, and the row is marked.

## Development Log

### 2026-11-20

- Implemented in one pass, following the handoff's suggested shape. The design decision the handoff
  left open — *what exactly the stash holds* — is the raw `FreestyleStepsInput` (the four lists' JSON
  as posted) plus the reason. Rendering converts drafts for display only; nothing rebuilds a spec.
- **The fidelity decision** this card rests on: a saved row renders from a frozen `PipelineStep`,
  but a refused draft cannot round-trip through that shape — the env dictionary skips comments and
  blank lines, the timeout/retry fields are ints. Rendering a refused draft through it would
  silently drop exactly the text the reader must still see (the same failure class the split
  editor's move-don't-rerender rule exists for). So `FreestyleStepFields` gained a `Draft` source
  whose editor-shaped text wins verbatim; `Step` stays null on those rows.
- **Scope discipline**: a list the submission did not carry (a hand-crafted POST that omits a
  hidden field) renders that section from the saved spec, not as empty — the browser's serializer
  always posts all four lists as `[]`, so the omission means "never submitted".
- The stash is deliberately not session-scoped: the PRG's two requests are different scopes, so the
  registry is a singleton with a lock, mirroring `DefinitionValidationRegistry`'s shape (cap, prune,
  `now` parameter for tests).
- Found while verifying: the post list's refusal sentence for an empty name is the envelope's
  "must start with 'post/'" (the bare prefix), not "must be a non-empty string." — the sentence is
  the envelope's verbatim, as F-020 promises, and the first draft of the fact guessed wrong.

## Verification

- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.Runs.FreestyleRunTests"` — **14 passed /
  0 failed** (10 pre-existing, 4 new).
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.ProjectUi"` — **30 passed / 0 failed**
  (the split-editor pins and the row-template pins hold).
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.Localization|FullyQualifiedName~Zenkins.Tests.Theme"`
  — **38 passed / 0 failed** (the two new strings pass the two-way catalog check).
- `dotnet build src/Server/Zenkins.Server.csproj` — 0 warnings / 0 errors; `node --check` on both
  editor scripts clean.
- **Not yet run: the real-browser pass** (TC-001 through TC-004 clicked through, both languages,
  the marked row visually distinct and opened first). The environment could not run a browser; the
  server-side facts above pin the render and the marking, and the browser pass is what closes the
  last acceptance box.
