---
id: Q-019
title: A short organization name collides with JSON-escaped base64 in the tenant page scan — one real failure in 670 page reads
type: test
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
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

- [ ] The scan ignores machine-generated opaque payloads — **any** base64 run, escaped or bare, not
      only `\u002B` forms — or matches foreign names as whole words/attributes; whichever is chosen, a
      rendered tenant value in ordinary markup still fails the test.
- [ ] A test proves the chosen mechanism: a page fragment containing an escaped blob that spells the
      foreign name verbatim does **not** trip the scan, while a fragment that renders the foreign name
      as content does.
- [ ] The existing coverage is preserved: the ciphertext assertion, every foreign id and name, and the
      page list are unchanged in what they assert.
- [ ] The scan is stable: the implementer's reproducer (400 rounds × 10 pages) reports zero failures
      after the change, and that loop is either kept as a cheap fact or recorded as a one-off run.

## Subtasks

- [ ] Pick the mechanism (blob-aware pre-processing vs whole-word matching) and record why.
- [ ] Add the two-sided fact.
- [ ] Re-run the long loop and record the number.

## Dependencies

- none (I-005 is accepted; this is the flake its review exposed)

## Notes and Decisions

- The rejected alternative is worth recording: stripping antiforgery tokens looked reasonable and was
  provably inert, which is worse than no change — it produced a green suite and a false explanation.
  Any future fix here must be demonstrated by a reproducer, not by a plausible story.
