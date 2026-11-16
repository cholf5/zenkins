---
id: D-007
title: Multi-theme switcher in the app bar (dark/light, base gray scale, primary accent, radius)
type: feature
priority: P2
size: M
status: done
created: 2026-11-15
updated: 2026-11-16
blocked: none
---

## Goal

A signed-in user can pick among multiple themes from the app bar — dark/light,
five base gray scales, seventeen primary accent colors and the corner radius —
and the choice persists per browser and is already on the first paint (no
wrong-theme flash on reload).

## Background

The component library (BlazorBlueprint 4.2.0) ships the whole system: the
scoped `ThemeService` persists to the `bb-theme` localStorage entry, and
`BbThemeSwitcher` is the picker popover. D-001 wired only dark/light: the
`zenkins-theme` cookie drives the server-rendered dark class through
`wwwroot/js/theme-persistence.js`, and the UI entry was the one-button
`BbDarkModeToggle`. Two pieces of application-side wiring were missing, so a
color choice could not have worked at all:

- the palette stylesheet `_content/BlazorBlueprint.Components/css/themes.css`
  was never loaded — the `[data-base-color=…]` / `[data-primary-color=…]`
  token definitions live only there (verified: zero hits in
  `blazorblueprint.css`), so an accent picked via `ThemeService` would have
  set an attribute no stylesheet reads;
- the package's pre-paint restore script
  `_content/BlazorBlueprint.Components/js/theme-init.js` was never loaded, so
  an accent (and a saved radius) would have appeared only after hydration — a
  visible flash on every load.

The cookie contract (`ThemePreference`, exact `dark`/`light`) is deliberately
unchanged: the server only needs the dark class for SSR; the accent is
replayed client-side before the first paint from `bb-theme` localStorage.
This also cashes in the note in d-001's review that the bridge's duplication
of the package's `theme-init.js` logic "should be revisited": the package
script is now loaded next to the bridge instead of duplicating its attribute
logic.

## Acceptance Criteria

- [x] The app bar renders the theme switcher trigger (icon-only ghost button,
      `aria-label="Customize theme"`), replacing the bare dark-mode toggle;
      the popover offers dark/light, base colors, primary colors and radius.
- [x] An accent/radius choice actually changes the UI (palette stylesheet
      loaded) and survives a reload without a flash (the package's pre-paint
      script is loaded after the bridge, before first paint).
- [x] The `zenkins-theme` cookie contract is unchanged (exact `dark`/`light`):
      cookie dark still server-renders `<html class="dark">`, and the cookie's
      verdict is echoed to the pre-paint script as `data-default-dark`, so a
      cookie-dark browser with localStorage blocked cannot be flipped to the
      OS light preference by that script's own fallback.
- [x] HTTP tests (Category=Theme) cover the cookie contract plus
      `data-default-dark`, the head wiring plus the two assets really served,
      and the trigger/toggle swap in the shell.

## Subtasks

- [x] `App.razor`: load `css/themes.css` (after `blazorblueprint.css`, before
      `app.css`) and `js/theme-init.js` (after `js/theme-persistence.js` — it
      reads the `bb-theme` entry the bridge just repaired/seeded) with
      `data-default-dark` emitted from `ThemePreference.IsDark`.
- [x] `MainLayout.razor`: swap `BbDarkModeToggle` for `BbThemeSwitcher`
      (ghost variant, square `TriggerClass` to line up with the user menu).
- [x] Comment updates: App.razor load-order notes; Program.cs clarifies that
      `DetectSystemPreference = false` governs dark mode only.
- [x] `tests/Zenkins.Tests/Theme/ThemeShellTests.cs`: three HTTP tests
      through the real host and static web assets.
- [x] A throwaway probe render pinned the real trigger markup before the
      assertions were written; probe deleted afterwards.

## Dependencies

- D-001 (theme cookie bridge) — accepted.

## Test Cases

### TC-001: The theme cookie renders dark and tells theme-init so

Steps:
1. GET `/account/login` (static SSR, no sign-in needed) with the
   `zenkins-theme=dark` cookie.
2. GET the same page with no theme cookie.

