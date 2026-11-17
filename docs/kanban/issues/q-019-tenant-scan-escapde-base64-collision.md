---
id: Q-019
title: A short organization name collides with JSON-escaped base64 in the tenant page scan — one real failure in 670 page reads
type: test
priority: P2
size: S
status: done
created: 2026-11-16
updated: 2026-11-17
blocked: none
---

## Goal

`Tenant_pages_render_only_the_active_organizations_rows` stops failing by chance when the fixture's
organization name happens to appear inside a machine-generated blob, without weakening what the scan
actually checks.

## Background

The test scans ten rendered pages for the *other* organization's name and fails if it appears. The
name it scans for is the fixture's short `Beta`, and pages render machine-generated opaque payloads
whose escaped form can contain it by accident.

This became visible during I-005 (2026-11-16): the implementer hit a failure and "fixed" it by
stripping `__RequestVerificationToken` inputs — a no-op with an invented rationale. I-005's review
measured **zero** such inputs on all ten pages, and the stripper deleted **zero bytes**, so the change
was reverted. (The pages do carry a token, but as `<meta name="request-verification-token">`, not an
`<input>` — which is why stripping inputs removed nothing.)

I-005's implementer then reproduced the failure with the original assertions in place: run 67 of 400
(ten pages per run), page `/workers`, index 2390, and the collision looked like

```
...eYVk\u002B/4eATgK\u002BetaXIMvsRakk...
```

— a JSON-escaped base64 data-protection payload, where the escaper writes `+` as `\u002B` and the
following base64 happened to start with `eta`.

**The card originally attributed the collisions only to that escape shape, and I-005's review measured
that attributing it that way is incomplete.** Over 60,000 page reads it counted fifteen `Beta` hits,
of which only **four** came from `\u002B` + `eta`; the other **eleven** were a bare base64 run that
happened to spell `Beta` (the alphabet is case-sensitive letters, and a page carries thousands of
base64 characters). So the rate is about one hit per four thousand page reads — roughly one failure
every four hundred runs of the ten-page test — and **any** base64 run is a candidate, not only the
escaped form. A fix scoped to JSON escapes would miss three quarters of the hits.

Every observed hit landed inside a long base64 run; none landed on rendered content, so the property
the test protects has not actually been violated. Evidence (not tracked; artifacts are ignored):
`artifacts/logs/i-005-q006-flake-evidence.txt` plus the review's own reproducer logs.

The test is not wrong about what it wants — the page really must not render the other tenant's data.
The scan is just too literal for machine-generated blobs.

## Acceptance Criteria

- [x] The scan ignores machine-generated opaque payloads — **any** base64 run, escaped or bare, not
      only `\u002B` forms — or matches foreign names as whole words/attributes; whichever is chosen, a
      rendered tenant value in ordinary markup still fails the test.
      → blob-aware pre-processing over **both encodings on the pages**: standard base64 (minimum 32, the
      escaped payload I-005 saw) and base64url (minimum 40, the `<meta name="request-verification-token">`
      antiforgery token). Round 1 covered only the first, which left 105 of a 198-symbol token in front
      of the assertions; round 2 closes that. See the resolution.
- [x] A test proves the chosen mechanism: a page fragment containing an escaped blob that spells the
      foreign name verbatim does **not** trip the scan, while a fragment that renders the foreign name
      as content does.
      → `tests/Zenkins.Tests/Tenancy/TenantPageScanTests.cs`, 20 cases in 11 facts: three payload
      encodings ignored, six rendered shapes still caught (including a real `<meta>` and a real
      `<!--Blazor:…-->` shell), all twelve values proven visible, two run-length boundaries, and the two
      known limitations pinned.
