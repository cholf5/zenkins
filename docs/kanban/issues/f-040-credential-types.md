---
id: F-040
title: More credential types — username/password pairs and secret files, bindable per step
type: feature
priority: P2
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
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

- [ ] `Secret` gains a kind: `text` (today's behaviour, unchanged), `usernamePassword`
      and `file`; all three keep the existing ciphertext envelope, Owner-only
      management and write-only read-back rules.
- [ ] Reference syntax extends without breaking existing scripts:
      `${secret:NAME}` (text), `${secret:NAME.username}` / `.password` (pair, each part
      resolvable independently), `${secretFile:NAME}` (resolves to a per-run file path,
      material written by the worker with owner-only permissions on Unix).
- [ ] A file secret is written inside the run's own directory (`work/{run-id}/...`),
      never a shared location, is bounded (e.g. 64 KiB), and is removed by the run's
      temp cleanup; the path substituted into a step contains no secret material.
- [ ] Redaction covers the new shapes: the pair's password and the file's contents never
      appear in logs, the run page, artifacts or an API response; the *path* is safe to
      print and is not redacted.
- [ ] The frozen spec's `secretReferences` (D-006) records the exact references used —
      including the `.password` part and file secrets — so the Secrets page's "which
      pipelines use this" stays truthful.
- [ ] The run's secret-delivery endpoint (worker-authenticated, run-scoped) serves the
      new kinds without widening its refusal contract: a foreign run, a foreign name,
      another worker's run or an ended run is one bodyless 404.
- [ ] The claim payload carries no secret material (names only), so a large file secret
      never bloats it: the worker fetches the value it needs by name.

## Subtasks

- [ ] Data: secret kind + JSON payload shape inside the existing ciphertext (one
      envelope version, migration for existing rows as `text`).
- [ ] Reference grammar: extend `StepReference` and the Lua `secret()` bridge; reject
      `.username`/`.password` on a text secret and any part access on a file secret.
- [ ] Worker: fetch by (run, name), materialize a file secret under the run directory
      with restrictive permissions, register it for cleanup, and register every
      revealed value with the redactor.
- [ ] UI: the Secrets page's create form gains the kind and its fields; the value is
      still shown once on create/rotate only.
- [ ] Docs: `docs/` secrets section — the three kinds, the reference grammar, and what is
      never redacted (the path).
- [ ] PRD §3.8: record kinds, grammar and the file rules.
- [ ] Q-006: the surface inventory stays green (no new user-facing route returns a value).

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

### TC-003: Wrong part access is a definition error

Steps:
1. Use `${secret:registry}` (a pair) as a plain value, and `${secret:text.password}`.

Expected:
- Both are refused at definition time with a clear message naming the reference.

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
