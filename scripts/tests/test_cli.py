"""Smoke tests for the three command-line front ends.

These run the real ``main()`` with captured output: they prove the wiring (exit
codes, what the plan prints, what a log summarises) without touching an SDK.
"""

from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import publish
import suite_duration
import verify
from zt.publish_plan import WORKER
from zt.rids import host_rid_or_none

FAST_FILTER = (
    "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot"
    "&Category!=GitSshd&Category!=Postgres&Category!=Load"
)


class FakeTTY(io.StringIO):
    """Answers a prompt the way a terminal would present itself to it."""

    def isatty(self) -> bool:  # noqa: D401 - the protocol, not a property
        return True


def run_main(module, argv: list[str], *, stdin: str = "", tty: bool = False) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    saved = sys.stdin
    sys.stdin = FakeTTY(stdin) if tty else io.StringIO(stdin)
    try:
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = module.main(argv)
    finally:
        sys.stdin = saved
    return code, out.getvalue(), err.getvalue()


class PublishCliTests(unittest.TestCase):
    def test_a_dry_run_prints_the_commands_and_runs_nothing(self) -> None:
        code, out, _ = run_main(publish, ["--dry-run", "osx-arm64"])
        self.assertEqual(code, 0)
        self.assertIn("plan", out)
        self.assertIn("3 publishes", out)
        self.assertIn("dotnet publish", out)
        self.assertIn("PublishAot=false", out)
        self.assertIn("dry run: nothing was published", out)

    def test_the_default_selection_is_the_documented_bundle(self) -> None:
        code, out, _ = run_main(publish, ["--dry-run"])
        self.assertEqual(code, 0)
        for rid in ("win-x64", "linux-x64", "osx-arm64"):
            self.assertIn(rid, out)

    def test_a_framework_dependent_dry_run_drops_the_self_contained_rows(self) -> None:
        code, out, _ = run_main(publish, ["--dry-run", "--no-self-contained"])
        self.assertEqual(code, 0)
        self.assertIn("6 publishes", out)  # three RIDs x (Server + Worker)
        self.assertNotIn("WorkerSingleFile", out)

    def test_an_unknown_rid_is_an_error_not_a_publish(self) -> None:
        code, _, err = run_main(publish, ["--dry-run", "plan9-mips"])
        self.assertEqual(code, 2)
        self.assertIn("unknown runtime identifier", err)

    def test_a_terminal_is_asked_and_a_dry_run_stops_after_the_plan(self) -> None:
        # answers in question order: preset (default), self-contained (yes), AOT (no), single-file (yes)
        code, out, _ = run_main(publish, ["--dry-run"], stdin="\n\nn\ny\n", tty=True)
        self.assertEqual(code, 0)
        self.assertIn("What should be published?", out)
        self.assertIn("-> The release bundle", out)
        self.assertIn("9 publishes", out)  # three RIDs x (Server + Worker + single-file)
        self.assertIn("dry run: nothing was published", out)

    def test_answering_no_at_the_confirmation_publishes_nothing(self) -> None:
        # the same four answers, then "n" to "Run these publishes now?"
        code, out, err = run_main(publish, [], stdin="\n\nn\ny\nn\n", tty=True)
        self.assertEqual(code, 130)
        self.assertIn("nothing published", err)
        # the plan is printed before the question, but nothing was executed: no execution heading
        self.assertNotIn("== publish:", out)

    def test_configuration_reaches_every_command(self) -> None:
        code, out, _ = run_main(publish, ["--dry-run", "osx-arm64", "-c", "Debug"])
        self.assertEqual(code, 0)
        self.assertRegex(out, r"configuration\s+Debug")
        self.assertEqual(out.count("dotnet publish"), out.count("-c Debug"))

    def test_configuration_survives_the_interactive_flow(self) -> None:
        # A flag the wizard path drops would be worse than one that never existed: the plan
        # would print Release and the operator would read it back as their own answer.
        code, out, _ = run_main(publish, ["--dry-run", "-c", "Debug"], stdin="\n\nn\ny\n", tty=True)
        self.assertEqual(code, 0)
        self.assertRegex(out, r"configuration\s+Debug")
        self.assertEqual(out.count("dotnet publish"), out.count("-c Debug"))

    def test_a_lone_option_is_a_selection_and_is_not_answered_by_a_wizard(self) -> None:
        # On a terminal, --no-self-contained must publish the framework-dependent matrix,
        # not open a wizard whose "Self-contained?" default (yes) contradicts the flag.
        code, out, _ = run_main(publish, ["--dry-run", "--no-self-contained"], tty=True)
        self.assertEqual(code, 0)
        self.assertNotIn("What should be published?", out)  # no menu was shown
        self.assertIn("6 publishes", out)
        self.assertNotIn("WorkerSingleFile", out)

    def test_a_lone_aot_option_is_not_dropped_on_a_terminal(self) -> None:
        host = host_rid_or_none()
        if host is None:
            self.skipTest("this machine's RID is not in the matrix")
        code, out, _ = run_main(publish, ["--dry-run", "--aot"], tty=True)
        self.assertEqual(code, 0)
        self.assertIn(f"{host}: WorkerAot", out)

    def test_output_resolves_against_the_repository_root(self) -> None:
        code, out, _ = run_main(publish, ["--dry-run", "osx-arm64", "-o", "artifacts/elsewhere"])
        self.assertEqual(code, 0)
        self.assertIn("artifacts/elsewhere", out)
        self.assertNotIn("/tmp/", out)

    def test_platform_and_arch_flags_expand_to_rids(self) -> None:
        code, out, _ = run_main(publish, ["--dry-run", "--platform", "Linux", "--arch", "arm64"])
        self.assertEqual(code, 0)
        self.assertIn("linux-arm64", out)
        self.assertNotIn("win-x64", out)