- [x] The existing coverage is preserved: the ciphertext assertion, every foreign id and name, and the
      page list are unchanged in what they assert.
      → the ten paths and all twelve assertions are byte-identical; only their subject changed from
      `body` to `scanned`. Three things were added so the claim is checkable rather than asserted: a
      positive control in the loop (org A's own name and owner must still read as page text), a fact that
      all twelve values survive in rendered markup, and the GUID exemption that keeps a foreign id
      visible even inside `/projects/{guid}/settings` or a generated `rotate-{guid}` DOM id.
- [x] The scan is stable: the implementer's reproducer (400 rounds × 10 pages) reports zero failures
      after the change, and that loop is either kept as a cheap fact or recorded as a one-off run.
      → one-off run, recorded; and the residual payload window count is now **0** per page read
      (round 1 left 50–63). The deterministic facts stay in the suite instead (see the resolution).

## Subtasks

- [x] Pick the mechanism (blob-aware pre-processing vs whole-word matching) and record why.
- [x] Add the two-sided fact.
- [x] Re-run the long loop and record the number.
- [x] Round 2: cover base64url (the antiforgery token) without widening the mask into the ids the scan
      exists to catch, and re-measure the residual window count.

## Dependencies

- none (I-005 is accepted; this is the flake its review exposed)

## Notes and Decisions

The rejected alternative is worth recording: stripping antiforgery tokens looked reasonable and was
provably inert, which is worse than no change — it produced a green suite and a false explanation.
Any future fix here must be demonstrated by a reproducer, not by a plausible story.

## Resolution (2026-11-17)

### The mechanism

`TenantPageScan.IgnoringMachineGeneratedPayloads(body)` masks a payload — replacing it with
`<opaque-payload/>` — when it is an unbroken run of at least **32 standard base64 symbols**
(alphanumeric, `+`, `/`, `=`) or at least **40 base64url symbols** (alphanumeric, `-`, `_`, `=`). A JSON
`\u00XX` escape of an alphabet character counts as that character, so an escaped payload is one run
rather than several. Everything else passes through byte for byte, and every assertion stays a literal
ordinal containment test over that text.

**Two alphabets, not one union.** The pages carry both encodings: the escaped payload I-005 saw is
standard base64 (it contains `+` and `/`), the antiforgery token is base64url. Measured on the real
pages, one union alphabet (which is what round 2's review proposed, minimum 40) makes a foreign id
**invisible** in two places the scan exists to cover:

| mask | `/projects/{guid}/settings` | `rotate-{guid}` DOM id | payload windows left per read |
| --- | --- | --- | --- |
| round 1: `alnum + / =`, min 32 | visible | visible | 50–63 |
| union `alnum + / − _ =`, min 40 | **masked** | **masked** | 0 |
| **shipped: base64 32 / base64url 40** | visible | visible | **0** |

A path is 55 symbols under the union alphabet, so the mask eats the id; the two alphabets keep it
visible because `/` is not a base64url character. `rotate-{guid}` is 43 symbols, so a widened alphabet
without more care eats it as well.

**A run that contains an `8-4-4-4-12` hex GUID is never masked.** That is the rule that keeps the
`ProjectBId` / `BuildRunBId` assertions meaningful: the product renders row ids inside generated DOM
ids (`rotate-{guid}`, `schedule-outcomes-{guid}`, 43–54 symbols). A payload never contains a GUID — it
is not in the encoding's structure and 40 random symbols cannot spell one (measured: 0 of the masked
runs on the ten pages contains one, and 0 payload characters survive).

**The encoding is decided base64url-first.** The first version tested the standard alphabet first, and
a base64url token (a 198-symbol antiforgery value can contain `-`, `_`, or neither, depending on the
render) whose first separator was `_` had its alphanumeric prefix masked under the base64 rule and its
tail — 4 to 30 symbols — left in front of the assertions. The reproducer caught that at 0.15 windows per
page read; `A_base64url_payload_whose_first_separator_is_an_underscore_is_masked_whole` pins the fix.

Why this and not whole-word matching: whole-word matching changes the meaning of all twelve assertions
at once and would need a separate argument about word boundaries per needle (`Beta` in a text node,
`CfDJ8-fixture-beta-ciphertext` in a cell, `b0000000-…` in an href). The mask keeps every assertion an
ordinary ordinal containment test over page text and fixes the one thing that was ever imprecise: a
machine payload being read as prose.

### What the mask eats, measured (100 page reads, one `artifacts/q-019/reproducer-round2.log` run)

- **8.8 %** of the characters (round 1's narrower mask: 8.4 %), i.e. ≈3.10 kB of ≈35.1 kB per page.
- per page, **≈4 data-protection payloads = 2.68 kB (87 % of the masked bytes)**: one
  `<meta name="request-verification-token">` token (198 symbols), one
  `<!--Blazor-Server-Component-State:…-->` comment (~1.2 k) and two `<!--Blazor:{…}-->` descriptor
  markers (~0.6 k each). Round 1 said "the `<meta>` token plus three in the server-side component
  state"; only one of the other three is a component state.
- per page, **≈11 generated-id runs = 0.42 kB (13 %)**, by the probe's classifier: **4.7** runs that are
  pure hex and ≥28 symbols (the 32- and 64-symbol prerender ids) and **6.2** other generated runs (the
  two `bb-scrollarea-viewport-*`/`-scrollbar-v-*` runs, the two locationHash-derived element ids, and the
  rest). Round 1 wrote "14–16 payload runs"; that conflated payloads with generated ids, and its
  "4.7 hex / 6 other" split is the classifier's, not the page's: round 2's review splits the same runs as
  6.0 hex ids (4 prerenderId + 2 locationHash) and 3.0 other. The two counts agree on the payload count
  (4) and on the byte total (3.10 kB masked) and differ by ~2 runs/page only in which bucket the
  locationHash and `bb-scrollarea-*` runs land in.
- it never eats a GUID, so the two id assertions survive bare, in an href and inside a generated DOM id.

### Why the twelve values survive — the true reason

Round 1 wrote that a value "can only be swallowed if it sits inside a 32-symbol unbroken run of base64
characters, which ordinary markup — text nodes, attribute values, element names — never is". **That was
false and is retracted.** Each page carries three 32-symbol and two 64-symbol hex prerender ids in
attribute values, plus `bb-scrollarea-*` element ids, and the mask does remove them.

The protection is narrower and checkable: every scanned value is shorter than both minimums (the
ciphertext placeholder is 29 symbols, a GUID 36, everything else shorter), and a run that embeds a GUID
is exempt. Two facts pin it — all twelve values are proven visible in rendered markup, and the ten-page
scan keeps a positive control (the active organization's own name and owner must still read as page
text; measured present on all ten pages, 0 misses over 4,000 reads).

### Known limitations, pinned as behaviour

1. A value that **touches** a payload is inside the payload's run and is masked with it:
   `data-x="{80 symbols}AALBeta"`, and the mirror case with the name in front. No such shape exists in
   the fixture's pages, and it is the price of not reading payloads as page text at all. Pinned by
   `Known_limitation_a_value_touching_a_payload_is_masked_with_it`.
2. A rendered value that is **itself** ≥40 symbols of hyphenated base64url with no GUID in it is masked
   (`bb-Beta-panel-viewport-{32 hex}`). **Consequence worth naming: a foreign organization name rendered
   inside such a value — 40+ symbols of hyphenated base64url — would be missed.** No fixture value has
   that shape (and no real page renders one): the longest are the 29-symbol ciphertext and the 36-symbol
   GUID, both proven visible. Pinned by
   `Known_limitation_a_long_hyphenated_rendered_value_is_masked`.

### The numbers

The flake is a rate, so it needs a model rather than one sample: a page read feeds N base64 windows to
assertions over a 64-symbol alphabet, so P(hit) = N × 64⁻⁴ = N × 5.96e-8, and one round is ten page
reads (400 rounds = 4,000 reads).

| mask | payload windows per **page read** | per ten pages (one round) | P(page read) | per 400 rounds (4,000 reads) |
| --- | --- | --- | --- | --- |
| none (the original bug) | **≈2,670** — the four payload spans, 2,682 symbols | ≈26,700 | 1.6e-4 | 0.64 by the model; **1.00 observed** (15 hits / 60,000 reads) |
| round 1: base64 32 only | **55** (553 over 10 reads; 50–63 across runs) | ≈553 | 3.3e-6 | **0.013** (≈1 in 30,000 rounds) |
| round 2 (shipped) | **0** over 100 reads | 0 | 0 | **0** |

**The three numbers now multiply.** Unmasked: 2,670 × 5.96e-8 = 1.6e-4 per page read, × 4,000 reads =
0.64 per 400 rounds. Round 1: 55 × 5.96e-8 = 3.3e-6, × 4,000 = 0.013. Round 2: 0. Round 1's revision
of this section printed "~36,500 windows / P = 2.2e-3 / **0.87** per 400 rounds", and those cannot all
hold: 36,500 × 5.96e-8 is 2.2e-3 per page read, which is **8.7** per 400 rounds. The 36,500 was a
ten-page figure in a per-page column; the 0.87 belongs with the observed rate, not with that window
count - the field rate is 15 hits in 60,000 page reads = 2.5e-4 per read = **1.00 per 400 rounds**. The
corrected per-page window count is ≈2,670 (2,682 payload symbols per page read, four contiguous spans,
from `reproducer-round2.log`), and 2,670 × 5.96e-8 × 4,000 = 0.64, i.e. the same order as the field.

**Model against measurement.** The four payload spans are not the only long random-looking runs on a
page: counting every base64-alphabet window would give ≈16,000 per page (12,900 measured on the page
*after* round 1's mask) and predict 3.8 per 400 rounds — three to four times the observed 1.00, because
most of those windows are rendered text, where a match is a fixed string rather than a rate. The
payload-span count is the conservative bound and brackets the truth from below: **0.64 modelled against
1.00 observed in the field** (15 / 60,000 reads) and 1.0 and 2.0 in round 1's two 4,000-read reproducers.

So round 1 cut the rate about eightyfold (1.00 → 0.013 per 400 rounds) but left it above the ≤0.01
target of this card's review; round 2 removes the surface instead of shrinking it — the window count is
0, not merely small.

Reproducer, 400 rounds × 10 pages (`artifacts/q-019/reproducer-round2.log`): shipped scan **0 failures**
in 4,000 reads, 0 positive-control misses, residual payload windows **0**. The round-1 scan measured on
the same pages reported 0 failures too — at 0.013 expected per 400 rounds a zero is the common outcome,
which is exactly why the window count and not the hit count is the evidence. Round 1's paired runs are
kept: `reproducer-run1-before.log` (1 failure in 4,000, the bare base64 shape) and
`reproducer-run2-after.log` (2 failures in 4,000 → 0 through the mask).

**Both hit counts are paired sampling, not a rate.** 2/4,000 and 0/4,000 come from the same process
(≈1.0 expected either way); a single loop cannot distinguish 0.013 from 0, so the window count above
is the measurement and the hit counts are only proof that the two shapes are real and that the mask
meets them.

### One-off, not a suite fact

Logged in `artifacts/q-019/` (ignored): `reproducer-round2.log`, `reproducer-round2-source.cs.txt`,
`anatomy-alphabet-probe.log`, `anatomy-probe-source.cs.txt`, plus round 1's
`reproducer-run1-before.log`/`reproducer-run2-after.log`/`reproducer-source.cs.txt`. The harness was
deliberately not kept in the suite: it takes ~20 s and it is a statistical loop — the shape of test this
card removed. What stays is `TenantPageScanTests`: 20 deterministic cases whose failure does not depend
on a rate.

## Verification

Independent review took three rounds - FAIL, FAIL, PASS - and every number below comes from a run on
the final tree rather than from the implementer's report.

- **Round 1 (FAIL).** The mask covered two of the three payload encodings: the antiforgery `<meta>`
  token is **base64url**, so a 198-symbol token was masked as 93 symbols and the remaining 105 symbols
  still reached the assertions. Proven deterministically by a sensitivity row, not by statistics: a page
  patched with a complete base64url payload spelling `Beta` in sub-32 fragments stayed **RED**. The same
  round found a false sentence in the class remark (*markup is never a long base64 run*) - three
  32-symbol hex attribute values per page were being masked all along.
- **The implementer rejected the reviewer's prescribed fix, with measurement.** A union alphabet
  (`alnum + / - _ =`) at minimum 40 masks `/projects/{guid}/settings` (55 symbols), `rotate-{guid}` (43)
  and `schedule-outcomes-{guid}` (54) - i.e. it hides foreign ids in exactly the shapes they most
  plausibly leak in. Shipped instead: **two alphabets** (standard base64 at 32, base64url at 40), decided
  base64url-first, plus a rule that a run embedding an `8-4-4-4-12` hex GUID is never masked. The
  reviewer re-measured, confirmed its own probe had been flawed (its `<td>value</td>` probe was cut short
  by `<`/`>`, so the URL shape never appeared) and **withdrew the prescription**.
- **Build.** `dotnet build Zenkins.slnx -c Release` - 0 warnings, 0 errors.
- **Tests.** `TenantPageScanTests` 20/20; tenancy subset 149/149; **fast suite 1262/1262, 0 skipped**
  (reviewer's own run, 5m51s; the implementer's 5m36s). 1250 -> 1262 is exactly the eight cases becoming
  twenty - nothing else moved, and no category or exclusion was added.
- **Reproducer, run independently by both.** 400 rounds x 10 pages: shipped scan **0 failures** in 4,000
  reads, literal scan 0, positive-control misses 0. Residual payload windows measured against the raw
  data-protection spans: **0** (from 2,670 per page read before the change).
- **The two things that could have made the fix hollow, checked.** The twelve scanned values keep their
  per-page parity between raw and masked text (same counts, including `<title>` in `head` and six
  attribute shapes), and a foreign GUID patched into an href, an attribute, a `<code>` cell and two
  generated DOM ids still turns the scan red.
- **Numbers corrected twice after the verdict.** The card's first rate table held three figures that
  could not multiply (36,500 windows x 5.96e-8 is 2.2e-3 per page read, which is 8.7 per 400 rounds, not
  0.87); it now states the per-page and per-round columns separately and keeps the old row as a
  self-referential counter-example. The reviewer's own payload-span count (1,276/page) was wrong too - a
  regex that demanded one quote more silently dropped every component state - and was corrected to
  2,592/page, 3 % from the card's 2,670. Neither correction moved a conclusion.
- **Not claimed.** Two known limitations are pinned as facts rather than left as prose: a value touching
  a masked payload is masked with it, and a rendered value that is itself 40+ symbols of hyphenated
  base64url would be missed. Neither shape occurs in this fixture's pages.
- **Logs.** `artifacts/q-019/` (ignored): `review-criteria.md`, `review-baseline-page-anatomy.log`,
  `review-verdict.log`, `review3-verdict.log`, `review3-{build,fast,tenancy,reproducer}.log`,
  `anatomy-alphabet-probe.log`, `reproducer-round2.log`, and round 1's `reproducer-run1-before.log` /
  `reproducer-run2-after.log` plus their sources.
