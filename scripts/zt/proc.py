"""Running external commands: echo them, stream them, and fail loudly."""

from __future__ import annotations

import subprocess
from dataclasses import dataclass
from pathlib import Path

from .console import Console
from .errors import ToolError


@dataclass(frozen=True)
class Command:
    """One external command, kept as data so it can be printed without running."""

    argv: tuple[str, ...]
    cwd: Path | None = None

    @classmethod
    def of(cls, *argv: str, cwd: Path | None = None) -> "Command":
        return cls(tuple(str(part) for part in argv), cwd)

    def __str__(self) -> str:
        from .console import quote

        return quote(self.argv)


class CommandRunner:
    """Runs commands with inherited stdio, so test output and progress stay live.

    Output is deliberately *not* captured: the suites print their own failures,
    and a captured buffer would either have to be replayed at the end or lost on
    a crash. What the runner adds is the echo (the log then shows what ran) and
    the failure sentence (which suite/job went red).
    """

    def __init__(self, console: Console) -> None:
        self._console = console

    def run(self, command: Command) -> int:
        self._console.command(command.argv)
        try:
            completed = subprocess.run(
                list(command.argv),
                cwd=str(command.cwd) if command.cwd else None,
                check=False,
            )
        except FileNotFoundError as exc:
            raise ToolError(
                f"cannot run {command.argv[0]!r}: not on PATH",
                detail="Install it, or add its directory to PATH, then re-run.",
            ) from exc
        except KeyboardInterrupt:
            self._console.error_line("")
            raise
        return completed.returncode