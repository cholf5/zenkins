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
- [x] Light and dark themes both render legibly; the choice persists across reloads, with no light→dark flip after hydration for an OS-dark user on a first visit.
- [x] All chrome from BlazorBlueprint components (or its layout primitives) — no hand-rolled where a component exists.
- [x] UI CSS is shipped pre-built by `BlazorBlueprint.Components` as a static web asset; running the app requires no Node.js and no committed generated stylesheet.
- [x] No CSS regeneration command is needed: `dotnet restore` / `dotnet build` refresh the package's static web assets. Project theme overrides live in `wwwroot/styles/theme.css`.
- [x] Status pages never rewrite a non-navigation response: asset 404s and future 403/500 responses keep their real status and an empty body.

## Subtasks

- [x] Add `BlazorBlueprint.Components` 4.2.0 (version in `Directory.Packages.props`). `Primitives` 4.2.0 and `Icons.Lucide` 2.0.2 are pinned too because the shell uses `BbPortalHost` and `LucideIcon` directly.
- [x] Link the package's pre-built CSS and put project theme overrides in `wwwroot/styles/theme.css`, loaded before it. No Tailwind entry point or generated `app.css`.
- [x] Build `MainLayout` + `NavMenu` + Organization-switcher placeholder + user-menu placeholder.
- [x] Add theme toggle with persistence.
- [x] Add placeholder pages for Dashboard / Projects / Workers / Secrets so nav is not dead.
- [x] Record the visual-direction decision (shadcn defaults vs. custom density) in the Development Log.
- [x] Scope `UseStatusCodePagesWithReExecute` to 404 HTML navigations only.

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

### TC-005: First visit of an OS-dark user has no flip

Steps:
1. Fresh browser profile, no `zenkins-theme` cookie, no `bb-theme` entry, OS in dark mode.
2. Open `/`.

Expected:
- `<html class="dark">` before the first paint; `zenkins-theme=dark` written; `bb-theme` seeded with `isDarkMode: true`; no light flash after hydration.
- With no `matchMedia` available the page resolves to light instead of staying undecided.

### TC-006: Status pages only for HTML navigations

Steps:
1. `curl -H 'Accept: text/html' http://127.0.0.1:5099/missing-route`
2. `curl http://127.0.0.1:5099/styles/missing.css`

Expected:
- (1) `404` with the "Page not found" page rendered inside the shell.
- (2) `404`, zero bytes, no `text/html` content type.

## Development Log

### 2026-11-12

**Decision 1 — shadcn defaults, not BlueprintJS density.** BlazorBlueprint v4 is shadcn/Tailwind-themed, not a BlueprintJS clone. Zenkins takes the shadcn defaults. PRD §4.1's "developer-tool visual style" is satisfied by a clean, compact, information-dense-enough layout (sidebar + top bar + independently scrolling content) rather than replicating BlueprintJS pixel density. If BlueprintJS density becomes a hard requirement, that is a different component library and should be a separate decision, not a theme override.

**Decision 2 — no Tailwind build step exists at all.** The package ships compiled CSS (`_content/BlazorBlueprint.Components/blazorblueprint.css`) as a static web asset. There is no Tailwind CLI, no `package.json`, no Node.js, and nothing in this repo to regenerate. This is stronger than the card's "commit the compiled CSS" fallback. Project theme tokens live in `wwwroot/styles/theme.css` and load before the package CSS so they can override its defaults. Regeneration story: `dotnet restore` / `dotnet build` refresh the package's static web assets; there is no Node or Tailwind CLI to run.

**Shell.** `MainLayout` uses `BbSidebarProvider` / `BbSidebar` / `BbSidebarInset` / `BbSidebarRail` with `BbAppBar`, `BbScrollArea` (`FillContainer`, independent scrolling), `BbDropdownMenu`, `BbAvatar`, `BbSeparator`, `BbButton` and `BbDarkModeToggle`. `NavMenu` uses `BbSidebarMenuButton` links with Lucide icons. The sidebar collapses to an icon rail on desktop and becomes a `BbSheet` drawer below the package's `md` breakpoint; `BbSidebarTrigger` is in the top bar. Organization and user menus are explicit disabled placeholders pending F-001.

**Rendering and theme (revised after review).** I-001 shipped no `@rendermode`; `Routes` and `HeadOutlet` are now `InteractiveServer`. The theme cookie (`zenkins-theme`) is written by `wwwroot/js/theme-persistence.js`, which also mirrors the cookie into BlazorBlueprint's `bb-theme` localStorage entry. The server reads the cookie in `App.razor` and emits `<html class="dark">` on the first response, so a persisted dark theme has no flash. The package's own `BbDarkModeToggle` + `ThemeService` remain the toggle; the cookie bridge is the only app-specific persistence code.

