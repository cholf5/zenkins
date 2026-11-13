---
id: DOC-002
title: Lua runtime contract (engine, sandbox, execution model)
type: docs
priority: P2
size: S
status: todo
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

1. `CoreModules.Preset_HardSandbox` removes `pcall`, `xpcall`, `setmetatable` and `coroutine`, and
   removes `os` entirely — the sandbox is an explicit module composition, not a preset. Its
   opposite, `Preset_SoftSandbox`, keeps the CLR bridge (`dynamic`, `json`).
2. `dynamic = nil` is not a complete defence: if the `Dynamic`/`Json` module is composed in,
   `package.loaded.dynamic` keeps the bridge alive (`eval` works). The contract therefore forbids
   composing those modules *and* nils the globals.
3. `error({...})` degrades to a string across `pcall`; SDK errors are message strings.
4. `DoString` cannot be interrupted; runs are coroutines driven by the host, and the driver must
   branch on `CoroutineState` because a force-suspended resume rejects arguments.
5. `collectgarbage('count')` returns `nil`; memory limits are the OS's job, with no macOS mechanism.
6. The language ceiling is Lua 5.2 on both engines.

## Acceptance Criteria

- [x] `docs/lua-runtime.md` records the engine decision, the rejected alternatives and the switch
      triggers, with measured evidence rather than assertion.
- [x] The sandbox contract names the exact module composition (including `GlobalConsts` and why),
      the mandatory post-construction hardening, and the registry hazard for what it is: a door that
      reopens only if `Dynamic`/`Json` is composed in.
- [x] The execution model specifies the state-branching coroutine driver, the host-call suspension
      protocol (with a real transcript), the budget/cancellation path, thread affinity and the
      memory story per platform.
- [x] The dependency contract covers the CPM pin, scope, license (BSD-3-Clause) and the required
      third-party notices file.
- [x] A conformance fixture table exists with exact expected output, so an engine upgrade is a
      deliberate, checked decision.
- [x] The probe program is reproduced in an appendix that runs as-is, so every claim can be
      re-derived.
- [x] `README.md` and `docs/kanban/roadmap.md` link the document; `docs/development.md` §7 carries
      the deployment boundary the sandbox section points at.

## Subtasks

- [x] Run the probe set against both packages and record what was measured.
- [x] Write the contract (decision, sandbox, language subset, execution model, layering, logging,
      dependency, fixtures, limitations).
- [x] Link it from the README and the roadmap.
- [x] Review pass: correct the drive protocol, the registry claim, `collectgarbage`, the
      frame-propagation rule, the deployment anchor and the package-metadata claims; extend the
      probe until it produces every quoted number; add the deployment section the sandbox section
      cites.

## Dependencies

- none

## Test Cases

### TC-001: Links and claims resolve

Steps:
1. Open `docs/lua-runtime.md` and follow every relative link and anchor (including
   `development.md#7-production-hardening`).
2. Re-run Appendix A against the pinned package.

Expected:
- Every link and anchor resolves.
- Appendix A compiles and runs unchanged, printing the sections quoted in §2, §3, §5 and §9; timings
  and the concatenation benchmark may differ by machine, the `nil`/error/text results may not.

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
hazard (§3.1) means a card that composes `Dynamic` back in can never fix it by nil-ing globals alone.

### 2026-11-13 — review round 1 (FAIL → fixes)

The independent review failed the document and it was right on every blocking point. What it found,
what I reproduced, and what changed:

1. **The drive protocol was wrong, and following it would crash.** The document said both yield
   kinds are "resumed by the same call". Measured: an explicit `Suspended` yield must be resumed
   with the answer (`Resume(DynValue.NewString("exit=0"))` → `build -> exit=0`); `Resume()` with no
   argument delivers `nil` (`got:nil`); and resuming a `ForceSuspended` (instruction-budget) yield
   **with** an argument throws `ArgumentException: When resuming a force-suspended coroutine, args
   must be empty.` §5.1 now branches on `CoroutineState`, and the transcript in §5.2 is real probe
   output instead of a hand-written example.
2. **The registry escape was overstated as unconditional.** Under the composition the document
   itself specifies, `package.loaded` has no `dynamic` and the escape is unreachable (`loaded=
   [bit32,coroutine,math,os,string,table] escape=unreachable`); it appears only when `Dynamic`/`Json`
   is composed in (`escape=2`). §3.1 is now conditional, the cards' background was corrected, and
   fixture §9-12 asserts the hazard where it actually exists (so a future edit that composes
   `Dynamic` back fails the fixture instead of silently reopening the door).
3. **`collectgarbage('count')` returns `nil`, not `false`.** Corrected in §1, §5.3 and fixture 13.
   The conclusion (no memory introspection; the OS owns the limit) is unchanged.
