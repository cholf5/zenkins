---
id: D-001
title: Blazor Server app shell on BlazorBlueprint v4
type: feature
priority: P1
size: M
status: todo
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

A navigable application shell — top bar with Organization switcher, left navigation, content region, light/dark theme — built on BlazorBlueprint v4 so later cards only add pages, never chrome.

## Background

Verified facts (2026-11-12, from NuGet):

- `BlazorBlueprint.Components` **4.2.0** — pre-styled components built on **shadcn/ui + Tailwind CSS**.
- `BlazorBlueprint.Primitives` **4.2.0** — headless, ARIA + keyboard support.
- `BlazorBlueprint.Templates` **4.0.0** — project templates with v4 pre-installed.
- `BlazorBlueprint.Icons.Lucide` **2.0.2** — icon set.

Two consequences to settle in this card, not later:

1. **Visual direction.** These are shadcn/themed components, not BlueprintJS's dense industrial dark UI. PRD §4.1 asks for "developer-tool visual style". Default shadcn is the fastest path and looks modern; if the intent was BlueprintJS density, that is a different component library and the decision must be recorded here.
2. **Tailwind build step vs. PRD §2.2 ("no Node.js runtime required").** Tailwind's CLI needs Node at *build* time. Acceptable: commit the compiled CSS so running Zenkins never needs Node. Record this in the card.

## Acceptance Criteria

- [ ] `dotnet run --project src/Server` serves a shell with: brand, Organization switcher slot, primary nav (Dashboard / Projects / Workers / Secrets), user menu, content outlet.
- [ ] Nav collapses to a sane narrow layout; content region scrolls independently.
- [ ] Light and dark themes both render legibly; the choice persists across reloads.
- [ ] All chrome comes from BlazorBlueprint components (or its layout primitives) — no hand-rolled where a component exists.
- [ ] Compiled CSS is committed; running the app requires no Node.js.
- [ ] A documented command regenerates the CSS after component/theme changes.

## Subtasks

- [ ] Add `BlazorBlueprint.Components`, `.Primitives`, `.Icons.Lucide` (pinned in `Directory.Packages.props`).
- [ ] Bring in the Tailwind v4 CSS entry point + theme tokens; produce `wwwroot/app.css`.
- [ ] Build `MainLayout` + `NavMenu` + `OrgSwitcher` placeholder + `UserMenu` placeholder.
- [ ] Add theme toggle with persistence.
- [ ] Add placeholder pages for Dashboard / Projects / Workers / Secrets so nav is not dead.
- [ ] Record the visual-direction decision (shadcn defaults vs. custom density) in the Development Log.

## Dependencies

- I-001

## Test Cases

### TC-001: Shell renders

Steps:
1. `dotnet run --project src/Server`
2. Open `/`

Expected:
- Shell renders; nav shows four items; no console errors.

### TC-002: Theme persists

Steps:
1. Toggle to dark, reload.

Expected:
- Still dark after reload.

### TC-003: No Node required to run

Steps:
1. On a machine without Node.js, restore and run only.

Expected:
- App starts and styles apply (committed CSS).

## Development Log

### 2026-11-12

- Card created. Not started.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Components/**`, `src/Server/wwwroot/**`
- How to run/verify: `dotnet run --project src/Server` then browse `/`
- Results: not run yet
