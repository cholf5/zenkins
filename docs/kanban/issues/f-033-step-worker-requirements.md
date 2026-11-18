---
id: F-033
title: Per-step worker requirements and label expressions — the runs_on the PRD already promises
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A step can require worker capabilities (`runs_on = { "windows", "dotnet" }`, plus
`&&` / `||` / `!` expressions), the run's required labels are the union of what its
steps need, and an unsatisfiable requirement is refused with an explanation instead
of running on the wrong machine.

## Background

PRD §3.4.4 documents `runs_on` in its example pipeline, and the implementation has
no such field: `PipelineStep` carries no worker requirement, and the only knob is
the run-level `RequiredTags` a person types at trigger time. Q-010 records the
consequence: the trigger accepts a union of tags no single worker can satisfy, then
the run sits in the queue forever.

Two honest paths exist, and the card must say which one it takes:

- **(a) split a run across workers** — workspace hand-off, artifact/log transfer,
  per-step leases. Large; it is the prerequisite for parallel stages and belongs
  with that work, not here.
- **(b) resolve the requirement at definition time** — compute the union, let the
  scheduler assign one worker that satisfies all of it, and refuse a definition
  whose steps cannot share one worker.

This card takes (b): it closes the PRD gap and the Q-010 asymmetry without pretending
to do cross-worker builds.

## Acceptance Criteria

- [ ] Lua accepts `runs_on = { "linux", "arm64" }` (all must match) and an
      expression string (`"linux && (arm64 || x64)"`, `!windows`) per step; the
      frozen spec stores the normalized requirement per step.
- [ ] The run's required labels are the union of its steps' requirements combined
      with the trigger's `RequiredTags`; a step whose requirement contradicts another
      step's is refused at definition time as `pipeline-invalid` with both steps
      named. **Contradictory by construction is the only refusal** - see the two
      situations in AC-4.
- [ ] Expression parsing (`&&`, `||`, `!`, parentheses, tag atoms) is one shared
      implementation used by save-time validation, the trigger's satisfiability check
      and the scheduler — not three interpretations.
- [ ] **Two situations, two answers.** (a) A requirement that is **contradictory by
      construction** - `windows && linux`, `!linux && linux`, or two steps that cannot
      share one worker - is refused **at definition time** as `pipeline-invalid`, with
      both steps and both labels named; no run row ever exists. (b) A requirement that
      is satisfiable in principle but that **no worker satisfies right now** is **not
      refused**: the run is queued, and the missing label is recorded in three places
      that survive the request - the run page says which label is missing, the run row
      stores the normalized requirement it is waiting on, and a queryable record on the
      run carries the same reason (a product state, not a log line). Refusing (b) would
      turn every poll, webhook, schedule and upstream trigger into a silent drop,
      because a worker that starts in ten minutes is normal operation, not a bad
      request. (Q-010's asymmetry is closed by answering both questions with the same
      predicate; Q-010's card is updated to say which half it recorded.)
- [ ] The scheduler assigns a worker that satisfies the whole expression; a worker
      that satisfies part of it is never chosen.
- [ ] The run page shows the requirement in its readable form (`linux and arm64`, not
      the stored DNF) and, on a queued run whose requirement has no matching worker,
      says which label is missing instead of "queued" forever. Nothing waits forever
      invisibly and nothing is failed by a clock: a queue timeout is a different card's
      subject (F-009 is about a running run, not a queued one).
- [ ] Freestyle steps get the same field in the steps editor.
- [ ] PRD §3.4.4 and §3.3.5: record the expression grammar, the union rule and the
      explicit statement that a run executes on one worker.

## Subtasks

- [ ] Shared `WorkerRequirement` type: parse (Lua table or expression string),
      normalize, `SatisfiedBy(tags)`, `Union`, and a readable `ToString`.
- [ ] Spec: `PipelineStep.RunsOn` (normalized requirement), validation rules and the
      contradiction check across steps.
- [ ] Definition phase: collect per-step requirements into the spec.
- [ ] Scheduler/claim: evaluate the union against candidate workers' tags; the
      existing `RequiredTags` string stays the storage shape (or is upgraded to the
      normalized form — record the migration decision).
- [ ] Trigger path: satisfiability check against known workers' tags before insert;
      Q-010's validation asymmetry test updated to the new contract.
- [ ] UI: requirement display on the run page and in the pipelines tab; Freestyle
      field.
- [ ] Update Q-010 (close or re-scope) and the PRD sections named above.

## Dependencies

- Q-010 (the tag asymmetry this card closes)
- Q-005 (multi-worker tag assignment) — accepted, its scheduler is the evaluation
  point

## Test Cases

### TC-001: An expression selects the right worker

Steps:
1. Two workers: tags `linux,x64` and `linux,arm64`. A pipeline with one step
   `runs_on = "linux && arm64"`.

Expected:
- The arm64 worker claims it; the x64 worker never does.

### TC-002: Contradicting steps are refused at definition time

Steps:
1. Step A `runs_on = { "windows" }`, step B `runs_on = { "linux" }`; trigger.

Expected:
- The run fails as `pipeline-invalid`, naming both steps and both labels; no run
  sits queued.

### TC-003: A requirement nobody satisfies yet is queued with its reason, not refused

Steps:
1. Only `linux` workers exist; trigger with `RequiredTags = "windows"` (and a
   step-level `windows`).

Expected:
- The run **is queued** (a trigger from a poll or a webhook has nobody to show an
  error to, and a `windows` worker may start later), the run row stores the normalized
  requirement, and its page says `windows` is missing.
- A `windows` worker joining later claims it; a `linux`-only worker never does.
- The only refusal of this shape is **contradictory by construction**
  (`windows && linux`), which is answered at definition time as `pipeline-invalid` -
  see TC-002.

### TC-004: The requirement is readable, not a raw expression blob

Steps:
1. Open a run whose requirement is `linux && arm64`.

Expected:
- The page reads "linux and arm64" (or the normalized form), and a queued run with
  no match names the missing label.

## Notes and Decisions

- **Why a "no worker right now" trigger is queued rather than refused** (revised
  after review). The original wording of AC-4/TC-003 said "refused". That conflates two
  different facts: a requirement that can *never* hold, and a requirement that does not
  hold *yet*. The first is a mistake in the definition and must be caught where the
  definition is read. The second is ordinary operation - workers come and go, and a
  `windows` runner that registers in ten minutes is exactly the case polling exists
  for. Refusing the second would make every non-human trigger (poll, webhook, schedule,
  upstream) fail on a condition its caller cannot see or fix, i.e. a silent drop. The
  asymmetry Q-010 recorded is closed either way, because both questions are now asked
  with the same predicate.
- **No queue timeout.** A queued run waiting for a worker is visible (page, stored
  requirement, record) and stays queued until a matching worker appears or a person
  cancels it. "Fail a run that has waited too long" is F-009's neighbourhood (a
  *running* run) and a separate card; adding a clock here would hide the very state
  this card makes observable.
