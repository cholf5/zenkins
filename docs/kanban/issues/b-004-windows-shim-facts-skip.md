---
id: B-004
title: Nine shim-observed Git facts skip on Windows - a compiled shim TestAsset would run them there
type: test
priority: P3
size: M
status: todo
created: 2026-11-15
updated: 2026-11-15
blocked: none
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
- [ ] The nine facts run (not skip) on a Windows machine and pass.
- [ ] The POSIX path keeps using the shell scripts; the shim log format stays identical for both.

## Subtasks

- [ ] Add the TestAsset project and locate its binary like the E2E locates ArgvEcho.
- [ ] Parameterise the shim by environment (log path, real git/ssh path), keep the exact log lines.
- [ ] Point `RecordingGitShim`/`RecordingSshShim`/`CreateHangingGit` at the compiled form on
      Windows and drop the `RecordingShimUnavailableReason` guards.

## Dependencies

- none

## Test Cases

### TC-001: Windows facts unskipped and green

Steps:
1. Run the fast suite on Windows.

Expected:
- the nine facts report as passed, not skipped.

Result: not yet run (card opened by the CI-repair session; the skips are the recorded interim).
