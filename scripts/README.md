# `scripts/`

The repository's tooling: the acceptance gate, the publish matrix, and the test-log
reader. Python 3.9+, **standard library only** — nothing to install, no virtualenv,
no `requirements.txt`.

| Entry point | What it does | Docs |
|---|---|---|
| `verify.py` | The acceptance gate of `docs/development.md` §2 in one command: build, the fast suite, then one filtered run per harness, fail-fast on the first red suite. `--list` prints the commands without running them. | §2 |
| `publish.py` | The publish matrix (I-003/I-004). With no arguments on a terminal it asks what to publish; with flags it publishes exactly that. `--dry-run` prints the `dotnet publish` lines and stops. | `docs/install.md`, `docs/native-aot.md` |
| `suite_duration.py` | One `dotnet test --output detailed` log in, the slowest areas and slowest tests out. | `docs/development.md` ("Reading a run") |

```bash
scripts/publish.py                        # a terminal asks; a pipe publishes the documented bundle
scripts/publish.py osx-arm64 --aot        # one RID, with the NativeAOT Worker
scripts/publish.py --dry-run --platform Linux --arch arm64
scripts/verify.py                         # the gate
scripts/verify.py --list                  # the same commands, printed, nothing run
scripts/suite_duration.py /tmp/fast.log 10
```

## Layout

The three entry points are thin: argument parsing, printing, exit codes. Behaviour
lives in `zt/`, one concern per module, so nothing here grows into a class that
knows everything.

| Module | Concern |
|---|---|
| `zt/console.py` | terminal output (headings, notes, warnings, quoted commands, tables) |
| `zt/errors.py` | `ToolError` and its exit codes (2 = usage, 130 = cancelled) |
| `zt/proc.py` | `Command` (argv as data) and `CommandRunner` (echo, run, fail fast) |
| `zt/repo.py` | locating the repository root and its projects |
| `zt/rids.py` | the RID matrix, the host RID, the AOT cross-compile rule |
| `zt/dotnet.py` | the `dotnet` command builders and the filter strings |
| `zt/gate.py` | §2's suite table and the gate runner |
| `zt/publish_plan.py` | options → publish jobs (pure; no I/O, no prompts) |
| `zt/publish_wizard.py` | the interactive questions → options |
| `zt/prompt.py` | prompts; the answer parsing is pure and unit-tested |
| `zt/duration.py` | parsing a runner log into timings |
| `zt/duration_report.py` | rendering those timings |

## Which checkout is "the repository"

`Repo.discover()` walks up from the script's own location first, so `src/...` and
`artifacts/publish` are always the ones beside the script you ran - the checkout wins
over the working directory. `ZENKINS_REPO_ROOT` is the fallback for a tree that is not
beside the script (it is tried after the script's location and before the working
directory). `--output` follows the same rule: a relative path resolves against the
repository root, not against wherever you typed the command.

## Tests

```bash
python -m unittest discover -s scripts/tests -v
```

They need no .NET SDK and no network: the argument parsing, the matrix expansion,
the gate's order and filter, the log reader and the prompt loops are all pure
functions or driven from a string. CI runs them before the gate on all three runners,
and again in a separate path-filter-free workflow, because one test reads
`docs/development.md` §2 and `docs/native-aot.md` §8: every fast-suite filter those
documents print must be the one the gate runs, so the docs and the script are one
fact and that test is what keeps them from drifting. (That is also why the docs-only
skip in `ci.yml` cannot be the only place they run.)

## Conventions

- **No third-party packages.** A script that needs one is a script a fresh clone
  cannot run. `argparse`, `subprocess`, `re`, `dataclasses` are enough.
- **Commands are data.** Build a `Command` (argv tuple) and let the runner echo and
  run it; never shell out through a string. Nothing is quoted for a shell: the argv
  goes straight to the process, so an `&`-joined filter needs no quoting on any OS.
  The echo is quoted for *this* platform's shell (POSIX `shlex` on macOS/Linux,
  `cmd.exe` rules on Windows) because that echo is what a human pastes.
- **Nothing is guessed.** A log without timings is reported as such, an AOT publish
  for another OS is refused with the reason, and the gate stops at the first red
  suite instead of carrying on and reporting a pile.
- **Exit codes carry meaning:** `0` success; `1` a step failed, or the log carried no
  per-test timings (a summary of no data is a failure, not a zero-row table); `2` usage
  error, including a missing log file or an unknown RID; `130` cancelled (Ctrl-C, or
  answering "no" to the publish confirmation).
