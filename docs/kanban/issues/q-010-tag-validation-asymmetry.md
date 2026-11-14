---
id: Q-010
title: Tag validation asymmetry — the trigger accepts a union no single worker can cover (Q-005 hardening)
type: refactor
priority: P2
size: S
status: done
created: 2026-11-14
updated: 2026-11-15
blocked: none
---

## Goal

The tag rule the trigger enforces and the tag rule the claimer enforces are the same rule: either
the trigger refuses a run whose required tags no single worker can cover, or "union accepts, queue
starves honestly" is pinned as the documented semantics and asserted on both sides.

## Background

Q-005 recorded the asymmetry verbatim (development log, concurred by the independent reviewer):
trigger-side tag validation checks the **union** of all workers' tags, while claiming requires **one
worker** to cover all required tags — so a multi-tag run split across two workers passes the form and
then starves honestly (`Queued`), which the card adjudicated as AC-3's honest-starvation semantics.
The user's ledger names it a hardening candidate ("tag 并集校验与单 worker 全覆盖的不对称").

The risk is not incorrect execution — a run never lands on a worker that cannot cover it, and an
unmatched run never goes `Lost`/`Failed` for lack of a worker (Q-005 TC-002 proved both). The risk
is operator-facing surprise: the form accepts something the queue can only starve, and the only
explanation lives in a card's development log. Decide, align, pin.

## Decision (2026-11-15): keep union-accept + honest starvation, documented and pinned — no behaviour change

The exact seam, traced (file:line):

- **Trigger, manual form.** The project page's form POSTs `/projects/{id}/runs/start`
  (`ProjectDetail.razor`) to `RunEndpoints.StartRunAsync` (`RunEndpoints.cs:117`), which calls
  `RunService.TriggerAsync`. The union check is `RunService.cs:386-398`: every required tag must be
  declared by **at least one** organization worker row — all rows, no status filter (`:388`,
  Offline rows count too) — else the form answers `runError=tag-unknown`. So a required set
  `linux,cuda` passes when worker A declares `linux` and worker B declares `cuda`.
- **Trigger, webhook.** `WebhookDeliveryService.cs:337` → `RunService.QueueWebhookRunAsync`
  (`RunService.cs:455`). Required tags are **not checked against workers at all** on this path,
  deliberately, per the method's own contract (`RunService.cs:437-443`): a push whose tag set no
  worker covers yet stays `Queued`, "which is the honest answer". The two trigger paths do **not**
  share tag validation; the webhook already implements pure honest starvation.
- **Claim.** `RunService.ClaimAsync` filters candidates by
  `RunValidation.WorkerTagsCover(run.RequiredTags, worker.Tags)` (`RunService.cs:769`;
  `RunValidation.cs:153-164`): **one** Online, enabled worker's own tags must cover **all**
  required tags. A split set matches no worker's filter and stays `Queued` (never `Lost`, never
  `Failed` — Q-005 TC-002 proved the settle and the later execution).

The trade-off, both sides weighed:

1. **Tighten, honestly stated.** A form error naming the uncoverable set would make trigger and
   claim read as one rule and would surface the mistake at submit time — a real benefit in a
   static fleet.
2. **But the check cannot see the future, and the false negative is the workflow itself.** Workers
   come and go: registration, heartbeats that can retag a worker (`WorkerService.cs:713-715`), the
   offline monitor flipping rows. "No worker covers `linux,cuda` today" is not "no worker ever
   will" — the check cannot distinguish the two. A run submitted while only a `linux` worker
   exists, for a `cuda` machine that joins an hour later, is exactly the queue-first workflow
   honest starvation exists to serve; Q-005 TC-002 proved it end to end (a `gpu` run queued while
   only an Offline gpu *row* existed executed once a live gpu worker registered). Tightening
   rejects that run at the form — a false negative for future capacity.
3. **Tightening only the form moves the asymmetry rather than closing it.** The webhook trigger
   path already accepts uncoverable tag sets deliberately (F-017, `RunService.cs:437-443`). After a
   form-only tighten, the manual trigger would refuse what a signed push delivery queues; making
   the webhook match would mean rejecting valid signed pushes for missing capacity, contradicting
   F-017's recorded decision. Alignment at the trigger can therefore never be exact — the claim is
   the only point where the rule can be.
4. **What tightening would buy, the claim already guarantees.** The claim re-checks coverage
   against the live worker row on every poll (`WorkerTagsCover`), so a run never lands on a worker
   that cannot cover it and an unmatched run never goes `Lost`/`Failed` (Q-005 TC-002). The
   residual risk was operator surprise only — a documentation problem, fixable at the point of
   surprise without the false-negative cost.
