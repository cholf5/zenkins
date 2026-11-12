---
id: I-002
title: EF Core data layer with enforced tenant isolation
type: feature
priority: P1
size: M
status: todo
created: 2026-11-12
updated: 2026-11-12
blocked: none
---

## Goal

A single `ZenkinsDbContext` that can never return another tenant's rows, on SQLite for development and PostgreSQL for production, with the v0.1 entity set migrated and seeded.

## Background

PRD §3.1.3 and §8.8 make isolation a data-access-layer guarantee, not a UI convention. The mechanism must work without every query author remembering to add `Where(x => x.OrganizationId == ...)`.

Entities for this card (v0.1 scope only):

| Entity | Notes |
|---|---|
| `User` | Identity user; not tenant-scoped |
| `Organization` | the tenant |
| `OrganizationMember` | (OrganizationId, UserId, Role: Owner\|Member) |
| `Project` | tenant-scoped; repo URL, default branch, pipeline path, credential ref |

`Project.Credentials` / `Secret` / `Worker` / `BuildRun` arrive in later cards — do not stub them.

Isolation design:

- `ITenantContext` (scoped) exposing the active `OrganizationId`, resolved from the authenticated user's *selected* org — never from a route/query parameter alone.
- Global query filter on every `ITenantOwned` entity, injected from `ITenantContext`.
- `SaveChanges` override: stamp and reject writes whose `OrganizationId` does not match the active tenant.
- Explicit escape hatch for system-level operations (scheduler, migrations) that is marked `#pragma`-free and requires a distinct service type, so it cannot be reached by accident.

## Acceptance Criteria

- [ ] `dotnet ef migrations add Initial` produces a migration; `dotnet ef database update` applies it to SQLite.
- [ ] The same migration applies to PostgreSQL (provider selected by connection string / config).
- [ ] Every tenant-scoped entity has a global query filter; a query with no `Where` clause returns only the active tenant's rows.
- [ ] `SaveChanges` throws when a tenant-scoped entity carries a foreign `OrganizationId`.
- [ ] `OrganizationMember` enforces a unique `(OrganizationId, UserId)` index.
- [ ] The context is registered scoped and resolves the tenant per request, not per process.

## Subtasks

- [ ] Add `Microsoft.EntityFrameworkCore`, `.Sqlite`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`.
- [ ] Define `Organization`, `OrganizationMember`, `Project` (+ enums) per PRD §6.
- [ ] Implement `ITenantOwned`, `ITenantContext`, filter injection, and the `SaveChanges` guard.
- [ ] Provider selection in `Program.cs` from configuration.
- [ ] Create the initial migration; verify against SQLite and PostgreSQL.
- [ ] Wire a design-time `IDbContextFactory` for `dotnet ef`.

## Dependencies

- I-001

## Test Cases

### TC-001: Filter returns only active tenant rows

Steps:
1. Seed Organizations A and B, one Project each.
2. Set active tenant to A; `db.Projects.ToList()`.

Expected:
- Exactly one project, belonging to A.

### TC-002: Cross-tenant write rejected

Steps:
1. Active tenant A; add a `Project` with `OrganizationId = B`.
2. `SaveChanges`.

Expected:
- Throws; nothing persisted.

### TC-003: Duplicate membership rejected

Steps:
1. Insert two `OrganizationMember` rows for the same (org, user).

Expected:
- Unique index violation on the second insert.

### TC-004: Migration parity

Steps:
1. `dotnet ef database update` against SQLite and against a PostgreSQL instance.

Expected:
- Both succeed; schema matches for the v0.1 entity set.

## Development Log

### 2026-11-12

- Card created. Not started.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Data/**`, `src/Shared/Tenancy/**`
- How to run/verify: `dotnet ef database update` then `dotnet test --filter Tenancy`
- Results: not run yet
