---
id: F-041
title: More parameter types — password, file, run and git ref, with the same snapshot contract
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-18
blocked: none
---

## Goal

Build parameters cover what people actually pass today: a password (never displayed
after trigger), an uploaded file, a choice of a previous run, and a git ref — all under
the contract F-021 established: definitions on the project, resolved values frozen onto
the run, injected into every step's environment.

## Background

F-021 shipped four types (`string`, `text`, `boolean`, `choice`), which cover
configuration but not the cases that force people back to shelling out: a deploy
password must not be echoed on the run page; a build needs a small input file (a
license, a config overlay); "deploy the previous good build" needs to name a run; "build
this tag" needs a ref that is not a branch.

Jenkins has `password`, `file`, `run` and `git parameter`; each has a security or
provenance detail that must not be glossed over.

## Acceptance Criteria

- [x] Four new types: `password`, `file`, `run`, `gitRef`. Existing types are unchanged
      and old parameter JSON still parses. The four F-021 types and every old parameter
      document parse unchanged: the run's value map kept its shape, and the new kinds ride
      three additive columns.
- [ ] `password`: the value is masked on the run page and in every list, is registered
      with the redactor so a step that echoes it logs `***`, and is never returned by an
      API (F-027). It is a per-run input, not a stored Secret, and the UI says so.
- [ ] `file`: the trigger form uploads a bounded file (e.g. 1 MiB); it is stored under
      the run's own directory, its **path** is the injected value, and it is not
      downloadable from the run page (it may be a secret). A file the worker cannot
      materialize fails the run with a typed reason before any step starts.
- [ ] `run`: a single-select list of the project's last N runs (`#N status branch`),
      prefilled with the previous run; the injected value is the run's number, plus
      `<NAME>_RUN_ID` and `<NAME>_COMMIT` as convenience variables for the step.
- [ ] `gitRef`: free text, validated against the parameters' rules (no whitespace, no
      leading `-`); when the project pins a commit (webhook/rebuild) the run's own
      commit wins for the checkout and the parameter is only what the step reads —
      the card must document which one is authoritative.
- [ ] All four: defaults for headless triggers (schedules, polls, upstream, API without
      a value), the 32-parameter cap, unknown-name refusal, type-mismatch refusal.
- [x] The run page shows each value with the right treatment (masked, path, run link,
      ref), and the JSON snapshot on the run cannot be used to recover a password.

## Subtasks

- [x] `ProjectParameter` type enum + validation and JSON (versioned so F-021 rows are
      readable), server-side resolution for each type.
- [x] File upload path and storage: the trigger form posts multipart (or a two-step
      upload), the file lands under the run's directory, size bound enforced in the
      request pipeline as well as the handler.
- [x] Redaction: register password values with the existing redactor and prove it with a
      test where a step echoes the value.
- [x] Environment injection: value + the two `<NAME>_RUN_ID` / `<NAME>_COMMIT`
      conveniences only for `run`.
- [x] UI: per-type controls in the trigger form (the editor picks the types up from
      `ProjectParameterTypes.All`), for the three delivered types.
- [x] PRD §3.11: record the seven types, the masking rules and the file rules (§3.11.2.1).

## Dependencies

- F-021 (parameter definitions, snapshot, injection) — in review

## Test Cases

### TC-001: A password is masked everywhere, redacted in logs

Steps:
1. Define a `password` parameter; trigger with `s3cr3t`; a step echoes the variable.

Expected:
- The run page shows `***` (not the value); the log line shows `***`.
- The API's run read (F-027) omits the value.

### TC-002: A file parameter becomes a readable path

Steps:
1. Define a `file` parameter; upload 3 bytes; a step reads the path and prints the
   content length.

Expected:
- The step prints `3`; the path is inside the run directory; the run page offers no
  download link.

### TC-003: A run parameter names an earlier build

Steps:
1. Two successful runs; trigger with the `run` parameter selecting `#1`.

Expected:
- The step sees the number `1` and `<NAME>_RUN_ID` equal to that run's id.

### TC-004: A ref that cannot exist fails honestly

Steps:
1. A `gitRef` parameter with a nonexistent ref; run.

Expected:
- The clone fails with a `git-*` reason naming the ref; no step runs.

## Notes and Decisions

- A `password` parameter is deliberately not a Secret: it is an input to one run, not
  organization state, and requiring Owner rights to trigger a deploy would be wrong.
  The masking is a display contract, and the redaction is the safety net for logs.
- `file` uploads are per-run and short-lived; the card keeps them out of the artifact
  store so a "download artifacts" link cannot leak an input file.

## Delivered

All four types are delivered. `password`, `run` and `gitRef` landed first (11 facts); `file` followed
once the byte channel was decided.

**`file` (4 facts, each proved live by a mutation):**

| Fact | What it pins | Mutation that turns it red |
|---|---|---|
| `ParameterFileTests.An_uploaded_file_parameter_is_recorded_as_a_path_and_never_in_the_claim` | the value map holds the run-relative location, the bytes are sealed beside it, the page shows the location with no download, and the claim names the parameter without carrying a byte of it | `RunParameterFiles.Serialize` returning null: `ParameterFilesJson` is null and the kind can never be materialised |
| `ParameterFileTests.The_parameter_file_route_serves_the_upload_and_refuses_every_miss_identically` | the worker executing the run gets the raw bytes with `no-store`; an unknown name, an unknown run, another worker's token and an ended run are four bodyless 404s | dropping `IsTerminal(run.Status)` from the refusal predicate: the ended run answers 200 |
| `ParameterFileTests.TC_002_a_step_reads_the_uploaded_file_at_a_path_in_the_run_directory` | the step reads an absolute path under the run's own `temp/parameters/`, the uploaded bytes are there, the file is `0600` on Unix, and the run's own cleanup removes it | writing the file into a shared temp directory: no emitted line starts with the run's `temp/` (`Assert.NotNull(path)`, `ParameterFileTests.cs:151`) |
| `ParameterFileTests.A_file_parameter_the_worker_cannot_fetch_fails_the_run_before_any_step` | a refused fetch throws a typed failure naming the parameter, and the run-level reason is one the server accepts from a worker | tolerating a null fetch (writing an empty file): `Assert.Throws` finds no exception (`ParameterFileTests.cs:186`) |