Expected:
- Step 1: `<html lang="en" class="dark">` and the theme-init script tag
  carries `data-default-dark="true"`.
- Step 2: `<html lang="en">` (no dark class) and `data-default-dark="false"`.

Result (2026-11-15, `ThemeShellTests.The_theme_cookie_renders_dark_and_tells_theme_init_so`):
**PASS.** Both branches exact-matched in the rendered HTML; the cookie value
is sent from the production `ThemePreference.CookieName` constant.

### TC-002: The head wires the palette stylesheet and the pre-paint script, and serves both

Steps:
1. Sign in through the real forms; GET `/`.
2. GET `_content/BlazorBlueprint.Components/css/themes.css` and
   `_content/BlazorBlueprint.Components/js/theme-init.js`.

Expected:
- Step 1: the head references both paths.
- Step 2: both respond 200 (a missing asset would leave the switcher's
  colors silently unrendered).

Result (2026-11-15, `ThemeShellTests.The_head_wires_the_palette_stylesheet_and_the_pre_paint_script_and_serves_both`):
**PASS.** Both references present; both assets 200 through the test host.

### TC-003: The app bar offers the theme switcher, not the bare dark toggle

Steps:
1. Sign in through the real forms; GET `/`.

Expected:
- The shell contains `aria-label="Customize theme"` on a
  `aria-haspopup="true"` button; the package toggle's default
  `aria-label`s (`Switch to dark mode` / `Switch to light mode`, recorded in
  d-001) are absent.

Result (2026-11-15, `ThemeShellTests.The_app_bar_offers_the_theme_switcher_and_not_the_bare_dark_toggle`):
**PASS.** Trigger found (icon-only ghost button, `TriggerClass` applied);
both old labels absent.

## Development Log

### 2026-11-15

- Probed the package first: `css/themes.css` holds the OKLCH palettes keyed
  off the `data-base-color`/`data-primary-color` attributes (unlayered, with
  compound `.dark[data-base-color=…]` selectors — dark mode survives an
  attribute); `blazorblueprint.css` does not contain them;
  `js/theme-init.js` is the documented classic-blocking pre-paint restore
  (class, both color attributes, inline `--radius`, design attributes) and
  takes `data-default-dark` to override its OS fallback.
- Implementation: App.razor loads the stylesheet after the package CSS and
  the script after the bridge; the script tag's `data-default-dark` is
  derived from `ThemePreference.IsDark` on the same HttpContext the SSR class
  uses, so the two can never disagree. MainLayout swaps the toggle for
  `BbThemeSwitcher` (`ButtonVariant.Ghost`, `TriggerClass="bb:h-9 bb:w-9
  bb:p-0"`, `aria-label` via `AdditionalAttributes`). `ThemePreference.cs`
  and `theme-persistence.js` untouched — the bridge already preserves the
  base/primary/design fields when it seeds `bb-theme`.
- Probe render pass confirmed the emitted markup (script tag + attributes;
  trigger button classes and labels) before the assertions were written; the
  probe file was then deleted.
- Known limitation (recorded, not a regression): the d-001 follow-up hole
  where ThemeService's interactive `InitializeAsync` falls back to
  `DefaultDarkMode` when localStorage is blocked **and** a dark cookie
  exists. With this card the *first paint* is now correct (pre-paint dark,
  from the cookie-derived `data-default-dark`), but after hydration
  ThemeService can still flip to light and the cookie observer rewrites the
  cookie. Closing it would need a package cookie/header hook that does not
  exist; the trade-off stands as recorded in d-001.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | Interactive flip in the blocked-storage + dark-cookie corner (d-001 follow-up). | Deferred | First paint is correct since this card; the hydration-time flip needs a package hook that does not exist. Recorded in the Development Log. |
