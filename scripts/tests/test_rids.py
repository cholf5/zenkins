"""Tests for the runtime-identifier table and the host RID mapping.

Run: python -m unittest discover -s scripts/tests
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from zt.errors import UsageError
from zt import rids


class ParseRidsTests(unittest.TestCase):
    def test_accepts_a_comma_separated_list(self) -> None:
        self.assertEqual(rids.parse_rids(["win-x64,linux-x64"]), ("win-x64", "linux-x64"))

    def test_is_case_insensitive_and_deduplicates(self) -> None:
        self.assertEqual(rids.parse_rids(["OSX-ARM64", "osx-arm64"]), ("osx-arm64",))

    def test_accepts_a_platform_name_only_when_it_has_one_rid(self) -> None:
        with self.assertRaises(UsageError):
            rids.parse_rids(["windows"])

    def test_refuses_an_unknown_rid_and_lists_the_known_ones(self) -> None:
        with self.assertRaises(UsageError) as caught:
            rids.parse_rids(["plan9-mips"])
        self.assertIn("win-x64", caught.exception.detail)

    def test_refuses_an_empty_selection(self) -> None:
        with self.assertRaises(UsageError):
            rids.parse_rids([])


class ExpansionTests(unittest.TestCase):
    def test_platform_and_arch_expand_to_existing_rids_only(self) -> None:
        self.assertEqual(
            rids.rids_for(["Windows", "macOS"], ["arm64"]),
            ("win-arm64", "osx-arm64"),
        )

    def test_unknown_platform_contributes_nothing(self) -> None:
        self.assertEqual(rids.rids_for(["Solaris"], ["x64"]), ())

    def test_default_bundle_is_in_the_known_table(self) -> None:
        for rid in rids.DEFAULT_RIDS:
            self.assertIn(rid, rids.KNOWN_RIDS)

    def test_every_rid_is_its_platform_prefix_plus_a_known_arch(self) -> None:
        # The prefix is the contract with .NET (win-/linux-/osx-), so assert it against a
        # local table rather than reading the answer back out of the module under test -
        # a rid filed under the wrong platform is exactly the bug this has to catch.
        prefixes = {"Windows": "win", "Linux": "linux", "macOS": "osx"}
        seen: list[str] = []
        for platform_name, rids_for_platform in rids.RIDS_BY_PLATFORM.items():
            self.assertTrue(rids_for_platform, platform_name)
            for rid in rids_for_platform:
                self.assertTrue(rid.startswith(prefixes[platform_name] + "-"), rid)
                self.assertIn(rids.arch_of(rid), rids.ARCHES, rid)
                self.assertEqual(rids.arch_of(rid), rid.split("-", 1)[1], rid)
                seen.append(rid)
        self.assertEqual(tuple(seen), rids.KNOWN_RIDS)
        self.assertEqual(len(set(seen)), len(seen), "a RID is listed twice")

    def test_the_supported_platform_names_are_exactly_the_grouped_ones(self) -> None:
        self.assertEqual(tuple(rids.RIDS_BY_PLATFORM), rids.PLATFORMS)


class HostRidTests(unittest.TestCase):
    def test_maps_the_three_supported_platforms(self) -> None:
        self.assertEqual(rids.host_rid("darwin", "arm64"), "osx-arm64")
        self.assertEqual(rids.host_rid("win32", "AMD64"), "win-x64")
        self.assertEqual(rids.host_rid("linux", "x86_64"), "linux-x64")
        self.assertEqual(rids.host_rid("linux", "aarch64"), "linux-arm64")

    def test_refuses_an_unsupported_platform(self) -> None:
        with self.assertRaises(UsageError):
            rids.host_rid("freebsd", "x86_64")

    def test_refuses_an_unsupported_architecture(self) -> None:
        with self.assertRaises(UsageError):
            rids.host_rid("linux", "riscv64")

    def test_this_machines_rid_is_in_the_matrix(self) -> None:
        if sys.platform.startswith(("darwin", "linux", "win")):
            self.assertIn(rids.host_rid(), rids.KNOWN_RIDS)


class AotCrossCompileTests(unittest.TestCase):
    def test_the_host_rid_may_build_aot(self) -> None:
        self.assertIsNone(rids.aot_cross_compile_problem("osx-arm64", "osx-arm64"))

    def test_another_rid_names_the_host(self) -> None:
        problem = rids.aot_cross_compile_problem("win-x64", "osx-arm64")
        self.assertIsNotNone(problem)
        self.assertIn("osx-arm64", problem)
        self.assertIn("cross-compile", problem)

    def test_an_unknown_host_refuses_rather_than_guesses(self) -> None:
        self.assertIsNotNone(rids.aot_cross_compile_problem("win-x64", None))


if __name__ == "__main__":
    unittest.main()
