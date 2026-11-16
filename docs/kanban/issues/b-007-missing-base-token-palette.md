---
id: B-007
title: The app never defines the base token palette — dark/light changes nothing until a base color is picked
type: bug
priority: P1
size: M
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A signed-in user who never opens the theme popover still gets a themed UI: the base token set is
defined on `:root` and `.dark`, so the dark class and every `--primary`-driven surface actually
change.

## Background

Found by D-007's independent review (2026-11-16) in a real Chromium against the real stylesheets, in
the App.razor order. With no `data-base-color` / `data-primary-color` attribute — which is the state
of every user who never touches the switcher — `--background`, `--foreground`, `--primary`,
`--muted`, `--border` and `--destructive` are all **undefined**:

- `--primary` resolves to the empty string, so a `bb:bg-primary` element (the primary button, the
  logo square) computes `background-color: rgba(0, 0, 0, 0)` — transparent.
- `body` has no background; `class="dark"` only flips `color-scheme`, so dark mode changes no color.

The palette stylesheet D-007 loads (`themes.css`) only defines tokens *under* the data attributes,
and `blazorblueprint.css` defines none at all (the Background's "zero hits" claim was verified). The
app's own `styles/theme.css:15-19` never supplied the shadcn base set, and `app.css:21`'s comment
claiming `--destructive` "comes from the BlazorBlueprint token set" is wrong.

This predates D-007 (it is a D-001-era gap), so it is not a regression — but it caps the value of
every theme choice the switcher offers, which is why it is filed as P1 rather than a nit.

## Acceptance Criteria

- [x] `styles/theme.css` defines the full base token set on `:root` and `.dark`: background,
      foreground, card, popover, primary, primary-foreground, secondary, muted, muted-foreground,
      accent, accent-foreground, destructive, border, input, ring and the sidebar group.
- [x] The values match the library's own defaults (zinc base + default primary), so a user who
      picks nothing sees the same rendering as one who picks zinc — not a third, accidental theme.
- [x] Default dark mode stays off (`ThemeOptions.DefaultDarkMode = false`) and the default radius
      stays `0.5rem`, so the token set does not silently change the app's current look beyond
      making it coherent.
- [x] A real-CSS test pins it: with no data attributes, `--primary` resolves non-empty and a
      `bb:bg-primary` element computes a non-transparent background, in both light and dark.
- [x] `app.css:21`'s comment is corrected or removed.

## Subtasks

- [x] Take the base set from the package README's "Theming" section (or copy zinc + default
      from `themes.css`) and land it as `:root` / `.dark` blocks in `styles/theme.css`.
- [x] Add the computed-style test next to the Category=Theme shell tests.
- [x] Cross-check the `.dark` block against `themes.css`'s `.dark[data-base-color=…]` shape so both
      paths agree.

## Dependencies

- none (D-007 is accepted; this is the D-001-era gap it exposed)

## Test Cases

### TC-001: The defaults are themed, not transparent

Steps:
1. Load the shell with no theme popover interaction and no data attributes.
2. Compute the styles of `body` and of a `bb:bg-primary` element; repeat with `class="dark"`.

Expected:
- `--primary`, `--background`, `--border` all resolve to real colors; the primary element's
  `background-color` is not transparent; the dark pass differs from the light pass.

## Development Log

### 2026-11-16

- **The tokens.** `src/Server/wwwroot/styles/theme.css` now carries the whole base set on `:root`
  and `.dark`, copied from the package's own `[data-base-color="zinc"]` pair in
  `_content/BlazorBlueprint.Components/css/themes.css` — which is exactly what the package README's
  "Theming" section tells an app to put in `wwwroot/styles/theme.css`, and the sheet order
  `App.razor` already had (`styles/theme.css` first, then the package CSS). Zinc with no
  `data-primary-color` *is* the library's default primary: `[data-base-color="zinc"]` defines
  `--primary` / `--primary-foreground` / `--ring` itself, and the palette sheet says so in a comment
  ("the 'default' primary inherits from the base color"). So "picks nothing" and "picks zinc" are the
  same rendering by construction rather than by a second, hand-tuned palette.
- **Why the cascade still works.** `theme.css` is unlayered and loads first; the palette sheet loads
  later and its attribute rules are more specific (`.dark[data-base-color=…]` is two classes,
  `.dark` / `:root` are one). The `.dark` block is deliberately the bare `.dark`, not `html.dark`,
  so this file never becomes the most specific rule on the page and a picked base or accent keeps
  winning — the file's header comment states the rule, and a test now pins it.
