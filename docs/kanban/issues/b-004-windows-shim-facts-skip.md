---
id: B-004
title: Nine shim-observed Git facts skip on Windows - a compiled shim TestAsset would run them there
type: test
priority: P3
size: M
status: review
created: 2026-11-15
updated: 2026-11-16
blocked: windows: AC1 partial, AC2 unrun
---

## Goal

The recording-shim facts (`GitCli.RecordingShimUnavailableReason()` guards, added when CI first
ran the gate on Windows runners) run on all three OSes, by replacing the `#!/bin/sh` shim scripts
with a compiled TestAsset executable when the test executes on Windows.

## Background

The F-013 recording shims (`RecordingGitShim`, `RecordingSshShim`, `GitTimeoutTests`' hanging-git
stub) are POSIX shell scripts by design: they must observe the child's real argv and environment
through the exec path. The worker starts the git shim itself through `Process.Start`, and Windows
cannot execute a shebang script ("not a valid application for this OS platform"). Git for Windows
running such a script itself works fine - which is why only the shim-spawned facts are affected.

The facts currently recorded as skipped on Windows (reason carried in the skip, per Q-009's
contract): `GitLfsTests.Without_git_lfs_*`, `GitSubmoduleTests.A_submodule_*`,
`GitCredentialSecrecyTests.The_HTTPS_token_*` and `A_rejected_token_*`,
`GitAmbientCredentialHardeningTests.With_no_credential_*`,
`GitSshConstructionTests.An_ssh_clone_*`, and both `GitTimeoutTests` facts. The ask-pass fact was
reshaped instead (the product already writes a `git-askpass.cmd` there).

A compiled shim (a second TestAsset console project beside ArgvEcho, logging the same lines from
environment variables, spawning the real git/ssh and propagating its exit code) would remove the
need for the skips and restore those observations on Windows.

## Acceptance Criteria

- [ ] The recording shims exist in a compiled form usable from a Windows `Process.Start`.
      Partial: the compiled TestAsset exists, starts as a native process and drives a real clone on
      POSIX; starting it from a Windows `Process.Start` is unverified — no Windows machine here.
- [ ] The nine facts run (not skip) on a Windows machine and pass. Unverified: the guards are gone
      (8 of them, see Resolution) and those facts are green on POSIX, but the Windows run itself has
      not happened.
- [x] The POSIX path keeps using the shell scripts; the shim log format stays identical for both.

## Subtasks

- [x] Add the TestAsset project and locate its binary like the E2E locates ArgvEcho.
- [x] Parameterise the shim by environment (log path, real git/ssh path), keep the exact log lines.
- [ ] Point `RecordingGitShim`/`RecordingSshShim`/`CreateHangingGit` at the compiled form on
      Windows and drop the `RecordingShimUnavailableReason` guards.
      Done for `RecordingGitShim` and `CreateHangingGit`. The ssh half is vacuous today: no fact
      carries a `RecordingShimUnavailableReason` guard around `RecordingSshShim` — its three
      consumers are gated by `SshdGitServer.UnavailabilityReason()` instead, which is a different
      card's surface. Left untouched.

## Resolution