*Review-fix round (same day).* The first review found that first paint was still wrong for OS-dark users with no cookie: `AddBlazorBlueprintComponents()` leaves `DetectSystemPreference = true`, so `theme.js` resolves dark from `prefers-color-scheme` while `App.razor` renders light from the missing cookie, and `theme-persistence.js` did nothing when neither store existed — a whole-page flip after hydration. The bridge now resolves the OS preference itself when neither store exists, then sets the class, writes the cookie and seeds `bb-theme`, so DOM, cookie and `ThemeService` agree from the first paint. The same run now covers a dropped-cookie browser (localStorage present) and falls back to a deterministic light when `matchMedia` is missing or throws. The cookie name is no longer a JS literal: `App.razor` emits `ThemePreference.CookieName` as `data-theme-cookie`.

**Error/404 (revised after review).** Added a `Router.NotFound` view plus `/not-found` page, a `Routes` `ErrorBoundary`, and `/error` for non-Development via `UseExceptionHandler`. The unwrapped `UseStatusCodePagesWithReExecute("/not-found")` re-executed for *every* 4xx/5xx before this round: a future 403 answered with "page not found" copy and a mistyped asset URL answered `200 text/html`. `Program.cs` now keeps the framework re-execute (it is the only registration that re-runs routing correctly here) but disables it per request via `IStatusCodePagesFeature` unless the response is a `404` on an HTML navigation — GET/HEAD, no file extension, not under `/_content`, and `Accept` contains `text/html`. Everything else keeps its status and empty body; `/not-found` requested directly is still `200`.

