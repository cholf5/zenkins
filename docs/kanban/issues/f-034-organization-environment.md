---
id: F-034
title: Organization environment variables — one shared env layer for every project
type: feature
priority: P2
size: S
status: todo
created: 2026-11-16
updated: 2026-11-16
blocked: none
---

## Goal

An organization defines plain environment variables once and every project's steps
receive them, so a registry host, a tool path or a proxy setting is not copied into
every project.

## Background

The environment layers today are: server-provided `ZENKINS_*` metadata, build
parameters (F-021), pipeline-level `env`, step-level `env`, plus secrets resolved by
reference. There is no organization layer, so shared settings are duplicated per
project or smuggled into a secret (wrong: secrets are Owner-only and write-only, and
these values are not sensitive).

Jenkins' equivalent is a global environment property; the interesting part here is
layering and snapshotting, not the storage.

## Acceptance Criteria

- [ ] An organization can define variables (name/value) with the same name rules as
      secret names; Owner-only edit, Member-readable, values never treated as
      secrets (no masking, no write-only form).
- [ ] The documented layering is enforced and tested (lowest to highest):
      `ZENKINS_*` metadata → organization vars → parameters → pipeline env → step env.
- [ ] `ZENKINS_*` and other reserved prefixes cannot be overridden by an organization
      variable; the save is refused with a field error naming the collision.
- [ ] The resolved organization layer is **snapshotted onto the run** at trigger time:
      editing an organization variable never changes a queued or running run.
- [ ] The worker injects the snapshot exactly like parameters (same claim field, same
      precedence), and `${env:NAME}` in a step resolves against it.
- [ ] Removing a variable does not break history: old runs keep their snapshot, and
      the run page can show the environment it ran with (values included — they are
      configuration, not secrets).
- [ ] A cap on count and value length (e.g. 100 vars, 4 KiB each) with a clear error.

## Subtasks

- [ ] `OrganizationEnvironment` entity (org, name, value, attribution) + migration and
      a unique (org, name) index.
- [ ] Merge function in the run-create path that builds the frozen environment
      snapshot, plus the reserved-name check.
- [ ] Claim payload: the snapshot travels with the run (extend the existing
      server-provided environment block rather than adding a second one).
- [ ] UI: an Environment card under organization settings (list, add, edit, delete),
      Owner-only mutations, read-only for Members.
- [ ] PRD §3.11.3 / §3.1.1: record the layer order and the snapshot rule.

## Dependencies

- F-021 (the parameter layer and the claim's environment block) — in review

## Test Cases

### TC-001: A shared variable reaches every project's steps

Steps:
1. Define `REGISTRY_HOST=registry.internal` at organization level.
2. Run two different projects that echo `${env:REGISTRY_HOST}`.

Expected:
- Both logs show `registry.internal`; neither project defines the variable itself.

### TC-002: Precedence is the documented one

Steps:
1. Organization `TARGET=org`, project env `TARGET=project`, step env `TARGET=step`;
   trigger with parameter `TARGET=param` for a second project.

Expected:
- The step sees `step`; a project without a step override sees `project`; a project
  with only a parameter sees `param`; a project with nothing sees `org`.

### TC-003: The snapshot is frozen

Steps:
1. Trigger a run (no worker), change the organization variable, then let it execute.

Expected:
- The queued run's steps see the old value and the run page shows the old snapshot.

## Notes and Decisions

- Values are plain configuration: they appear in the run's environment view and in
  logs if a step echoes them. Anything sensitive belongs in Secrets (F-012), and the
  UI says so next to the form.
