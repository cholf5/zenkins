"""Rendering a :class:`zt.duration.Report` the way §2's workflow reads it.

The order is the order an operator debugs in: did anything fail, how long in
total, which area owns the time, then which single test does. Shares are of
**summed test time**, never of wall clock - collections run in parallel, so the
sum can exceed the clock and a wall-clock denominator would be a lie.
"""

from __future__ import annotations

from typing import Sequence

from .duration import Report

_AREA_WIDTH = 26


def render(report: Report, *, top: int) -> list[str]:
    lines: list[str] = ["SUMMARY"]
    failed = report.failures
    lines.append(f"  tests with timings : {len(report.timings)} (failed {len(failed)})")

    if report.summary_lines:
        # Keep the runner's own wording: it may be English or the host's locale.
        for line in report.summary_lines:
            lines.append(f"  run summary        : {line}")
    else:
        lines.append("  run summary        : the runner did not print a summary block this script recognises")

    lines.append(_summed_line(report))

    if failed:
        lines.append("  failed:")
        lines.extend(f"    {timing.name}" for timing in failed)

    lines.append("")
    lines.append("SLOWEST AREAS (share of summed test time, not of wall clock - collections run in parallel)")
    lines.extend(_area_rows(report, top=top))

    lines.append("")
    lines.append(f"SLOWEST TESTS (top {top})")
    lines.extend(_test_rows(report, top=top))
    return lines


def _summed_line(report: Report) -> str:
    seconds = report.total_milliseconds / 1000
    return f"  summed test time   : {seconds:.1f} s (the denominator for the shares below)"


def _area_rows(report: Report, *, top: int) -> list[str]:
    rows: list[Sequence[str]] = [
        ("area".ljust(_AREA_WIDTH), "tests".rjust(6), "sum".rjust(9), "share".rjust(8))
    ]
    for area in report.areas[:top]:
        rows.append(
            (
                area.name.ljust(_AREA_WIDTH),
                str(area.tests).rjust(6),
                f"{area.milliseconds / 1000:.1f}s".rjust(9),
                f"{report.share_of(area):.1f}%".rjust(8),
            )
        )
    return [f"  {'  '.join(row)}".rstrip() for row in rows]


def _test_rows(report: Report, *, top: int) -> list[str]:
    return [
        f"  {timing.milliseconds / 1000:8.1f}s  {timing.status:<7} {timing.name}"
        for timing in report.slowest(top)
    ]