**CSS layering (revised after review).** `wwwroot/app.css` is now document-level only (`html`/`body` height, `color-scheme`). It no longer sets `body { font-family: … }` — that was unlayered, so it beat the package's `body { font-family: var(--font-sans) }` and killed the `data-bb-font` theme fonts — and it no longer applies a global `a:focus-visible, button:focus-visible` outline, which double-rang every package control. The two remaining hand-rolled anchors in `MainLayout` carry the package's own `bb:focus-visible:ring-*` classes instead. `styles/theme.css` dropped its `--radius` rule (`ThemeService` writes `--radius` inline on `<html>`, so the rule could never win) and its `:root:root` doubling (the package's rules sit in cascade layers, so unlayered overrides win on order-independent specificity).

**Headings and chrome (revised after review).** Every page now has exactly one `h1` (the page title, `tabindex="-1"` so `FocusOnNavigate` can move focus to it) and in-card titles render `h2` instead of the package default `h3`, so the outline no longer skips a level. The `Routes` error card no longer claims "Your data has not been changed." Avatar fallbacks show a Lucide icon (`building-2`, `user`) instead of invented initials `AC`/`ZK` in "No organization"/"Not signed in" states.

**No database.** No component injects `ZenkinsDbContext` or `IDbContextFactory`; pages do not touch tenant context because no organization is selected yet.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| B1 | blocking | First paint light→dark flip for OS-dark users with no cookie; permanently wrong when cookies are dropped | Fixed | Pre-paint bridge resolves OS preference, sets class, writes cookie, seeds `bb-theme`; deterministic light fallback |
| B2 | blocking | `UseStatusCodePagesWithReExecute` re-executed for every 4xx/5xx, hiding real statuses and serving HTML for asset URLs | Fixed | Re-execute disabled per request unless 404 + HTML navigation (GET/HEAD, no extension, not `/_content`, `Accept: text/html`) |
| B3 | blocking | Unlayered `app.css` font-family and focus outline fought the package theme and doubled focus rings | Fixed | `app.css` reduced to document-level rules; hand-rolled anchors use the package's `bb:focus-visible:ring-*` classes |
| B4 | blocking | Card verification was not reproducible from a clone | Fixed | Runnable one-port command block + Node harness for the pre-paint branch in Verification; full logs under `/tmp/orca-logs/D-001/` |

## Verification

- Related files: `src/Server/Components/**`, `src/Server/wwwroot/**`, `src/Server/Program.cs`
- How to run/verify: the command block below. Full output: `/tmp/orca-logs/D-001/` (`build.log`, `test.log`, `smoke.log`, `smoke.sh`, `theme-prepaint.log`). Nothing under `/tmp` is committed.

### Reproducible gate (one port, 5099)

```bash
# 1. build + tests
dotnet build -c Release     # expect: 0 Warning(s), 0 Error(s)
dotnet test                 # expect: total 15, failed 0, skipped 0

# 2. run the app
ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet run --project src/Server -c Release --no-launch-profile &
for i in $(seq 1 30); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5099/)" = 200 ] && break; sleep 1; done
B=http://127.0.0.1:5099
HTML='Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8'

# 3. navigation smoke: every page 200
for p in / /projects /workers /secrets; do
  printf '%-10s %s\n' "$p" "$(curl -s -o /dev/null -w '%{http_code}' -H "$HTML" "$B$p")"
done

# 4. unknown HTML URL -> 404 page inside the shell
curl -s -H "$HTML" -o /tmp/d001-not-found.html -w 'status=%{http_code}\n' "$B/missing-route"
grep -c 'Page not found' /tmp/d001-not-found.html          # 2: <title> and <h1>
grep -c 'data-sidebar="header"' /tmp/d001-not-found.html   # 1: rendered inside MainLayout

# 5. everything else keeps its real status and an empty body
curl -s -o /dev/null -w 'accept-star   %{http_code} bytes=%{size_download}\n' "$B/missing-route"
curl -s -o /dev/null -w 'missing css   %{http_code} bytes=%{size_download}\n' -H 'Accept: text/css,*/*;q=0.1' "$B/styles/missing.css"
curl -s -o /dev/null -w 'missing asset %{http_code} bytes=%{size_download}\n' -H "$HTML" "$B/_content/BlazorBlueprint.Components/nope.js"

# 6. dark cookie vs no cookie, server-rendered <html>
curl -s -H 'Cookie: zenkins-theme=dark' "$B/" | grep -o '<html[^>]*>'
curl -s "$B/" | grep -o '<html[^>]*>'

kill %1
```

Observed (2026-11-12, `smoke.log`):

```
PASS   nav /                    status=200   PASS   nav /projects  status=200
PASS   nav /workers             status=200   PASS   nav /secrets   status=200
PASS   direct /not-found        status=200
PASS   unknown html url         status=404 bytes=21327   "Page not found": 2   shell sidebar: 1
PASS   unknown url, Accept */*  status=404 bytes=0
PASS   missing asset            status=404 bytes=0
PASS   missing package asset    status=404 bytes=0
PASS   unknown path w/ extension status=404 bytes=0
PASS   POST to unknown path     status=405 bytes=0
       no cookie  : <html lang="en">
       dark cookie: <html lang="en" class="dark">
       <script src="js/theme-persistence.js" data-theme-cookie="zenkins-theme">
SMOKE: PASS
```

Additional one-off probe for the "no status rewriting" rule (temporary `/__probe/{forbidden,failure}` endpoints, added for the check and removed before the commit, `build-probe403.log`): `GET /__probe/forbidden` with `Accept: text/html` → `403`, 0 bytes; `GET /__probe/failure` → `500`, 0 bytes. Neither body contained "Page not found".

Rendered markup pinned (from `curl -H 'Accept: text/html' /`):

```
<h1 class="bb:text-2xl bb:font-semibold bb:leading-none bb:tracking-tight" tabindex="-1">Dashboard
<h2 class="bb:text-2xl bb:font-semibold bb:leading-none bb:tracking-tight">Build activity
<div class="bb:flex bb:w-full bb:h-full"                       <- BbSidebarProvider (HeightClass="bb:h-full")
aria-label="Toggle navigation"                                 <- BbSidebarTrigger
aria-label="Switch to dark mode"                               <- BbDarkModeToggle (package default, tracks state)
aria-label="Open user menu"                                    <- user menu trigger
```

Exactly one `h1` per page (checked on `/`, `/projects`); `/styles/theme.css` serves no `--radius`; `/app.css` serves no `font-family` and no `:focus-visible` rule.

### Pre-paint theme proof (no browser)

The OS-dark branch lives in `theme-persistence.js`, so it is proven by executing that real file in a stubbed DOM. Node is needed **only** for this harness — Zenkins itself still requires no Node (TC-003).

```bash
cat > /tmp/theme-prepaint-harness.mjs <<'JS'
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const source = readFileSync(process.argv[2], 'utf8');

function run({ cookie = null, stored = null, mediaDark = false, matchMedia = 'ok', themeAttr = 'zenkins-theme' }) {
  const jar = new Map(); if (cookie) jar.set('zenkins-theme', cookie);
  const store = new Map(); if (stored) store.set('bb-theme', stored);
  const classes = new Set();
  const document = {
    currentScript: { getAttribute: (n) => (n === 'data-theme-cookie' ? themeAttr : null) },
    documentElement: { classList: {
      toggle(n, f) { if (f) classes.add(n); else classes.delete(n); },
      contains: (n) => classes.has(n) } },
    get cookie() { return [...jar].map(([k, v]) => `${k}=${v}`).join('; '); },
    set cookie(v) { const p = v.split(';')[0], i = p.indexOf('='); jar.set(p.slice(0, i), p.slice(i + 1)); },
  };
  const window = {};
  if (matchMedia === 'ok') window.matchMedia = () => ({ matches: mediaDark });
  if (matchMedia === 'throws') window.matchMedia = () => { throw new Error('nope'); };
  const sandbox = { document, window,
    localStorage: { getItem: (k) => (store.has(k) ? store.get(k) : null), setItem: (k, v) => { store.set(k, v); } },
    MutationObserver: class { observe() {} } };
  vm.createContext(sandbox); vm.runInContext(source, sandbox);
  const raw = store.get('bb-theme');
  return { isDark: classes.has('dark'), cookie: jar.get('zenkins-theme') ?? null,
           stored: raw === undefined ? null : JSON.parse(raw) };
}

const cases = [
  ['OS dark, no stores',        { mediaDark: true },                       { d: true,  c: 'dark',  s: true }],
  ['OS light, no stores',       { mediaDark: false },                      { d: false, c: 'light', s: false }],
  ['matchMedia throws',         { matchMedia: 'throws' },                  { d: false, c: 'light', s: false }],
  ['matchMedia absent',         { matchMedia: 'absent' },                  { d: false, c: 'light', s: false }],
  ['cookie dark beats OS light',{ cookie: 'dark', mediaDark: false },      { d: true,  c: 'dark',  s: true }],
  ['cookie light beats OS dark',{ cookie: 'light', mediaDark: true },      { d: false, c: 'light', s: false }],
  ['legacy bb-theme adopted',   { stored: JSON.stringify({ isDarkMode: true, baseColor: 'zinc' }), mediaDark: false }, { d: true, c: 'dark', s: true }],
  ['attribute missing = inert', { themeAttr: null, mediaDark: true },      { d: false, c: null,    s: null }],
];

let failed = 0;
for (const [name, input, want] of cases) {
  const got = run(input);
  const dark = got.stored === null ? null : got.stored.isDarkMode;
  const ok = got.isDark === want.d && got.cookie === want.c && dark === want.s;
  if (!ok) failed++;
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name.padEnd(28)} class.dark=${got.isDark} cookie=${got.cookie} bb-theme.isDarkMode=${dark}`);
}
console.log(failed === 0 ? `ALL ${cases.length} CASES PASS` : `${failed} FAILED`);
process.exitCode = failed === 0 ? 0 : 1;
JS

