---
id: B-005
title: Two product defects the first three-OS CI gate caught — cancel race and key-ring path shape
type: bug
priority: P1
size: S
status: done
created: 2026-11-15
updated: 2026-11-15
blocked: none
---

## Goal

The record of the two product defects P-001's gate caught on its first complete three-OS run,
and their fixes. Every other red the campaign turned was test-side (observation races, platform
facts); these two were the product's.

## Background

The gate (card P-001) had never completed on the CI runners: the fast suite failed from the first
run, so Milestone/Recovery/PublishSmoke/PublishAot were unreached there. Once the test-side
observation races were fixed (see the campaign commits under `test(P-001)`), the deeper suites
ran and named these:

### 1. A user cancel racing the accept edge was dropped (F-009)

`RunService.CancelRunAsync` read the run (`Queued`), lost the queued-cancel CAS to the worker's
accept (`ExecuteUpdate` writes the database without touching tracked state), and re-read the row
**on the same tracked context** — EF's identity resolution handed back the in-memory instance
whose `Status` still said `Queued`. The Running branch never ran: the cancel was dropped behind a
302, the run executed on with no `CancelRequestedAt`, and the cancel signal broadcast was skipped
over the stale null `WorkerId`. The `RunCancellationTests` claim-storm fact caught it
nondeterministically; the windows runner's load widened the window until it failed every run.

Fix: the re-read is `AsNoTracking()`, so the race window takes the cancel-request path it was
designed to take.

### 2. The postgres-fallback key-ring path embedded a forward slash on Windows

`SecretDataProtection.DefaultKeysDirectoryRelativePath` is written `"App_Data/keys"`-style with a
forward slash so every document quotes one portable path, and `ResolveKeysDirectory` joined it
verbatim — `Path.Combine(base, "App_Data/keys")` embeds the slash on Windows
(`...\App_Data/keys` between backslashes). Caught by `SecretDataProtectionTests` and
`ReleasePackagingTests` on the windows runner; the directory still worked (Windows tolerates
mixed separators) but its shape matched nothing any other surface reports. Fix: joined
segment-wise, so the runtime path uses the platform's own separators.

## Acceptance Criteria

- [x] The claim-storm cancellation fact passes on the CI runners (the race window takes the
      cancel-request path).
- [x] The key-ring path resolves with platform separators on all three OSes (both tests green).
- [x] CHANGELOG entries recorded under the 1.0.0 known issues, in the B-002 shape.

## Dependencies

- none

## Verification

- Run 43680919338 (2026-11-15): the full gate green on ubuntu, macos and windows-latest — the
  first complete three-OS CI run of the gate.
- Both fixes carry their reasoning in source (`RunService.cs`, `SecretDataProtection.cs`).
