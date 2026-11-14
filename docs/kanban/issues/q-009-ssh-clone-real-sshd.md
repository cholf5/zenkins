---
id: Q-009
title: SSH clone verified against a real sshd — the keyless-sshd fixture (F-018 follow-up)
type: test
priority: P1
size: M
status: done
created: 2026-11-14
updated: 2026-11-15
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

- [x] A real `sshd` fixture (test-generated host key + authorized key, loopback port) serves a
      private bare repository; a real Server + Worker pair clones the project's repository from the
      **assigned SSH credential** and runs the pipeline to `Success`, with the worker host offering
      no ambient key or agent — the F-018 standard (hardening/ambient counters unchanged).
- [x] No credential assigned + SSH URL fails honestly (`git-credential-not-assigned`), matching the
      HTTPS behaviour; a rejected key fails with a reason naming the authentication failure.
- [x] A wrong host key is refused by the per-run `UserKnownHostsFile` pin and surfaces as a failure
      with a reason — proving the pin does something, not merely that it is present.
- [x] The ssh invocation actually used is captured from the real run and matches the shape pins
      (`-F`, `IdentityAgent=none`, `-i`/`IdentitiesOnly` or `BatchMode` per credential presence) —
      the construction-level pins become runtime-verified.
- [x] The `sshd` prerequisite and skip behaviour are documented in development.md; the fast suite and
      the existing Git suite stay green on a machine without the fixture changes.
- [x] F-018's bug #4 row is closed or updated to point at this card's result.

## Subtasks

- [x] sshd fixture: host key generation, `sshd_config` (loopback port, pubkey auth only),
      `authorized_keys`, lifecycle tracked like every other harness process.
- [x] Credential round-trip: organization SSH credential → project assignment → worker fetch →
      clone → run to `Success`, with the ambient/hardening counters of F-018's proof.
- [x] Rejection paths: no credential, wrong key, wrong host key — each with its observable reason.
- [x] development.md prerequisite/skip note; F-018 bug-table update.

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

Result (observed, 2026-11-15): PASS — three consecutive green runs of `Category=GitSshd`
(`A_private_repository_clones_only_from_the_delivered_ssh_credential_against_a_real_sshd`). The
fixture is `SshdGitServer`: a real `/usr/sbin/sshd` (OpenSSH 10.2) started as the test's own user in
the foreground (`-D -e`), with four `ssh-keygen`-generated ed25519 keys (host, delivered, rejected,
wrong-host), an `authorized_keys` file holding exactly the delivered key, and a generated
`sshd_config` (loopback, ephemeral port, `PubkeyAuthentication yes`, every password/agent/tty channel
off, `StrictModes no`, `LogLevel VERBOSE`). Nothing on the host's `~/.ssh` is read or modified
(Q-008 precedent); the worker's `HOME` is a scratch directory with no `.ssh` and `SSH_AUTH_SOCK` is
emptied before the product empties it again.

The order of evidence, in one fact:
1. **Control clone, outside the product** (`git clone` with the fixture's key, `-F` at its own empty
   config and its own scratch known-hosts): exit 0, and the sshd's VERBOSE log shows the accepted
   fingerprint — the fixture really serves a private clone, so the product's refusals below are
   meaningful.
2. **Run 1, nothing attached** → `Failed`, `FailureReason=git-credential-not-assigned`, the run log
   saying no credential is assigned and that a credential stored on the worker host is not used, the
   run page carrying `run-git-credential` and `run-failure-explanation` — and the decisive counter:
   the sshd's accepted-publickey count **did not move** (whatever the keyless ssh offered, the
   server accepted nothing; the refusal is the product's hardening, not an empty host).
3. **Attach through the settings form** (`DEPLOY_KEY`, kind `SshKey`, value = the fixture's private
   key block): the settings page shows `credential-assignment-current` and the name, and never the
   key material.
4. **Run 2** → `Success`, `GitCommit == repository.MainTip == OriginMainTip`, the `Clone` step
   `Success` (`git --version`, a real child process), the run log recording that it cloned with the
   project's assigned `SshKey` Git credential, the server logging `Git credential delivered` (never
   the value), and the run page showing "assigned credential; fetched by the worker". The sshd's
   accepted count grew by exactly one and **every accepted fingerprint so far equals the delivered
   key's fingerprint** — the server's own observation of what authenticated, not an inference from a
   success message.
