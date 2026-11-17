---
id: Q-017
title: The idempotency fingerprint drops invalid tags before hashing, and replay-vs-queue-cap has no fact
type: test
priority: P3
size: S
status: done
created: 2026-11-16
updated: 2026-11-17
blocked: none
---

## Goal

A retried trigger whose only difference is an invalid tag is refused the way a new key would be, and
the "a replay wins over a full queue" rule is proven by a fact instead of by statement order.

## Background

Both found by B-008's independent review (2026-11-16), neither affects what gets built:

- `RunTriggerIdempotency.cs:48-54` fingerprints the **output** of `RunValidation.ParseTags(...)`,
  which silently drops over-long or control-character tags (`RunValidation.cs:130-146`) — exactly
  the values `ValidateRequiredTags` refuses with `tag-invalid`. So a resend that differs only in an
  invalid tag hashes identically: it replays `200` where the same request with a new key would be
  `400 tag-invalid`. Nothing wrong is built; a refusal is lost.
- F-029's note says the replay-wins-over-the-cap rule is pinned by two facts, but neither fills a
  queue: one only disables the project, the other never sets `MaxQueuedRuns`. The rule holds because
  of statement order alone (`RunService.CreateQueuedRunAsync` looks up the key before
  `StageQueuedRunAsync` counts). The note has been corrected to say so; this card adds the fact.

## Acceptance Criteria

- [x] The fingerprint hashes the tags **as sent** (trimmed, de-duplicated, ordered — but keeping the
      values validation would refuse), so an invalid-tag difference produces `409
      idempotency-key-reused` rather than a replay.
- [x] A test covers the tag dimension and the parameters dimension, not only branch and timeout.
- [x] A fact fills a `MaxQueuedRuns = 1` queue, then: a **new** key answers `409 queue-full` and
      queues nothing; the **same** key answers `200` with `Idempotency-Replayed` and adds no run.
- [x] F-029's note can then name that fact instead of describing the ordering.
- [x] Fast-suite counts re-recorded in `docs/development.md` §2.

## Subtasks

- [x] Move the hash input to the unparsed (trimmed) tag list.
- [x] Two test dimensions + one queue-cap replay fact.

## Dependencies

- none (B-008 is accepted)

## Notes and Decisions

- Two more B-008 review notes need no work: when a raced winner's record disappears between the
  unique violation and the read-back, the request answers 500 in a window the reviewer could not
  reproduce in 35 attempts (recorded on B-008); and `ApiTokenService`'s catch deliberately includes
  `InvalidOperationException`, which can swallow a genuine programming error as one warning — a
  trade-off that card already states.
