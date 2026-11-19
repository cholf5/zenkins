#!/usr/bin/env python3
"""The acceptance gate of docs/development.md §2, in one command.

    scripts/verify.py             # build, fast suite, then one run per harness
    scripts/verify.py --list      # the commands, in order, without running them
    scripts/verify.py --dry-run   # the header a real run prints, then those commands

It runs §2's commands in §2's order and stops at the first red one, naming the
suite that went red; nothing after a red suite runs, so the fix cycle always starts
where the failure is. §2 stays the written gate (prerequisites, what each harness
needs, the counts per suite) - the filters in that table and in this script are one
change (`scripts/tests/test_gate.py` compares them).

Prerequisites are §1's, per suite: PublishAot needs the machine's native toolchain
(macOS: Xcode CLT; Windows: VS C++ workload; Linux: clang + zlib dev headers),
GitSshd needs the OpenSSH tools, and Postgres needs a disposable database named by
ZENKINS_TEST_POSTGRES. Where those are absent the suite's facts skip with the reason
recorded - that is the suites' designed behaviour, not a silent omission - so the
gate stays green on a machine without them.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from zt.console import Console
from zt.dotnet import Dotnet
from zt.errors import ToolError
from zt.gate import HARNESS_CATEGORIES, Gate, suites
from zt.proc import CommandRunner
from zt.repo import Repo


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="verify.py",
        description="Run the repository's acceptance gate (docs/development.md §2).",
    )
    parser.add_argument("--list", action="store_true", help="print the commands in order and exit")
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="print what a real run prints before its first command, then exit",
    )
    parser.add_argument("--dotnet", default="dotnet", help="the dotnet executable to use (default: dotnet)")
    return parser


def main(argv: list[str] | None = None) -> int:
    console = Console()
    args = build_parser().parse_args(argv)
    try:
        return _run(console, args)
    except KeyboardInterrupt:
        console.error_line("interrupted")
        return 130
    except ToolError as exc:
        console.error(str(exc), detail=exc.detail)
        return exc.exit_code


def _run(console: Console, args: argparse.Namespace) -> int:
    repo = Repo.discover()
    dotnet = Dotnet(repo, executable=args.dotnet)
    plan = suites(dotnet)
    gate = Gate(console, CommandRunner(console), plan)

    if args.list or args.dry_run:
        if args.dry_run:
            _header(console)
        console.heading("verify: plan")
        for suite in plan:
            console.step(suite.name)
            if suite.detail:
                console.note(suite.detail)
            console.command(suite.command.argv)
        console.blank()
        console.note(
            f"{len(plan) - len(HARNESS_CATEGORIES)} build and fast-suite steps, then "
            f"{len(HARNESS_CATEGORIES)} acceptance harnesses; nothing was run"
        )
        return 0

    _header(console)
    result = gate.run()
    console.blank()
    console.success(
        f"verify: gate green - build, fast suite and {len(HARNESS_CATEGORIES)} harnesses "
        f"in {result.seconds:.0f}s."
    )
    return 0


def _header(console: Console) -> None:
    """What a real run prints before its first command (``--dry-run`` shows it too)."""
    console.heading("verify: gate")
    console.note(
        "build -c Release, then the fast suite, then one filtered run per acceptance harness; "
        "the first red suite stops the gate and nothing after it runs."
    )


if __name__ == "__main__":
    sys.exit(main())
