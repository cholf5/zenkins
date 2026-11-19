"""Tests for the interactive publish questions, answered from a script."""

from __future__ import annotations

import sys
import unittest
from io import StringIO
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from zt.console import Console
from zt.prompt import TerminalPrompt
from zt.publish_wizard import ask_options
from zt.rids import host_rid_or_none


def _prompt(answers: tuple[str, ...]) -> TerminalPrompt:
    return TerminalPrompt(
        Console(out=StringIO(), err=StringIO()),
        stdin=StringIO("\n".join(answers) + "\n"),
        interactive=True,
    )


class WizardTests(unittest.TestCase):
    def test_the_release_bundle_is_the_default_answer(self) -> None:
        prompt = _prompt(["", "", "", ""])
        options = ask_options(prompt)
        self.assertEqual(options.rids, ("win-x64", "linux-x64", "osx-arm64"))
        self.assertTrue(options.self_contained)
        self.assertTrue(options.single_file)
        self.assertFalse(options.worker_aot)

    def test_picking_platforms_and_arches_expands_to_rids(self) -> None:
        # preset 2 (pick), platforms 1,2 (Windows, Linux), arch 1 (x64), then no AOT, no single-file
        prompt = _prompt(["2", "1,2", "1", "y", "n", "n"])
        options = ask_options(prompt)
        self.assertEqual(options.rids, ("win-x64", "linux-x64"))
        self.assertTrue(options.self_contained)
        self.assertFalse(options.single_file)
        self.assertFalse(options.worker_aot)

    def test_aot_and_single_file_are_not_asked_when_the_publish_is_framework_dependent(self) -> None:
        # preset 2, platform 3 (macOS), arch asks for the host's, then "no" self-contained:
        # the remaining answers are the two it would have asked - they must go unread.
        prompt = _prompt(["2", "3", "", "n", "unused", "unused"])
        options = ask_options(prompt)
        self.assertFalse(options.self_contained)
        self.assertFalse(options.worker_aot)
        self.assertFalse(options.single_file)

    def test_this_machine_only_publishes_the_host_rid(self) -> None:
        host = host_rid_or_none()
        if host is None:
            self.skipTest("this machine's RID is not in the matrix")
        options = ask_options(_prompt(["3", "y", "n", "y"]))
        self.assertEqual(options.rids, (host,))


if __name__ == "__main__":
    unittest.main()
