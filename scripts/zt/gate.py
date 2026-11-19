"""The acceptance gate: the suite table from docs/development.md §2, in order.

This module holds no I/O. The table below *is* §2's executable form - it runs the
documented commands in the documented order and stops at the first red one - so a
change to a filter here and a change to §2's table are one change
(``scripts/tests/test_gate.py`` compares the produced filter string with the one
the document prints).
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Sequence

from .console import Console
from .dotnet import Dotnet, category_filter, fast_filter
from .errors import ToolError
from .proc import Command, CommandRunner

# §2's harnesses, in §2's order. The name is what a red run accuses.
HARNESS_CATEGORIES: tuple[str, ...] = (
    "Milestone",
    "Recovery",
    "PublishSmoke",
    "PublishAot",
    "GitSshd",
    "Postgres",
)


@dataclass(frozen=True)
class Suite:
    name: str
    command: Command
    detail: str = ""


def suites(dotnet: Dotnet) -> tuple[Suite, ...]:
    """Build the gate's commands, in the order §2 runs them."""
    return (
        Suite("build (Release)", dotnet.build()),
        Suite(
            "Fast suite",
            dotnet.test(filter=fast_filter()),
            detail="every acceptance category excluded - this is the fast loop's suite",
        ),
        *(
            Suite(category, dotnet.test(filter=category_filter(category)), detail=detail)
            for category, detail in zip(HARNESS_CATEGORIES, _HARNESS_DETAILS)
        ),
    )


_HARNESS_DETAILS: tuple[str, ...] = (
    "real Server + Worker processes, a real Git repository, a real webhook (needs git, loopback only)",
    "the same launcher plus restarts, kills and the real lease sweeper",
    "publishes self-contained artifacts for the local RID and runs the scenario against them",
    "publishes the NativeAOT Worker (needs this OS's native toolchain)",
    "a real sshd on loopback (needs the OpenSSH tools; skips with a recorded reason without them)",
    "a real PostgreSQL server named by ZENKINS_TEST_POSTGRES (skips with a recorded reason without it)",
)


@dataclass(frozen=True)
class GateResult:
    seconds: float
    suites_run: int


class Gate:
    """Runs the suite table, fail-fast, and reports the wall clock."""

    def __init__(self, console: Console, runner: CommandRunner, plan: Sequence[Suite]) -> None:
        if not plan:
            raise ValueError("an empty gate is not a gate")
        self._console = console
        self._runner = runner
        self._suites = tuple(plan)

    @property
    def suites(self) -> tuple[Suite, ...]:
        return self._suites

    def run(self) -> GateResult:
        started = time.monotonic()
        for suite in self._suites:
            self._console.heading(f"verify: {suite.name}")
            if suite.detail:
                self._console.note(suite.detail)
            self._run_one(suite)
        return GateResult(seconds=time.monotonic() - started, suites_run=len(self._suites))

    def _run_one(self, suite: Suite) -> None:
        code = self._runner.run(suite.command)
        if code == 0:
            return
        raise ToolError(
            f"verify: FAILED - the '{suite.name}' suite went red",
            detail=(
                f"Its output above names the failing test(s); nothing after it ran (exit code {code})."
            ),
        )