- **The comment.** `app.css` claimed `--destructive` "comes from the BlazorBlueprint token set". That
  was false in both directions (`blazorblueprint.css` defines no color token at all — zero
  `--background:`/`--primary:`/`--border:` hits — and the palette sheet defines none without an
  attribute), so it now points at `styles/theme.css` and says why.
- **The test.** `tests/Zenkins.Tests/Theme/BaseTokenPaletteTests.cs` plus
  `Theme/ServedThemeCascade.cs`. The test fetches the stylesheets the served page's own `<head>`
  links, in that order, and resolves the cascade for a document with no `data-*` attributes: the
  winning declaration per custom property (specificity, then document order — the browser's rule for
  these selectors), one level of `var()` substitution, and an alpha check. It then asserts the three
  things the Background measured, in both modes: every base token resolves to a real color,
  `body`'s `background-color: var(--background)` is not transparent, and `.bb\:bg-primary`'s
  `background-color: var(--primary)` is not transparent and differs between the passes.
  - A real headless Chromium was the alternative and was rejected on cost, not on rigour: the gate is
    `dotnet test` with no Node.js and no browser download (docs/development.md §1). The probe fails
    closed — an unmodelled selector answers "no match", an unknown color syntax answers "not a
    color" — so a stylesheet it cannot read cannot pass it.
  - Two sibling facts use the same probe to pin this card's second criterion: the default state's
    tokens equal the library's own `data-base-color="zinc"` run, token for token in both directions,
    and `data-primary-color="violet"` still overrides `--primary` while leaving `--background` alone.
    Together they are the "not a third theme" guarantee, and they fail if this file ever wins the
    cascade over the palette sheet.
- **Radius and dark mode are untouched.** `ThemeOptions` / `Program.cs` were not edited, so
  `DefaultDarkMode` stays off and `detectSystemPreference` stays `false`; the new `:root` block keeps
  the existing `--radius: 0.5rem` line untouched and the test asserts it.

## Verification

- `dotnet build Zenkins.slnx -c Release` — clean, 0 warnings / 0 errors.
- `scripts/verify.sh`, all seven suites green on 2026-11-16: fast **1241**, Milestone 12, Recovery 4,
  PublishSmoke 2, PublishAot 2, GitSshd 3, Postgres 3 (**3 skipped — no `ZENKINS_TEST_POSTGRES`**, by
  design), **all seven 1267**. Logs: `artifacts/logs/b007-q013-fast.log`,
  `artifacts/logs/b007-q013-<Suite>.log`, `artifacts/logs/b007-q013-verify.log`.
- Mutation evidence that the new facts bite (both reverted immediately):
  - `styles/theme.css` back to HEAD (no token block) — all three `BaseTokenPaletteTests` facts go red
    with `light: --background is undefined with no data attributes`.
  - `theme-init.js` moved out of `<head>` into `<body>` — the shell test's ordering fact goes red with
    `'…/js/theme-init.js' is referenced after </head>`.
- What it cannot see: a browser's actual paint (fonts, compositing, `color-mix` support), and
  `@layer` precedence — the latter is argued in the probe's doc comment (every sheet declaring these
  tokens is unlayered) rather than measured.

## Review

2026-11-16 — independent review: **PASS**, five of five, with the reviewer cross-checking the home-made
cascade model against real Chromium rather than trusting it.

The token set is the library's own: parsed directly from both files, `:root` and `.dark` each carry 32
tokens and every one matches `themes.css`'s `[data-base-color="zinc"]` pair in both directions, so
"picked nothing" and "picked zinc" are the same palette by construction, not a third one. In a real
browser the rendered output of those two states is byte-identical in light and in dark — which is the
claim that actually matters and the one the model alone could not have made.

Default behaviour is untouched: `Program.cs`'s `DetectSystemPreference = false` is still there,
`ThemeOptions` and `MainLayout` are not in the diff, `--radius` is still `0.5rem`, and reverting
`theme.css` to HEAD turns all three palette facts red with `light: --background is undefined with no
data attributes` — so the facts really are load-bearing.

The reviewer's browser pass also settled the cascade question the card's reasoning rests on: with
`data-base-color="slate"` (a palette whose values differ from zinc) the palette wins in every
combination, including dark + base colour, and a lone `data-primary-color` overrides `--primary`
without moving `--background`.

One caveat carried to Q-021: the resolver models element selectors as zero specificity and ignores
`@layer`, so it is trustworthy for these four values and not as a general oracle.
