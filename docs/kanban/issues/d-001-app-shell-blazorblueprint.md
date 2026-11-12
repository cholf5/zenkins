---
id: D-001
title: Blazor Server app shell on BlazorBlueprint v4
type: feature
priority: P1
size: M
status: review
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

- [x] `dotnet run --project src/Server` serves a shell with: brand, Organization switcher slot, primary nav (Dashboard / Projects / Workers / Secrets), user menu, content outlet.
- [x] Nav collapses to a sane narrow layout; content region scrolls independently.
- [x] Light and dark themes both render legibly; the choice persists across reloads.
- [x] All chrome comes from BlazorBlueprint components (or its layout primitives) — no hand-rolled where a component exists.
- [x] UI CSS is shipped pre-built by `BlazorBlueprint.Components` as a static web asset; running the app requires no Node.js and no committed generated stylesheet.
- [x] No CSS regeneration command is needed: `dotnet restore` / `dotnet build` refresh the package's static web assets. Project theme overrides live in `wwwroot/styles/theme.css`.

## Subtasks

- [x] Add `BlazorBlueprint.Components` 4.2.0 (version in `Directory.Packages.props`). `Primitives` 4.2.0 and `Icons.Lucide` 2.0.2 are pinned too because the shell uses `BbPortalHost` and `LucideIcon` directly.
- [x] Link the package's pre-built CSS and put project theme overrides in `wwwroot/styles/theme.css`, loaded before it. No Tailwind entry point or generated `app.css`.
- [x] Build `MainLayout` + `NavMenu` + Organization-switcher placeholder + user-menu placeholder.
- [x] Add theme toggle with persistence.
- [x] Add placeholder pages for Dashboard / Projects / Workers / Secrets so nav is not dead.
- [x] Record the visual-direction decision (shadcn defaults vs. custom density) in the Development Log.

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
- App starts and styles apply from the package's pre-built CSS; no `package.json` or Tailwind CLI is present.

### TC-004: Persisted dark theme is server-rendered

Steps:
1. `curl -H 'Cookie: zenkins-theme=dark' http://127.0.0.1:5099/`
2. `curl http://127.0.0.1:5099/`

Expected:
- The first response's `<html>` carries `class="dark"`; the second does not.

## Development Log

### 2026-11-12

**Decision 1 — shadcn defaults, not BlueprintJS density.** BlazorBlueprint v4 is shadcn/Tailwind-themed, not a BlueprintJS clone. Zenkins takes the shadcn defaults. PRD §4.1's "developer-tool visual style" is satisfied by a clean, compact, information-dense-enough layout (sidebar + top bar + independently scrolling content) rather than replicating BlueprintJS pixel density. If BlueprintJS density becomes a hard requirement, that is a different component library and should be a separate decision, not a theme override.

**Decision 2 — no Tailwind build step exists at all.** The package ships compiled CSS (`_content/BlazorBlueprint.Components/blazorblueprint.css`) as a static web asset. There is no Tailwind CLI, no `package.json`, no Node.js, and nothing in this repo to regenerate. This is stronger than the card's "commit the compiled CSS" fallback. Project theme tokens live in `wwwroot/styles/theme.css` and load before the package CSS so they can override its defaults. Regeneration story: `dotnet restore` / `dotnet build` refresh the package's static web assets; there is no Node or Tailwind CLI to run.

**Shell.** `MainLayout` uses `BbSidebarProvider` / `BbSidebar` / `BbSidebarInset` / `BbSidebarRail` with `BbAppBar`, `BbScrollArea` (`FillContainer`, independent scrolling), `BbDropdownMenu`, `BbAvatar`, `BbSeparator`, `BbButton` and `BbDarkModeToggle`. `NavMenu` uses `BbSidebarMenuButton` links with Lucide icons. The sidebar collapses to an icon rail on desktop and becomes a `BbSheet` drawer below the package's `md` breakpoint; `BbSidebarTrigger` is in the top bar. Organization and user menus are explicit disabled placeholders pending F-001.

**Rendering and theme.** I-001 shipped no `@rendermode`; `Routes` and `HeadOutlet` are now `InteractiveServer`. The theme cookie (`zenkins-theme`) is written by `wwwroot/js/theme-persistence.js`, which also mirrors the cookie into BlazorBlueprint's `bb-theme` localStorage entry. The server reads the cookie in `App.razor` and emits `<html class="dark">` on the first response, so a persisted dark theme has no flash. The package's own `BbDarkModeToggle` + `ThemeService` remain the toggle; the cookie bridge is the only app-specific persistence code.

**Error/404.** Added a `Router.NotFound` view plus `/not-found` page, `UseStatusCodePagesWithReExecute("/not-found")` and a `Routes` `ErrorBoundary`; `/error` is wired for non-Development via `UseExceptionHandler`. `/missing-route` returns 404 and renders the NotFound page without a loop; `/not-found` itself returns 200.

**No database.** No component injects `ZenkinsDbContext` or `IDbContextFactory`; pages do not touch tenant context because no organization is selected yet.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Components/**`, `src/Server/wwwroot/**`, `src/Server/Program.cs`, `Directory.Packages.props`, `src/Server/Zenkins.Server.csproj`
- How to run/verify: `dotnet run --project src/Server` then browse `/`
- Results (2026-11-12, logs in `/tmp/zenkins-logs/D-001/`, never committed):
  - `dotnet build -c Release` → **0 warnings, 0 errors** (`build.log`).
  - `dotnet test` → **15 passed, 0 failed, 0 skipped** (`test.log`).
  - Server smoke: `curl` `/`, `/projects`, `/workers`, `/secrets` → **200** each; HTML contains `Dashboard`, `Projects`, `Workers`, `Secrets`, `Zenkins`, `No organization`, `Open user menu` (`page-root.html`, `page-*.html`).
  - Theme persistence: `curl -H 'Cookie: zenkins-theme=dark' /` → `<html lang="en" class="dark">`; `curl /` without the cookie → `<html lang="en">` (`dark.html`, `light.html`).
  - Responsive: rendered sidebar has `bb:hidden bb:md:flex`; package CSS defines `@media (min-width:48rem)`; `BbSidebarTrigger` present (`page-root.html`).
  - Independent scrolling: content viewport renders `overflow-y: scroll` and sits in a `bb:flex-1 bb:min-h-0` region below the app bar (`page-root.html`).
  - No Node: `grep -r "node\|tailwind" package.json` → no such file; no `package.json` or lockfile exists in the repo.

**Not automatically covered by curl:** the click path through `BbDarkModeToggle` (it requires a browser/Blazor circuit) and the mobile `BbSheet` drawer after JS sets `IsMobile`. The cookie bridge and server-rendered class are verified; the toggle delegates to the package's own `ThemeService`.
