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

### 2026-11-13 — review round 3 (adversarial team)

Two independent reviewers attacked the contract from opposite directions (an implementer reading it as
a spec, and an edge-case hunter running it against both packages). Fourteen findings were reproduced
before being fixed; none of them changed the engine decision, and three invalidated the text rather
than the design:

- **The drive protocol could still crash on a legal script.** `CoroutineState.Suspended` is produced
  by a script's own `coroutine.yield`, not only by a primitive asking for I/O, so the documented
  `pending.Dequeue()` threw `InvalidOperationException: Queue empty.` outside the loop's handlers.
  §5.1 now has three branches plus a private-sentinel recommendation, and fixtures 18/19 record the
  two engine behaviours that were being assumed rather than measured (`coroutine.yield`, and
  `CheckThreadAccess` not actually rejecting cross-thread calls).
- **Two claims pointed at APIs that do not exist or mean the opposite of what was written.**
  `RecursionGuard` is in neither package (type, member, enum or DLL string), and §3.1's closing
  sentence read as "nil-ing `package` is not enough" when the measurement says it is exactly what
  closes the door.
- **The fixture table could not all be executed.** Rows 3, 4–7, 11, 15–17 were prose or needed a
  value that the quoted expression does not return; row 12 asserted a value that only holds
  unhardened; row 14's threshold was below what the machine produces. Rows are now one runnable
  chunk each with an `==`-comparable expected value, split where a row carried two claims
  (4a–4c, 12a/12b, 15a/15b) and never renumbered.
- **The evidence now covers the new rows.** Appendix A grew §D2 (the hardened registry), three lines
  at the end of §E (the script's own yield), three at the end of §F (number/table/nil resumed through
  Lua frames), a three-run form of §J (all three runaway shapes, plus distinct `ManagedThreadId`) and
  a new §K for the §9 fixtures with no other home. §2 lost the one row (`HELLO sum=5`) that no probe
  section could produce and gained the `_VERSION` and `CallStack` differences that make the two
  packages not-quite-identical.

Verification (2026-11-13): `verify-appendix.sh` — extract Appendix A from the document, `dotnet`
build under `TreatWarningsAsErrors` (0 warnings / 0 errors), run it, compare with the pasted output:
PASS, no difference beyond the seven timing lines. The probe source still compiles and runs unchanged
against 3.0.0-beta.1. `verify-repo.sh` — `dotnet build Zenkins.slnx -c Release` 0 warnings / 0
errors, every relative link and heading anchor in the document resolves, working tree unchanged.

### 2026-11-13 — review round 4 (lead pass over the round-3 diff)

Read the round-3 diff line by line against the appendix output it claims to come from; five numbers
did not survive that comparison and were corrected in place (prose only, probe untouched, both
verification scripts re-run afterwards):

- §2 and §7 still printed `0.8–3.6 s` for the 400 KB concatenation while the pasted §J output in the
  same document reads `3484–4258 ms`; widened to `0.8–4.3 s` (and `1.6–4.3 s` in the prose).
- §5.1 cited `3 174–5 629` yields for the two non-tight runaway shapes; the printed run is
  `3 609–5 629`, so the lower bound was clamped to the observation.
- Fixture 17 demanded "exactly 8 distinct `ManagedThreadId`"; that is a thread-pool property, not an
  engine property, and an engine-switch gate must not fail on a pool decision. Expected value is now
  the result (`1800006`, no exception), with the 8-thread observation recorded as measured.