| B-007 | High | The app defines no base token palette, so with no base color picked `--primary`/`--background`/`--border` are undefined and a dark/light flip changes nothing. Real-Chromium evidence in review. | Open | Pre-existing (D-001 era), not introduced here. Filed as B-007 — it caps the value of every choice this card offers. |
| Q-013 | Medium | The three shell tests only check that the two asset URLs answer 200: moving `theme-init.js` into `<body>`, or emptying `themes.css`, leaves them green. | Open | Filed as Q-013 with the positional and body-content assertions. |
| — | Low | The trigger is 36px (`bb:h-9 bb:w-9`) beside the 40px user-menu button, so "lines up" is only true vertically. | Open | Needs either `bb:h-10 bb:w-10` or the user menu dropped to `h-9`. |
| — | Low | With no cookie, `data-default-dark="false"` overrides the package script's `matchMedia` fallback, so a blocked-storage browser on a dark OS gets a light first paint. | Open | Correct fix is to omit the attribute when there is no valid cookie value (a `bool?` parse in `ThemePreference`), letting the two scripts agree. Narrow impact, not a regression. |

## Verification

- Related files:
  `src/Server/Components/App.razor`,
  `src/Server/Components/Layout/MainLayout.razor`,
  `src/Server/Program.cs` (comment),
  `tests/Zenkins.Tests/Theme/ThemeShellTests.cs`.
- How to run/verify:
  `dotnet test --filter "Category=Theme"` (3 tests); then the full suite.
  Manually (reviewer): `dotnet run --project src/Server`, sign in, open the
  app-bar theme popover, pick e.g. base slate / primary violet, reload — the
  accent should already be on the first paint, `<html>` should carry
  `data-primary-color="violet"`, and the `zenkins-theme` cookie stays
  `dark`/`light`.
- Results (2026-11-15, macOS arm64): Theme filter **3/3 green**. Full suite
  **1068/1069**, three consecutive runs each failing only
  `DashboardQueryCeilingTests.The_read_model_command_count_is_fixed_regardless_of_history_length`
  (Expected 201, Actual 401) — reproduced on a stashed clean tree at d8b7b96
  with `--filter Category=Dashboard` (1/13), so it is pre-existing on main
  and unrelated to this card. Diagnosed and recorded on B-003. The
  interactive picker behavior (popover, persistence, no-flash reload) is not
  machine-verified here and is left to the reviewer's manual pass above; the
  HTTP tests pin everything reachable without a browser.

### 2026-11-16 — independent review: PASS

Reviewed in a separate clean clone at `eceb019`, with real HTTP requests and a real browser rather
than a reading of the tests. All four criteria hold, and the Background's factual claims were
re-derived rather than taken on trust.

- The trigger was captured from real rendered HTML: an icon-only ghost button with
  `aria-label="Customize theme"`, adjacent to the user menu, and `BbDarkModeToggle` is gone from the
  source (the old `Switch to dark/light mode` labels appear zero times).
- Both assets are genuinely served and genuinely needed: `themes.css` answers 200 with 22,668 bytes
  and carries the five base-gray and seventeen primary palettes, while the served
  `blazorblueprint.css` (166,990 bytes) contains zero `data-base-color` / `data-primary-color`
  definitions — so the Background's "zero hits" claim is true, and the pre-card `App.razor` loaded
  neither asset.
- Load order is real: `blazorblueprint.css` < `css/themes.css` < `app.css`, and
  `theme-persistence.js` < `theme-init.js` < `</head>`, both scripts blocking. Real Chromium
  computed styles confirm the accent changes the UI (`--primary` resolves and `.bb:bg-primary`
  computes `oklch(0.541 0.281 293.009)`, switching under `class="dark"`).
- The cookie contract is intact: real requests give `class="dark"` + `data-default-dark="true"` for
  `dark`, `false` for no cookie and for `light`, and `false` for the non-canonical `Dark` — ordinal
  matching, as the card says. Executing the two served scripts in order against a fake DOM showed a
  dark cookie survives both a throwing `localStorage` and an OS light preference.
- One high-severity finding, **pre-existing and not a regression**: with no base color selected the
  base token palette is undefined, so dark/light has no visual effect for most users. It is filed as
  B-007; the weaker test assertions it exposed are Q-013.
- The full fast suite's only failure on the accepted tree was
  `ArtifactStreamingTests.A_directory_download_is_zipped_on_the_fly_and_writes_no_temp_archive`,
  reproduced on a clean `eceb019` worktree (1 green / 2 red) — unrelated to this card and filed as
  Q-014.

**Verdict: PASS.** Status → done; board row moved to Done (2026-11-16).
