---
id: DOC-002
title: Lua runtime contract (engine, sandbox, execution model)
type: docs
priority: P2
size: S
status: review
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

One document that pins the Lua engine, the sandbox surface, the execution model and the
engine-switch gate, so the v0.3 cards implement a contract instead of re-deciding it in code
review.

## Background

The roadmap marked the engine choice (MoonSharp 2.0.0 / 3.0.0-beta / hand-rolled) as a spike to be
done before writing the SDK. It was resolved by measurement instead of a spike card: both candidate
packages were run through the same probe set on `net10.0`, and they came out behaviourally
identical for every capability Zenkins uses. The evidence, the resulting constraints and the
conditions that would re-open the decision now live in [`docs/lua-runtime.md`](../../lua-runtime.md).

Six findings in that document change what the cards may do, and none of them were known before the
probe:

1. `CoreModules.Preset_HardSandbox` removes `pcall`, `xpcall`, `setmetatable` and `coroutine` — the
   sandbox is an explicit module composition, not a preset.
2. `dynamic = nil` is not enough: `package.loaded.dynamic` keeps the CLR bridge (`eval` works).
3. `error({...})` degrades to a string across `pcall`; SDK errors are message strings.
4. `DoString` cannot be interrupted; runs are coroutines driven by the host.
5. `collectgarbage('count')` returns `false`; memory limits are the OS's job.
6. The language ceiling is Lua 5.2 on both engines.

## Acceptance Criteria

- [x] `docs/lua-runtime.md` records the engine decision, the rejected alternatives and the switch
      triggers, with measured evidence rather than assertion.
- [x] The sandbox contract names the exact module composition, the mandatory post-construction
      hardening, and the escape vector through `package.loaded`.
- [x] The execution model specifies the coroutine driver, the host-call suspension protocol, the
      budget/cancellation path, thread affinity and the memory story.
- [x] The dependency contract covers the CPM pin, scope, license (BSD-3-Clause) and the required
      third-party notices file.
- [x] A conformance fixture table exists so an engine upgrade is a deliberate, checked decision.
- [x] The probe program is reproduced in an appendix, so every number can be re-derived.
- [x] `README.md` and `docs/kanban/roadmap.md` link the document.

## Subtasks

- [x] Run the probe set against both packages and record what was measured.
- [x] Write the contract (decision, sandbox, language subset, execution model, layering, logging,
      dependency, fixtures, limitations).
- [x] Link it from the README and the roadmap.

## Dependencies

- none

## Test Cases

### TC-001: Links and claims resolve

Steps:
1. Open `docs/lua-runtime.md` and follow every relative link and section anchor.
2. Re-run Appendix A against the pinned package.

Expected:
- Every link resolves; Appendix A reproduces the numbers quoted in §2 (timings may differ by
  machine, `nil`/error results may not).

## Development Log

### 2026-11-13

Contract written and evidence collected on macOS 26.5 (arm64), .NET SDK 10.0.403, in a throwaway
`net10.0` console app (`MoonSharp 2.0.0` and `3.0.0-beta.1`); nothing from the probe is compiled
into this repository. The two versions are behaviourally identical for our surface (same language
level, same sandbox sets, same error text, same timings within noise, same `pcall`-with-`__call`
defect), so 2.0.0 was chosen as the stable tag with 3.0 as the documented switch target.

Two probe results are worth flagging for the reviewers of this document, because they are the
reason the cards are shaped the way they are: the `Preset_HardSandbox` finding (§3) means a card
that "just uses the safe preset" would ship a runtime with no `pcall`; and the `package.loaded`
escape (§3.1) means a card that nils `dynamic` and declares victory would ship a runtime with a
working CLR `eval`. Both are pinned as fixtures (Appendix A, §9 items 8–9).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `docs/lua-runtime.md` (new), `README.md`, `docs/kanban/roadmap.md`
- How to run/verify: read the document; run Appendix A in a scratch console app.
- Results: document written 2026-11-13; every quoted measurement came from a run on this machine.
  No build or test impact (documentation only).