class StaleOutputTests(unittest.TestCase):
    def _job(self, **overrides: object) -> publish.PublishJob:
        fields = {"rid": "osx-arm64", "variant": WORKER, "self_contained": True}
        fields.update(overrides)
        return publish.PublishJob(**fields)  # type: ignore[arg-type]

    def test_a_self_contained_publish_never_warns(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root) / "osx-arm64" / WORKER
            directory.mkdir(parents=True)
            (directory / "libcoreclr.dylib").write_text("x")
            self.assertEqual(publish._stale_output(self._job(), Path(root)), "")

    def test_a_framework_dependent_publish_over_a_runtime_warns_once(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root) / "osx-arm64" / WORKER
            directory.mkdir(parents=True)
            (directory / "libcoreclr.dylib").write_text("x")
            message = publish._stale_output(self._job(self_contained=False), Path(root))
            self.assertIn("libcoreclr.dylib", message)
            self.assertIn("would ship it too", message)

    def test_an_empty_or_framework_dependent_folder_is_quiet(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            self.assertEqual(publish._stale_output(self._job(self_contained=False), Path(root)), "")
            directory = Path(root) / "osx-arm64" / WORKER
            directory.mkdir(parents=True)
            (directory / "Zenkins.Worker.dll").write_text("x")
            self.assertEqual(publish._stale_output(self._job(self_contained=False), Path(root)), "")


class VerifyCliTests(unittest.TestCase):
    def test_a_dry_run_prints_the_header_a_real_run_prints(self) -> None:
        code, out, _ = run_main(verify, ["--dry-run"])
        self.assertEqual(code, 0)
        self.assertIn("verify: gate", out)
        self.assertLess(out.index("verify: gate"), out.index("verify: plan"))
        self.assertNotIn("verify: build (Release)", out)  # nothing was run

    def test_the_plan_lists_the_gate_in_order_without_running_it(self) -> None:
        code, out, _ = run_main(verify, ["--list"])
        self.assertEqual(code, 0)
        self.assertIn(FAST_FILTER, out)
        self.assertLess(out.index("build (Release)"), out.index("Fast suite"))
        self.assertLess(out.index("Fast suite"), out.index("Postgres"))
        self.assertIn("nothing was run", out)


class SuiteDurationCliTests(unittest.TestCase):
    def _write(self, text: str) -> Path:
        handle = tempfile.NamedTemporaryFile("w", suffix=".log", delete=False, encoding="utf-8")
        handle.write(text)
        handle.close()
        return Path(handle.name)

    def test_summarises_a_log_with_timings(self) -> None:
        path = self._write("  passed Zenkins.Tests.Theme.CascadeTests.Value (417ms)\n")
        try:
            code, out, _ = run_main(suite_duration, [str(path), "3"])
        finally:
            path.unlink()
        self.assertEqual(code, 0)
        self.assertIn("SUMMARY", out)
        self.assertIn("tests with timings : 1 (failed 0)", out)

    def test_a_log_without_timings_exits_one_and_explains_itself(self) -> None:
        path = self._write("Build succeeded.\n")
        try:
            code, _, err = run_main(suite_duration, [str(path)])
        finally:
            path.unlink()
        self.assertEqual(code, 1)
        self.assertIn("--output detailed", err)

    def test_a_missing_file_exits_two(self) -> None:
        code, _, err = run_main(suite_duration, ["/nonexistent/never.log"])
        self.assertEqual(code, 2)
        self.assertIn("no such file", err)


if __name__ == "__main__":
    unittest.main()