`tests/Zenkins.Tests/TestAssets/RecordingShim` is a console TestAsset with two roles selected by
`ZENKINS_RECORDING_SHIM_ROLE` (`git` records and execs the real git; `hang` records both its own
pid and a sleeping child's, then waits). `RecordingShimAsset` locates the apphost next to the DLL
the way the E2E locates ArgvEcho — `<name>.exe` on Windows, `<name>` on POSIX — which also settles
the `.exe` suffix the `Path.Combine(BinDirectory, "git")` shape would have missed.

The log format is pinned from both sides: the POSIX shell script text moved into a shared
`PosixScript(...)`, and a new fact compares the script's log bytes against the compiled shim's log
bytes for the same invocation. Twelve lines, `\n` terminated, no CR.

All eight `RecordingShimUnavailableReason` guards are gone (definition and call sites: zero
references left in code). The ninth fact the card's title counts —
`GitSshConstructionTests.The_askpass_helper_is_0700_and_answers_both_prompts` — never had a guard;
it already branches on `OperatingSystem.IsWindows()`. The two facts that build a `RecordingGitShim`
but deliberately never execute it (`GitSshConstructionTests` known-hosts pin, `GitCredentialSecrecyTests`
token-in-URL) stay guard-free and still assert an empty shim log.

## Verification

- POSIX (macOS 26.5 / arm64, .NET 10.0.403): `--filter Category=Git` → 62/62, 0 skipped;
  `--filter Category=Dashboard` → 13/13; full suite → 1129/1129, 0 skipped; Release build of the
  solution green. Reviewer reran all of it independently.
- Reviewer's own byte comparison of the script log against the compiled shim log: identical, 12
  lines, zero CR. Exit codes pass through (real git's own code, `127` when the real binary is
  missing, `64` on an unknown role).
- Change surface: `Zenkins.slnx`, `tests/Zenkins.Tests/Zenkins.Tests.csproj`, and `tests/**` only.
  `src/` untouched; ssh-side files not in the diff.

## Windows verification still owed

1. `--filter Category=Git` on Windows reports 0 skipped and the eight formerly guarded facts
   actually run and pass.
2. `RecordingShim.exe` really starts from the worker's `Process.Start` (shim facts 1/3/4 green).
3. The five chains pass through the compiled shim: HTTPS clone, submodule recursion, missing-LFS
   warning, ssh construction, ambient credential hardening — watch `GitLfsTests`' PATH mirror on
   the copy branch for a `git` 127.
4. Both timeout facts: the hang role writes exactly two pids and `Kill(entireProcessTree)` reaps
   both.
5. Format equivalence on Windows: shim fact 1's literal twelve lines plus the no-CR assertion green
   (fact 2 self-skips there by design — the two forms only coexist on POSIX).

Known Windows-side risks carried on purpose, none of them verified: `GitLfsTests` does not clear
`GIT_EXEC_PATH` (git for Windows also resolves `git-lfs` there, so the missing-LFS branch may not
trigger); the PATH mirror copies instead of symlinking there; the ambient-helper control group
relies on git for Windows running the helper through its own `sh`.

## Dependencies

- none

## Test Cases

### TC-001: Windows facts unskipped and green

Steps:
1. Run the fast suite on Windows.

Expected:
- the nine facts report as passed, not skipped.

Result: POSIX PASS (the eight formerly guarded facts run and pass, 0 skipped); Windows not yet run
(2026-11-16).

## Update 2026-11-16 — the first real Windows run is red, and it is not the shim

The guards are gone, so CI's `windows-latest` gate now runs these facts for real. Run 43870254355
(F-024's push) and run 43830386630 both fail one of them:

`GitSshConstructionTests.An_ssh_clone_uses_the_key_file_and_deletes_it_afterwards` at
`GitSshConstructionTests.cs:218` — `Assert.Contains(work.TempPath …, ssh command line)` reports the
substring not found, while the string it searched is the real command line beginning
`ssh -F "C:\Users\<runner>\AppData\Local\Te…`. So the shim launched, `ssh` ran, and the assertion
about the `-F` config path's shape is what disagrees — a path-shape expectation, not a missing shim.

That makes B-004's AC2 not merely unverified but **failing**: the eight facts do now run on Windows,
and one of them does not pass. Diagnosis needs a Windows machine with the repo (the compiled shim
exists there, so this is reproducible locally rather than only in CI).

Same run also fails `Concurrency.ConcurrencyMassTriggerTests`
(`Twelve_runs_across_three_workers_are_claimed_and_executed_exactly_once`: three of twelve runs `Lost`
with `worker-offline`), which the previous Windows run did not — tracked separately, not here.
