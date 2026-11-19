"""Interactive questions, with the parsing kept pure so it can be tested.

The menu is numbered rather than arrow-key driven on purpose: it works in every
terminal the repository is used from (macOS Terminal, Windows Terminal under
Git Bash, a CI shell that happens to be a TTY) with no dependency and no
``termios``/``msvcrt`` split. Answer formats are documented in the prompt itself:

``1,3`` / ``1 3`` / ``1-3`` / ``all`` / ``none`` / empty for the default.
"""

from __future__ import annotations

import sys
from dataclasses import dataclass
from typing import Sequence, TextIO

from .console import Console
from .errors import Cancelled, UsageError

_TRUE = {"y", "yes", "true", "1", "on", "是", "好"}
_FALSE = {"n", "no", "false", "0", "off", "否", "不"}


@dataclass(frozen=True)
class Choice:
    """One menu row. ``value`` is what callers get back; ``detail`` is a hint column."""

    value: str
    label: str
    detail: str = ""


class SelectionError(ValueError):
    """The typed answer could not be understood. Recoverable: the prompt asks again."""


def parse_indexes(text: str, count: int, *, default: Sequence[int] = ()) -> tuple[int, ...]:
    """Parse a multi-select answer into 1-based indexes, in menu order.

    ``text`` may be empty (the default is used), ``all``/``*`` (everything),
    ``none`` (nothing), or a list of numbers and ``a-b`` ranges separated by
    commas or spaces.
    """
    if count < 1:
        raise ValueError("a menu needs at least one row")

    cleaned = text.strip().lower()
    if not cleaned:
        indexes = tuple(default)
    elif cleaned in ("all", "*", "全部"):
        indexes = tuple(range(1, count + 1))
    elif cleaned in ("none", "无"):
        indexes = ()
    else:
        picked: list[int] = []
        for token in cleaned.replace(",", " ").split():
            picked.extend(_parse_token(token, count))
        indexes = tuple(picked)

    for index in indexes:
        if not 1 <= index <= count:
            raise SelectionError(f"{index} is not between 1 and {count}")
    return tuple(sorted(dict.fromkeys(indexes)))


def _parse_token(token: str, count: int) -> list[int]:
    if "-" in token[1:]:  # a leading minus is not a range
        start_text, _, end_text = token.partition("-")
        if not start_text.isdigit() or not end_text.isdigit():
            raise SelectionError(f"{token!r} is not a number or a range like 1-3")
        start, end = int(start_text), int(end_text)
        if start > end:
            raise SelectionError(f"{token!r} counts down; write {end}-{start}")
        return list(range(start, end + 1))
    if not token.isdigit():
        raise SelectionError(f"{token!r} is not a number")
    number = int(token)
    if not 1 <= number <= count:
        raise SelectionError(f"{number} is not between 1 and {count}")
    return [number]


def default_label(default: Sequence[int], count: int) -> str:
    """How a default selection reads in a prompt: ``all``, ``none``, or ``1,3``."""
    if not default:
        return "none"
    if len(set(default)) == count:
        return "all"
    return ",".join(str(index) for index in default)


def parse_yes_no(text: str, *, default: bool) -> bool:
    cleaned = text.strip().lower()
    if not cleaned:
        return default
    if cleaned in _TRUE:
        return True
    if cleaned in _FALSE:
        return False
    raise SelectionError("answer y or n")


class TerminalPrompt:
    """Asks questions on a terminal. Refuses to block when there is no terminal."""

    def __init__(
        self,
        console: Console,
        *,
        stdin: TextIO | None = None,
        interactive: bool | None = None,
    ) -> None:
        self._console = console
        self._stdin = stdin if stdin is not None else sys.stdin
        self._interactive = interactive

    @property
    def is_interactive(self) -> bool:
        if self._interactive is not None:
            return self._interactive
        stream = self._stdin
        return bool(hasattr(stream, "isatty") and stream.isatty())

    def require_interactive(self) -> None:
        if not self.is_interactive:
            raise UsageError(
                "the interactive flow needs a terminal",
                detail=(
                    "Pass the options instead, e.g. "
                    "`scripts/publish.py osx-arm64 --self-contained --aot` "
                    "(see --help)."
                ),
            )

    def note(self, text: str) -> None:
        """A remark the question depends on (framework-dependent rules, for instance)."""
        self._console.note(text)

    # -- question forms -----------------------------------------------------

    def choose_many(
        self,
        question: str,
        choices: Sequence[Choice],
        *,
        default: Sequence[int],
    ) -> tuple[str, ...]:
        self.require_interactive()
        self._write_menu(question, choices, note='comma/space separated, e.g. 1,3 or 1-2; "all"/"none"')
        indexes = self._ask_many(choices, default)
        self._console.line(f"  -> {', '.join(choices[index - 1].label for index in indexes) or '(none)'}")
        return tuple(choices[index - 1].value for index in indexes)

    def choose_one(self, question: str, choices: Sequence[Choice], *, default: int) -> str:
        self.require_interactive()
        self._write_menu(question, choices, note="one number")
        while True:
            answer = self._read(f"Select [{default}]: ")
            try:
                picked = parse_indexes(answer, len(choices), default=(default,))
            except SelectionError as exc:
                self._console.warn(str(exc))
                continue
            if len(picked) != 1:
                self._console.warn("pick exactly one")
                continue
            chosen = choices[picked[0] - 1]
            self._console.line(f"  -> {chosen.label}")
            return chosen.value

    def confirm(self, question: str, *, default: bool) -> bool:
        self.require_interactive()
        suffix = "Y/n" if default else "y/N"
        while True:
            answer = self._read(f"{question} [{suffix}]: ")
            try:
                chosen = parse_yes_no(answer, default=default)
            except SelectionError as exc:
                self._console.warn(str(exc))
                continue
            self._console.line(f"  -> {'yes' if chosen else 'no'}")
            return chosen

    # -- internals ----------------------------------------------------------

    def _ask_many(self, choices: Sequence[Choice], default: Sequence[int]) -> tuple[int, ...]:
        hint = default_label(default, len(choices))
        while True:
            answer = self._read(f"Select [{hint}]: ")
            try:
                return parse_indexes(answer, len(choices), default=default)
            except SelectionError as exc:
                self._console.warn(str(exc))

    def _write_menu(self, question: str, choices: Sequence[Choice], *, note: str) -> None:
        self._console.blank()
        self._console.step(question)
        self._console.rows(
            [(f"{index}) {choice.label}", choice.detail) for index, choice in enumerate(choices, start=1)]
        )
        self._console.note(note)

    def _read(self, prompt: str) -> str:
        self._console.out.write(prompt)
        self._console.out.flush()
        try:
            line = self._stdin.readline()
        except KeyboardInterrupt:
            self._console.error_line("")
            raise Cancelled() from None
        if not line:
            self._console.error_line("")
            raise Cancelled("no answer (end of input)")
        return line.rstrip("\r\n")