- **The frozen hash values are HEAD's, not the review's, and the difference is one field.** The
  fingerprint of a legal request must not move (the `RequestHash` is stored and compared for the
  key's whole retention), so a review supplied two digests to reproduce byte for byte. Reproducing
  them was impossible *and* would have been the very failure they were meant to prevent: their frames
  write the parameter **count** as `1:;` and `0:;`, while the shipped algorithm writes every field
  through the same length-prefixed writer, i.e. `1:1;` (count 1) and `1:0;` (count 0). Their two
  digests are the hashes of the short frames (`e5803b45…7a1`, `e4196193…257`); the shipped frames
  hash to `a7413c7e…8c1c` and `70982699…ace8`, which is what the product writes today. Adopting the
  review's numbers as a target would have re-hashed every stored binding and turned every live key
  into a `409` against its own retry. Both sides are recorded here; the fact pins the shipped values
  (`The_fingerprint_of_a_legal_request_is_the_byte_the_deployed_algorithm_produced`) and the
  unchanged-algorithm mutation (M1 below) is green on it, which is the byte-identity proof.
- **The card's "invalid tag" scope is narrower than "everything validation refuses".** `ParseTags`
  silently drops exactly two kinds of entry: one longer than `MaxTagLength` (64), and one still
  carrying a **non-whitespace** control character after trimming. `MaxRequiredTagsLength` (400) is a
  refusal of its own, not a drop, so a long list of short tags was never a collision - the facts use
  one over-long entry, not a 401-character pile. The other side matters as much: `\t`, `\v` and
  `\f` are trimmed away and never refused, so a list padded with them is the same request and must
  still replay; the tag fact pins that green direction explicitly.

## Development Log

### 2026-11-16 — implementation and evidence

**1. What the fingerprint hashes now (before → after).**

- Before: `RunTriggerFingerprint.Of` hashed `RunValidation.ParseTags(input.RequiredTags)` — the list
  the trigger **accepts** (trimmed, empties dropped, over-long/control entries *dropped*,
  case-insensitively de-duplicated) — then ordered it and joined it.
- After: it hashes `RunValidation.ParseTagsAsSent(input.RequiredTags)` — the list **as sent**
  (same trim, same empty-drop, same case-insensitive de-duplication, first spelling wins) but the
  over-long and control-bearing entries are kept — then the existing ordering
  (`OrdinalIgnoreCase`, then `Ordinal`) and the length-prefixed framing are unchanged.
- The normalization layer is the only thing that moved: `RunValidation.ParseTagsAsSent`
  (`src/Server/Runs/RunValidation.cs:164`) is a second parser beside `ParseTags`, and
  `RunTriggerFingerprint` (`src/Server/Runs/RunTriggerIdempotency.cs:62`) calls it. Framing, field
  order, branch/timeout trimming, the parameter ordering and the count field are untouched, so a
  legal request hashes to the same 64 hex characters it hashed to before (see the frozen-digest
  fact and mutation M1).
- Why it cannot put a refused value back into the request: the parser only decides what the **hash**
  sees; `ValidateRequiredTags` still runs on the raw text and still answers `tag-invalid`, and the
  key's mismatch is answered before field validation ever runs — so the caller gets the key's `409`.
- Why it cannot split a legal resend: the two parsers are identical on input with no over-long or
  control-bearing entry (same trim, same empty-drop, same de-duplication), and the ordering is
  applied afterwards in the fingerprint, so every re-spelling of the same legal tag set still lands
  on the same digest.

**2. Facts added (5, all in `tests/Zenkins.Tests/Api/ApiV1RunApiTests.cs`).**

| Fact | Line | What it asserts |
|---|---|---|
| `A_resend_that_differs_only_in_a_refused_tag_is_refused_while_the_same_tags_still_replay` | 230 | **Tag dimension, both directions.** The legal re-spellings `" gpu , linux "`, `"gpu,linux,gpu"`, `" linux\t, gpu\v"` and `"linux, gpu"` all replay `200` with `Idempotency-Replayed` and the first run's id (the whitespace-control padding is trimmed, never refused). The refused differences — one 65-character entry, `bad\u0001tag`, and the adversarial `linux, gpu, lin\u0001ux` (a sanitizing fingerprint would drop the control character and collapse it onto the first `linux`) — each answer `409 idempotency-key-reused` with no replay marker, and each body under a **fresh** key answers `400 tag-invalid` with `fieldErrors[0].field = requiredTags`, which is the control that stops the assertion passing for another reason. One run and one binding afterwards, and the base request still replays. |
| `A_reused_key_refuses_a_different_tag_set_before_the_worker_coverage_check` | 330 | **Tag-set dimension (AC-2).** Base `linux` → `linux,docker` is `409 idempotency-key-reused`, and the same body under a fresh key is `400 tag-unknown` — so the difference is real, legal and unclaimable, and the `409` proves the key was decided *before* the coverage check. |
| `A_reused_key_refuses_a_different_parameter_value` | 379 | **Parameters dimension (AC-2).** `GREETING` `hi` → `bye` is `409 idempotency-key-reused`; the same body under a fresh key is `201`, so the refusal is the key's decision and not validation; the first key still names the first run. |
| `The_fingerprint_of_a_legal_request_is_the_byte_the_deployed_algorithm_produced` | 438 | **Frozen digest (the hard constraint).** `{branch=main, requiredTags=dotnet,Linux, timeoutMinutes=30, parameters={GREETING:hi}}` stores `a7413c7e579d36a8fe90b723434ca771286c9cd447bbf4be55636d5f5e118c1c`, and `{branch=main}` alone stores `70982699a4a519ea2d90305a08ed6a489f9ab5d079323c78680112320ba5ace8`; the frames are quoted in the comment so a reader can re-derive them. |
| `A_replay_wins_over_a_full_queue_and_a_new_key_reads_the_refusal` | 605 | **AC-3, `MaxQueuedRuns = 1`.** After the `201`, exactly **one** run exists, `Status = Queued`, with the id that `201` returned (the occupancy is the key's own run, not just "a row"). A **new** key then answers `409` `error = queue-full`, `fieldErrors[0].field = queue`, no `Idempotency-Replayed`, the run count is unchanged, and no binding exists for that key. The occupancy is re-read and still `Queued` before the retry. The **same** key answers `200` with `Idempotency-Replayed`, the same run id, still one run and one record. |

No existing fact asserted the behaviour being fixed: the key facts that touched payloads asserted
branch and timeout differences only, and the queue-cap facts never set `MaxQueuedRuns` alongside a
key (`The_idempotency_key_queues_one_run_for_a_retried_post` included). Nothing was deleted, loosened
or rewritten to match.

**3. Mutation record (each → red → restored; logs in `artifacts/q-017/`).**

| # | Mutation | Result |
|---|---|---|
| M1 | `ParseTagsAsSent` → `ParseTags` (i.e. the algorithm as shipped before this card) | **1 red**: the tag fact, `tag dimension: a resend differing only in the refused tag 'linux, gpu, aaaa…' is not the request that ran: expected 409, got 200`. The frozen-digest fact stays **green** — that is the byte-identity proof — and the tag-set and parameter facts stay green. `artifacts/q-017/mutation-1-parse-tags.log` |
| M2 | The parameter name/value pairs dropped from the hash input (count kept) | **2 red**: the parameter fact (`parameters dimension: one changed value is a different payload: expected 409, got 200`) and the frozen-digest fact. Tag facts stay green. `artifacts/q-017/mutation-2-parameter-values.log` |
| M3 | The queue cap counted in `CreateQueuedRunAsync` **before** the key's lookup (F-029's ordering assumed away) | **1 red**: `A_replay_wins_over_a_full_queue_and_a_new_key_reads_the_refusal`, `Expected: OK / Actual: Conflict`. Every other fact green — this is exactly the hole the card described. `artifacts/q-017/mutation-3-cap-before-key.log` |
| M4 | Tags hashed raw (`input.RequiredTags`, no trim, no de-duplication, no ordering) | **1 red**: the tag fact, `tag dimension: the legal re-spelling ' gpu , linux ' must still replay the run it repeats: expected 200, got 409` — the over-refining direction is pinned too. `artifacts/q-017/mutation-4-raw-tags.log` |

Restoration verified by hash (`artifacts/q-017/pristine.sha256`, unchanged for
`RunTriggerIdempotency.cs`, `RunService.cs` and the test file). AC-2's two dimensions are separable:
M1 reds the tag dimension with the parameter dimension green, M2 reds the parameter dimension with
the tag dimension green.

**4. F-029's note** (`docs/kanban/issues/f-029-queue-controls.md`, Notes and Decisions) now names
both facts by name instead of describing statement order: the disabled half by
`A_replay_wins_over_the_disabled_gate_and_a_new_key_reads_the_refusal`, the queue-cap half by
`A_replay_wins_over_a_full_queue_and_a_new_key_reads_the_refusal` — with the fact's actual assertions
spelled out (a filled `MaxQueuedRuns = 1` queue, `409 queue-full` for a new key with nothing queued,
`200` + `Idempotency-Replayed` and no new run for the same key), and a sentence saying the queue-cap
half previously rested on statement order alone.

**5. Counts.**

- `dotnet build -c Release`: `Build succeeded. / 0 Warning(s) / 0 Error(s)`.
- Fast suite (the §2 filter, `Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres`): **1279 → 1284**, `failed: 0`, `skipped: 0` (`artifacts/q-017/fast-final.log`, 5 m 45 s).
- `docs/development.md` §2: the Q-017 entry in the note, the re-baselined **fast 1284 / all seven
  1310** (1284 + 12 + 4 + 2 + 2 + 3 + 3), the Fast table row, and the "all seven (1310 …)" paren.
  The six-exclusion filter string is byte-identical in all three places it appears — §2's command
  line, §2's table row and `scripts/verify.sh:53` — so "no new exclusion" holds by construction.
- `Category=Postgres`: **ran, 3 skipped, 0 failed** — `ZENKINS_TEST_POSTGRES` is not set on this
  machine, so all three facts (including Q-016's trigger-side queue cap) skip with their recorded
  reason (`artifacts/q-017/postgres.log`). This card changes only the fingerprint's input, not the
  creation path's ordering or locking, and the queue-cap fact added here is SQLite-side and in the
  fast suite.

## Verification

Independent review PASSed all five criteria, re-applied six of its own mutations (6/6 red, every tree
restored byte-identically), and ran the counts itself.

- **The reviewer's own frozen digests were wrong, and the implementer refused them.** Round 1 handed
  over two "HEAD" frames as `...;1:;...` / `...;0:;`. The parameter count goes through the same
  length-prefixed writer as every other field, so HEAD writes `1:1;` and `1:0;`; the handed-over values
  were the hashes of frames with that prefix missing (`e5803b45…`, `e4196193…`). Pinning them would have
  re-hashed every stored binding and turned every live key into a 409 against its own retry - the exact
  failure the constraint existed to prevent. The shipped values are `a7413c7e…8c1c` and `70982699…ace8`;
  the reviewer re-derived them from code and voided its own, and the card records both sides.
- **Byte-identity is proven, not asserted.** The frozen-digest fact is endpoint level (a real POST, then
  the stored `RequestHash` is read back). Mutation M1 - `ParseTagsAsSent` reverted to `ParseTags`, i.e.
  HEAD's algorithm - leaves that fact green while the tag fact goes red, which is HEAD's own code
  producing the shipped digests.
- **Both reverse failure shapes are pinned and both mutations land.** Hashing raw tags (no trim, dedupe
  or order) goes red on the legal re-spelling `" gpu , linux "` - the "this fix lost a replay" shape.
  Reverting to HEAD's parser goes red on the refused-tag resend - the original shape. AC-2's
  independence is shown by M1 (tag red, parameters green) and M2 (parameters red, tag green) being
  disjoint.
- **The adversarial case is the only thing that catches the cheap fix.** M6 - a sanitising
  `ParseTagsAsSent` that strips control characters and truncates at 64 - produces exactly **one** red,
  and it is `linux, gpu, lin\u0001ux`. The over-long entry and `bad\u0001tag` both survive sanitising
  unchanged, so without that case a "clean then hash" fix would have passed.
- **AC-3 asserts occupancy, not a filled-queue story.** `Assert.Single(runs)`, the occupancy row's `Id`
  equal to the id the 201 returned, `Status = Queued`, the 409 naming `queue-full` with
  `fieldErrors[0].field = queue` and no replay header, no binding for the second key, the run re-read as
  still `Queued` immediately before the replay, and the replay then answering 200 with the same id.
- **Precedence has a second guard.** M5 - deleting the early replay return in `RunService` - reds the
  disabled-project fact and the queue-cap fact together, because the unique-index race path still
  answers a replay on its own. The queue-cap fact is only meaningful paired with that one.
- **Counts and docs.** Fast **1284** = 1279 + 5, counted by the reviewer (5m44s, `ApiV1RunApiTests`
  18 -> 23, nothing else moved). §2's four figures agree, the all-seven arithmetic is 1284 + 12 + 4 + 2
  + 2 + 3 + 3 = 1310, and the sextuple filter is byte-identical in the §2 command, the §2 table row and
  `scripts/verify.sh`. Postgres: 3 skipped, 0 failed, `ZENKINS_TEST_POSTGRES` unset on this machine.
- **The one card outside this scope.** `docs/kanban/issues/f-029-queue-controls.md` is edited because
  AC-4 asks for exactly that; the reviewer checked that the sentence names a fact which really makes the
  occupancy assertions, and that no live "pinned by code order alone" claim remains in `docs/`. The
  implementer's refusal to test the 400-character joined-length limit was correct - that limit refuses a
  request and never drops a value, so it is not in the collision set - and the reason is left in the
  test's comment.
- **Fixed at close-out.** The card abbreviated the digest as `…1c1c` where the tail is `8c1c`, and the
  §2 note dated the five facts 2026-11-16 where its neighbours use the 17th; both corrected.
- Logs: `artifacts/q-017/` (both sides) - `mutation-*.log`, `review-mutation-1..6.log`,
  `review-{build,fast,postgres}.log`, `review-verdict.md`.
