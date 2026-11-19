"""Tests for the command echo: the line a human copies into a shell.

The two branches matter separately - the same argv has to survive a POSIX shell on
macOS/Linux and cmd.exe on Windows - so ``quote`` takes the platform as an argument
instead of reading ``os.name`` and being untestable off Windows.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from zt.console import quote, table

FILTER = "Category!=Milestone&Category!=Postgres"
INTERACTIVE = ("dotnet", "test", "-c", "Release", "--filter", FILTER)


class PosixQuoteTests(unittest.TestCase):
    def test_a_metacharacter_argument_is_single_quoted(self) -> None:
        self.assertEqual(
            quote(INTERACTIVE, windows=False),
            "dotnet test -c Release --filter 'Category!=Milestone&Category!=Postgres'",
        )

    def test_plain_arguments_are_left_alone(self) -> None:
        self.assertEqual(quote(("dotnet", "build", "-c", "Release"), windows=False), "dotnet build -c Release")


class WindowsQuoteTests(unittest.TestCase):
    def test_a_metacharacter_argument_is_double_quoted(self) -> None:
        # list2cmdline alone would leave this bare: `&` then runs the tail as a command.
        self.assertEqual(
            quote(INTERACTIVE, windows=True),
            'dotnet test -c Release --filter "Category!=Milestone&Category!=Postgres"',
        )

    def test_spaces_and_empty_arguments_are_quoted(self) -> None:
        self.assertEqual(quote(("a b",), windows=True), '"a b"')
        self.assertEqual(quote(("",), windows=True), '""')

    def test_plain_arguments_are_left_alone(self) -> None:
        self.assertEqual(quote(("dotnet", "publish", "-r", "osx-arm64"), windows=True), "dotnet publish -r osx-arm64")


class TableTests(unittest.TestCase):
    def test_columns_are_padded_and_can_be_right_aligned(self) -> None:
        rows = table([("a", "1"), ("bbb", "22")], aligns=("l", "r"))
        self.assertEqual(rows, ["a     1", "bbb  22"])
        self.assertEqual(len({len(row) for row in rows}), 1, "columns line up")

    def test_an_empty_table_is_no_lines(self) -> None:
        self.assertEqual(table([]), [])


if __name__ == "__main__":
    unittest.main()
