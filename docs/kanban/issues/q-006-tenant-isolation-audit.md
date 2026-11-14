---
id: Q-006
title: Cross-tenant isolation audit (data, logs, artifacts, git credentials)
type: test
priority: P1
size: L
status: done
created: 2026-11-14
updated: 2026-11-14
blocked: none
---

## Goal

A written audit plus regression tests proving that one organization cannot reach another's data, logs,
artifacts, git credentials or secrets through any surface — HTTP routes, the SignalR hub, webhook
endpoints, or file-system paths (PRD §10 scenario 5).

## Background

Roadmap v1.0 ("Cross-tenant isolation audit"). Isolation was built in slice by slice and each slice
was tested by its own card: Q-001 probes the query filters and form models, F-012 encrypts secrets,
F-013 stores git credentials, F-016 keeps artifacts path-safe, F-008 streams logs. Nobody has audited
the composition across every surface at once, and composition is exactly where F-008's review once
found a rejection discriminator that never matched. The PRD's promise is per-organization isolation of
"projects, workers, secrets and build records" — this card makes the promise explicit and enumerable.

Decisions:

- **Enumerate before probing.** The first deliverable is a surface list (every route, hub method,
  stream id and store location), committed with the audit, so a probe that is missing is a visible
  gap rather than an unknown.
- **Id-swap style, like Q-001**: for every surface, org A's session asks for org B's resource by id,
  path or name, and the answer must be 404/403 without leaking existence.
- **Filesystem-level checks for stores.** Artifacts and git credentials live on disk keyed by run or
  project; the audit checks the layout itself (no org-B id reachable under org A's access, no
  credential material inside org A's artifact or log bytes), not only the HTTP status.
- **Holes are fixed here**, each with its failing probe kept as a regression test.

## Acceptance Criteria

- [x] A committed surface inventory lists every tenant-reachable surface: MVC/Blazor routes, SignalR
      hub methods, webhook endpoint, artifact store layout, credential and secret storage.
- [x] Every inventoried surface passes the id-swap probe: org A requesting org B's resource by id,
      name or stream id is refused with 404/403, and the refusal does not confirm existence.
- [x] Artifact bytes are unreachable across tenants: another org's run id is refused on download, and
      no artifact path under org A's access resolves into org B's files (path-traversal re-check
      included, F-016 rules re-verified from the audit).
- [x] Git credentials and secret values never appear in another tenant's responses, logs or artifacts;
      F-015's redaction is re-verified cross-tenant, not only within one org.
- [x] The SignalR log stream refuses org A's browser session for an org B run id (hub-level
      authorization, not only page-level).
- [x] Every hole found is fixed in this card and its probe is a permanent regression test.

## Subtasks

- [x] Build the surface inventory (routes, hub methods, streams, stores) and commit it.
- [x] Id-swap probe suite over the inventory, reusing Q-001's two-org fixture.
- [x] Filesystem audit of artifact layout and credential/secret storage locations.
- [x] Hub-level authorization checks for log streaming.
- [x] Fix findings; keep each failing probe as a regression.

## Dependencies

- none (Q-001, F-008, F-012, F-013, F-015, F-016, F-017 are accepted)

## Test Cases

### TC-001: The inventory is complete against the router — PASS

Steps:
1. Enumerate registered endpoints and hub methods from the built Server assembly; diff against the
   committed inventory.

Expected:
- No undocumented tenant-reachable surface; the audit fails loudly when a new endpoint is added
  without entering the inventory.

Result (observed, 2026-11-14): PASS. `TenantIsolationInventoryTests` boots the real `Program`, walks the
`EndpointDataSource` (Blazor pages included), reflects the two hubs' public methods, and diffs both ways
against the committed inventory (`docs/audits/q-006-surface-inventory.md`: 64 HTTP surfaces, 6 hub
routes/methods, 7 non-tenant infrastructure rows, 12 stores/ids); every tenant-reachable row's Probe
column is verified to name an existing probe by reflection. The independent reviewer's own preread
inventory (built from source, before the implementation existed) matched the committed list with zero
missing surfaces; the extra rows the diff requires (static assets, `/_blazor`, fallbacks, negotiate
sub-routes, group ids) are non-tenant infrastructure, correctly classified.

### TC-002: Cross-tenant artifact and credential reach is zero — PASS

Steps:
1. As org A, request org B's run artifacts by id; attempt path-traversal spellings; search org A's
   logs and artifact bytes for org B's credential or secret material.

Expected:
- All requests refused without existence leaks; zero credential or secret bytes in org A's surfaces.

Result (observed, 2026-11-14): PASS. Both cross-tenant artifact spellings (B-run/B-artifact and
A-run+B-artifact) answer bare 404 with zero bytes, against a same-tenant positive control; six
traversal spellings (including the `%2e%2e` wire form and a backslash path) all answer 400, and
`....//` normalizes to a file landing inside the artifact directory; the store tree is strictly
`{root}/{org}/{run}/{artifact}/…` with bidirectional byte isolation asserted over the whole tree. The
material audit scans org A's log download, artifact bytes, secrets page and credential list — and the
raw SQLite file bytes (Latin-1) — for org B's plaintext secret and credential values in both
directions: zero hits, with row-level isolation asserted alongside.

### TC-003: The hub is tenant-honest — PASS

Steps:
1. Open org A's browser session and subscribe to an org B run's log stream id.

Expected:
- Refusal at the hub method; no log line from org B's run ever reaches the session.

