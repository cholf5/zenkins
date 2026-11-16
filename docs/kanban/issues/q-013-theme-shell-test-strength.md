---
id: Q-013
title: The theme shell tests do not pin the load order, the trigger wiring or the cookie's negative cases
type: test
priority: P2
size: S
status: review
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

`Category=Theme` fails when the thing it claims to protect breaks: a script moved out of `<head>`, an
empty palette stylesheet, a dropped `AdditionalAttributes`, or a cookie that is not exactly
`dark`/`light`.

## Background

D-007's independent review read the three shell tests against their ACs and found the assertions are
weaker than the card says. Each gap was demonstrated, not guessed: moving `theme-init.js` to the end
of `<body>` (the flash D-007 exists to prevent) leaves the suite green, and so does emptying
`themes.css` — the test only checks that the two URLs answer 200.

## Acceptance Criteria

- [x] The head wiring is asserted positionally:
      `index("blazorblueprint.css") < index("css/themes.css") < index("app.css")` and
      `index("js/theme-persistence.js") < index("theme-init.js") < index("</head>")`.
- [x] The served bodies are asserted, not just their status: `themes.css` contains
      `[data-primary-color="violet"]` and `[data-base-color="zinc"]`; `theme-init.js` contains its
      `data-default-dark` branch.
- [x] The trigger's own start tag is extracted before asserting `aria-haspopup="true"` and the
      ghost square classes (`bb:h-9 bb:w-9 bb:p-0`) — today the assertion looks back 600 characters
      from the label, and `aria-label="Customize theme"` is the package's own default, so dropping
      the wiring would still pass.
- [x] Icon-only is asserted as "no text node inside the trigger", not as "the old label is absent".
- [x] The cookie negatives are covered: `zenkins-theme=light` renders no dark class and
      `data-default-dark="false"`; `zenkins-theme=Dark` (wrong case) is treated as no preference.

## Subtasks

- [x] Tighten `ThemeShellTests` as above; keep them HTTP tests through the real host.
- [x] Re-record the suite's counts in `docs/development.md` §2 if the fact count moves.

## Dependencies

- none (D-007 is accepted)

## Notes and Decisions

- Two adjacent low-severity items from the same review live on D-007's Bugs table rather than here:
  the 4px mismatch between the theme trigger (36px) and the user-menu button (40px), and the card
  Background's over-claim that the bridge's duplicate attribute logic was retired (it was not —
  `theme-persistence.js` still parses the class and owns the OS fallback).

## Development Log

### 2026-11-16

`tests/Zenkins.Tests/Theme/ThemeShellTests.cs` rewritten in place — still three HTTP facts through the
real host, still `Category=Theme`, but each one now fails on the regression it names.

- **Ordering (AC1).** One fact walks the served page: `IndexInHead` finds each asset and fails if it is
  not inside `<head>` at all, then the positions are compared
  (`styles/theme.css` < `blazorblueprint.css` < `css/themes.css` < `app.css`, and
  `js/theme-persistence.js` < `theme-init.js` < `</head>`). The `index < headEnd` half is what the
  old test lacked: a script moved to the end of `<body>` keeps its relative order, so an
  order-only comparison would still pass — verified by actually moving it (`is referenced after
  </head>`).
- **Bodies (AC2).** The palette response must contain `[data-base-color="zinc"]` and
  `[data-primary-color="violet"]`; `theme-init.js` must contain `getAttribute('data-default-dark')`
  and both of its branch comparisons (`defaultDark === 'true'` / `=== 'false'`). A 200 with an empty
  body has none of those strings, so the status-only version's hole is closed.
- **Trigger (AC3, AC4).** `StartTagAround` scans back to the element's own `<` and forward to its
  `>` with quote awareness (so an attribute value containing `>` cannot end the tag early); the tag
  must start with `<button`, carry `aria-haspopup="true"` and `bb:h-9 bb:w-9 bb:p-0` — i.e. the
  `AdditionalAttributes` and `TriggerClass` from `MainLayout` are on the trigger itself, not on a
  wrapper 600 characters away. `ElementContent` then matches the element's own tag name to its close
  tag and `StripTags` reduces the inner HTML to its text nodes, which must be whitespace: the
  positive form of "icon-only", not "the previous label is gone".
- **Cookie negatives (AC5).** One fact drives four clients through `/account/login` (static SSR, no
  session needed): `dark` → dark class + `data-default-dark="true"`; no cookie → light + `"false"`;
  `light` → light + `"false"`; `Dark` (wrong case) → light + `"false"`, which is `ThemePreference`'s
  `Ordinal` contract and the bridge's canonical-value rule together.
- The two low-severity items from the same review (the 36px/40px trigger mismatch, the bridge's
  duplicate attribute logic) stay on D-007's Bugs table as filed; nothing here touches them.
- Counts: `ThemeShellTests` is still 3 facts. The batch's `+3` comes from B-007's new
  `BaseTokenPaletteTests`; `docs/development.md` §2 now reads fast **1241** / all seven **1267**.

## Verification

- `dotnet build Zenkins.slnx -c Release` — clean, 0 warnings / 0 errors.
- `scripts/verify.sh`, all seven suites green on 2026-11-16: fast **1241**, Milestone 12, Recovery 4,
  PublishSmoke 2, PublishAot 2, GitSshd 3, Postgres 3 (**3 skipped — no `ZENKINS_TEST_POSTGRES`**, by
  design), **all seven 1267**. Logs: `artifacts/logs/b007-q013-fast.log`,
  `artifacts/logs/b007-q013-<Suite>.log`.
- Each tightened assertion was checked against the mutation it is meant to catch where a mutation was
  cheap: moving `theme-init.js` into `<body>` turns the ordering fact red (reverted). The body and
  trigger assertions are plain `Contains`/tag-scan reads of the served bytes, so an empty stylesheet
  or a trigger without the wiring fails by construction rather than by status code.
- Not covered: nothing was asserted about the trigger's rendered geometry (the 4px mismatch above) or
  about `theme-persistence.js`'s own behaviour — both stay out of this card's scope.
