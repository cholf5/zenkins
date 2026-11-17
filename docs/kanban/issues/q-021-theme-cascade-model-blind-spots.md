---
id: Q-021
title: The theme cascade model counts element selectors as zero and ignores @layer, so it can pass what a browser rejects
type: test
priority: P2
size: S
status: done
created: 2026-11-16
updated: 2026-11-17
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

- [x] Element and pseudo-element selectors count toward specificity (at least `html` → one element).
- [x] `@layer` order is modelled, or a layered declaration is refused rather than treated as
      unlayered.
- [x] An unrecognised `@media`/`@supports` condition, and any `!important` it does not model, cause the
      resolver to answer "unknown" (and the fact to fail closed) instead of choosing a winner.
- [x] A fact pins the `html:root` vs `[data-…]` case against a browser-verified expectation, so the
      resolver cannot regress into agreeing with itself.
- [x] B-007's comment "every sheet declaring these tokens is unlayered" is corrected either way.

## Subtasks

- [x] Fix specificity counting.
- [x] Model or refuse layers, media queries and `!important`.
- [x] Add the cross-check fact.

## Dependencies

- none (B-007 and Q-013 are accepted; their current facts are correct today)

## Notes and Decisions

### The three gaps, and how each was closed

Only `tests/Zenkins.Tests/Theme/` changed. The reader is still a hand-rolled reader — the point was to
make it know what it is modelling, not to grow a CSS engine.

1. **Specificity is a tuple, not a scalar.** `Specificity(Ids, Classes, Types)` compared
   lexicographically, computed from the *subject compound*: `html` +1 type, `:root`/`.dark`/`[…]` +1
   class, `::before` +1 type, `#id` +1 id. The old scalar (`classes + :root + [`) could not have been
   repaired by adding an element count either — `(0,1,10)` and `(0,2,0)` both collapse to 20 under
   `10·b + c`, which is why the columns are kept apart.