**The byte channel is (b):** a run-scoped, worker-authenticated route
(`POST /api/workers/runs/{runId}/parameter-files`, name in the body), the bytes sealed on the run with the
same purpose as the password values, and the worker writing them before anything runs. The claim never
carries material, which is why it stays small for a megabyte upload. The route is in Q-006's committed
inventory; `TenantIsolationInventoryTests` fails if it is not.

Two design points worth keeping: a file parameter's *value* on the run is the run-relative location
(`temp/parameters/{NAME}`), so the page is truthful while the absolute path stays the worker's; and the
trigger endpoint reads the uploaded files through `HttpRequest.ReadFormAsync` rather than binding an
`IFormFileCollection` parameter, because that parameter type makes the endpoint accept **only** multipart
and every existing urlencoded caller would get a 415.

Gates: `dotnet build -c Release` 0 warning / 0 error. `--list-tests` **1496 -> 1511** (+15 facts).
Targeted: `Zenkins.Tests.Runs|Api|Projects|Webhooks|Schedules|Steps` 431 green (2 skipped), the tenancy
surface inventory and `ModelSnapshotTests`. The full suite and the `Category=Postgres` harness are deferred
to the branch's single final run.

Schema: `20261118125809_AddRunProtectedParameters` (`ProtectedParametersJson`, `ParameterDetailsJson`) and
`20261118131532_AddRunParameterFiles` (`ParameterFilesJson`), both generated with
`Database__Provider=Postgres`, both `text`, no SQLite store types in the snapshot.

## Follow-up: the cross-tenant fact, and the two layers that refuse a foreign run (2026-11-18)

The review's tenth mutation stayed green: adding `.IgnoreQueryFilters()` to the parameter-file route
refused nothing, because the fact's "other worker" was same-tenant and its "unknown run" was a fresh Guid.
The tenant half of the refusal contract was unguarded, and `TwoOrgsFixture` already carried a real run id
for the other organisation that nothing used.

The fact now exists (`TenantIsolationParameterFileProbeTests`), and building it produced a structural
detail worth recording: the route refuses a foreign run **twice** - a tenant filter and a
"this run belongs to the calling worker" check - so removing either one alone still refuses, and only
removing both lets another organisation's bytes through (`Expected: NotFound / Actual: OK`). A fact that
reddens on one layer would have been a fact about that layer, not about the boundary; both are now pinned,
one per layer, and the two sentences in the Q-006 row match what is actually asserted.

## Close-out verification (2026-11-18)

Three types landed in one pass, the fourth in a second, and the merge of the two branches produced the kind
of conflict git cannot see.

- **Four new types on F-021's contract.** `password`, `run`, `gitRef` and `file`; old parameter JSON still
  parses and the existing four types are untouched. `password` is masked in the snapshot (`***`), sealed
  beside it under its own data-protection purpose, absent from the run resource, refuses a declared default
  (a default would put plaintext on the project row), and a step that echoes it logs the mask through the
  real server, real claim, real executor and real redactor. `run` injects the run number plus
  `<NAME>_RUN_ID` and `<NAME>_COMMIT`, resolves headless triggers to the most recent *successful* run,
  refuses a run from another project, and has its own failure code for "no successful run yet". `gitRef`
  is validated at save and at trigger, and a pinned commit stays authoritative: the parameter is only what
  the step reads.
- **A file parameter never travels in the claim.** The upload is sealed under the same purpose as the
  password, the claim carries only the name, and the worker fetches the bytes from a run-scoped route -
  `POST /api/workers/runs/{runId}/parameter-files` - whose refusal contract is copied from F-040's: a
  foreign run, a foreign name, another worker's run and an ended run are one body-less 404, with a
  cross-tenant fact on top because this is the only new route a worker can reach that returns ciphertext.
  The worker writes the bytes into the run's own `temp/parameters/`, mode 0600 on Unix, adopted by the
  existing temp cleanup, and injects the **path**.
- **The route's content type was a regression the author found by failing.** Binding `IFormFileCollection`
  as a handler parameter made the endpoint accept multipart only, turning every urlencoded trigger into a
  415 and reddening unrelated tests; reading the form inside the handler restores both. Worth recording
  because the symptom appeared nowhere near the cause.
- **Two merge facts are worth keeping.** `git merge-tree` reported no conflict between this branch and
  F-040, the migration timestamps interleave correctly, and the model snapshot merged as a union - but the
  merged tree did not compile: both cards had added an identical `CleanupTemp` helper to the same test
  harness in different regions, so the textual merge kept both and produced CS0111. The lesson is not
  "avoid parallel branches" but "build the merged tree before pushing it"; `ModelSnapshotTests`,
  `ParameterFileTests`, `SecretKind*` and `ParameterType*` then ran 32/32 green on the merged tree.
- **The surface inventory bites.** A new route has to be listed in Q-006 *with a probe name that resolves
  to an existing test*, and the tenant-isolation inventory fails if it is missing - which is how the
  cross-tenant fact got required rather than remembered.
- **Counts.** fast 1462 -> 1502 measured on the merged tree, Postgres stays 9, other harnesses 23, so all
  seven is 1534. The full suite is deferred to the single batch run that closes this series of cards.