5. A tightening would also inherit a second arbitrary choice: the union check reads all worker rows
   regardless of status, so a single-worker-coverage trigger check must pick online-only vs
   any-row — another knob that goes stale as the ring changes. Union-accept is the superset that
   never blocks a legitimately satisfiable run.

**Decision: keep.** The semantics are documented where the operator meets them — the trigger form's
tags-field help text and card description (`ProjectDetail.razor`) and the install guide's
worker-tags section (`docs/install.md` §6) — and pinned by a permanent end-to-end test
(`ConcurrencyTagValidationTests`). Trigger and claim demonstrably agree afterwards: the form's
acceptance rule (union) is the superset of the claim's rule (one worker covers all), and the pin
asserts both ends of that relationship — accepted-but-starved while uncovered, then claimed by the
one worker that covers all.

## Acceptance Criteria

- [x] A written decision on this card: tighten trigger-side validation to single-worker coverage, or
      keep union-accept + honest starvation as documented semantics — with the reasoning.
- [x] Whichever way, trigger side and claim side demonstrably agree, and a permanent end-to-end test
      pins the chosen behaviour (form response + queue outcome for a split-across-workers tag set).
- [x] Q-005's tag scenarios stay green (`FullyQualifiedName~Zenkins.Tests.Concurrency`); any claim
      query change runs the PostgreSQL contract gate and records it. (No claim query change —
      gate not required; evidence under Verification.)
- [x] The CHANGELOG mentions the decision if behaviour changed (via P-001's known-issues/notes
      entry). (No behaviour change; the entry is rewritten as resolved-by-card with the decision
      recorded.)

## Subtasks

- [x] Write the decision (with the trade-off) on this card.
- [x] Implement the alignment (documentation + pin; no validation change).
- [x] End-to-end pin test + Q-005 regression.

## Dependencies

- none technically; scheduled **after the v1.0 tag** (P-001) per the user's ledger decision (worked
  now — see the development log's sequencing note)

## Test Cases

### TC-001: The split tag set, end to end — PASS

Steps:
1. Register two workers whose tags cover the required set only in union (`linux` + `cuda` vs. a run
   requiring `linux,cuda`); submit the run through the real form; observe the response and the
   queue.

Expected:
- The chosen semantics, deterministically: the run is accepted, stays `Queued` through polling, and
  executes when a covering worker registers — matching the written decision, asserted by the test.

Result (observed 2026-11-15): PASS. Two real worker processes (`q010-linux` tags `linux`,
`q010-cuda` tags `cuda`) online; the run submitted through the real form (`/projects/{id}/runs/start`,
the product's own POST): **302 redirect to the new run with no `runError`** — the union rule accepts
the split set exactly as it accepts a single-tag run. Queue outcome: the run stayed `Queued` through
an 8 s hold window sampled repeatedly plus the settle window, a linux-tagged probe's direct claim
returned 204 at every sample (a worker matching one tag but not both — the asymmetry's own shape),
and neither mismatched worker's output ever mentions the run; never `Lost`, never `Failed`. When a
`linux,cuda` worker registered, the run executed **exactly once** with the three-way attribution
standard (run row names the cover worker; the owner's process output claims it; the persisted log
names only that worker) and an uncorrupted step sequence. Green on 3 consecutive runs (the
Concurrency-filter run plus two solo runs) — deterministic.

### TC-002: The matched case unchanged — PASS

Steps:
1. Trigger a run whose required tags one worker covers.

Expected:
- Accepted and placed exactly as Q-005 TC-002 proved; no behaviour change.

Result (observed 2026-11-15): PASS. The same required set (`linux,cuda`) with one live worker
declaring all of it — accepted by the same form and placed as Q-005 TC-002 proved: `Running`
immediately, the run row names the covering worker's id, executed once under the same three-way
attribution standard, steps uncorrupted. No behaviour change.

## Development Log

### 2026-11-14

- Card opened from Q-005's recorded seam and the user's ledger decision: deferred past v1.0 with a
  CHANGELOG mention, scheduled v1.0.1. Next step: write the tighten-vs-document decision.

### 2026-11-15

- Sequencing: the card was scheduled after the v1.0 tag per the ledger, but the user directed the
  board be cleared while the v1.0 tag is blocked on their push decision — Q-010 is worked now, and
  its content (documentation + pin, no behaviour change) is v1.0.1 material either way.
- Traced the seam on both sides (the Decision section holds the file:line record): the trigger's
  union check (`RunService.cs:386-398`, all worker rows regardless of status), the webhook path
  deliberately unchecked (`QueueWebhookRunAsync`, `RunService.cs:437-443` — the two trigger paths do
  not share tag validation), and the claim's single-worker coverage (`RunService.cs:769` +
  `RunValidation.WorkerTagsCover`, `RunValidation.cs:153-164`).
- **Decision: keep union-accept + honest starvation as documented semantics** (full reasoning in
  the Decision section). The decisive considerations: a trigger-time single-worker check cannot
  distinguish "no worker covers it today" from "no worker ever will", so it false-negatives the
  queue-first/worker-registers-later workflow Q-005 TC-002 proved — what that TC proved also cuts
  against tightening directly, because the starved-then-executed run is the semantics working, not
  a defect; a form-only tighten would leave the webhook path accepting what the form refuses, so
  the asymmetry moves rather than closes; and the claim already guarantees the safety properties
  per poll, leaving operator surprise as the only residual risk — a documentation problem.
- Implemented the KEEP alignment: no product behaviour change. The trigger form's tags field and
  its card description state the rule where the operator enters tags (`ProjectDetail.razor`), and
  `docs/install.md` §6 states it where workers are configured (the `ZENKINS_WORKER_TAGS` row now
  names the one-worker-covers-all rule, plus a short paragraph on split sets). The CHANGELOG's
  known-issue entry is rewritten as resolved-by-card with the decision recorded (the preamble's
  rule: the card updates its entry when it lands).
- The permanent pin: `tests/Zenkins.Tests/Concurrency/ConcurrencyTagValidationTests.cs` — TC-001
  and TC-002 in one scenario, following the Q-005 harness patterns (real server, real worker
  processes, the real form POST, probe claims against the real endpoint, read-only row reads).
  Green on 3 consecutive runs; `FullyQualifiedName~Zenkins.Tests.Concurrency` → 6 passed / 0 failed
  / 0 skipped (Q-005's scenarios + B-002's cap regression + this pin); fast suite 1047 passed /
  0 failed / 0 skipped (the pin is `Category=Concurrency`, which runs in the fast suite). development.md
  §2's counts updated (fast 1047, all six 1066 — arithmetic as B-002's: fast + the five harnesses'
  19) with its header note dated.
