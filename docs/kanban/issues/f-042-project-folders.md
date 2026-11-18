---
id: F-042
title: Project folders — nested grouping per organization, with unique names inside a folder
type: feature
priority: P3
size: M
status: done
created: 2026-11-16
updated: 2026-11-18
blocked: none
---

## Goal

Projects can be grouped into nested folders inside an organization, so a team's jobs or
a monorepo's many components are one click away instead of a flat list of fifty.

## Background

The projects page is one flat list per organization. With multibranch (F-023), one
repository already produces many branch jobs; a handful of repositories makes the list
unusable. Jenkins' folders solve this with nesting, per-folder naming and per-folder
permissions — only the first two are wanted here (per-folder permissions are the RBAC
parking lot, and organization-level isolation is already the product's model).

The constraint that needs care: project names are currently unique **per organization**
(`AddProjectUniqueName`). Introducing folders changes "unique" to "unique inside its
folder", which is a data migration and a URL change.

## Acceptance Criteria

- [x] A folder has a name, an optional parent folder (depth capped, e.g. 5 levels) and a
      description; names are unique among siblings, case-insensitively.
- [x] Project names become unique within their folder (root = the org's top level), with
      a migration that keeps existing names valid and cannot collide.
- [x] The projects page lists subfolders then projects with a breadcrumb; a folder page
      shows its projects' last-run status, and counts roll up to the parent.
- [x] Project URLs keep working: `/projects/{id}` is unchanged (id-based), and the
      folder only affects listing and the create form's default parent.
- [x] Moving a project into another folder refuses a name collision instead of renaming
      silently; a folder with non-empty contents cannot be deleted (explicit delete or
      move first), and a deleted empty folder deletes nothing else.
- [x] Search/filter on the projects page spans the current folder subtree, with a
      "everywhere" option.
- [x] Folders are tenant-owned: a foreign folder id is a bare 404, and Q-006's inventory
      gains the folder routes.

## Subtasks

- [x] `Folder` entity (org, parent, name, description) with a sibling-unique index and a
      depth check; `Project.FolderId` nullable.
- [x] Migration for the unique index change: drop (org, name) uniqueness, add
      (org, folder, name) uniqueness (a null folder compares as the root — decide the
      provider-safe encoding, since SQLite and PostgreSQL treat nulls differently in
      unique indexes).
- [x] UI: folder list/page/breadcrumb, create/rename/move/delete forms, the create-project
      parent selector.
- [x] Q-006 inventory rows for the new pages and forms.
- [x] PRD §3.2 and the UI section: record folders and the naming rule.

## Dependencies

- F-023 (branch jobs make the flat list painful; not a hard dependency)

## Test Cases

### TC-001: Same project name in two folders

Steps:
1. Create folders `team-a` and `team-b`; create `build` in each.

Expected:
- Both exist with their own ids and pages; the projects page shows them under their
  folders, not in one list.

### TC-002: Root names stay unique

Steps:
1. Create `build` at the root, then try to create `build` at the root again.

Expected:
- Refused with a field error naming the existing project (no silent suffix).

### TC-003: A non-empty folder cannot be deleted

Steps:
1. Put one project in a folder, then delete the folder.

Expected:
- Refused with "1 project inside" and no data change; after moving the project out, the
  delete succeeds.

### TC-004: Nesting is capped and cycles are impossible

Steps:
1. Nest folders to the cap, then try one more level; try to move a folder into itself.

Expected:
- Both refused with clear errors; no partial write.

## Development Log

### 2026-11-18

- **The root's null-folder encoding (the card's hard part).** Names are compared through stored key
  columns, not through collations: `Folders.ParentKey` and `Projects.FolderKey` are `Guid`s whose
  `Guid.Empty` *is* the root, and `Folders.NameKey`/`Projects.NameKey` are the invariant-lowercased
  names. One unique index per table (`(OrganizationId, ParentKey, NameKey)` on `Folders`,
  `(OrganizationId, FolderKey, NameKey)` on `Projects`) then means "unique among siblings, ignoring
  case" on both providers. The rejected alternatives: a nullable parent column in the index (both
  SQLite and PostgreSQL treat `NULL`s as distinct, so root names would never collide), and
  `UseCollation("NOCASE")` / `citext` (provider-specific, so the one migration set would mean two
  things). A model check constraint ties each key to its nullable id, and
  `ZenkinsDbContext.StampFolderKeys` is the single writer of the derived keys - the same place the
  audit timestamps are stamped - so no service, seed or future caller can store a disagreeing row.
