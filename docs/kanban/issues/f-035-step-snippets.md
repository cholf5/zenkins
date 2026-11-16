---
id: F-035
title: Step snippets — reusable, insertable step definitions shared across an organization
type: feature
priority: P3
size: M
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

An organization can keep named step snippets (a script step, an env set, a small Lua
helper block) and insert one into a project's definition, so the tenth project that
needs "publish to the internal registry" does not copy the same 20 lines.

## Background

A Freestyle step's script is a text field on one project; an inline pipeline's script
is a text body on one project (F-022); a repository pipeline cannot share code at all
(the Lua sandbox has no `require`, and the definition phase is one script). Jenkins
solves the same need with Shared Libraries, which are a plugin-scale mechanism. The
cheap, honest 80% is a text library that is **frozen at save time**: no runtime
dependency, no new execution surface, no version skew between a snippet and a run.

## Acceptance Criteria

- [ ] An organization can create snippets: name, kind (`script-step` for Freestyle,
      `lua-fragment` for inline/repository pipelines), body, description; Owner or
      Member edit per the repo's existing rules.
- [ ] Inserting a snippet into a Freestyle steps list or a script body copies its text
      at that moment; the snippet is not referenced at run time, so editing a snippet
      never changes a queued run, a frozen spec or a saved project silently.
- [ ] The UI shows what a snippet looks like before insertion and offers a preview of
      the resulting step (name, env, artifacts) for a script-step snippet.
- [ ] An inserted snippet's secret references are captured by the same scanner as
      hand-written text (no bypass of the D-006 metadata).
- [ ] Deleting a snippet never touches projects that used it (no dangling references,
      because there are none) — the page says so explicitly.
- [ ] A snippet body is bounded like the target it feeds (script step ≤ 32 KiB, Lua
      fragment ≤ the inline cap) and is validated with the same rules.
- [ ] Repository pipelines get an "insert snippet" affordance that copies text into
      the clipboard/editor — the repository file stays the source of truth, and the
      product never rewrites a user's repository.

## Subtasks

- [ ] `StepSnippet` entity (org, name, kind, body, description, attribution) + unique
      (org, name) index.
- [ ] Endpoints for create/edit/delete with antiforgery and the usual 404/403 shapes.
- [ ] Freestyle editor: a snippet picker that appends a fully-formed step row.
- [ ] Pipelines tab (inline): a fragment picker that inserts text at the caret.
- [ ] Secret reference scanning of the inserted text at save time (existing
      `StepReference` scanner, no new parser).
- [ ] PRD §3.15 (new): snippets, their freeze-on-insert rule and their limits.

## Dependencies

- F-020 (script steps, the steps editor) — in review
- F-022 (inline script editor) — for the Lua-fragment half

## Test Cases

### TC-001: An inserted snippet is copied, not referenced

Steps:
1. Create a `script-step` snippet; insert it into a Freestyle project; save.
2. Edit the snippet's body; open the project and trigger a run.

Expected:
- The project's spec still holds the inserted text; the new run executes it.
- The snippet page states that projects are not updated.

### TC-002: Secret references survive the insertion

Steps:
1. A snippet body containing `${secret:DEPLOY_TOKEN}`; insert and save.

Expected:
- The frozen spec's `secretReferences` includes `DEPLOY_TOKEN`, exactly as if it
  had been typed.

### TC-003: Deleting a snippet breaks nothing

Steps:
1. Insert a snippet, save the project, delete the snippet, trigger a run.

Expected:
- The run builds normally; no page errors; the project shows the text it froze.

## Notes and Decisions

- Rejected: referencing snippets at run time (a "library" resolved by the worker).
  It would make a run depend on mutable organization state and turn a snippet edit
  into a behaviour change for every project.
- Rejected: a Lua `require`/module loader. It changes the sandbox contract
  (`docs/lua-runtime.md` §3) for a sharing feature that text insertion already covers.
