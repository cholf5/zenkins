---
id: Q-013
title: The theme shell tests do not pin the load order, the trigger wiring or the cookie's negative cases
type: test
priority: P2
size: S
status: todo
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

- [ ] The head wiring is asserted positionally:
      `index("blazorblueprint.css") < index("css/themes.css") < index("app.css")` and
      `index("js/theme-persistence.js") < index("theme-init.js") < index("</head>")`.
- [ ] The served bodies are asserted, not just their status: `themes.css` contains
      `[data-primary-color="violet"]` and `[data-base-color="zinc"]`; `theme-init.js` contains its
      `data-default-dark` branch.
- [ ] The trigger's own start tag is extracted before asserting `aria-haspopup="true"` and the
      ghost square classes (`bb:h-9 bb:w-9 bb:p-0`) — today the assertion looks back 600 characters
      from the label, and `aria-label="Customize theme"` is the package's own default, so dropping
      the wiring would still pass.
- [ ] Icon-only is asserted as "no text node inside the trigger", not as "the old label is absent".
- [ ] The cookie negatives are covered: `zenkins-theme=light` renders no dark class and
      `data-default-dark="false"`; `zenkins-theme=Dark` (wrong case) is treated as no preference.

## Subtasks

- [ ] Tighten `ThemeShellTests` as above; keep them HTTP tests through the real host.
- [ ] Re-record the suite's counts in `docs/development.md` §2 if the fact count moves.

## Dependencies

- none (D-007 is accepted)

## Notes and Decisions

- Two adjacent low-severity items from the same review live on D-007's Bugs table rather than here:
  the 4px mismatch between the theme trigger (36px) and the user-menu button (40px), and the card
  Background's over-claim that the bridge's duplicate attribute logic was retired (it was not —
  `theme-persistence.js` still parses the class and owns the OS fallback).
