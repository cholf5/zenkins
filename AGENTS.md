# AGENTS.md

Guidance for AI agents (and humans) writing to this repository.

## Privacy — no real environment details in tracked files

Everything tracked here becomes public when the project is open-sourced, and session
tooling may snapshot the whole workspace into commits as-is. Never write real personal
or infrastructure details into any tracked file — docs, kanban cards, code, tests, or
sample output. This includes:

- Device names, hostnames, SSH host aliases, and machine inventories. Use generic
  labels instead (`windows-dev`, `linux-cloud-host`). Never paste environment-inventory
  tool output (`list_machines` and friends) verbatim; paraphrase with generic labels.
- Real IP addresses. Use RFC 5737 documentation addresses (`203.0.113.10`) or omit them.
- Real usernames, absolute home paths, and process IDs. Use `~`, `/Users/<you>`, and
  omit pids.
- Token-shaped strings from real runs (worker tokens, registration codes). Use
  placeholders like `<token-id>`.

The rule applies everywhere, not just to documentation: kanban card evidence and
Verification sections, install and upgrade guides, terminal-output examples, tests,
and configs.

Enforcement is mechanical: a user-level pre-commit hook rejects commits whose staged
content matches known private patterns. Bypass it with `--no-verify` or
`ALLOW_PRIVATE_STRINGS=1` only for deliberate, user-approved exceptions.
