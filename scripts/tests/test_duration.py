"""Tests for the `dotnet test --output detailed` reader.

The shapes pinned here are the ones the old shell/awk implementation handled, plus
the localised verdict words the runner prints on a non-English host.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from zt.duration import (
    FAILED,
    PASSED,
    SKIPPED,
    area_of,
    decode_log,
    parse_line,
    parse_log,
    to_milliseconds,
)
from zt.duration_report import render

SAMPLE = """\
  Determining projects to restore...
  passed Zenkins.Tests.Theme.CascadeTests.Value (417ms)
  failed Zenkins.Tests.Queue.QueueControlTests.Replay (3s 029ms)
  已通过 Zenkins.Tests.Api.ContractTests.Get (1s)
  skipped Zenkins.Tests.Postgres.LockTests.Claim (2m 1s)
  passed SomeTestWithoutNamespace (12ms)
  passed A display name with spaces (5ms)
  passed Zenkins.Tests.Theme.Not.A.Timing
  total: 6 (passed 3, failed 1, skipped 2)
  duration: 12s
"""


class ParseLineTests(unittest.TestCase):
    def test_reads_milliseconds_seconds_and_minutes(self) -> None:
        timing = parse_line("  passed Zenkins.Tests.Theme.CascadeTests.Value (417ms)")
        self.assertIsNotNone(timing)
        assert timing is not None
        self.assertEqual(timing.milliseconds, 417)
        self.assertEqual(timing.status, PASSED)
        self.assertEqual(timing.name, "Zenkins.Tests.Theme.CascadeTests.Value")

    def test_sums_a_multi_part_duration(self) -> None:
        timing = parse_line("  failed A.Test (3s 029ms)")
        assert timing is not None
        self.assertEqual(timing.milliseconds, 3029)
        self.assertEqual(timing.status, FAILED)

    def test_reads_a_minutes_part(self) -> None:
        self.assertEqual(to_milliseconds("2m 1s"), 121_000)

    def test_reads_the_localised_verdicts(self) -> None:
        passed = parse_line("  已通过 A.Test (1s)")
        skipped = parse_line("  已跳过 A.Test (1s)")
        failed = parse_line("  失败 A.Test (1s)")
        assert passed is not None and skipped is not None and failed is not None
        self.assertEqual((passed.status, skipped.status, failed.status), (PASSED, SKIPPED, FAILED))

    def test_keeps_a_display_name_with_spaces(self) -> None:
        timing = parse_line("  passed A display name with spaces (5ms)")
        assert timing is not None
        self.assertEqual(timing.name, "A display name with spaces")

    def test_ignores_a_line_without_a_trailing_duration(self) -> None:
        self.assertIsNone(parse_line("  passed Zenkins.Tests.Theme.Not.A.Timing"))
        self.assertIsNone(parse_line("  passed A.Test (12ms) extra words"))
        self.assertIsNone(parse_line("Determining projects to restore..."))

    def test_a_capitalised_verdict_is_still_a_verdict(self) -> None:
        timing = parse_line("  Failed A.Test (12ms)")
        assert timing is not None
        self.assertEqual(timing.status, FAILED)


class ReportTests(unittest.TestCase):
    def setUp(self) -> None:
        self.report = parse_log(SAMPLE)

    def test_keeps_every_timed_test_and_ignores_the_untimed_line(self) -> None:
        self.assertEqual(len(self.report.timings), 6)
        self.assertEqual(self.report.total_milliseconds, 417 + 3029 + 1000 + 121_000 + 12 + 5)

    def test_keeps_the_runners_own_summary_lines(self) -> None:
        self.assertEqual(
            self.report.summary_lines,
            ("total: 6 (passed 3, failed 1, skipped 2)", "duration: 12s"),
        )

    def test_lists_the_failures_by_name(self) -> None:
        self.assertEqual([timing.name for timing in self.report.failures], ["Zenkins.Tests.Queue.QueueControlTests.Replay"])

    def test_groups_areas_by_the_third_namespace_segment(self) -> None:
        self.assertEqual(area_of("Zenkins.Tests.Theme.CascadeTests.Value"), "Theme")
        self.assertEqual(area_of("SomeTestWithoutNamespace"), "SomeTestWithoutNamespace")
        self.assertEqual(area_of("Other.Tests.Thing"), "Other")

    def test_orders_areas_by_summed_time(self) -> None:
        self.assertEqual([area.name for area in self.report.areas][:2], ["Postgres", "Queue"])

    def test_shares_add_up_to_a_hundred_percent(self) -> None:
        self.assertAlmostEqual(sum(self.report.share_of(area) for area in self.report.areas), 100.0, places=6)

    def test_slowest_returns_the_limited_list_in_order(self) -> None:
        slowest = self.report.slowest(2)
        self.assertEqual([timing.milliseconds for timing in slowest], [121_000, 3029])

    def test_a_log_without_timings_says_so(self) -> None:
        self.assertFalse(parse_log("Build succeeded.\n  0 Warning(s)\n").has_timings)


class DecodeTests(unittest.TestCase):
    def test_decodes_utf8_with_or_without_a_bom(self) -> None:
        self.assertEqual(decode_log(SAMPLE.encode("utf-8")), SAMPLE)
        self.assertEqual(decode_log(SAMPLE.encode("utf-8-sig")), SAMPLE)

    def test_decodes_a_powershell_redirect(self) -> None:
        # PowerShell's `>` writes UTF-16LE with a BOM; read as UTF-8 it would parse to nothing.
        utf16 = SAMPLE.encode("utf-16")
        self.assertTrue(utf16.startswith((b"\xff\xfe", b"\xfe\xff")))
        self.assertEqual(decode_log(utf16), SAMPLE)
        self.assertEqual(len(parse_log(decode_log(utf16)).timings), 6)

    def test_decodes_utf16_without_a_bom(self) -> None:
        self.assertEqual(decode_log(SAMPLE.encode("utf-16-le")), SAMPLE)


class RenderTests(unittest.TestCase):
    def setUp(self) -> None:
        self.lines = render(parse_log(SAMPLE), top=2)
        self.text = "\n".join(self.lines)

    def test_prints_the_summary_block(self) -> None:
        self.assertIn("SUMMARY", self.lines[0])
        self.assertIn("tests with timings : 6 (failed 1)", self.text)
        self.assertIn("run summary        : total: 6", self.text)
        self.assertIn("summed test time   : 125.5 s", self.text)

    def test_lists_failures_and_both_tables(self) -> None:
        self.assertIn("    Zenkins.Tests.Queue.QueueControlTests.Replay", self.lines)
        self.assertIn("SLOWEST AREAS (share of summed test time, not of wall clock - collections run in parallel)", self.text)
        self.assertIn("SLOWEST TESTS (top 2)", self.text)

    def test_the_tables_are_capped_by_top(self) -> None:
        area_rows = [line for line in self.lines if line.startswith("  Postgres") or line.startswith("  Queue")]
        self.assertEqual(len(area_rows), 2)
        slowest_section = self.lines[self.lines.index("SLOWEST TESTS (top 2)") + 1 :]
        self.assertEqual(len(slowest_section), 2)
        self.assertIn("Zenkins.Tests.Postgres.LockTests.Claim", slowest_section[0])
        self.assertIn("QueueControlTests.Replay", slowest_section[1])

    def test_an_unrecognised_summary_is_reported_not_invented(self) -> None:
        text = "\n".join(render(parse_log("  passed A.Test (1s)"), top=3))
        self.assertIn("did not print a summary block", text)


if __name__ == "__main__":
    unittest.main()