- **The migration keeps every existing row.** `AddProjectFolders` adds the three project columns with
  the empty/default values, backfills `NameKey = lower(Name)` and leaves every project at the root
  (`FolderKey` = `Guid.Empty`) before the new index is created, then adds `Folders`. The old
  `(OrganizationId, Name)` index was strictly stronger than the new root-level constraint for
  identical names, so no two existing projects of one organization can collide; case-only twins are
  the one dataset the new rule refuses, and there the index fails the migration loudly instead of
  renaming anything. `FolderMigrationUpgradeTests` proves the upgrade on a real SQLite database:
  legacy rows inserted with the pre-card schema survive, their keys are derived correctly, and a
  second root project named `BUILD` is refused by the index.
- **Depth and cycles.** `Folders.Depth` is stored and bounded by `CK_Folders_Depth`; a create derives
  it from the parent, and a move rewrites the whole subtree's depth in the same save. The cycle check
  walks up from the target parent with a visited set (bounded, no infinite loop), so moving a folder
  into itself or a descendant is one refusal (`folder-cycle`) with no partial write.
- **A collision is never renamed around.** `MoveToFolderAsync` looks the target folder's keys up
  first so the refusal can *name* the existing project (TC-002), and the unique index remains the
  guarantee - the pre-flight read only feeds the message. The edit form ignores a posted `FolderId`
  on purpose: moving is its own route.
- **Permissions.** A folder is grouping, not a second authorization model: Members and Owners may
  create, rename, move and delete (only empty folders can be deleted, so a delete destroys no
  history). Organization isolation stays the boundary.

## Notes and Decisions

- Rejected: per-folder permissions. It would create a second authorization model beside
  the organization's Owner/Member and contradicts the product's "multi-tenancy is the
  boundary" stance.
- Null-folder uniqueness is where this card will actually be hard: SQLite treats NULLs
  as distinct in a unique index, so the root needs a sentinel or a computed column.
  Record the choice in the development log rather than discovering it in production.
  **Resolved:** the sentinel is `Guid.Empty` in the key column (see the development log); no computed
  column is used, because a stored generated column cannot be added by SQLite's `ALTER TABLE`.

## Verification

- Related files: `src/Server/Folders/` (entity rules, service, endpoints), `src/Server/Data/Entities/Folder.cs`,
  `src/Server/Data/ZenkinsDbContext.cs` (model, indexes, check constraints, key stamping),
  `src/Server/Projects/ProjectService.cs` (folder-scoped uniqueness, move), `src/Server/Components/Pages/Folders/`,
  `src/Server/Components/Shared/FolderListing.razor`, `docs/audits/q-006-surface-inventory.md`,
  `src/Server/Data/Migrations/20261118135350_AddProjectFolders.cs`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=Folders"`;
  `dotnet test -c Release --filter "Category=TenantIsolation"`;
  `Database__Provider=Postgres ... dotnet ef migrations has-pending-model-changes --project src/Server`
- Results: build 0 warnings / 0 errors; Folders 11/11; TenantIsolation 167/167; the fast suite green;
  `has-pending-model-changes` prints "No changes have been made to the model since the last migration.".
  The new snapshot and migration carry `uuid` / `timestamp with time zone` / `character varying` only
  (no SQLite type names) and no `RenameTable` operation.


## Close-out verification (2026-11-18)

Folders arrived with the constraint the card predicted: "unique" stopped meaning "unique in the organisation"
and started meaning "unique among siblings", which is a data change and not only a UI one.

- **Names, depth and shape.** A folder has a name, a description and an optional parent, depth is capped at
  five levels, sibling names are unique case-insensitively, and a folder cannot become its own ancestor.
- **Project names move from organisation-unique to folder-unique**, with a migration that leaves existing
  projects at the root where the old constraint already made them unique - so the migration cannot collide,
  and a fact states it rather than leaving it as an argument.
- **URLs did not change.** `/projects/{id}` stays id-based; folders only affect listing and the create form's
  default parent.
- **Moving a project refuses a collision** rather than renaming silently, and the list renders subfolders then
  projects with a breadcrumb while counts roll up to the parent.
- **Counts.** fast 1628 -> 1643 measured on the merged tree, Postgres stays 9, the slow categories 24,
  everything 1676. Three integration failures appeared when this branch met F-031 - a new endpoint missing
  from the Q-006 inventory, and three upgrade tests whose raw inserts predate F-031's new column - and they
  are recorded here rather than papered over: they are fixed in the follow-up commit that closes the series.
