---
id: F-040
title: More credential types — username/password pairs and secret files, bindable per step
type: feature
priority: P2
size: M
status: done
created: 2026-11-16
updated: 2026-11-18
blocked: none
---

## Goal

Beyond a Git credential and a single secret string, an organization can store
username/password pairs and secret files, and a step can bind one to a chosen
environment variable or file path, so registry logins, package feed keys and key files
stop being flattened into one string.

## Background

Two mechanisms exist today and neither fits a pair:

- `GitCredential` (HTTPS token or SSH key) exists only to clone, is delivered only to
  the executing worker, and is never readable.
- `Secret` is a single string, injected by `${secret:NAME}` or `secret("NAME")`.

Real needs that break: `docker login` wants two values; a private feed key wants a
username plus a token; an SSH deploy key or a `.p12` wants a **file** whose path is
what the command reads. Jenkins models exactly this with credential types and
`withCredentials`, which binds a credential to the variable names the script chose
rather than to a fixed name.

## Acceptance Criteria

- [x] `Secret` gains a kind: `text` (today's behaviour, unchanged), `usernamePassword`
      and `file`; all three keep the existing ciphertext envelope, Owner-only
      management and write-only read-back rules.
- [x] Reference syntax extends without breaking existing scripts:
      `${secret:NAME}` (text), `${secret:NAME.username}` / `.password` (pair, each part
      resolvable independently), `${secretFile:NAME}` (resolves to a per-run file path,
      material written by the worker with owner-only permissions on Unix).
- [x] A file secret is written inside the run's own directory (`work/{run-id}/...`),
      never a shared location, is bounded (e.g. 64 KiB), and is removed by the run's
      temp cleanup; the path substituted into a step contains no secret material.
- [x] Redaction covers the new shapes: the pair's password and the file's contents never
      appear in logs, the run page, artifacts or an API response; the *path* is safe to
      print and is not redacted.
- [x] The frozen spec's `secretReferences` (D-006) records the exact references used —
      including the `.password` part and file secrets — so the Secrets page's "which
      pipelines use this" stays truthful.
- [x] The run's secret-delivery endpoint (worker-authenticated, run-scoped) serves the
      new kinds without widening its refusal contract: a foreign run, a foreign name,
      another worker's run or an ended run is one bodyless 404.
- [x] The claim payload carries no secret material (names only), so a large file secret
      never bloats it: the worker fetches the value it needs by name.

## Subtasks

- [x] Data: secret kind + JSON payload shape inside the existing ciphertext (one
      envelope version, migration for existing rows as `text`).
- [x] Reference grammar: extend `StepReference` and the Lua `secret()` bridge; reject
      `.username`/`.password` on a text secret and any part access on a file secret.
- [x] Worker: fetch by (run, name), materialize a file secret under the run directory
      with restrictive permissions, register it for cleanup, and register every
      revealed value with the redactor.
- [x] UI: the Secrets page's create form gains the kind and its fields; the value is
      still shown once on create/rotate only.
- [x] Docs: `docs/` secrets section — the three kinds, the reference grammar, and what is
      never redacted (the path).
- [x] PRD §3.8: record kinds, grammar and the file rules.
- [x] Q-006: the surface inventory stays green (no new user-facing route returns a value).

## Dependencies

- F-012 (encrypted secrets, Owner-only management) — accepted
- D-006 (reference capture) — accepted

## Test Cases

### TC-001: A pair binds both halves

Steps:
1. Store `registry` as a `usernamePassword` secret.
2. A step uses `${secret:registry.username}` and `${secret:registry.password}`.

Expected:
- The step receives the two halves; neither appears in the log (the password is
  redacted, the username may be visible).

### TC-002: A file secret is a path, and the file is gone afterwards

Steps:
1. Store a `file` secret; a step prints `${secretFile:deploy-key}` and reads the file.

Expected:
- The printed value is a path inside the run directory; the file exists during the
  step and is removed by the run's cleanup.
- The log does not contain the file's contents.

### TC-003: Wrong part access is a step-preparation error

Steps:
1. Use `${secret:registry}` (a pair) as a plain value, and `${secret:text.password}`.

Expected:
- Both are refused **at the step boundary**, before any process starts, with a message
  that names the reference and what the secret actually is; the step is recorded
  `Failed` with `secret-unavailable` and nothing is substituted.

Not a definition-time error, and that is settled rather than deferred: the definition phase
runs before anything is fetched (F-015's deferred references exist precisely so describing a
run never decrypts anything), so it cannot know a secret's kind. The kind is only known when
the value arrives, which is the step boundary. Recording the refusal there is the honest
place; moving it to save time would mean decrypting org secrets while a pipeline is being
described.

### TC-004: The refusal contract is unchanged

Steps:
1. Request another run's secret name with this run's worker token.

Expected:
- A bare 404, byte-identical in shape to today's refusal.

## Notes and Decisions

- File secrets are written under the run directory rather than an OS temp directory:
  the run's cleanup already owns that tree, and a shared temp directory would leak
  between runs on a busy worker.
- Rejected: a generic `withCredentials` block in Lua. The reference syntax already
  names the variable inside the script; a block would add a second binding vocabulary
  for the same thing.

## Verification

Build: `dotnet build -c Release` — 0 warning, 0 error. Discovered tests `--list-tests`:
**1502 → 1521** (+19 facts). Every new fact was proved live by breaking exactly one thing and
watching it go red, then restoring.

| AC | Fact(s) | The mutation that turns it red |
|---|---|---|
| a **new** secret with no kind is text (AC-1's "text stays today's behaviour") | `SecretKindStorageTests.A_secret_created_without_a_kind_is_a_text_secret` - the service, not the form, because a form always posts a kind and would never exercise the fallback | `SecretKinds.Default` -> `File`: the created row reads `file` (`SecretKindStorageTests.cs:256`, `Expected "text" / Actual "file"`) |
| kind on `Secret`, old rows are `text` | `SecretKindMigrationTests.A_secret_written_before_the_kind_column_survives_as_a_text_secret`, `SecretKindStorageTests.An_old_row_reads_as_a_text_secret` | the migration's `defaultValue: "text"` → `"file"`: the migrated row reads `file` (`Assert.Equal(Text, secret.Kind)`); absent kind normalised to `File` instead of `Default` |
| reference syntax, parts, `secretFile` | `SecretKindStepTests.TC_001_…`, `A_pair_referenced_twice_is_fetched_once_and_only_the_password_is_registered`, `TC_003_…` | swap the two halves in `Substitute` (`user=deploy-bot` line goes red); cache by recorded form instead of name (fetches 1 → 2); disable the `IsAllowedFor` check (mismatch step succeeds) |
| file inside `work/{run-id}`, bounded, cleaned up, path has no material | `TC_002_…`, `A_file_secret_over_the_bound_is_refused_and_nothing_is_written` | point `SecretsPath` at the OS temp directory (no line under the run's `temp/`); add `OtherRead` to the created file's mode (`UserWrite｜UserRead` vs actual); drop the byte bound (no exception) |
| redaction covers the new shapes, not the path | `TC_001_…`, `TC_002_…` | the same half-swap prints the password where the username belonged; the file fact asserts the path line verbatim and the material nowhere |
| `secretReferences` records the exact form | `A_large_file_secret_never_reaches_the_claim_payload` (claim's frozen spec holds `file:BIG_KEY`), `SecretKindPageTests` | make `ReferencesIn` return nothing (`file:BIG_KEY` missing from the claim's spec) |
| delivery serves every kind, refusals unchanged | `SecretKindDeliveryTests.The_run_scoped_fetch_…` | drop `IsTerminal(run.Status)` from the refusal predicate: the ended run answers 200 instead of the bodyless 404 |
| claim carries names only | `A_large_file_secret_never_reaches_the_claim_payload` | the fact fails if the capture records anything but the reference; the material/length assertions are the belt-and-braces half of the same fact |

Also pinned: `SecretKindStorageTests` (pair halves, file bytes, rotation keeps the kind, unknown kind,
missing half, over-size content, **and a create with no kind at all landing as `text`**) and
`SecretKindPageTests` (kind selector, both pair fields, per-kind
rotate form, the syntax each row is read with).

Schema: one migration, `20261118123606_AddSecretKind`, generated with `Database__Provider=Postgres`
(the snapshot diff is 7 lines, `character varying(32)`, no SQLite store types). `ModelSnapshotTests`
stays green. Not run here: the full suite and the `Category=Postgres` harness (deferred to the
branch's single final run); the PG-only facts skip without `ZENKINS_TEST_POSTGRES`.

Comment corrected in review: `StepReference.Match`'s "longest prefix first" note claimed that
matching the shorter prefix would read a file reference's name as `File:NAME`. It cannot — the two
prefixes diverge at the ninth character (`:` vs `F`) — so the order was unobservable and the note
described a bug that could not happen. The order stays (readability), the note now says what is true,
and `SecretReferenceTests.The_two_secret_prefixes_cannot_both_match_at_one_position` pins the
property the note relies on: make `"${secret:"` a prefix of `"${secretFile:"` and it goes red
(verified) instead of the order silently becoming load-bearing.

Known limitation, recorded rather than hidden: a mismatch is refused at the **step boundary**, not at
definition time - the definition phase cannot know a secret's kind without decrypting it, so the
refusal names the reference and fails the step before any process starts. Q-006's inventory is
untouched: no new user-facing route and no route that returns a value.

## Close-out verification (2026-11-18)

Six commits by two developers (the first handed over after the shared layer), twenty-two facts, and one
decision that moved a guarantee rather than a line of code.

- **A secret now has a kind, and the envelope did not change.** `text` is still the bare string inside the
  ciphertext, so an old row, an old reader and an old build all keep working; a pair is encoded with
  length prefixes rather than JSON, because `Shared` is compiled with Native AOT and `JsonSerializer`'s
  reflection path trips IL2026/IL3050. The new `Kind` column arrives with `defaultValue: "text"`, which is
  the compatibility rule itself rather than a convenience - and because EF then marks the property
  `ValueGeneratedOnAdd`, a create that never sets a kind is pinned by a fact (it runs the service, not the
  form, because the form always posts a kind and would never reach that path).
- **The reference grammar grew without breaking a single script.** `${secret:NAME}` still means the whole
  text value; `.username` / `.password` resolve independently; `${secretFile:NAME}` yields a path and
  takes no part. A mismatch says both things the reader needs: what the reference asked for and what the
  secret actually is. The four directions each have a mutation that reddens at its own message.
- **The redaction boundary is two-sided.** A pair's password and a file secret's *contents* are registered
  with the redactor; the **path is not**, and a reversed mutation proves it - registering the file's
  contents reddens the assertion that the printable path survived. A step that echoes a password logs
  `***` through the real server, real claim, real executor and real redactor, not through a stub.
- **A file secret lives in the run's own tree**: `work/{run-id}/temp/secrets`, mode `0600` on Unix,
  refused rather than truncated above 64 KiB, and adopted by the run's existing temp cleanup - four
  separate mutations, one per property, because "it is deleted" and "it was never in a shared directory"
  fail differently.
- **The claim carries names, never material.** That is what makes the delivery endpoint - which serves all
  three kinds and keeps its refusal contract identical (foreign run, foreign name, another worker's run,
  an ended run: one body-less 404) - the only place a value can be fetched from.
- **One guarantee moved from "definition time" to "step boundary", deliberately.** A wrong part access
  cannot be refused when the pipeline is described, because describing it must not decrypt anything; the
  kind is only known when the value arrives. The step therefore fails before its process starts, with
  `secret-unavailable`, naming the reference and the real kind. The card's TC-003 was rewritten to say
  this and to say why, so the next reader does not "fix" it back to a definition-time refusal by
  decrypting organisation secrets during description.
- **Two review findings were about the facts, not the code.** The card's own TC-003 contradicted its
  notes, and a comment claimed `NameStart` was asserted when only the prefix was: the test now slices the
  value at that offset so that "the prefix is right but the offset is wrong" fails at
  `Expected: 14 / Actual: 10`. A third finding - a comment that warned about a prefix ordering that cannot
  happen - was answered by pinning its premise instead: the two prefixes differ at character nine, so the
  ordering is unobservable, and the fact now asserts that they can never nest.
- **Counts.** fast 1502 -> 1521, Postgres stays 9. The full suite is deferred to the single batch run at
  the end of this series of cards, by instruction.