Re-verified afterwards: `verify-appendix.sh` PASS (no difference beyond timings) and `verify-repo.sh`
PASS (0 warnings / 0 errors, links and anchors resolve).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| R1 | major | §5.1/§5.2 drive loop resumed every state the same way; a `ForceSuspended` resume with an argument throws, and a `Suspended` resume without one delivers `nil` to the call site | fixed | Driver branches on `CoroutineState`; §5.2 transcript is real probe output |
| R2 | major | §3.1 presented the `package.loaded` escape as unconditional, which is false for the composition the document specifies (and would have made fixture 9 vacuous) | fixed | Made conditional, then split into 12a/12b in round 3 so the asserted row is the production shape, not the unhardened one |
| R3 | major | `collectgarbage('count')` stated as `false`; it returns `nil` (the fixture would have failed) | fixed | Corrected in §2 (table), §3.2, §5.3, fixture 13 |
| R4 | major | §5.2 claimed a resumed value cannot cross Lua frames, which would have driven a wrong prelude design in F-014 | fixed | Measured the opposite; rule deleted, replaced with the real limitation |
| R5 | minor | `docs/development.md` §5.3 citation was dead and the deployment boundary it described existed nowhere | fixed | `development.md` §7 "Production hardening" added |
| R6 | minor | `ulimit -v` cited for macOS; it fails with `setrlimit failed: invalid argument` | fixed | Per-platform table with the macOS gap stated |
| R7 | minor | `GlobalConsts` omitted, so `_G`/`_VERSION` were `nil` and the `_MOONSHARP` assertions were vacuous | fixed | `GlobalConsts` composed in; `_G`/`_VERSION` documented |
| R8 | minor | Appendix could not reproduce several quoted results and one transcript | fixed | Appendix rewritten; round 3 deleted the one remaining unproducible row (`HELLO sum=5`) and added §D2 and §K so every §9 row has a producing command |
| R9 | minor | License notes omitted that 2.0.0 ships no `LICENSE` file and understated the notices text | fixed | Notices must copy the file verbatim; sources enumerated |
| R10 | minor | Fixture rows were prose ("unreachable", "no cross-talk") and not assertable | fixed | Exact expected output per row; round 3 made the remaining eleven executable (4a–4c, 5, 6, 7, 11, 15a/15b, 16, 17) and added 18 and 19 |
| R11 | blocker | §5.1 tied the driver to a `RecursionGuard` that exists in neither package (no type, member, enum or DLL string in 2.0.0 or 3.0.0-beta.1) | fixed | Restated as an invariant no API provides: the loop never re-enters the script from a C# callback, and a callback-into-script primitive is a new design (§6.1), not a variation of this loop |
| R12 | blocker | §5.1/§5.2 read `CoroutineState.Suspended` as "a host call", but a script's own `coroutine.yield('user-payload')` produces the same state with an empty queue, and the documented `pending.Dequeue()` then throws `InvalidOperationException: Queue empty.` — which neither handler catches | fixed | §5.1 branches three ways (budget yield → `Resume()`; pending request → answer it; nothing pending → abandon as a script violation), adds the private sentinel as the second lock, and §5.2 says only the host creates an answerable suspension; fixture 18 pins it |
| R13 | major | §5.1 presented `CheckThreadAccess` as thread protection, but cross-thread `DoString` and `Resume` are silently accepted; only concurrent reuse of one `Script` corrupts the interpreter | fixed | §5.1 now says single-thread ownership is our discipline, not an engine guarantee, and keeps the I/O outside the resume (never `Resume(await …)`); fixture 19 records the measurement |
| R14 | major | §3 preset table listed `coroutine` as `function`; `type(coroutine)` is `table` (the function is `coroutine.create`) | fixed | Row relabelled `coroutine.create`; the `Preset_HardSandbox` conclusion (module absent) is unchanged |
| R15 | major | §3.1 scoped the registry door to `Dynamic`/`Json`, and its closing sentence read as "nil-ing `package` is not enough" — the opposite of what was measured | fixed | Door scoped to any module that registers a live table (`Dynamic`, `Json`, `IO`) with the measured `Preset_Default` `package.loaded.io` file write as the example; the conclusion now separates "per-module nil-ing is not enough" from "nil-ing `package` is what closes it" |
| R16 | blocker | §9 #12 asserted `2` as the fixture's expected value, which only holds *unhardened*; with §3.1 applied the same source raises `attempt to index a nil value`, so the gate row could never pass | fixed | Split without renumbering into 12a (records the hazard, `2`) and 12b (production shape: `nil` + `false` + the message); F-011 TC-002 asserts 12b |
| R17 | blocker | §9 #3 was not executable: a bare `select('#', 'x','y')` has no value, and adding `return` raises `attempt to concatenate a nil value` | fixed | Row replaced with a self-contained chunk that returns `2xy`, printed in Appendix A §K |
| R18 | major | §2 quoted `HELLO sum=5` from a two-function script that no probe section produces (one occurrence in the whole document) | fixed | Row deleted; "a script runs" is now represented by §9 #1/#2, both printed in Appendix A §K |
| R19 | medium | §1/§2 called the two packages "behaviourally identical" although `_VERSION` differs and 2.0.0 puts a `Location` only on the raising `CallStack` frame | fixed | Claim narrowed to "identical for every behaviour we depend on" with both exceptions named; `_VERSION` row added to §2; §6.2 states the per-frame difference and the re-measure condition |
| R20 | medium | §1 named a seam type `IPipelineScriptHost` that appears nowhere else and that §6 never defines | fixed | Name removed; `ILuaRuntime` is the seam |
| R21 | minor | §5.2 said a yield's return values are not a request channel, but `NewYieldReq(args)` returns those args to the host; §5.2 also claimed all four resumed value types were measured, with no probe output behind it | fixed | Channel bullet rewritten (the payload does come back; the queue exists to pair a request with its answer, and any suspension can produce a value); §F now resumes a number, a table and nil and prints all three |
| R22 | minor | §5.1 claimed three runaway shapes were measured while §J printed two, and the §2/#14 numbers (`> 1 000 resumes`, "7 190 yields") were not assertable | fixed | §J runs all three shapes three times; fixture 14 asserts the state plus at least 5 000 resumes; §2 carries the "one representative run" note |
| R23 | minor | §1 quoted two upstream facts (a 2026-10 fix, a 2026-03-03 tag) that cannot be checked offline, and §5.2/§6.1 disagreed about whether a v0.3 script calls `run()` | fixed | Both marked *upstream metadata, not verifiable from this machine*; §6 states that the four I/O primitives are F-014 C# executor code in v0.3 and that imperative `run()` is a later capability |
| R24 | major | §8 said the notices file must "copy the file verbatim", which is insufficient (the text names two MIT-licensed parts and carries neither MIT body); §9 rows 4/5/6/7/11/15/16/17 were not executable assertions | fixed | Notices enumerated as seven required items including both MIT texts and a scope note; §9 rows split or instantiated (4a–4c, 5, 6, 7, 11, 15a/15b, 16, 17), rows 18 and 19 added, all printed by Appendix A |

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
- Results (2026-11-13, after review round 3): fourteen findings reproduced and fixed (the
  `RecursionGuard` phantom, the `Suspended`-means-host-call assumption and its `Queue empty.`
  crash, thread affinity presented as an engine guarantee, the `coroutine` row, the registry door's
  scope and its reversed conclusion, fixture 12's unpassable expectation, fixture 3's
  non-executable source, the `HELLO sum=5` row, `_VERSION`/`CallStack` differences, the
  `IPipelineScriptHost` phantom, the yield-payload claim, the three-runaway claim, the unverifiable
  upstream facts, the notices text and the eleven prose fixtures). Documentation only. Verification:
  Appendix A re-extracted from the document, built (0 warnings / 0 errors under
  `TreatWarningsAsErrors`), run and diffed against the pasted output — identical apart from timings;
  `dotnet build -c Release` 0 warnings / 0 errors; all links and anchors resolve; working tree clean.
