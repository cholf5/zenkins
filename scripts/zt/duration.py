"""Parsing one ``dotnet test --output detailed`` log into per-test timings.

The runner is Microsoft.Testing.Platform, whose per-test lines look like::

      passed Zenkins.Tests.Theme.CascadeTests.Value (417ms)
      failed Zenkins.Tests.Queue.Tests.Replay (3s 029ms)
      已通过 Zenkins.Tests.Theme.CascadeTests.Value (1s)

so a line is: a verdict word, whitespace, a display name, then a parenthesised
duration made of ``Nms`` / ``Ns`` / ``Nm`` parts. The verdict word is localised
(``已通过`` / ``失败`` / ``已跳过``), which is why the mapping below accepts both
spellings instead of assuming English.

Nothing here is guessed: a line that does not match is not a timing, and a log
with no timings is reported as such by the report layer rather than summarised
into zeros.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

PASSED = "passed"
FAILED = "failed"
SKIPPED = "skipped"

#: Verdict words, both spellings of each state the runner prints.
_VERDICTS: dict[str, str] = {
    "passed": PASSED,
    "failed": FAILED,
    "skipped": SKIPPED,
    "已通过": PASSED,
    "失败": FAILED,
    "已跳过": SKIPPED,
    "跳过": SKIPPED,
}

_LINE = re.compile(r"^[ \t]*(?P<verdict>[A-Za-z]+|已通过|已跳过|失败|跳过)[ \t]+(?P<rest>\S.*?)[ \t]*$")
_TAIL = re.compile(r" \((?P<duration>(?:[0-9]+(?:ms|s|m))(?: [0-9]+(?:ms|s|m))*)\)$")
_PART = re.compile(r"([0-9]+)(ms|s|m)")

#: The runner's own summary block; kept verbatim in the output (see the report).
_SUMMARY = re.compile(
    r"^[ \t]*(total|succeeded|failed|skipped|duration|总计|通过|失败|已跳过|跳过|持续时间):",
    re.IGNORECASE,
)

#: The third namespace segment is the test area: ``Zenkins.Tests.<Area>...``.
_NAMESPACE = "Zenkins.Tests."


@dataclass(frozen=True)
class Timing:
    milliseconds: int
    status: str
    name: str


@dataclass(frozen=True)
class Area:
    name: str
    milliseconds: int
    tests: int


@dataclass(frozen=True)
class Report:
    """Everything the log said, aggregated once and read many times."""

    timings: tuple[Timing, ...]
    summary_lines: tuple[str, ...]

    @property
    def has_timings(self) -> bool:
        return bool(self.timings)

    @property
    def total_milliseconds(self) -> int:
        return sum(timing.milliseconds for timing in self.timings)

    @property
    def failures(self) -> tuple[Timing, ...]:
        return tuple(timing for timing in self.timings if timing.status == FAILED)

    @property
    def areas(self) -> tuple[Area, ...]:
        """Areas by summed time, slowest first (ties by name, so runs are comparable)."""
        totals: dict[str, list[int]] = {}
        for timing in self.timings:
            name = area_of(timing.name)
            bucket = totals.setdefault(name, [0, 0])
            bucket[0] += timing.milliseconds
            bucket[1] += 1
        areas = [Area(name, ms, count) for name, (ms, count) in totals.items()]
        return tuple(sorted(areas, key=lambda area: (-area.milliseconds, area.name)))

    def slowest(self, limit: int) -> tuple[Timing, ...]:
        return tuple(sorted(self.timings, key=lambda timing: (-timing.milliseconds, timing.name))[:limit])

    def share_of(self, area: Area) -> float:
        total = self.total_milliseconds
        return area.milliseconds * 100.0 / total if total else 0.0


def area_of(name: str) -> str:
    """The test's area: the third namespace segment, else the first one."""
    segments = name.split(".")
    if name.startswith(_NAMESPACE) and len(segments) > 2 and segments[2]:
        return segments[2]
    return segments[0]


def decode_log(raw: bytes) -> str:
    """Decode a captured runner log whatever encoding the shell's redirect used.

    ``> log`` writes UTF-8 in bash and UTF-16LE in PowerShell, and a UTF-16 log read as
    UTF-8 becomes NUL-separated rubbish that parses to zero timings - which would be
    reported as "this log has no timings", blaming the wrong thing on the platform the
    scripts were just ported to.
    """
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    if b"\x00" in raw[:4096]:
        return raw.decode("utf-16-le", errors="replace")
    return raw.decode("utf-8-sig", errors="replace")


def parse_log(text: str) -> Report:
    """Read a runner log into timings; lines that are not timings are left alone."""
    timings: list[Timing] = []
    summary: list[str] = []
    for raw in text.splitlines():
        if _SUMMARY.match(raw):
            summary.append(raw.strip())
        timing = parse_line(raw)
        if timing is not None:
            timings.append(timing)
    return Report(timings=tuple(timings), summary_lines=tuple(summary))


def parse_line(raw: str) -> Timing | None:
    """:data:`None` when the line carries no per-test timing."""
    line = raw.rstrip()
    head = _LINE.match(line)
    if head is None:
        return None
    rest = head.group("rest")
    tail = _TAIL.search(rest)
    if tail is None:
        return None
    name = rest[: tail.start()].strip()
    if not name:
        return None
    status = status_of(head.group("verdict"))
    if status is None:
        return None
    return Timing(
        milliseconds=to_milliseconds(tail.group("duration")),
        status=status,
        name=name,
    )


def status_of(verdict: str) -> str | None:
    """Map the (possibly localised) verdict word, tolerating a capitalised spelling.

    An English word this module does not know still means the test ran, so it is
    counted as passed - the old awk line did the same; a non-Latin word that is not
    one of the known localisations is not a verdict, so the line is not a timing.
    """
    lowered = verdict.lower()
    if lowered in _VERDICTS:
        return _VERDICTS[lowered]
    if verdict.isascii() and verdict.isalpha():
        return PASSED
    return None


def to_milliseconds(duration: str) -> int:
    """``"3s 029ms"`` -> ``3029``. Minutes are summed, never converted to seconds."""
    scale = {"ms": 1, "s": 1000, "m": 60_000}
    return sum(int(value) * scale[unit] for value, unit in _PART.findall(duration))
