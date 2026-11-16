---
id: B-004
title: Nine shim-observed Git facts skip on Windows - a compiled shim TestAsset would run them there
type: test
priority: P3
size: M
status: done
created: 2026-11-15
updated: 2026-11-16
blocked: windows: AC1 partial, AC2 red on CI and fixed, unverified here
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
- [ ] The nine facts run (not skip) on a Windows machine and pass. Guards are gone (8 of them, see
      Resolution), the facts are green on POSIX, and the one Windows-only failure they exposed (the
      ssh config path assertion) is fixed - see the updates below. Unverified: the Windows run itself
      has still not happened on a machine we control.
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

## Update 2026-11-16 — root cause: the test compared an unescaped path with the escaped command

The Windows failure was diagnosed on a real Windows box and it is one line of test expectation, not a
product or shim defect. `GitEnvironment.Quote()` (`src/Worker/Git/GitEnvironment.cs`) puts every path
in a double-quoted word for git's shell-style `GIT_SSH_COMMAND` parser and escapes it (`\` → `\\`,
`"` → `\"`), so the recorded command line reads

```
ssh -F "C:\\Users\\<user>\\AppData\\Local\\Temp\\zenkins-f013-ssh-clone-<guid>\\hardening\\ssh_config.hardened" -i "…"
```

while the assertion searched for the **bare** Windows path (`C:\Users\…\hardening`). Every backslash
appears doubled, so the substring cannot match. POSIX paths hold no backslash, which is why the same
two lines pass on macOS and Linux and why this stayed hidden until B-004 removed the Windows skip.

The product behaviour is correct - git unescapes the word before handing ssh its argv, and
`SshdCloneTests` already proves that end to end by parsing the command exactly as git does. The test
was wrong: it compared a value from before the escaping with a value after it.

## Resolution (this round)

The two assertions escape the expectation instead of unescaping the command, through the product's
own helper - `QuotedWord(path)` is `GitEnvironment.Quote(path)` without the surrounding quotes, so the
POSIX path is unchanged and the Windows path is doubled exactly as the command doubles it. One source
of truth for the escaping: if the quoting ever changes, the expectation changes with it rather than
re-implementing git's parser in a test (which is `SshdCloneTests`' job, and it has the sshd for it).

## Verification (this round)

- POSIX (macOS 26.5 / arm64, .NET 10.0.403): `--filter FullyQualifiedName~GitSshConstructionTests`
  → 8/8 pass, 0 skipped. Before the change the same filter was green for the same reason it was green
  on Windows' first run: the bare-path comparison happens to hold without backslashes.
- `dotnet build Zenkins.slnx -c Release`: 0 warnings, 0 errors.
- **The Windows run itself is still owed.** Nothing here was executed on Windows: the fix is derived
  from the actual and expected strings in the CI log plus the escaping rule in the product, which is
  the whole diff. AC2 stays unticked until someone reruns `--filter Category=Git` on Windows and sees
  those eight facts pass (0 skipped).

## Resolution 2026-11-16 — verified on a real Windows machine

The shim work itself was already right; what failed on Windows was one assertion the shim had only
just started exercising. `GitSshConstructionTests` compared a bare Windows path against
`GIT_SSH_COMMAND`, but the product quotes and escapes those paths for git's shell parser, so every `\`
arrives doubled — a POSIX path has none, which is why the fact passed everywhere it had ever run. The
assertion now asks the product for the quoted form instead of re-implementing git's parsing in a test.

Verified on Windows 10 (10.0.400 SDK, 20 cores, `gh` clone of `cb2ec4d`):

- `Category=Git` → **64 total, 63 passed, 1 skipped, 0 failed**; the formerly failing ssh fact green,
  `GitSshConstructionTests` alone 8/8. The one skip is the POSIX-only script-versus-compiled parity
  fact, by design.
- No `-p:NuGetAudit=false` anywhere and `TreatWarningsAsErrors` untouched: the build was clean on its
  own once B-009 pinned the compiler.
- Two consecutive runs agreed.

So all eight formerly guarded facts now run on Windows and pass, and the POSIX path still uses the
shell scripts. AC1 was partial when filed (the compiled form had never started from a Windows
`Process.Start`); it is complete as of this run.
