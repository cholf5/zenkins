#!/usr/bin/env python3
"""Summarise one ``dotnet test`` log so a single run shows where the time went (Q-023 AC-5).

    dotnet test -c Release --no-build --output detailed > /tmp/fast.log
    scripts/suite_duration.py /tmp/fast.log 10

It prints the run's own summary block, the slowest test areas with their share of
**summed test time**, the failed tests, and the slowest single tests.

Per-test timings only exist under ``--output detailed``: the runner is
Microsoft.Testing.Platform, so ``--logger "console;verbosity=detailed"`` is
rejected (exit 5). A log without timings is reported as such and exits non-zero -
a summary of no data would be worse than no summary.

A log written by PowerShell's ``>`` (UTF-16LE) is decoded as such, so "no timings" always
means the runner printed none - never that the reader mis-decoded the file.

Exit: 0 summarised, 1 the log carries no per-test timings, 2 the file is missing or a bad
argument.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from zt.console import Console
from zt.duration import decode_log, parse_log
from zt.duration_report import render
from zt.errors import UsageError

_NO_TIMINGS = """this log carries no per-test timings, so there is nothing to aggregate.

  Re-run with `--output detailed` (Microsoft.Testing.Platform). The VSTest form
  `--logger "console;verbosity=detailed"` is rejected with exit 5.

  Refusing to print zeros or an empty table: a summary of no data is worse than no summary."""


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="suite_duration.py",
        description="Summarise one dotnet test log: slowest areas and slowest tests.",
    )
    parser.add_argument("log", type=Path, help="the captured test log (needs --output detailed)")
    parser.add_argument("top", nargs="?", type=int, default=10, help="how many rows per table (default: 10)")
    return parser


def main(argv: list[str] | None = None) -> int:
    console = Console()
    args = build_parser().parse_args(argv)
    try:
        return _run(console, args)
    except UsageError as exc:
        console.error(str(exc), detail=exc.detail)
        return exc.exit_code


def _run(console: Console, args: argparse.Namespace) -> int:
    if args.top < 1:
        raise UsageError("top must be at least 1")
    if not args.log.is_file():
        raise UsageError(f"no such file: {args.log}", exit_code=2)

    report = parse_log(decode_log(args.log.read_bytes()))
    if not report.has_timings:
        console.error(_NO_TIMINGS)
        return 1

    for line in render(report, top=args.top):
        console.line(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
