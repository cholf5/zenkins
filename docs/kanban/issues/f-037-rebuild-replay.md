---
id: F-037
title: Rebuild and replay — re-run a finished run with the same inputs, or with the current definition
type: feature
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

A Member can re-run a finished run in one click, either exactly as it was (same
commit, parameters, tags, timeout and frozen definition) or against the project's
current definition, and the new run says which one it is.

## Background

Retrying a flaky failure today means re-entering the trigger form: pick the branch
again, retype the parameters, rediscover that the commit has moved in the meantime.
Jenkins has the Rebuild plugin for exactly this, and "Replay" for re-running with the
same script after an edit.

Two different intents have to stay distinct, because mixing them silently rebuilds
different code:

- **Rebuild** = the same inputs, including the **commit pinned by the original run**
  (F-017 already pins a commit; a rebuild must pin it again, not take the branch head).
- **Replay** = the same definition text, no matter what the project or repository says
  now (for an inline project, the run's own script snapshot).

## Acceptance Criteria

- [ ] The run page offers Rebuild (same inputs) and Replay (same definition), each
      producing a new run whose page links back ("rebuilt from #N") and whose
      `TriggerType` is `Rebuild`/`Replay`.
- [ ] Rebuild copies the original snapshot: branch, pinned commit, parameters, required
      tags, timeout, and the definition the original ran (a Freestyle project whose
      steps changed since must still rebuild the old steps).
- [ ] Replay uses the original definition text only: for an inline project or a
      Freestyle project the run's own snapshot; for a repository pipeline the run's
      spec cannot be replayed as source, so Replay is offered only where a script text
      exists and the button's tooltip says why it is absent otherwise.
- [ ] A rebuild of a run whose commit no longer exists fails honestly at clone time
      (`git-*` failure reason) rather than silently building the branch head.
- [ ] Disabled projects (F-028), queue caps (F-029) and concurrency limits apply to
      rebuilds exactly as to a fresh trigger.
- [ ] Parameters are editable before a rebuild ("Rebuild with parameters…") — one
      form, defaults prefilled from the original, the same validation.
- [ ] Artifacts, logs and steps of the original are never modified by a rebuild.

## Subtasks

- [ ] `BuildRun.RebuiltFromRunId` (nullable, no FK) + trigger types `Rebuild`/`Replay`.
- [ ] `RunService` rebuild path: read the original's snapshot, re-validate it against
      today's project (kind unchanged, credential still assigned, parameter definitions
      still containing the names), then queue through the same create path.
- [ ] UI: two buttons on the run page (Rebuild enabled only for terminal runs), the
      optional parameter form, and the "rebuilt from" line.
- [ ] Log/run page wording for "the commit this rebuild pinned is no longer available".
- [ ] PRD §3.5: record both intents and why they are separate buttons.

## Dependencies

- F-036 (numbers) for the "from #N" link (a guid fallback is acceptable)
- F-017 (pinned commit semantics) — accepted

## Test Cases

### TC-001: Rebuild pins the same commit even after the branch moved

Steps:
1. Build run `#1` at commit A.
2. Move the branch head to B; rebuild `#1`.

Expected:
- The new run's claim pins A; its branch and parameters match `#1`.
- A plain fresh trigger at that moment would have used B (asserted in the same test).

### TC-002: Rebuild survives a definition change

Steps:
1. Freestyle project with steps S1; run it.
2. Change the steps to S2; rebuild the old run.

Expected:
- The rebuild executes S1 (the original snapshot) and the page says so; a fresh
  trigger executes S2.

### TC-003: A vanished commit fails honestly

Steps:
1. Build at commit A, then rewrite history so A is gone; rebuild.

Expected:
- The rebuild fails with a `git-*` failure reason naming the missing commit; it does
  not build the branch head.

### TC-004: Limits still apply

Steps:
1. Fill the queue to the cap (F-029), then rebuild.

Expected:
- The rebuild is refused with the same `queue-full` message a fresh trigger gets.

## Notes and Decisions

- Replay for a repository pipeline is deliberately absent: the run stores the frozen
  spec, not the source that produced it, and pretending the spec can be re-executed as
  a definition would erase the definition phase.