- **The observation path's trace is a derivation, not a record.** When the branch observation refuses a
  requirement, what remains is the branch's own verdict (has-pipeline at that commit) and the absence of a
  run; there is no field that says "refused because <requirement>". That is enough today because a
  contradiction on this path is nearly unreachable - the definition layer already refuses it once, and the
  observation entry's tags come from a webhook row the form validated. It becomes a real gap the moment
  those tags can arrive from a source the definition layer never checked (an imported repository, a
  hand-edited row, another trigger's tags routed into an observation); that is when `BranchJob` gets a
  `RefusalReason` column beside the webhook's `refused:requirement` outcome, and not before.
- **Boundary: where the steps are known at creation time.** A Freestyle project and an
  inline-script Pipeline carry their steps on the project row, so the union is known
  when the run is created and the run records it. A repository Pipeline's steps live in
  `pipeline.lua`, which the Server learns only when a Worker reads it - so for those,
  the union cannot be part of the trigger's own answer. What this card guarantees there:
  the shared parser rejects a contradictory definition (`pipeline-invalid`, both steps
  named) wherever it is read, and a step requirement the claiming worker does not
  satisfy fails that run with a reason instead of quietly building on the wrong
  machine. Pretending otherwise would mean claiming a knowledge the Server does not
  have at trigger time.
- Rejected in this card: cross-worker step execution. It needs workspace transfer,
  per-step leases and a different cancellation story; it is the prerequisite of
  parallel stages, and that work has its own place (parking lot).
- Requirement storage is normalized text on the spec and on the run, so a later
  scheduler change cannot reinterpret old runs.
## Contract migrations this card caused (tests moved, not product code)

- `Concurrency.ConcurrencyTagAssignmentTests.Required_tags_place_a_run_and_starve_unmatched_workers_honestly`
  used to assert the trigger form answered `runError=tag-unknown` and queued nothing. Under this card a
  requirement nobody satisfies *yet* is queued, so the fact now asserts the run exists, is `Queued`, waits
  on `gpu`, and no worker is ever dispatched to it - the test's original intent ("a worker that does not
  fit is never sent to do the work"), kept and asserted just as strictly.
- `Api.ApiV1RunApiTests.A_reused_key_refuses_a_different_tag_set_before_the_worker_coverage_check` had the
  same old contract in its **control** (a fresh key with `linux,docker` expecting a 400). It now asserts that
  the control body is *accepted and queued* - which makes the 409 above it purely the key's decision. The
  fingerprint assertions themselves were never at risk: the same key with a different tag set still answers
  `409 idempotency-key-reused`, and the same set re-spelled (`gpu,linux`, ` linux\t, gpu\v`, a repeat) still
  replays the original run.

## Close-out verification (2026-11-18)

Thirteen commits, two refused review rounds, and both refusals were the same lesson: the facts covered
the code path but not the shape of the input.

- **The card had to be rewritten before it could be built.** Its acceptance criteria said "no matching
  worker → refuse", which conflates two different things: a requirement that can never hold
  (`windows && linux`) and one nothing carries *yet* (a Worker that comes online tomorrow). Refusing the
  second turns every trigger into a silent drop, so the card now says: a contradiction is refused at
  definition time as `pipeline-invalid`, naming both steps and the labels that clash; a requirement
  nobody satisfies yet is **queued** with its reason, and a queue timeout is explicitly a different
  card's subject.
- **One decision, three call sites.** Before this card, three paths queued runs with three different
  notions of "can anyone run this": claim compared the whole tag set (`RunService:2134`), the trigger path
  only checked that a tag *existed* (`:884`), and the definition-observation path checked nothing at all
  (`DefinitionValidationService:619`). All three now go through `WorkerRequirement.SatisfiedBy`, and the
  contradiction refusal moved down into `QueueWebhookRunAsync`, the funnel every non-form trigger passes
  through, so a new caller cannot forget it.
- **The refusal is visible now, not a 400 nobody sees.** A webhook whose requirement can never hold used
  to answer `InvalidPayload`, roll back and leave no record. It records a delivery with
  `refused:requirement`, answers 2xx, and leaves `RunId` empty - and that word passed Q-022's two
  vocabulary gates, which is how we know those gates work: adding it to `All` alone reddened the endpoint
  documentation fact.
- **DNF on disk, the expression as written on screen.** A requirement parses to disjunctive normal form
  (`MaxDisjuncts` 64) so union, satisfiability and `SatisfiedBy` are set operations; the original text is
  kept beside it and the page renders `ReadableForm` (`arm64 and linux`), never the storage form.
  `MaxStorageFormLength` is a policy cap, not a column width - the column is `text`, deliberately, and the
  constant says so.
- **Two facts that only a disjunction could have caught.** The first refusals were all about *coverage*:
  every claim fact used a conjunctive requirement, where the stored column and the legacy comma string are
  equal, so making `RequirementFor` ignore the column entirely left 44 facts green. A `linux && (arm64 ||
  x64)` requirement claimed by a `linux,arm64` worker closes it, with the reverse assertion that treating
  the comma string as a conjunction must refuse that worker. The cross-step contradiction check is
  satisfiability, not textual overlap, and both directions are pinned - a clash with no shared atom is
  refused, and two steps that merely share a label are not.
- **Writing the storage-form fact found a closed loop that did not close.** The parser accepted at most
  1000 characters while `StorageForm` could produce 4000, so a large requirement would be written and
  then fail to read back, silently falling back to the comma string on every claim. The two bounds are
  now the same number, and the fact asserts that the parser can re-read its own output.
- **The contract change was migrated in the tests, and the intent survived.** Two Windows-only reds were
  the old contract still encoded in tests, not product regressions: the idempotency test's control block
  expected `tag-unknown`, and the concurrency test expected a refusal where a run is now queued. The
  control now proves the 409 is *the key's* decision (the body is accepted under a fresh key), and the
  concurrency fact still kills a scheduler that ignores requirements (`AssertClaimGotNothing`).
- **Two known gaps are recorded rather than built.** The observation path's visible trace is the
  derivation "verdict exists and no run" rather than a stored refusal reason - it would need a `BranchJob`
  column, and the condition under which it becomes a real gap is written in the card. The three trace
  surfaces themselves are a page sentence, a stored requirement with `Status == Queued`, and a queryable
  predicate, each with its own killing mutation.
- **Counts and gates.** The authoritative run is on the Windows host: **1462 total, 1457 passed, 2
  failed, 3 skipped, 672 s**. One failure is main's own `ProcessRunnerTests` handle race and the three
  skips are POSIX-only; the other failure is the load-sensitive `The_step_limit_is_clamped...` timing
  fact, which was red in two of three runs but green in four isolated reruns, and is filed as its own
  card rather than papered over here. `Category=Postgres` went **8 -> 9** and passed 9/9, with both
  migrations applied from scratch and `RequiredRequirement` reading `text`.