node /tmp/theme-prepaint-harness.mjs src/Server/wwwroot/js/theme-persistence.js
```

Observed (`theme-prepaint.log`): `ALL 8 CASES PASS`, including `OS dark, no stores -> class.dark=true, cookie=dark, bb-theme.isDarkMode=true` and both `matchMedia` failure modes resolving to light. Together with the served markup above (`data-theme-cookie` emitted, `<html>` class from the cookie), this covers the whole path: server renders light for a cookie-less request, and the blocking head script turns DOM, cookie and `bb-theme` dark before any paint.

### Known follow-ups (recorded, deliberately not implemented)

- `theme-persistence.js` duplicates the contract the package already ships in `_content/BlazorBlueprint.Components/js/theme-init.js` (`bb-theme` key, `isDarkMode` flag, `.dark` on `<html>`). Zenkins needs a *server-readable* store as well, so a bridge is unavoidable, but the duplication should be revisited if the package exposes a cookie or header hook.
- No reconnection UI: I-001 deleted `blazor-error-ui` / `ReconnectModal`, so a dropped circuit shows a frozen shell. The package ships `bb-reconnect-*` styles; wiring them is a separate card.
- `BlazorBlueprint.Icons.Lucide` is pinned at `2.0.2` while the comment in `Directory.Packages.props` mentions a `2.0.0` floor; reconcile the comment when the icon package next moves.

**Not automatically covered by curl:** the click path through `BbDarkModeToggle` (it needs a Blazor circuit) and the mobile `BbSheet` drawer after JS sets `IsMobile`. The cookie bridge, the seeded `bb-theme` and the server-rendered class are verified; the toggle delegates to the package's own `ThemeService`.
