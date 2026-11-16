---
id: Q-012
title: The F-020/F-021 review proved four behaviours with probes that the suite does not carry
type: test
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

The behaviours the F-020/F-021 review had to prove with throwaway probes become regression tests in
`tests/`, so the next refactor cannot delete them as dead code.

## Background

Both cards passed review on 2026-11-16 with every criterion ticked, but four of those criteria had
no test behind them; the reviewer wrote probes in its own checkout to establish the truth and the
probes were not kept. Each is a defensive branch or a snapshot semantic, which is exactly the shape
that dies quietly in a refactor.

## Acceptance Criteria

- [ ] A Freestyle project cannot be given a webhook, and a delivery to one records
      `ignored:no-repository` without queueing a run — both in `tests/`
      (`WebhookService.cs:186-193`, `WebhookDeliveryService.cs:179-191`, `:342-346`).
- [ ] Editing a Freestyle project's steps after a trigger does not move the already-queued or
      already-running run's snapshot (`RunService.cs:456-459`).
- [ ] `Project.Kind` cannot be changed by an edit that posts a different kind
      (`ProjectService.cs:242-257`).
- [ ] The trigger form renders each parameter input pre-filled with its definition's default, not
      just with a valid field name (`ProjectDetail.razor:264-325`).
- [ ] An out-of-bounds parameter value is refused at the trigger endpoint, not only by the unit-level
      resolver (`ProjectParametersTests.cs:207` is the only coverage today).
- [ ] The script step's on-disk shape is pinned: `temp/step-N.cmd|sh`, UTF-8 **without a BOM**, and
      the `sh -e` / `cmd /d /s /c` invocation (`PipelineRunner.cs:942-960`). The Windows half may be
      asserted at the code level with a recorded reason if no Windows machine is in the loop.
- [ ] The fast suite stays green and its test count moves by exactly the number of facts added
      (`docs/development.md` §2).

## Subtasks

- [ ] Add the four missing facts in the suites they belong to (Webhooks, Runs, Projects, Pipelines).
- [ ] Add the bound-violation case to the existing `ParameterizedRunTests` theory.
- [ ] Update `docs/development.md` §2's counts.

## Dependencies

- none (F-020 and F-021 are accepted)

## Notes and Decisions

- The review's own probe source lived only in its `/tmp` checkout and is deliberately not pasted
  here; each item above names the production file and line the probe exercised, which is what the
  test needs.