4. **The frame-propagation rule was backwards.** The document claimed a resumed value only reaches
   the innermost yield point, so Lua wrappers would have to propagate suspensions themselves.
   Measured: the value crosses a Lua wrapper, `pcall`, `xpcall`, a `for` body and an `__index`
   metatable (`wrapped:VAL`, `pcall:true,VAL`, `xpcall:true,VAL`, `for:VAL;VAL;`, `meta:VAL`). The
   prelude can wrap primitives freely; the rule was deleted and replaced with what actually breaks
   (yielding from a C# callback on behalf of a waiting Lua frame).

Non-blocking items, also fixed: the `docs/development.md` §5.3 citation pointed at nothing (a real
§7 "Production hardening" now exists, and `development.md`'s stale "Current state" paragraph was
corrected while I was there); `ulimit -v` does not work on macOS (reproduced `setrlimit failed:
invalid argument`, so the memory section is now per platform with an explicit macOS gap);
`CoreModules.GlobalConsts` was missing, which left `_G` and `_VERSION` `nil` and made the
`_MOONSHARP` assertions vacuous (now composed in, with `_VERSION` documented); the appendix did not
produce several quoted results and one transcript it could not have produced (it now prints every
section the document cites, and its output is pasted verbatim); the license notes now say the 2.0.0
package ships no `LICENSE` file and that the notices must copy the full text (KopiLua, Eclipse icons,
MoonSharp icon, `vscode-mono-debug`); fixture rows are exact strings instead of prose; and the
"no way to resume it afterwards" phrasing was replaced with what we actually do (never resume it).

One review point did not hold as stated: the appendix was said to fail the repository's gate with
`CA1031`. The repository's gate is `TreatWarningsAsErrors` (no `AnalysisLevel` override), and the
appendix compiles clean under it; it only fails under `AnalysisLevel=latest-all`, which this
repository does not use. The substantive half — the appendix did not cover enough claims — was true
and is fixed.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| R1 | major | §5.1/§5.2 drive loop resumed every state the same way; a `ForceSuspended` resume with an argument throws, and a `Suspended` resume without one delivers `nil` to the call site | fixed | Driver branches on `CoroutineState`; §5.2 transcript is real probe output |
| R2 | major | §3.1 presented the `package.loaded` escape as unconditional, which is false for the composition the document specifies (and would have made fixture 9 vacuous) | fixed | Made conditional, fixture rewritten to compose `Dynamic` so the hazard is actually asserted |
| R3 | major | `collectgarbage('count')` stated as `false`; it returns `nil` (the fixture would have failed) | fixed | Corrected in §1, §5.3, fixture 13 |
| R4 | major | §5.2 claimed a resumed value cannot cross Lua frames, which would have driven a wrong prelude design in F-014 | fixed | Measured the opposite; rule deleted, replaced with the real limitation |
| R5 | minor | `docs/development.md` §5.3 citation was dead and the deployment boundary it described existed nowhere | fixed | `development.md` §7 "Production hardening" added |
| R6 | minor | `ulimit -v` cited for macOS; it fails with `setrlimit failed: invalid argument` | fixed | Per-platform table with the macOS gap stated |
| R7 | minor | `GlobalConsts` omitted, so `_G`/`_VERSION` were `nil` and the `_MOONSHARP` assertions were vacuous | fixed | `GlobalConsts` composed in; `_G`/`_VERSION` documented |
| R8 | minor | Appendix could not reproduce several quoted results and one transcript | fixed | Appendix rewritten; it prints every cited section and its output is pasted verbatim |
| R9 | minor | License notes omitted that 2.0.0 ships no `LICENSE` file and understated the notices text | fixed | Notices must copy the file verbatim; sources enumerated |
| R10 | minor | Fixture rows were prose ("unreachable", "no cross-talk") and not assertable | fixed | Exact expected output per row |

## Verification

- Related files: `docs/lua-runtime.md` (new), `docs/development.md` (§7 + current state),
  `README.md`, `docs/kanban/roadmap.md`
- How to run/verify: read the document; run Appendix A in a scratch console app; follow every link
  and anchor.
- Results (2026-11-13): document written; every quoted measurement came from a run on this machine.
  Link/anchor check: all resolve. Appendix A: compiles and runs under the repository's gate
  (`TreatWarningsAsErrors`, 0 warnings) and prints every section quoted.
- Results (2026-11-13, after review round 1): all ten findings reproduced before being fixed
  (drive protocol, registry scope, `collectgarbage`, frame propagation, dead anchor, macOS `ulimit`,
  `GlobalConsts`, appendix coverage, notices text, fixture assertability). Documentation only — no
  build or test impact; `dotnet build -c Release` re-run: 0 warnings / 0 errors.