2. **`@layer` is modelled, not refused.** Every layer is registered in first-appearance order across
   the whole document, including bare `@layer a, b;` statements (which the old flattener threw away)
   and nesting (`bb` → `bb.components`). Rank is `(importance, layer, specificity, order)`, with
   unlayered worth more than any layer for normal declarations and worth *less* than any layer for
   `!important` ones (CSS Cascading 5 §6.4's reversal). Refusing layered declarations was the other
   allowed answer, but it is wrong here: `--sidebar-width-icon` is declared **only** inside
   `@layer base`, so refusing would have to answer a token a browser computes as `3rem`. Modelling is
   the only choice that gets both halves right.
3. **Unmodelled conditions fail closed, per property.** Each rule carries its enclosing at-rule
   preludes and its layer, and its declarations are held as *candidates* rather than applied. A
   candidate under a condition the reader cannot evaluate becomes a **doubt** with its cascade rank;
   the property answers `Unknown` only when that doubt could actually win. So a
   `@media (prefers-color-scheme: dark)` block at the end of a sheet poisons its token, while the same
   block followed by an unconditional declaration of the same specificity does not — refusal is per
   candidate, never per sheet or per block.
   `@media (prefers-color-scheme: dark|light)` *is* answered, but only when the caller states the
   preference (`prefersDark`); the gate never does, because the app's dark mode is D-001's cookie plus
   the server-rendered `class="dark"`, not the OS setting. Every other `@media`, every `@supports`,
   `@container`, `@scope` and unknown at-rule block is `Unknown` for the properties inside it.
   `@keyframes`/`@property`/`@font-face`/`@page` are *not* walked into at all: they hold no element
   rules, which is knowledge rather than a guess.
   `!important` is modelled rather than refused: parsed off the value, ranked above every normal
   declaration, and therefore never able to reach a colour check as `"oklch(1 0 0) !important"`.

### The API is three-state, and `Tokens(...)` is gone

`Resolve(sheets, isDark, baseColor, primaryColor, prefersDark)` returns a `CascadeResolution` with
`Answer(name)` → `Declared` (with value) / `Unknown` (with reason) / `NotDeclared`. `Tokens` (the
declared-only map) still exists on the resolution, but the old static `Tokens(...)` convenience was
deleted: a fact can no longer reach a value without holding the object that can also say "unknown".
`UnknownTokens` and `Names` are exposed so a comparison between two document states cannot silently
skip a property that is unknown in both.

### Behaviour change on the four real served sheets

Measured, `artifacts/q-021/cascade-dump.md`: 264 tokens before, **190 definite + 74 unknown** after
(the same 74 in light and dark; both numbers already predicted by the review's baseline). **Exactly one
value moved, in both modes:**

| token | old (model) | new | why the old one was wrong |
|---|---|---|---|
| `--sidebar-width` | `16rem` | `15rem` | `blazorblueprint.css` declares `16rem` inside `@layer base`; `styles/theme.css` declares `15rem` unlayered. An unlayered normal declaration outranks every layered one (§6.4), so a browser computes `15rem`. The old model saw two `:root` rules, ignored the layer, and gave the later sheet the win. |

Nothing else moved: `--sidebar-width-mobile` stays `18rem` (declared 18/18 in both places),
`--sidebar-width-icon` stays `3rem` (layered only), all 27 `RequiredTokens` and `--radius` keep their
values, and the dark/light divergence is unchanged. The 74 that flipped to `Unknown` are `--tw-*` and
`--bb-menu-accent` — declarations that live inside Tailwind's `@supports` fallback blocks or on
selectors the reader does not evaluate (`:where(…)`). None is asserted by any fact, and none is a theme
token the app reads. No existing assertion was removed or loosened; two were tightened (the zinc
equivalence fact now also asserts the two paths refuse the *same* set, and the B-007 facts report the
`Unknown` reason in their failure message).

### New facts (`tests/Zenkins.Tests/Theme/ThemeCascadeModelTests.cs`)

Each fact as a whole goes red against the pre-Q-021 reader — proven by mutation, not by reading
(old layer logic → `16rem`/`layered`/`first`; old specificity → `from-palette`). Individual
assertions the old reader also got right are marked as controls in the fact, so nobody mistakes them
for new coverage:

- `Element_selectors_add_a_type_column_so_html_root_outranks_a_bare_attribute` — `html:root` (0,1,1)
  beats `[data-base-color="zinc"]` (0,1,0) in **both** sheet orders; `:root[data-base-color]` (0,2,0)
  still beats `html:root`; `html` alone (0,0,1) loses to the bare attribute; two equal (0,1,0)
  declarations still go to document order.
- `An_unlayered_declaration_outranks_a_layered_one_even_when_the_layer_comes_later` — both orders.
- `Layer_order_decides_between_two_layers_rather_than_source_order` — `@layer a, b;` statement order
  wins over block order, plus a nested layer.
- `The_layered_sidebar_tokens_resolve_the_way_the_browser_computes_them` — the real sheets, light and
  dark: `--sidebar-width` exactly `"15rem"`, `--sidebar-width-icon` exactly `"3rem"` (the pair that
  makes both "skip the layered sheet" and "refuse every layered declaration" unpassable), and no
  resolved value anywhere carries a `!`.
- `A_media_query_the_reader_cannot_evaluate_makes_the_token_unknown_not_light` — `prefers-color-scheme`
  and `@media print` are `Unknown` with the prelude in the reason; the refusal is scoped (a sibling
  token is still `red`); a provably losing conditional declaration does not block the answer; and
  stating `prefersDark` answers both branches.
- `An_unmodelled_supports_block_is_unknown_per_property_while_its_neighbour_still_answers` —
  `Unknown` ≠ `NotDeclared`, asserted on both sides and on `UnknownTokens`.
- `A_rule_that_cannot_select_html_never_supplies_html_s_token` — `body{…}` is not `<html>`'s value,
  `@container` is refused, `@keyframes` frames are not applied.
- `Important_is_ranked_and_never_survives_into_the_value` — `red !important` beats a later `blue` and
  a more specific normal rule; the value is bare; `IsOpaqueColor("oklch(1 0 0) !important")` is false.

### The `html:root` fact's evidence — read this before trusting the word "verified"

The card's AC 4 says "browser-verified", and the original gap description says the model "says the
palette wins where a browser says `app.css` wins". **Both need a correction, and the fact states the
real basis:**

- No served sheet contains `html:root` or `html.dark` (grep: 0 occurrences in `styles/theme.css`,
  `blazorblueprint.css`, `css/themes.css`, `app.css`), and `app.css` declares **no** custom property at
  all. The collision was never live in this app, so there is nothing in it to measure in a browser:
  gap 1 is latent, and its fact is therefore pinned on a **synthetic fixture**.
- The expectation `(0,1,1)` beats `(0,1,0)` comes from **CSS Cascading 5 §6.4** — specificity is the
  tuple (id, class/attribute/pseudo-class, type/pseudo-element) compared lexicographically — not from a
  browser run. The fact's comment cites the clause instead of claiming a measurement nobody made.
- What *was* browser-verified is the layer case: Q-013's cross-check measured `--sidebar-width: 15rem`
  in Chromium, which is what `The_layered_sidebar_tokens_resolve_the_way_the_browser_computes_them`
  pins on the real sheets.

**Lead ruling (2026-11-17): AC 4 is met in substance, not literally.** Its word "browser-verified"
cannot be honoured for this case — no served sheet contains `html:root`, so there is nothing in this app
a browser could be asked about — and the synthetic fixture plus the Cascading 5 §6.4 citation above,
with the genuinely browser-verified `15rem` pinned in its own fact, is the only honest form the AC can
take. Lead accepted it on that basis. Recorded here so nobody later reads the AC as literally satisfied.

### Corrected comment (AC 5)

The class remark at `ServedThemeCascade.cs:16-18` said the reader "does not model layering … and it
does not have to, because every sheet that declares these tokens is unlayered". The corrected wording,
now in the type's summary:

> **Cascade layers** are ordered by first appearance across the document, and the unlayered bucket sits
> where CSS Cascading 5 §6.4 puts it: strongest for normal declarations, weakest for `!important` ones.
> This is not optional — `blazorblueprint.css` declares `:root { --sidebar-width: 16rem }` inside
> `@layer base`, so the unlayered `15rem` in `styles/theme.css` wins in a browser. B-007's comment
> claiming "every sheet that declares these tokens is unlayered" was simply false, and it both hid that
> value and forbade the reader from answering `--sidebar-width-icon` correctly (that token is declared
> *only* inside `@layer base`).

The second sentence of the old summary (`:6-8`, "descending into at-rules, because the package hides
utilities inside `@layer`/`@media`/`@supports`) described the mechanism of the bug — unconditional
descent — and is replaced by a list of what is modelled and what is answered `Unknown` instead.

### Known limits, stated rather than fixed

Round 2's review found the four below; they are recorded rather than fixed, because this is an S card
and none of them reaches a served sheet.

- **A parent layer's own rules are ranked as if they came *before* its sublayers; a browser puts them
  after.** Fixture: `@layer outer { :root { --direct } @layer inner { :root { --inner } } }`. The parent's
  direct rules belong to an implicit *final* sublayer of `outer`, so a browser answers `direct`; this
  reader answers `inner`. That is the exact failure shape the card exists to kill — a **definite** answer
  where the reader should not be sure — and it is tolerated only because nothing on the four served sheets
  reaches it: the six direct rules of `@layer bb` declare no custom property at all, so none of the 264
  tokens moves. Fixing it means ordering a layer's own bucket after its sublayers — a code change this
  card should not grow to carry.
- **Anonymous layers all collapse to one name** (`<anonymous>`), so two of them would share a rank and the
  `!important` reversal could pick the wrong one. No sheet here uses an anonymous layer.
- **An attribute selector with a case flag** — `[data-base-color="zinc" i]` — is answered
  `DoesNotMatch`, because the reader compares the literal value and never parses the flag. Wrong in the
  *false-red* direction (a rule a browser would apply is ignored), not false-green, and no sheet writes it
  today.
- **Tail risk in the zinc fact.** `The_defaults_are_the_library_s_own_zinc…` `continue`s past a token that
  both document states refuse, so a token *outside* the 27 `RequiredTokens` that quietly became unknown in
  **both** states would not turn any fact red. Today's 74 unknowns were shown in round 2 to be exactly the
  at-rule-borne set, minimised, with no extras — so nothing is hiding there — but that is an observation,
  not a guard, and this card deliberately adds no 74-name pin to make it one.

Also unmodelled, and named rather than guessed at: cascade origins other than the author's (no
user-agent or user sheet is in the head); `@property` initial values (all `--tw-*` registrations, none
asserted); `revert-layer` and `@scope`; and `DeclaredValue`, which still returns the first matching
declaration rather than the cascaded winner and is only used by B-007's two `body`/`bb:bg-primary`
probes.

### Verification

- `dotnet build -c Release` — PASS, 0 warnings.
- File hashes of the tree the suite ran on: `artifacts/q-021/theme-sha1.txt` (`ServedThemeCascade.cs`
  `f462aab9…`, `BaseTokenPaletteTests.cs` `9b24f95f…`, `ThemeCascadeModelTests.cs` `c7f89b44…`,
  `ShellStylesheets.cs` `6ea97a6a…`, and `ThemeShellTests.cs` `2324460b…` — unchanged, the same hash
  the review's baseline recorded).
- Fast suite — **1270/1270**, 0 skipped, 0 failed (`docs/development.md` §2 filter) — the 1262 the
  suite held before this card, plus the eight new model facts.
- Theme subset — 14/14 (six facts before this card: three B-007 palette facts, three shell facts; the
  eight model facts are new and no existing fact was removed).
- Logs in `artifacts/q-021/` (ignored): `cascade-dump.md` (per-mode counts, every changed value, every
  newly-unknown token with the reason), `theme-and-dump.log` (14 model/palette/shell facts plus the
  dump), `fast.log`, and `mutation-layer.log` / `mutation-specificity.log` — the two mutations proving
  the new facts bite: collapsing layers to source order turns the three layer facts red
  (`15rem`→`16rem`, `unlayered`→`layered`, `second`→`first`, i.e. exactly HEAD's answers), and giving
  element selectors zero specificity turns the `html:root` fact red (`from-app-css`→`from-palette`).
  `harness/` holds the dump reporter plus the pre-Q-021 resolver, deliberately **not** in the test
  project, with the recipe to re-run both.

**Independent review, 2026-11-17 — PASS, 5/5 acceptance criteria.** Everything below was executed by
 the reviewer on the same five file hashes above, not read off this card.

- **Both failure directions were measured, not argued.** 13 false-green checks (specificity triple and
  both sheet orders, unlayered beating layered in either order, first-appearance layer order including
  bare `@layer a, b;`, `!important` inversion in layers, evaluation-unknown at-rules, per-property
  refusal, `body`/`@container`/`@keyframes` not supplying `<html>`, bracket/quote-aware declaration
  splitting) and 8 false-ugly checks (three-state answers with `Unknown` distinguishable from never
  declared, the deleted `Tokens(...)` with no remaining caller, refusal scoped per property, all 27
  `RequiredTokens` still definite on the real sheets, no assertion removed or loosened).
- **Three mutations, run and reverted by the reviewer** (sha1 verified back to `f462aab9…`): element
  specificity to zero → 1/14 red, reproducing HEAD's `from-palette` answer; `LayerRank` to zero → 3/14
  red including `16rem`; **every answer forced to unknown → 10/14 red**, because `RequiredTokens` go
  unknown and B-007's palette fact fails. That last one is the card's own worst case, executed rather
  than assumed.
- **The token set is conserved.** 190 definite + 74 unknown = 264, identical to and disjoint from
  HEAD's 264. The 74 (73 `--tw-*` plus `--bb-menu-accent`, all at-rule borne) are asserted by no fact,
  and none of them has an unwrapped declaration on any sheet — the refusal set is minimal, not padded.
- **One value moved, confirmed by a full diff of both modes**: `--sidebar-width` 16rem → **15rem**.
  `--sidebar-width-icon` reads `3rem` and `--sidebar-width-mobile` `18rem`; no resolved value in either
  mode contains `!`.
- **A carried defect is recorded, not fixed.** A parent layer's own rules rank before its sublayers
  where a browser puts them after — a *definite* wrong answer, which is the shape this card exists to
  remove, tolerated only because `@layer bb`'s six direct rules declare no custom property. See Known
  limits; closing it is a follow-up, not this S card's work.
- **The one thing this card does not carry**: a pin on the 74 unknown names, so a token outside the 27
  `RequiredTokens` going unknown in both states would turn no fact red. Recorded under Known limits as
  an observation rather than a guard.
- Reviewer logs: `artifacts/q-021/review-criteria.md`, `review-baseline.log`, `review-verdict.log`.