Result (observed, 2026-11-14): PASS. Org A's ticket joining org B's run is refused inside the hub
method (generic rejection, no existence leak, zero rows reach the session); during org B's worker
live-streaming its run, A's session receives zero B rows while A's own history still arrives; the
worker hub refuses both cross-tenant appends and same-tenant non-executing workers with zero
persistence (positive control rows carry org A's stamp); ticket/worker-token cross-use is rejected at
negotiate in all four combinations.

## Development Log

### 2026-11-14

- Planned from roadmap v1.0. Inventory-first so the audit survives future cards: an endpoint outside
  the inventory is a test failure by construction.
- Implemented in one pass. Deliverables: the committed surface inventory
  (`docs/audits/q-006-surface-inventory.md`) and 35 probe tests in 11 classes
  (`tests/Zenkins.Tests/Tenancy/`, trait `Category=TenantIsolation`), reusing Q-001's `TwoOrgsFixture`
  and real-product credential paths (Owner mints a registration token → `/api/workers/register` → a
  real worker token; secrets and git credentials created through the Owner forms so the probes scan
  real ciphertext). The inventory is machine-enforced: real-`Program` `EndpointDataSource` (Blazor
  pages included) + hub-method reflection, bidirectional diff, and a per-row Probe existence check.
- No product-code changes: the composition held, no hole found (AC-6's empty set). Two contract
  details confirmed as established semantics and recorded in the inventory: the webhook delivery
  endpoint answers 401 (not 404) for a valid id with a bad signature — F-017's capability-URL ruling
  (residual: 401 ⇒ id exists and enabled; mitigated by 128-bit Guid entropy plus per-id budget
  limiting, 429 before any lookup); and `ReportDefinitionAsync` validates payload shape before the
  tenant-filtered run lookup, so validation errors carry no existence leak.
- Independent review round 1: FAIL on two documentation items only (webhook residual row missing the
  mitigation element; the three filter-critical / hub-scope invariants not pinned with file:line and
  their enabling conditions). Dev fixed the inventory doc only; review round 2: PASS. During the fix,
  dev corrected three file:line citations the review had mis-cited (the Artifact query filter lives at
  `ArtifactConfiguration.cs:59`, not :88; the download gate at `ArtifactService.cs:553/:584`, not
  :490-520; `IsPageRequest` at `TenantResolutionMiddleware.cs:46-49`, not :53-56) — the reviewer
  re-verified against source and confirmed dev's coordinates.
- Adjudicated residual (PM): the "Documented invariants" section is intentionally outside the
  parser's machine validation (unknown headings are skipped), so its prose is not cell-checked. The
  drift-prevention duty stays with the four controlled sections, and invariant prose is reviewed by
  eyes, not cell counts. Making the parser recognize a fifth section would be a ~10-line follow-up
  card if ever wanted.
- Verification (dev run + independent review run + review re-verify):
  `dotnet build -c Release` 0 warnings; `dotnet test -c Release --filter
  "FullyQualifiedName~TenantIsolation"` → 107/107, 0 skipped; `--filter "Category=TenantIsolation"` →
  114/114 (the 7 extra are Q-001's `ActiveOrganizationResolutionTests`, which carry the trait but not
  the FQN string); full suite → 998/998, 0 failed, 0 skipped; inventory re-verify after the doc fix →
  3/3. No PostgreSQL gate: no claim-query or product changes.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| — | — | No product defect found: the composition held across every surface probed (cookie pages/forms, worker-token API, webhook delivery, both hubs, artifact store, credential/secret storage). | Closed | Two established semantics recorded in the inventory (webhook capability-URL 401; definition validation order); one adjudicated residual (the Documented invariants section is prose, outside machine validation) recorded in the development log. |

## Verification

- Related files: `docs/audits/q-006-surface-inventory.md` (the committed inventory);
  `tests/Zenkins.Tests/Tenancy/TenantIsolationSurfaceInventory.cs` (inventory data),
  `TenantIsolationInventoryTests.cs`, `TenantIsolationHttpSurfaceProbeTests.cs`,
  `TenantIsolationWebhookDeliveryProbeTests.cs`, `TenantIsolationWorkerApiProbeTests.cs`,
  `TenantIsolationArtifactUploadProbeTests.cs`, `TenantIsolationMaterialAuditTests.cs`,
  `TenantIsolationHubProbeTests.cs` (probe suites) with `TenantIsolationAuditProbes.cs` /
  `TenantIsolationWorkerApiProbes.cs` (shared host plumbing); `TwoOrgsFixture.cs` and
  `AuthenticatedTenantApplicationFactory.cs` (Q-001 fixture, reused unchanged); server surfaces
  audited, not modified.
- How to run/verify: `dotnet test -c Release --filter "FullyQualifiedName~TenantIsolation"`;
  `dotnet test -c Release --filter "Category=TenantIsolation"` (also picks up Q-001's
  `ActiveOrganizationResolutionTests`); full suite; after inventory-doc-only edits, the inventory
  class alone is the re-verify gate.
- Results (observed 2026-11-14; dev run, independent review run, and a re-verify after the doc fix):
  `dotnet build -c Release` 0 warnings / 0 errors; TenantIsolation 107/107 by FQN, 114/114 by trait,
  0 skipped; full suite `dotnet test -c Release` → 998/998, 0 failed, 0 skipped; inventory class
  3/3 after the doc-only fix; no PostgreSQL gate (no claim-query or product changes).
