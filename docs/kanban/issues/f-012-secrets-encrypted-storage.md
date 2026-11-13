---
id: F-012
title: Secrets: encrypted storage and Owner-only management
type: feature
priority: P1
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
blocked: none
---

## Goal

An Owner can create, rotate and delete organization secrets; the value is encrypted at rest, is
never readable again through any API or page, and every cross-tenant or Member-level access is
refused with no existence oracle. Secret *delivery to a run* is F-013 (git credentials) and F-015
(`secret()`), not this card.

## Background

PRD §3.8: secrets encrypted at rest, plaintext never re-readable in the UI, per-tenant isolation,
Owner-only management. PRD §6 lists `Secret` as a core entity. PRD §5.5 (security) and the Q-001
harness set the bar for tenant probes.

Decisions:

- **Encryption: ASP.NET Core Data Protection with a persisted key ring.** The application already
  depends on `IDataProtectionProvider` (the run-log subscription ticket), so this adds no new
  dependency; the protector is purpose-bound (`Zenkins.Secrets.v1`) so a ciphertext from another
  purpose cannot be substituted. The key ring is persisted to `App_Data/keys` at startup (it is
  currently whatever the framework default resolves to, which is outside the application's data
  directory and does not survive a machine move). Losing the key ring means every secret has to be
  re-entered — documented as a backup requirement, not a surprise.
- **Secrets are write-only above the storage layer**: `GetValueAsync` exists only on an internal
  service used by run execution (F-013/F-015), never exposed by an endpoint. The UI has create /
  rotate / delete and a name list; there is no reveal affordance, and the API has no GET-value route
  at all (not a disabled button — no route).
- **Identity is the name**: unique per organization, case-sensitive, validated against
  `^[A-Za-z_][A-Za-z0-9_]{0,63}$` so `secret("NUGET_TOKEN")` is unambiguous and env-variable
  compatible. Rotation replaces the ciphertext and stamps `RotatedAt`; the name cannot be renamed
  (a rename is delete + create, so no run silently picks up a different value shape).
- **Ownership**: per PRD §3.1.2 only an Owner manages secrets; a Member gets 403 for writes and 404
  for reads of a foreign organization's secret id (same generic answer as elsewhere).
- **Audit-light by design**: `CreatedAt`, `UpdatedAt`, `RotatedAt`, `CreatedByUserId` stored; no
  plaintext, no value hash (a hash of a low-entropy secret is a guessing oracle).

## Acceptance Criteria

- [ ] `Secret` entity (`OrganizationId`, `Name`, `Ciphertext`, `CreatedAt`, `UpdatedAt`, `RotatedAt`,
      `CreatedByUserId`) + migration; unique index `(OrganizationId, Name)`; PostgreSQL contract
      gate reports no pending changes.
- [ ] Create/rotate/delete endpoints exist and are Owner-only; a Member's write is 403, a foreign
      organization's secret id is 404, and no response ever contains plaintext.
- [ ] No endpoint or page can return a stored value: verified by enumerating the endpoint table in a
      test (a route-shape assertion), not by remembering.
- [ ] Encryption round-trips through the purpose-bound protector; a ciphertext written under a
      different purpose fails to decrypt; the key ring path is configured and asserted at startup.
- [ ] The name validator rejects empty, over-long, whitespace, and `=`/`$`/`-` names — a secret name
      is usable directly as an environment variable name by F-015.
- [ ] A minimal Owner-only page lists names with created/rotated timestamps, creates, rotates and
      deletes (with confirmation); it renders no value and has no reveal control. Full secrets
      management UI stays in v0.9.
- [ ] Tenant isolation: Q-001 harness gains read, write and unique-index probes for `Secret`; the
      two-org fixture seeds one secret per organization.
- [ ] Rotation invalidates the old ciphertext (a second decrypt with the pre-rotation ciphertext is
      impossible by construction — the column is replaced), and a rotate of a missing name is 404.

## Subtasks

- [ ] Entity + migration + `ZenkinsDbContext` configuration and unique index.
- [ ] `Secrets/SecretProtector` (purpose-bound encrypt/decrypt) and `Secrets/SecretService`
      (Owner-only CRUD, name validation, internal `TryGetValueAsync` for later cards).
- [ ] `Secrets/SecretEndpoints` + a minimal Owner-only Razor page; nav entry.
- [ ] Data Protection key-ring persistence in `Program.cs` (`App_Data/keys`) and a startup
      assertion that the directory is writable, with the backup requirement in
      `docs/development.md`.
- [ ] Tests (`Category=Secrets`): round-trip, wrong-purpose rejection, Owner/Member/foreign-org
      matrix, name validation, route-shape "no plaintext route", rotation semantics; tenancy probes.

## Dependencies

- none (uses the existing tenancy and auth infrastructure)

## Test Cases

### TC-001: Owner manages, Member and foreign org are refused

Steps:
1. As an Owner of A, create `NUGET_TOKEN`; rotate it; list secrets.
2. As a Member of A, attempt create and rotate.
3. As an Owner of B, request A's secret id for rotate and delete.

Expected:
- Owner's operations succeed and the list shows names + timestamps only.
- Member: 403 on both writes.
- B's Owner: 404 with an empty body, A's secret unchanged.

### TC-002: The value is unrecoverable through the API

Steps:
1. Enumerate the application's endpoint metadata and search for any route whose response could carry
   a secret value.
2. Create a secret, then fetch every secret-related response the UI issues.

Expected:
- No route returns a value; the create/rotate responses contain name and timestamps only.

### TC-003: Encryption is purpose-bound and the key ring is persisted

Steps:
1. Encrypt with the `Zenkins.Secrets.v1` protector; attempt to decrypt with a protector for another
   purpose.
2. Restart the host with the same data directory and read the secret.

Expected:
- (1) throws `CryptographicException`. (2) decrypts successfully, i.e. the key ring persisted.

### TC-004: Name validation

Steps:
1. Attempt to create secrets named `""`, `"a b"`, `"1TOKEN"`, `"A-VAR"`, `"$(x)"`, and a 65-char name.

Expected:
- Only the environment-variable-safe name is accepted; each rejection names the rule, and no row is
  written.

## Development Log

### 2026-11-13

- Planned from the v0.3 plan; no work started. Delivery to runs (`secret()` accessor, worker-side
  redaction) is deliberately left to F-015, and git credentials to F-013, so this card has no
  dependency on the pipeline work.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Data/Entities/Secret.cs`, `src/Server/Data/ZenkinsDbContext.cs`,
  `src/Server/Data/Migrations/*`, `src/Server/Secrets/*`, `src/Server/Components/Pages/Secrets/*`,
  `src/Server/Program.cs`, `docs/development.md`, `tests/Zenkins.Tests/Secrets/*`,
  `tests/Zenkins.Tests/Tenancy/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Secrets"`;
  full suite; PostgreSQL contract gate.
- Results: not run (card not started).