- PostgreSQL contract gate: not required — **model unchanged / claim query unchanged**; the diff
  contains no C# product code at all (Razor help text, two docs, the CHANGELOG, one new test file),
  verified from the diff. No migration added.

### 2026-11-15 — independent review: PASS

- **(a) Trace, re-verified line by line.** Every file:line claim in the Decision section holds
  against the current tree: the form posts `/projects/{id}/runs/start` (`ProjectDetail.razor`),
  mapped at `RunEndpoints.cs:36`, calling `TriggerAsync` at `RunEndpoints.cs:117`; the union check
  is `RunService.cs:386-398` — **per-tag**: the union of all organization worker rows' tags is
  built at `:388` (`context.Workers.ToListAsync`, no status predicate; the only filter is the
  tenant `HasQueryFilter`, `ZenkinsDbContext.cs:649`, so Offline rows count exactly as the card
  says) and the first required tag absent from that union answers `TagUnknown` (`:396`; the code
  string is `"tag-unknown"`, `BuildRunStateMachine.cs:15`); the webhook path is
  `WebhookDeliveryService.cs:337` → `QueueWebhookRunAsync` (`RunService.cs:455`), whose body
  validates branch and commit only — no worker-tag check — with the deliberate contract in its own
  doc comment (`:437-439`); the claim filters queued candidates by
  `WorkerTagsCover(run.RequiredTags, worker.Tags)` (`:769`; `RunValidation.cs:153-164`,
  `required.All(available.Contains)` — one worker's own tags must cover the whole set) after the
  worker row must be `Online` and not disabled (`ClaimAsync` step 2); heartbeat retag is
  `WorkerService.cs:713-715`. No cited line was wrong.
- **(b) Decision soundness, checked in both directions.** The webhook's accept is deliberate
  (comment and body agree), so a form-only tighten would indeed leave the webhook accepting what
  the form refuses — claim (ii) is correct. The union check is per-tag-any-worker, not whole-set:
  `tag-unknown` fires only when no row at all declares a tag, which is exactly what the new help
  text says and what the install guide's new paragraph describes (split set → accepted → queued
  until a covering worker registers; never mis-executed, never failed). Offline-nuance wording:
  both texts say "at least one worker / registered worker" — literally true (offline rows persist
  and remain registered) and neither claims online-only; the card records the nuance explicitly.
  Minor note, no defect. The claim-side safety property holds: every poll re-reads the live worker
  row and re-filters with `WorkerTagsCover`; an unmatched run is `Queued`, and nothing on that
  path can reach `Lost` (needs a `Running` run with a dead worker) or `Failed` (worker-reported
  only).