5. **The captured invocations**: the worker's `PATH` carries a recording `ssh` shim (F-013's
   recording-shim pattern, one program closer to the wire), so the exact argv of every real ssh
   invocation is on disk. Run 1's captured argv equals `GitEnvironment.BuildNoIdentitySshCommand`
   (no `-i`, `BatchMode=yes`, per-run `UserKnownHostsFile`, `accept-new`) and run 2's equals
   `GitEnvironment.BuildSshCommand` (`-i` into the run's own `temp/id`, `IdentitiesOnly=yes`,
   `IdentityAgent=none`, `-F …/hardening/ssh_config.hardened`, per-run `UserKnownHostsFile`) — both
   compared word for word after the shell consumed the product's quoting, followed by git's own
   additions (`-o SendEnv=GIT_PROTOCOL`, `-p <port>`, `<user>@127.0.0.1`,
   `git-upload-pack '<path>'`).
6. Hygiene: no `PRIVATE KEY` material in the server output, the worker output, the run pages or
   either run's persisted logs; the worker home still holds no `.ssh`; the worker is its own live
   process; the tracked cleanup really killed the server/worker/sshd and removed both temporary
   roots (`ChildProcessesExited`, `TemporaryRootRemoved`, `ExitedOnDispose`, `RootRemoved`).

### TC-002: The honest failures

Steps:
1. Repeat TC-001's project with no credential assigned; then with a credential the fixture does not
   recognize; then with a client that does not trust the fixture's host key.

Expected:
- `git-credential-not-assigned`; an authentication-failure reason naming the rejection; a host-key
  refusal — each surfaced on the run, none silent, none an opaque clone error.

Result (observed, 2026-11-15): PASS, in two further facts (each with its own fixture, server and
worker; three consecutive green suite runs).

