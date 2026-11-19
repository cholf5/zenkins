"""Tests for the interactive prompts: the parsing, and the answering loop.

The parsing is pure and gets the bulk of the cases; the loop is driven from a
:class:`io.StringIO` so a question can be answered in a test without a terminal.
"""

from __future__ import annotations

import sys
import unittest
from io import StringIO
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from zt.console import Console
from zt.errors import Cancelled, UsageError
from zt.prompt import (
    Choice,
    SelectionError,
    TerminalPrompt,
    default_label,
    parse_indexes,
    parse_yes_no,
)


class ParseIndexesTests(unittest.TestCase):
    def test_empty_means_the_default(self) -> None:
        self.assertEqual(parse_indexes("  ", 4, default=(2,)), (2,))

    def test_all_and_none(self) -> None:
        self.assertEqual(parse_indexes("all", 3, default=(1,)), (1, 2, 3))
        self.assertEqual(parse_indexes("none", 3, default=(1, 2)), ())

    def test_numbers_ranges_and_commas(self) -> None:
        self.assertEqual(parse_indexes("1,3", 4), (1, 3))
        self.assertEqual(parse_indexes("1 3", 4), (1, 3))
        self.assertEqual(parse_indexes("1-3", 4), (1, 2, 3))
        self.assertEqual(parse_indexes("3,1,1", 4), (1, 3))

    def test_out_of_range_is_refused(self) -> None:
        with self.assertRaises(SelectionError):
            parse_indexes("5", 4)
        with self.assertRaises(SelectionError):
            parse_indexes("0", 4)
        with self.assertRaises(SelectionError):
            parse_indexes("1-9", 4)

    def test_garbage_is_refused(self) -> None:
        with self.assertRaises(SelectionError):
            parse_indexes("first", 4)
        with self.assertRaises(SelectionError):
            parse_indexes("3-1", 4)

    def test_an_empty_menu_is_a_programming_error(self) -> None:
        with self.assertRaises(ValueError):
            parse_indexes("1", 0)


class DefaultLabelTests(unittest.TestCase):
    def test_reads_whole_none_and_a_list(self) -> None:
        self.assertEqual(default_label((1, 2, 3), 3), "all")
        self.assertEqual(default_label((), 3), "none")
        self.assertEqual(default_label((1, 3), 3), "1,3")


class ParseYesNoTests(unittest.TestCase):
    def test_empty_means_the_default(self) -> None:
        self.assertTrue(parse_yes_no("", default=True))
        self.assertFalse(parse_yes_no("", default=False))

    def test_accepts_the_usual_spellings(self) -> None:
        for answer in ("y", "Y", "yes", "true", "1", "是"):
            self.assertTrue(parse_yes_no(answer, default=False), answer)
        for answer in ("n", "no", "false", "0", "否"):
            self.assertFalse(parse_yes_no(answer, default=True), answer)

    def test_garbage_asks_again(self) -> None:
        with self.assertRaises(SelectionError):
            parse_yes_no("maybe", default=True)


class PromptTests(unittest.TestCase):
    def _prompt(self, answers: str, *, interactive: bool = True) -> tuple[TerminalPrompt, StringIO, StringIO]:
        out, err = StringIO(), StringIO()
        console = Console(out=out, err=err)
        prompt = TerminalPrompt(console, stdin=StringIO(answers), interactive=interactive)
        return prompt, out, err

    def test_a_multi_select_returns_values_not_numbers(self) -> None:
        prompt, out, _ = self._prompt("1,3\n")
        choices = [Choice("Windows", "Windows"), Choice("Linux", "Linux"), Choice("macOS", "macOS")]
        self.assertEqual(prompt.choose_many("Which platform(s)?", choices, default=(1,)), ("Windows", "macOS"))
        self.assertIn("1) Windows", out.getvalue())
        self.assertIn("Select [1]:", out.getvalue())  # the default is shown, not implied

    def test_a_bad_answer_is_asked_again(self) -> None:
        prompt, _, err = self._prompt("nope\n2\n")
        choices = [Choice("a", "A"), Choice("b", "B")]
        self.assertEqual(prompt.choose_one("Pick", choices, default=1), "b")
        self.assertIn("warning:", err.getvalue())

    def test_confirm_reads_the_answer(self) -> None:
        prompt, _, _ = self._prompt("n\n")
        self.assertFalse(prompt.confirm("Run these publishes now?", default=True))

    def test_end_of_input_is_a_cancellation(self) -> None:
        prompt, _, _ = self._prompt("")
        with self.assertRaises(Cancelled):
            prompt.confirm("Run?", default=True)

    def test_a_pipe_refuses_to_be_asked(self) -> None:
        prompt, _, _ = self._prompt("", interactive=False)
        self.assertFalse(prompt.is_interactive)
        with self.assertRaises(UsageError) as caught:
            prompt.confirm("Run?", default=True)
        self.assertIn("scripts/publish.py", caught.exception.detail)


if __name__ == "__main__":
    unittest.main()
