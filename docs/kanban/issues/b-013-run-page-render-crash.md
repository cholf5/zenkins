---
id: B-013
title: A run page shows "The page failed to render." — the log panel renders once before its plan exists
type: bug
priority: P1
size: S
status: review
created: 2026-11-19
updated: 2026-11-19
blocked: none
---

## Goal

Starting a run opens the run page: the run's facts, its steps and the log panel. A fresh Freestyle
project's run page is not the error boundary.

## Background

Reported against the running UI: create a Freestyle project, click **Start run**, and the page the
browser lands on says **出错 / 页面渲染失败。** (*Something went wrong / The page failed to render.*)
with a **Try again** button — the run was queued (the redirect target is `/runs/{id}`), the page that
should show it never rendered.

The server log had the defect, not a theory about it:

```text
warn: Microsoft.AspNetCore.Components.Web.ErrorBoundary[100]
      Unhandled exception rendering component: Object reference not set to an instance of an object.
      System.NullReferenceException: Object reference not set to an instance of an object.
         at Zenkins.Server.Components.Pages.Runs.RunLogPanel.<BuildRenderTree>b__0_2(...)
            in .../src/Server/Components/Pages/Runs/RunLogPanel.razor:line 61
         at BlazorBlueprint.Components.BbCardContent.BuildRenderTree(RenderTreeBuilder __builder)
```

Line 61 is `@_plan.Indicator`. The field was declared `private RunLogPlan _plan = null!;` and filled
in `OnParametersSet`, with the note that the hook "always runs before the first render". For a
prerendered page that is true — the static response rendered fine, and every server-side page fact
stayed green. It is not true on an interactive circuit: the panel's `OnInitializedAsync` awaits the
log hub (`HubConnection.StartAsync`), and Blazor renders a component once while that await is
pending — **before** `OnParametersSet`. That render read a null plan and the ErrorBoundary replaced
the whole page.

Instrumenting the component on a live circuit showed the order plainly:

```text
SetParametersAsync start        instance=48020429
OnInitializedAsync  interactive=True
render fragment                 instance=48020429 plan=null   <- the render that threw
OnParametersSet                 instance=48020429
```

The ordering hazard arrived with F-043: before it, `_plan` was initialized inline from a literal
empty plan (`RunLogPanel.EmptyPlan()`), so the intermediate render had something to draw. F-043
needed the localizer inside the plan and moved the construction into `OnParametersSet`, leaving the
field null until then.

Nothing about the defect is Freestyle-specific: every run page has this panel, so every run page
failed interactively. The report surfaced it on the shortest path to a run page — a Freestyle project
needs no repository, so **Start run** is the first thing an empty project can do.

## Acceptance Criteria

- [x] Opening a queued run page on a live interactive circuit renders the run, its steps and the log
      panel — no ErrorBoundary, no server NRE in the log.
- [x] The panel has a renderable plan at *any* render, including the one that happens while
      `OnInitializedAsync` awaits the log hub — not only after `OnParametersSet`.
- [x] The plan still carries the localizer's copy (F-043's contract: the indicator, cap notice and
      search summary are translated, not English literals).
- [x] A fact pins the mechanism and fails on the pre-fix component (fail-closed).

## Subtasks

- [x] `src/Server/Components/Pages/Runs/RunLogPanel.razor`: the plan becomes a lazily built property
      (`private RunLogPlan Plan => _plan ??= _viewModel.Plan(BuildStepHeaders(), L10n);`); `_plan` is a
      nullable cache that `Replan()` refreshes, and the markup/handlers read `Plan`.
- [x] `tests/Zenkins.Tests/RunUi/LogPanelFirstRenderTests.cs`: one fact — a bare panel instance, with
      no lifecycle call at all, yields a non-null plan.
- [x] `CHANGELOG.md` (Unreleased).

## Dependencies

- F-008 (the log panel), D-004 (the plan and its controls), F-043 (the localizer moved the plan's
  construction into `OnParametersSet` — the change that exposed this).

## Test Cases

### TC-001: A freestyle project's first run page

Steps:
1. Create a Freestyle project and save at least one step.
2. Open the project page and click **Start run**.

Expected: the browser lands on `/runs/{id}`, which renders **Run #1**, the status badge, the frozen
step list and the **Log** card. No **出错 / 页面渲染失败。** page, no blank Retry screen.

### TC-002: The same page after the circuit connects

Steps:
1. Reload the run page from TC-001 (the interactive circuit connects over the websocket).

Expected: the page stays rendered; the log panel shows its controls and its "Following live"
indicator; the server log records no `ErrorBoundary` warning.

### TC-003: A run page reached on a Pipeline project

Steps:
1. Open any finished run of a repository project.

Expected: same result — the defect was in the shared panel, not in the project kind.

## Development Log

### 2026-11-19

- **Reproduced first, in a real browser.** A local server on a throwaway SQLite database was set up
  through the real first-run wizard, a Freestyle project was created and one step saved through the
  real forms, and the run was triggered through the real trigger. The static HTTP response was
  correct; loading the same URL in headless Chrome (with the session cookie) put the ErrorBoundary on
  screen and printed the NRE above in the server console. The static-only test suite could not see
  it, which is why the card records the browser pass.
- **Instrumented, then fixed.** Temporary `SetParametersAsync` / `OnInitializedAsync` /
  `OnParametersSet` / render-fragment logs (all removed before commit) showed the render happening
  between `OnInitializedAsync` and `OnParametersSet`, with `plan=null`. The fix is the lazy `Plan`
  property: the first render builds the same empty plan from the injected localizer and the view
  model's (empty) state, and `Replan()` overwrites the cache whenever the controls or the parameters
  change the state.
- **Deliberately not** "render a skeleton until the plan arrives": the panel's empty state is a real
  state of the view model (no lines yet), not a special case, and the localizer is available at any
  render, so there is no reason to render a second, different markup first.
- **The fact.** `LogPanelFirstRenderTests` constructs the component and reads the render-facing plan
  without any lifecycle call. Against the pre-fix component the property does not exist and the fact
  fails (1 failed / 0 passed); with the fix it passes.

## Verification

- Real browser, headless Chrome, live server on a throwaway database (macOS, Chrome 157):
  - Before the fix: the run page body was **出错 / 页面渲染失败。** and the server console carried the
    `RunLogPanel` NRE above.
  - After the fix: the same URL rendered **Run #1 / Queued**, the **Pin build** and **Cancel run**
    buttons, the frozen step list (`1. Greet / Pending`), the run's facts, and the **Log** card; the
    server console had no `ErrorBoundary` warning and no `NullReferenceException`.
  - The failure was re-checked after each restart so the before/after pair comes from the same URL and
    the same seeded run.
- `dotnet test --filter "FullyQualifiedName~LogPanelFirstRenderTests"` — **1 passed / 0 failed**; against
  the reverted component **1 failed / 0 passed**, then green again with the fix restored.
- `dotnet test --filter "FullyQualifiedName~RunUi"` — **34 passed / 0 failed**.
- `dotnet build Zenkins.slnx` — 0 warnings / 0 errors.
- **Not run: the whole gate** (`scripts/verify.py`) and the Milestone/Recovery/Postgres/publish suites.
  The change is one component file and one fact; the browser pass above is the end-to-end evidence.

## Review

Pending.
