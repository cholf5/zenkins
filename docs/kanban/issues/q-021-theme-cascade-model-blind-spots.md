---
id: Q-021
title: The theme cascade model counts element selectors as zero and ignores @layer, so it can pass what a browser rejects
type: test
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

`ServedThemeCascade` either models the cascade correctly for the sheets it reads, or refuses to answer
for anything it does not model — it must not silently prefer a declaration a browser would reject.

## Background

B-007 needed to prove that the default token set resolves without a browser in the gate, so it wrote a
small cascade resolver in the test project. B-007/Q-013's review cross-checked it against real Chromium
and cleared it **for the facts it currently asserts** (four values match exactly), but found three
modelling gaps that all err toward false green:

- **Element selectors contribute 0 specificity** (`ServedThemeCascade.cs:315-317`): it counts classes,
  `:root` and `[`, so `html:root` (really 0,1,1) ties with `[data-base-color=…]` (0,1,0) and the model
  says the palette wins where a browser says `app.css` wins. This is exactly the dimension the
  "`.dark` rather than `html.dark`" decision rests on — that case happens to agree, but by luck.
- **`@layer` is ignored** (`:16-18`): the comment claims every sheet declaring these tokens is
  unlayered, which is false — `blazorblueprint.css` puts `body { background-color: var(--background) }`
  and `:root { --sidebar-width }` inside `@layer base`. The model already computes
  `--sidebar-width: 16rem` where Chromium computes `15rem`.
- **`@media` and `@supports` apply unconditionally** (`:373-377`): a `prefers-color-scheme: dark`
  block is taken even in light mode. `!important` is also not modelled — it stays in the value
  (`:203-260`, `:419-440`) and a colour check still accepts it.

## Acceptance Criteria

- [ ] Element and pseudo-element selectors count toward specificity (at least `html` → one element).
- [ ] `@layer` order is modelled, or a layered declaration is refused rather than treated as
      unlayered.
- [ ] An unrecognised `@media`/`@supports` condition, and any `!important` it does not model, cause the
      resolver to answer "unknown" (and the fact to fail closed) instead of choosing a winner.
- [ ] A fact pins the `html:root` vs `[data-…]` case against a browser-verified expectation, so the
      resolver cannot regress into agreeing with itself.
- [ ] B-007's comment "every sheet declaring these tokens is unlayered" is corrected either way.

## Subtasks

- [ ] Fix specificity counting.
- [ ] Model or refuse layers, media queries and `!important`.
- [ ] Add the cross-check fact.

## Dependencies

- none (B-007 and Q-013 are accepted; their current facts are correct today)

## Notes and Decisions

- The review's own verdict on the resolver is the right standard to keep: trustworthy for the handful
  of values B-007 asserts, not trustworthy as a general cascade oracle. This card closes that gap
  before someone adds a fact that leans on it.
- Keep the browser cross-check out of the gate: it needs Playwright, which the gate does not have. The
  card's job is to make the model fail closed where it is blind, and to pin one browser-verified case.
