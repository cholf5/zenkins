"""Tests for the gate's suite table: §2's commands, in §2's order, with §2's filter.

The last test is the anti-drift one: it reads docs/development.md and requires the
fast filter this module produces to appear there verbatim. A filter changed in one
place and not the other turns red here instead of silently weakening the gate.
"""

from __future__ import annotations

import re
import sys
import unittest
from io import StringIO
from pathlib import Path
from typing import Sequence

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from zt.console import Console
from zt.dotnet import FAST_EXCLUSIONS, Dotnet, category_filter, fast_filter
from zt.errors import ToolError
from zt.gate import HARNESS_CATEGORIES, Gate, suites
from zt.proc import Command
from zt.repo import Repo


class FakeRunner:
    """Records what would run and fails only where told to."""

    def __init__(self, fail_on: Sequence[int] = ()) -> None:
        self.commands: list[Command] = []
        self._fail_on = set(fail_on)

    def run(self, command: Command) -> int:
        self.commands.append(command)
        return 1 if len(self.commands) in self._fail_on else 0


def _suite_names() -> list[str]:
    repo = Repo.discover(Path(__file__).resolve().parents[2])
    return [suite.name for suite in suites(Dotnet(repo))]


class FilterTests(unittest.TestCase):
    def test_the_filter_excludes_every_acceptance_category_and_load(self) -> None:
        self.assertEqual(
            fast_filter(),
            "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot"
            "&Category!=GitSshd&Category!=Postgres&Category!=Load",
        )

    def test_every_harness_category_is_excluded_from_the_fast_suite(self) -> None:
        for category in HARNESS_CATEGORIES:
            self.assertIn(category, FAST_EXCLUSIONS)

    def test_a_harness_filter_names_one_category(self) -> None:
        self.assertEqual(category_filter("Milestone"), "Category=Milestone")

    def test_every_documented_copy_of_the_filter_is_the_same_string(self) -> None:
        # Every live doc under docs/ (kanban cards are history and live a level down), and
        # every line in one of them that spells a fast filter. A test that only required
        # "the string appears somewhere in the file" stays green while the copy readers
        # copy drifts, and a test that only looked at two files misses the third copy.
        repo = Repo.discover(Path(__file__).resolve().parents[2])
        expected = fast_filter()
        starts_a_filter = re.compile(r"Category\s*!=\s*Milestone")
        copies = 0
        for path in sorted((repo.root / "docs").glob("*.md")):
            for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
                if not starts_a_filter.search(line):
                    continue
                copies += 1
                self.assertIn(expected, line, f"docs/{path.name}:{number} prints a different fast filter")
        self.assertGreaterEqual(copies, 3, "the documented filter copies went missing")

    def test_the_documented_harness_filters_are_the_ones_the_gate_runs(self) -> None:
        repo = Repo.discover(Path(__file__).resolve().parents[2])
        documented = (repo.root / "docs" / "development.md").read_text(encoding="utf-8")
        for category in HARNESS_CATEGORIES:
            self.assertIn(category_filter(category), documented)


class SuiteTableTests(unittest.TestCase):
    def test_the_gate_is_build_then_fast_then_the_harnesses(self) -> None:
        self.assertEqual(
            _suite_names(),
            ["build (Release)", "Fast suite", *HARNESS_CATEGORIES],
        )

    def test_the_build_suite_carries_no_filter(self) -> None:
        repo = Repo.discover(Path(__file__).resolve().parents[2])
        build = suites(Dotnet(repo))[0]
        self.assertNotIn("--filter", build.command.argv)
        self.assertIn("-c", build.command.argv)
        self.assertIn("Release", build.command.argv)

    def test_every_test_suite_is_one_filtered_release_run(self) -> None:
        repo = Repo.discover(Path(__file__).resolve().parents[2])
        for suite in suites(Dotnet(repo))[1:]:
            self.assertIn("--filter", suite.command.argv)
            self.assertIn("Category", " ".join(suite.command.argv))


class GateRunTests(unittest.TestCase):
    def _gate(self, runner: FakeRunner) -> Gate:
        repo = Repo.discover(Path(__file__).resolve().parents[2])
        console = Console(out=StringIO(), err=StringIO())
        return Gate(console, runner, suites(Dotnet(repo)))  # type: ignore[arg-type]

    def test_a_green_gate_runs_every_suite(self) -> None:
        runner = FakeRunner()
        result = self._gate(runner).run()
        self.assertEqual(result.suites_run, len(HARNESS_CATEGORIES) + 2)
        self.assertEqual(len(runner.commands), result.suites_run)

    def test_the_first_red_suite_stops_the_gate_and_is_named(self) -> None:
        runner = FakeRunner(fail_on=(3,))
        with self.assertRaises(ToolError) as caught:
            self._gate(runner).run()
        self.assertEqual(len(runner.commands), 3)  # build, fast suite, Milestone - and nothing after it
        self.assertIn("Milestone", str(caught.exception))

    def test_an_empty_gate_is_refused(self) -> None:
        with self.assertRaises(ValueError):
            Gate(Console(out=StringIO()), FakeRunner(), [])


if __name__ == "__main__":
    unittest.main()