- **(c) The pin, scrutinized then run.** The form POST is the real one — `PostTriggerFormAsync`
  GETs the page, extracts the antiforgery meta token and POSTs form-urlencoded fields to
  `/projects/{projectId}/runs/start` (no service-level shortcut). The Queued hold cannot
  false-pass: every sample opens a fresh read-only SQLite connection to the server's DB file
  (`MilestoneHost.ReadAsync`), so a `Failed`/`Lost` flip would fail the sample; the probe's 204 is
  asserted at every sample, and the mismatched workers' output is re-checked after execution in
  the attribution assertion. Exactly-once/attribution is the three-way standard (run row names the
  cover row; the owner's process output claims it; the persisted log names only that worker), the
  probe is a token-only row that cannot execute, and the step sequence is asserted uncorrupted.
  Cleanup is the harness's own (host dispose kills the process tree and deletes the root; the git
  fixture disposes). Green on 4 consecutive solo runs, 17.8 s / 18.4 s / 18.1 s / 18.2 s wall,
  plus the filter run below — 5 green runs of the pin in this review, deterministic.
- **(d) Regression re-run.** `dotnet build -c Release` 0 warnings / 0 errors;
  `FullyQualifiedName~Zenkins.Tests.Concurrency` 6 passed / 0 failed / 0 skipped (48.8 s); fast
  suite 1047 passed / 0 failed / 0 skipped (2 m 10 s). development.md §2 arithmetic verified from
  its own table: 1047 + 8 + 4 + 2 + 2 + 3 = 1066; the fast-row parenthetical reads "(1047 tests,
  snapshot of the revision)" and the header note is dated 2026-11-15.
- **(e) CHANGELOG.** The rewritten Q-010 entry follows the preamble's card-coupling rule ("the
  card updates or removes its entry when it lands") and mirrors B-002's closed-entry pattern
  ("resolved by card Q-010" ↔ "closed by card B-002"; both close with "ships with the next
  release"). "No behaviour change" is literal: the diff contains zero product `.cs` files — the
  razor hunk adds only a help paragraph and a Razor comment (no `@code` change); no migration
  added, so the PG gate's not-required call stands. (B-002's review already recorded that the
  release section's "1045 tests" is the tagged revision's historical snapshot; correctly left
  alone here.)
- **(f) Bookkeeping.** All four ACs ticked with evidence that holds — AC-2's agreement is pinned
  by the end-to-end test asserting both ends of the superset relationship (union accept at the
  form, single-worker cover at the claim) plus the two docs; the TC results match my runs; the dev
  log is dated; `status: review` found as required; the board row was untouched before this
  review. No stray processes or temp roots left; no commit made.

- **Reviewer's own runs.** `dotnet build -c Release` 0/0; pin solo
  (`FullyQualifiedName~ConcurrencyTagValidationTests`) 4/4 consecutive green — 17.8 s, 18.4 s,
  18.1 s, 18.2 s; `FullyQualifiedName~Zenkins.Tests.Concurrency` 6 passed / 0 failed / 0 skipped
  (48.8 s); fast suite 1047 passed / 0 failed / 0 skipped (2 m 10 s). No commit made.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for the asymmetry Q-005 recorded (P2, deferred with rationale). | Closed | Resolved by decision: union-accept + honest starvation kept as documented semantics, documented at the point of surprise (form help text, install guide) and pinned end to end (`ConcurrencyTagValidationTests`). |

## Verification

- Related files: `src/Server/Runs/RunService.cs` (trigger union check `:386-398`; claim filter
  `:769`), `src/Server/Runs/RunValidation.cs` (`WorkerTagsCover` `:153-164`),
  `src/Server/Runs/RunEndpoints.cs` (form endpoint), `src/Server/Components/Pages/Projects/ProjectDetail.razor`
  (help text), `docs/install.md` §6, `tests/Zenkins.Tests/Concurrency/ConcurrencyTagValidationTests.cs`
  (the pin), `CHANGELOG.md` (entry rewritten as resolved).
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter
  "FullyQualifiedName~Zenkins.Tests.Concurrency"`; the fast suite (`Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd`).
  PostgreSQL contract gate only if the claim query changed — it did not.
- Results (observed 2026-11-15): `dotnet build -c Release` 0 warnings / 0 errors;
  `FullyQualifiedName~Zenkins.Tests.Concurrency` → 6 passed / 0 failed / 0 skipped (Q-005's five
  scenarios + B-002's cap regression + the new pin), the pin itself green on 3 consecutive runs
  (TC-001 and TC-002 as recorded above); fast suite → 1047 passed / 0 failed / 0 skipped (1046 +
  the pin). PostgreSQL contract gate not required: **model unchanged / claim query unchanged** —
  the diff contains no C# product code at all (verified from the diff; no migration added). No
  commit made; the tree is left for review.