- `A_rejected_ssh_key_fails_with_a_named_authentication_reason_under_a_correct_host_key_pin` — the
  worker pins the **correct** host key, so every refusal is about the credential:
  1. No credential assigned, under the strict pin → `Failed` with
     `git-credential-not-assigned`; the captured argv shows the no-identity shape under
     `StrictHostKeyChecking=yes` against the pinned file (`BatchMode=yes`, no `-i`).
  2. A real key the fixture generated but never installed (`REJECTED_KEY`) → `Failed` with
     `git-auth-rejected`, the run log carrying ssh's own `Permission denied (publickey)`, and the
     sshd's log refusing **exactly the rejected credential's fingerprint** (accepted count stayed 0).
  3. The delivered key under the same strict pin → `Success` (commit equals the bare origin's tip),
     exactly one accepted authentication with the delivered key's fingerprint, and the captured argv
     showing `-i` + strict pinned `UserKnownHostsFile` — the contrast that makes fact 3's refusal
     exact.
- `A_wrong_host_key_is_refused_by_the_known_hosts_pin_and_surfaces_as_a_named_failure` — the
  credential is the known-good one and the **only variable is the pin** (same
  `[127.0.0.1]:<port>` field, same key type, one different key body): the run `Failed` with
  `FailureReason=git-failed` (the closed vocabulary's "git reported a failure; the run log carries
  the detail"), the run log carrying ssh's own `REMOTE HOST IDENTIFICATION HAS CHANGED` banner and
  `Host key verification failed`, the sshd's accepted count 0 (refused before any authentication),
  and the pinned file **byte-identical after the run** — the pin refused and was not repaired. The
  captured argv shows `StrictHostKeyChecking=yes` with `UserKnownHostsFile` naming the wrong pin.
  The run page keeps the credential answer ("assigned credential; fetched by the worker") and the
  failure-explanation row; the failure is honest and named, not opaque.

## Development Log

### 2026-11-14

- Card opened from F-018 bug #4 / "Limits recorded" and the user's post-v1.0-evidence ledger, where
  it was prioritized as a pre-tag gate (outcome unknown ⇒ start early). Next step: decide the
  category and the sshd prerequisite handling, then build the fixture.

### 2026-11-15

- **Decision: a new category carries the harness — `Category=GitSshd`.** The three options weighed:
  `Category=Git` would put a real-process Server+Worker+sshd suite (~20 s) inside the fast loop and
  make the fast suite require sshd (its 0-skipped, ~2-minute contract would break); `Category=
  Milestone` would keep the filter unchanged but raise the milestone suite's needs from "git on
  PATH, loopback only" to "plus an OpenSSH server", which Windows machines cannot meet; a new
  category follows the PublishAot precedent exactly — one suite, one named prerequisite, its own
  filter, excluded from fast. The fast filter grew its fifth exclusion
  (`&Category!=GitSshd`), documented in development.md §1/§2 with the suite table row.
- **Decision: the prerequisite is named and the skip is recorded.**
  `SshdGitServer.UnavailabilityReason()` answers *why* the suite cannot run on a machine (no sshd/
  ssh-keygen; Windows ships no sshd) and every fact throws `Xunit.Sdk.SkipException.ForSkip(reason)`
  before touching anything, so a runner on a machine without the OpenSSH tools records **skipped —
  with that reason**, never a silent omission and never a fixture crash. Two xunit.v3 4.0.3
  realities worth recording: the older `Xunit.Skip.If` helper does not exist in this version, and
  `SkipException`'s constructor is private — `ForSkip` is the public factory.
- **Fixture (`tests/Zenkins.Tests/Git/SshdGitServer.cs`).** Four `ssh-keygen` keys; `authorized_keys`
  holds exactly the delivered key (0600); the config is validated with `sshd -t` before start and
  **version-unsupported options are dropped by the name sshd itself prints** ("Bad configuration
  option"), never guessed — so the fixture runs on OpenSSH versions older than 9.8 too. Started as
  the test's own user (`-D -e`, foreground), waited for `Server listening on`, and its lifecycle is
  tracked like every harness process: tree-kill, waited exit recorded (`ExitedOnDispose`), temporary
  root deleted (`RootRemoved`) — both asserted by TC-001. A bind-retry loop handles the
  probe/bind race; the port is a `TcpListener` probe, loopback only.
- **macOS/OpenSSH quirks, recorded because they shaped the fixture:**
  - OpenSSH 9.8+ enables `PerSourcePenalties` by default: failed authentications would make 127.0.0.1
    progressively refused — exactly what the rejection-path tests do on purpose — so the config sets
    `PerSourcePenalties no` (dropped automatically where unsupported).
  - A non-root sshd cannot set the BSM audit session: the log shows
    `BSM audit: bsm_audit_session_setup: setaudit_addr failed: Operation not permitted`; the line is
    logged and the session proceeds — a quirk of running unprivileged, not a failure.
  - Non-root sshd can authenticate only the user running it, so the clone URL names the test's own
    account; the fixture's `authorized_keys` holds exactly one fixture-generated key, so no host key
    can satisfy the server.
  - ssh takes `~` from the passwd entry rather than `$HOME` (F-018's documented `-F` reason): the
    control clone and the product therefore both pass `-F` at an empty config of their own — now
    runtime-proven, since the fixture never touches the host's `~/.ssh` and the tests still pass.
  - Client-side identity permission reality confirmed end to end: `ssh-keygen` writes 0600 and the
    product materialises the delivered key at 0600 (`GitCredentialFiles`), and a real ssh accepts
    that file — the too-open-key failure mode is covered by construction, the accepted case by the
    real clone.
- **The captured invocation, and what git adds.** The worker's `PATH` carries
  `RecordingSshShim` (a `ssh` script logging its argv per argument, then execing the real client) —
  the same observation pattern as F-013's `RecordingGitShim`. The shell that runs
  `GIT_SSH_COMMAND` consumes the product's quoting, so the honest comparison is word-for-word after
  parsing (`ParseGitSshCommand` implements the one quoting `GitEnvironment.Quote` emits); the shim's
  `$@` excludes the program word, and git appends `-o SendEnv=GIT_PROTOCOL`, `-p <port>`, the
  destination and `git-upload-pack '<path>'` — all asserted. Both pinned shapes and both host-key
  policies (`accept-new` per-run file, `yes` pinned file) are runtime-verified across the three
  facts.
- **Counters instead of messages.** The F-018 standard ("the ambient credential was not used" is a
  count that did not move) transfers to SSH through the sshd's own VERBOSE auth log: accepted and
  refused fingerprints. The no-credential run leaves the accepted count exactly where the control
  clone left it; the credentialed run's every accepted fingerprint equals the delivered key's; the
  rejected-key run's refusal names the rejected credential's fingerprint. No assertion depends on
  which keys happen to sit in the developer's real `~/.ssh` — whatever ssh offers there cannot be
  accepted, and nothing on that directory is read or written.
- **Harness note.** `MilestoneHost.CreateSshGitCredentialAsync` was added (the settings form's
  `Kind=SshKey` path, value = the full private-key block; the credential id resolved by name, since a
  project can hold several credentials). No product source was touched: every failure and success
  path behaved as F-018 built it, and the host-key refusal honestly lands on `git-failed` (the
  closed vocabulary has no host-key-specific reason; the page's sentence points at the log, which
  carries ssh's own words — recorded here as an observation, not a defect).
- Verification (developer runs, Release): `dotnet build -c Release` 0 warnings; `Category=GitSshd`
  3/3 on three consecutive runs (~20 s each); `Category=Git` 58/58; fast suite (with the fifth
  exclusion) 1044/1044, 0 skipped, ~2½ min; `Category=Milestone` 8/8 (the harness grew one helper).
  development.md gained the §1 prerequisite paragraph, the §2 sixth-suite bullet, the fast-filter
  quintuple and the suite-table row.

### 2026-11-15 (independent review — PASS)

- Verdict **PASS**; `status: done` and the board row moved to Done (2026-11-15) in the same
  operation. Adversarial checks, each against the working tree and the reviewer's own runs:
  - **(a) The shim forwards faithfully.** `RecordingSshShim.Create` writes `for argument in "$@"; do
    printf 'SSH-ARG:%s\n' …; done` then `exec '<real ssh>' "$@"` — every argument logged verbatim,
    then the process is *replaced* by the real client, so exit code and both output streams are the
    real ssh's. Logging precedes the exec, so a failed exec cannot hide an invocation; bypassing PATH
    (absolute-path ssh) would produce zero entries and fail `Assert.True(entries.Length >
    entryIndex)`. The program word is correctly excluded on both sides
    (`Assert.Equal("ssh", expected[0])` + `expected.Skip(1)`). The pinned words compared are the
    product's own builders called with the run's real paths (`RunPaths` mirrors
    `WorkDirectoryService.Prepare`'s canonical-GUID form, `temp/id`, `temp/known_hosts`,
    `hardening/ssh_config.hardened`): TC-001 entries 0/1 = `BuildNoIdentitySshCommand`/`BuildSshCommand`
    under `accept-new`; TC-002a entries 0/1/2 under `PinnedHostKeyPolicy`; TC-002b entry 0 with
    `WrongKnownHostsPath` — all `Assert.Equal`ed word-for-word in order, plus git's additions
    (`-p`, port, `user@127.0.0.1`, `git-upload-pack '<path>'`) asserted.
  - **(b) Exclusivity is server-observed.** The worker env carries a scratch `HOME` with no `.ssh`
    (asserted before and after), `SSH_AUTH_SOCK` emptied at both levels, and the product's `Base` +
    `-F`/`IdentitiesOnly`/`IdentityAgent=none` cut the remaining ambient channels. The decisive
    counter is the sshd's VERBOSE log: no-credential run leaves the accepted count at the control's
    value; the success run's every accepted fingerprint equals the delivered key's; TC-002a ends with
    exactly one accepted authentication total. `authorized_keys` holds exactly the delivered key, so
    no ambient key *could* be accepted without failing `Assert.All`. F-018's counter standard is
    asserted in all three facts.
  - **(c) The host-key pin provably refuses.** TC-002b varies only the key body of the pin (same
    `[127.0.0.1]:port` field and type, asserted field-by-field): run `Failed` with `git-failed`, log
    carries `REMOTE HOST IDENTIFICATION HAS CHANGED` and `Host key verification failed`, accepted
    count 0, and the pin file byte-identical after. The AC asks for refusal + a named reason — met
    without a product change: `PipelineRunner`'s closed mapping puts `GitFailureKind.Failed →
    git-failed` exactly where the HTTPS side puts its non-authentication git failures; the card
    records the vocabulary gap as an accepted observation, not a dodge.
  - **(d) Skip mechanics verified empirically, not just read.** A throwaway project outside the repo
    (xunit.v3 4.0.3 from the same NuGet cache) reflects `Xunit.Sdk.SkipException`: the constructor is
    private and `ForSkip(String)` is the only public factory — exactly as the dev log records — and
    throwing it makes the runner report `Skipped: 1` with the reason printed. Every fact checks
    `UnavailabilityReason()` before touching anything. The filter is verified from both sides: the
    quintuple fast filter ran 1044/1044 with the sshd suite absent, and re-running the *old*
    quadruple filter pulled 1047 = 1044 + 3, proving the fifth exclusion is the only thing keeping
    GitSshd out of fast (development.md's claim).
  - **(e) Fixture hygiene.** `authorized_keys` 0600 explicitly; `ssh-keygen` writes the private keys
    0600; every path (host key, pid, authorized_keys, config) is under the fixture's temp root; the
    control clone and worker use scratch `HOME`s and their own `-F`/`UserKnownHostsFile`, so nothing
    on the host's `~/.ssh` is read or written (and `-F` also removes the system-wide config).
    Lifecycle: `sshd -t` before every start with drop-by-sshd's-own-name for unknown options (only
    version-unsupported knobs are ever dropped; the restrictive set — pubkey-only,
    `AuthenticationMethods publickey`, agent/TCP/X11 forwarding off — is asserted by the green runs),
    tree-kill + waited exit on Start's own failure paths and on Dispose, temp-root deletion with
    retry; TC-001 asserts `ExitedOnDispose`/`RootRemoved`. After two suite runs: no `sshd` processes,
    no `zenkins-q009-*` temp roots. `dotnet build -c Release` 0 warnings; the tree stayed clean of
    tool output.
  - **(f) Bookkeeping.** All six ACs and all four subtasks are true against the code and runs; F-018's
    bug #4 row is closed with an accurate summary; development.md's §1 prerequisite paragraph, §2
    sixth bullet, quintuple filter and suite table match the actual filter and counts, and the
    arithmetic closes: 1044 + 8 + 4 + 2 + 2 + 3 = 1063, the no-filter total the table states.
- Reviewer's own runs (Release, macOS arm64, OpenSSH 10.2): `Category=GitSshd` × 2 green
  (3/0/0, 20.5 s / 20.6 s); `Category=Git` 58/0/0 (15.4 s); fast suite (quintuple) 1044/0/0
  (2 m 18.6 s, 0 skipped); `Category=Milestone` 8/0/0 (33.6 s); `Category=Recovery` 4/0/0 (27.5 s);
  `Category=PublishSmoke` 2/0/0 (39.6 s); `Category=PublishAot` 2/0/0 (43.5 s); the old quadruple
  filter 1047/0/0 (2 m 14.3 s) — the per-suite sum confirms the table's 1063 no-filter total.
- One observation recorded, not a defect: `AssertCapturedSshIsThePinnedShape` asserts a lower bound
  (`entries.Length > entryIndex`), so an ssh invocation *beyond* the expected count would not fail an
  assertion by itself. The exclusivity property does not depend on it — the server-side
  accepted-fingerprint counters carry it in every fact — and only the product invokes ssh on the
  worker (scratch HOME, no hooks). Worth an exact-count assertion if the suite ever grows a fact
  where an extra invocation is the failure mode.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | This card is the resolution vehicle for F-018 bug #4 (Low): SSH hardening is shape-pinned only. | Closed (2026-11-15) | The SSH surface is runtime-verified against a real sshd: the delivered-key clone, the three honest rejections and the captured hardened `GIT_SSH_COMMAND` are all proven by real processes; see F-018's bug #4 row. |
| — | Low | A host-key mismatch under a pinned `ZENKINS_GIT_KNOWN_HOSTS` reports `git-failed` (with ssh's `Host key verification failed` in the run log), not a host-key-specific reason. | Observed, accepted | The reason vocabulary is closed by design (F-014); the refusal is named in the run log and the page's explanation row points at it. Adding a `git-host-key-refused` reason would be a product change outside this card. |

## Verification

- Related files: `src/Worker/Git/GitEnvironment.cs` and `src/Worker/Git/GitWorkspace.cs` (the pinned
  surface, unchanged), `tests/Zenkins.Tests/Git/SshdGitServer.cs` (the sshd fixture),
  `tests/Zenkins.Tests/Git/RecordingSshShim.cs` (the invocation capture),
  `tests/Zenkins.Tests/Git/SshdCloneTests.cs` (the suite), `tests/Zenkins.Tests/Milestone/MilestoneHost.cs`
  (`CreateSshGitCredentialAsync`), `docs/development.md` (§1/§2 prerequisite + suite table).
- How to run/verify:
  `dotnet test -c Release --filter "Category=GitSshd"`; the fast suite
  `dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd"`;
  `Category=Git` (the existing suite must stay green); `Category=Milestone` (the milestone host
  changed).
- Results (observed 2026-11-15, Release, macOS 26.5 arm64 / OpenSSH 10.2): build 0 warnings;
  `Category=GitSshd` 3/3 on the final two consecutive runs (21.1 s, 20.1 s), and 3/3 on three
  consecutive runs (20.0 s, 20.6 s, 19.3 s) for the suite's earlier revision, before fact 2 gained
  its pinned-success contrast — two fixture bugs (a double stream-reader attach, a `[sshd]` log
  prefix breaking the counter parse) and two assertion bugs (the pin-file body token, the argv
  program word) were fixed in between, each on the run's own evidence; `Category=Git` 58/58; fast
  suite 1044/1044 with 0 skipped (2 m 25 s); `Category=Milestone` 8/8 (two green runs, before and
  after the harness helper's final form). The skip path was exercised indirectly (its mechanism is
  `Xunit.Sdk.SkipException.ForSkip` with the fixture's named reason; on this machine the tools
  exist, so no skip fired).
