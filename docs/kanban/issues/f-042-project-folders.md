---
id: F-042
title: Project folders — nested grouping per organization, with unique names inside a folder
type: feature
priority: P3
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
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

- [ ] A folder has a name, an optional parent folder (depth capped, e.g. 5 levels) and a
      description; names are unique among siblings, case-insensitively.
- [ ] Project names become unique within their folder (root = the org's top level), with
      a migration that keeps existing names valid and cannot collide.
- [ ] The projects page lists subfolders then projects with a breadcrumb; a folder page
      shows its projects' last-run status, and counts roll up to the parent.
- [ ] Project URLs keep working: `/projects/{id}` is unchanged (id-based), and the
      folder only affects listing and the create form's default parent.
- [ ] Moving a project into another folder refuses a name collision instead of renaming
      silently; a folder with non-empty contents cannot be deleted (explicit delete or
      move first), and a deleted empty folder deletes nothing else.
- [ ] Search/filter on the projects page spans the current folder subtree, with a
      "everywhere" option.
- [ ] Folders are tenant-owned: a foreign folder id is a bare 404, and Q-006's inventory
      gains the folder routes.

## Subtasks

- [ ] `Folder` entity (org, parent, name, description) with a sibling-unique index and a
      depth check; `Project.FolderId` nullable.
- [ ] Migration for the unique index change: drop (org, name) uniqueness, add
      (org, folder, name) uniqueness (a null folder compares as the root — decide the
      provider-safe encoding, since SQLite and PostgreSQL treat nulls differently in
      unique indexes).
- [ ] UI: folder list/page/breadcrumb, create/rename/move/delete forms, the create-project
      parent selector.
- [ ] Q-006 inventory rows for the new pages and forms.
- [ ] PRD §3.2 and the UI section: record folders and the naming rule.

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

## Notes and Decisions

- Rejected: per-folder permissions. It would create a second authorization model beside
  the organization's Owner/Member and contradicts the product's "multi-tenancy is the
  boundary" stance.
- Null-folder uniqueness is where this card will actually be hard: SQLite treats NULLs
  as distinct in a unique index, so the root needs a sentinel or a computed column.
  Record the choice in the development log rather than discovering it in production.
