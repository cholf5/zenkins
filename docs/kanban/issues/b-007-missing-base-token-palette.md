---
id: B-007
title: The app never defines the base token palette — dark/light changes nothing until a base color is picked
type: bug
priority: P1
size: M
status: review
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
