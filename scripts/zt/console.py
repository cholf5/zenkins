"""Terminal output: one small writer that keeps the scripts' voice consistent.

Colour is opt-in and only when the target stream is a terminal, so piping a run
to a log (which is what CI does) yields plain text. Everything here formats
strings; nothing here decides what to print or runs a command.
"""

from __future__ import annotations

import os
import shlex
import subprocess
import sys
from typing import Iterable, Sequence, TextIO

_RESET = "\033[0m"
_BOLD = "\033[1m"
_DIM = "\033[2m"
_RED = "\033[31m"
_GREEN = "\033[32m"
_YELLOW = "\033[33m"
_CYAN = "\033[36m"


def _supports_colour(stream: TextIO) -> bool:
    if os.environ.get("NO_COLOR"):
        return False
    if os.environ.get("FORCE_COLOR"):
        return True
    return hasattr(stream, "isatty") and stream.isatty()


#: Characters ``cmd.exe`` treats as syntax, so an unquoted one splits the copy-paste.
_CMD_METACHARACTERS = "&|<>^()"


def quote(argv: Sequence[str], *, windows: bool | None = None) -> str:
    """A copy-pasteable command line for ``argv``, quoted for *this* platform's shell.

    POSIX shells need ``shlex.quote``. ``cmd.exe`` does not understand single quotes and
    treats ``&`` as "end of command", so on Windows a ``&``-joined filter has to be
    double-quoted even though it contains no space - ``subprocess.list2cmdline`` alone
    would leave it bare and the pasted line would run as several commands.

    ``%VAR%`` and ``!VAR!`` expansion cannot be escaped by quoting on Windows; the echo
    leaves them alone rather than pretending otherwise.
    """
    parts = [str(part) for part in argv]
    on_windows = os.name == "nt" if windows is None else windows
    if on_windows:
        return " ".join(_quote_windows(part) for part in parts)
    return " ".join(shlex.quote(part) for part in parts)


def _quote_windows(part: str) -> str:
    if not part:
        return '""'
    if not any(character in part for character in ' \t"' + _CMD_METACHARACTERS):
        return part
    quoted = subprocess.list2cmdline([part])
    if quoted.startswith('"'):
        return quoted
    return f'"{quoted}"' 


def table(rows: Iterable[Sequence[str]], *, aligns: Sequence[str] | None = None) -> list[str]:
    """Render rows as left-aligned columns, padded to the widest cell.

    ``aligns`` accepts ``l``/``r`` per column; missing entries mean left.
    """
    body = [[str(cell) for cell in row] for row in rows]
    if not body:
        return []
    widths = [max(len(row[i]) for row in body if i < len(row)) for i in range(max(len(r) for r in body))]
    marks = (aligns or "")
    lines = []
    for row in body:
        cells = []
        for i, cell in enumerate(row):
            width = widths[i]
            cells.append(cell.rjust(width) if i < len(marks) and marks[i] == "r" else cell.ljust(width))
        lines.append("  ".join(cells).rstrip())
    return lines


class Console:
    """Writes the tool's output. Two streams, plain when not a terminal."""

    def __init__(self, out: TextIO | None = None, err: TextIO | None = None) -> None:
        self.out = out if out is not None else sys.stdout
        self.err = err if err is not None else sys.stderr
        self._colour_out = _supports_colour(self.out)
        self._colour_err = _supports_colour(self.err)

    # -- primitives ---------------------------------------------------------

    def _paint(self, text: str, code: str, *, err: bool) -> str:
        enabled = self._colour_err if err else self._colour_out
        return f"{code}{text}{_RESET}" if enabled else text

    def line(self, text: str = "") -> None:
        print(text, file=self.out)

    def error_line(self, text: str) -> None:
        print(text, file=self.err)

    # -- the tool's vocabulary ---------------------------------------------

    def heading(self, text: str) -> None:
        print(self._paint(f"== {text} ==", _BOLD, err=False), file=self.out)

    def command(self, argv: Sequence[str]) -> None:
        print(self._paint(f"+ {quote(argv)}", _DIM, err=False), file=self.out)

    def note(self, text: str) -> None:
        for line in text.splitlines() or [""]:
            print(f"  {self._paint(line, _DIM, err=False)}", file=self.out)

    def success(self, text: str) -> None:
        print(self._paint(text, _GREEN, err=False), file=self.out)

    def warn(self, text: str) -> None:
        print(self._paint(f"warning: {text}", _YELLOW, err=True), file=self.err)

    def error(self, text: str, *, detail: str = "") -> None:
        print(self._paint(f"error: {text}", _RED, err=True), file=self.err)
        if detail:
            for line in detail.splitlines():
                print(f"  {line}", file=self.err)

    def step(self, text: str) -> None:
        print(self._paint(text, _CYAN, err=False), file=self.out)

    def rows(self, rows: Iterable[Sequence[str]], *, aligns: Sequence[str] | None = None, indent: int = 2) -> None:
        pad = " " * indent
        for line in table(rows, aligns=aligns):
            print(pad + line, file=self.out)

    def blank(self) -> None:
        print(file=self.out)
