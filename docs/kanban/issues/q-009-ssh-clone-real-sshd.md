---
id: Q-009
title: SSH clone verified against a real sshd — the keyless-sshd fixture (F-018 follow-up)
type: test
priority: P1
size: M
status: todo
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

F-018's SSH side stops being shape-pinned: a real `sshd` serves a private repository on loopback and
the suite proves the worker clones **from the delivered SSH credential and nothing else** — the same
standard the HTTPS side already meets with `AuthenticatedHttpsGitServer`.

## Background

F-018's bug #4 is an open follow-up and its "Limits recorded" section says it plainly: the SSH
command surface is pinned (`-F <run>/hardening/ssh_config.hardened`, `-o IdentityAgent=none`,
`-i` + `IdentitiesOnly=yes` with a credential, `BatchMode=yes` and no `-i` without one, a per-run
`UserKnownHostsFile`), but **no real SSH clone exists in the suite** — every SSH assertion so far is
at the command-construction level, and F-018's real proof used TLS + Basic auth over HTTPS.

Shape-pinned is not link-proven. What a real sshd adds that the pins cannot see:

- **Host-key handling end to end** — whether the per-run `UserKnownHostsFile` pin actually refuses a
  mismatched host, and that the refusal surfaces as a named failure, not an opaque clone error.
- **Key format and permission reality** — sshd refuses keys that are too open; key generation, the
  `authorized_keys` path and the delivered-credential kind F-018 supports have never met a real
  server.
- **The hardened `-F` interaction with the real binary** — `-F` overriding `~/.ssh/config` on macOS
  (ssh takes `~` from the passwd entry, not `$HOME`) is a documented reason, not a tested one.

The PRD's credential story advertises SSH; the v1.0 tag (P-001) should not be cut while the SSH half
of F-018 has no verdict. Precedent to follow: Q-008 (real dependency, synthetic environment — the
fixture generates its own host key, authorized key and repository; nothing on the host's `~/.ssh` is
read or modified).

Decisions to make on the card: which category carries the harness (existing `Category=Git` vs. a new
one), and how the `sshd` prerequisite is documented and skipped where absent (the PublishAot
precedent in development.md §1/§2: a named prerequisite for one suite, with the skip recorded, not
silent).

## Acceptance Criteria

- [ ] A real `sshd` fixture (test-generated host key + authorized key, loopback port) serves a
      private bare repository; a real Server + Worker pair clones the project's repository from the
      **assigned SSH credential** and runs the pipeline to `Success`, with the worker host offering
      no ambient key or agent — the F-018 standard (hardening/ambient counters unchanged).
- [ ] No credential assigned + SSH URL fails honestly (`git-credential-not-assigned`), matching the
      HTTPS behaviour; a rejected key fails with a reason naming the authentication failure.
- [ ] A wrong host key is refused by the per-run `UserKnownHostsFile` pin and surfaces as a failure
      with a reason — proving the pin does something, not merely that it is present.
- [ ] The ssh invocation actually used is captured from the real run and matches the shape pins
      (`-F`, `IdentityAgent=none`, `-i`/`IdentitiesOnly` or `BatchMode` per credential presence) —
      the construction-level pins become runtime-verified.
- [ ] The `sshd` prerequisite and skip behaviour are documented in development.md; the fast suite and
      the existing Git suite stay green on a machine without the fixture changes.
- [ ] F-018's bug #4 row is closed or updated to point at this card's result.

## Subtasks

- [ ] sshd fixture: host key generation, `sshd_config` (loopback port, pubkey auth only),
      `authorized_keys`, lifecycle tracked like every other harness process.
- [ ] Credential round-trip: organization SSH credential → project assignment → worker fetch →
      clone → run to `Success`, with the ambient/hardening counters of F-018's proof.
- [ ] Rejection paths: no credential, wrong key, wrong host key — each with its observable reason.
- [ ] development.md prerequisite/skip note; F-018 bug-table update.

## Dependencies

- F-018 (accepted; its bug #4 and "Limits recorded" section are this card's charter)
- Blocks P-001 (the v1.0 gate waits for this verdict — its outcome is unknown, which is why the card
  starts before the release work)

## Test Cases

### TC-001: Clone from the delivered credential, against a real sshd

Steps:
1. Start the sshd fixture serving a private bare repository; create the organization SSH credential;
   attach it to the project; trigger a run on a real Server + Worker pair whose host environment has
   no ambient key or agent for the fixture.

Expected:
- The run reaches `Success`; the clone used the delivered key (hardening counters unchanged, no
  ambient channel was contacted); the captured ssh invocation matches the shape pins.

### TC-002: The honest failures

Steps:
1. Repeat TC-001's project with no credential assigned; then with a credential the fixture does not
   recognize; then with a client that does not trust the fixture's host key.

Expected:
- `git-credential-not-assigned`; an authentication-failure reason naming the rejection; a host-key
  refusal — each surfaced on the run, none silent, none an opaque clone error.

## Development Log

### 2026-11-14

- Card opened from F-018 bug #4 / "Limits recorded" and the user's post-v1.0-evidence ledger, where
  it was prioritized as a pre-tag gate (outcome unknown ⇒ start early). Next step: decide the
  category and the sshd prerequisite handling, then build the fixture.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for F-018 bug #4 (Low): SSH hardening is shape-pinned only. | Open | Closed here when the acceptance criteria pass. |

## Verification

- Related files: `src/Worker/Git/GitEnvironment.cs`, `src/Worker/Git/GitWorkspace.cs`,
  `tests/Zenkins.Tests/Git/` (fixture home), `tests/Zenkins.Tests/Milestone/MilestoneGitCredentialTests.cs`
  (the real-process pattern to extend), `docs/development.md` (§1/§2 prerequisite note)
- How to run/verify: the chosen suite filter; the full fast suite; `Category=Milestone` if the
  milestone host changes.
- Results: not run yet (card creation).
