"""Tests for the publish matrix: options -> jobs -> dotnet command lines.

The commands matter as much as the jobs: ``PublishAot=false`` on every JIT row is
what keeps the Worker's csproj default (I-004) from turning a folder publish into a
NativeAOT publish.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from zt.dotnet import PublishRequest
from zt.errors import UsageError
from zt.publish_plan import (
    SERVER,
    WORKER,
    WORKER_AOT,
    WORKER_SINGLE_FILE,
    PublishOptions,
    build_plan,
    default_options,
)
from zt.publish_wizard import options_from_flags

HOST = "osx-arm64"


class DefaultPlanTests(unittest.TestCase):
    def setUp(self) -> None:
        self.plan = build_plan(default_options(), HOST)

    def test_publishes_the_three_documented_rids_in_order(self) -> None:
        self.assertEqual(self.plan.options.rids, ("win-x64", "linux-x64", "osx-arm64"))

    def test_each_rid_gets_server_worker_and_single_file(self) -> None:
        per_rid = {}
        for job in self.plan.jobs:
            per_rid.setdefault(job.rid, []).append(job.variant)
        for rid, variants in per_rid.items():
            self.assertEqual(variants, [SERVER, WORKER, WORKER_SINGLE_FILE], rid)

    def test_no_aot_row_unless_asked(self) -> None:
        self.assertFalse([job for job in self.plan.jobs if job.aot])
        self.assertEqual(self.plan.notices, ())

    def test_output_folders_sit_under_the_publish_root(self) -> None:
        root = Path("/tmp/artifacts/publish")
        self.assertEqual(
            self.plan.jobs[0].output_dir(root),
            root / "win-x64" / SERVER,
        )
        self.assertEqual(
            self.plan.jobs[2].output_dir(root),
            root / "win-x64" / WORKER_SINGLE_FILE,
        )


class AotTests(unittest.TestCase):
    def test_aot_is_added_for_the_host_rid(self) -> None:
        plan = build_plan(PublishOptions(rids=("osx-arm64",), worker_aot=True), HOST)
        self.assertEqual([job.variant for job in plan.jobs], [SERVER, WORKER, WORKER_SINGLE_FILE, WORKER_AOT])
        self.assertEqual(plan.notices, ())

    def test_aot_for_another_rid_is_skipped_with_a_notice(self) -> None:
        plan = build_plan(PublishOptions(rids=("win-x64",), worker_aot=True), HOST)
        self.assertEqual(len(plan.notices), 1)
        self.assertTrue(plan.notices[0].startswith("skipping win-x64: "))
        self.assertIn("cross-compile", plan.notices[0])
        self.assertNotIn(WORKER_AOT, [job.variant for job in plan.jobs])

    def test_a_foreign_rid_still_gets_its_jit_rows(self) -> None:
        plan = build_plan(PublishOptions(rids=("win-x64",), worker_aot=True), HOST)
        self.assertEqual([job.variant for job in plan.jobs], [SERVER, WORKER, WORKER_SINGLE_FILE])


class FrameworkDependentTests(unittest.TestCase):
    def setUp(self) -> None:
        self.plan = build_plan(
            PublishOptions(rids=("linux-x64",), self_contained=False, single_file=True, worker_aot=True),
            HOST,
        )

    def test_single_file_and_aot_are_dropped_with_notices(self) -> None:
        self.assertEqual([job.variant for job in self.plan.jobs], [SERVER, WORKER])
        self.assertEqual(len(self.plan.notices), 2)

    def test_the_jobs_are_not_self_contained(self) -> None:
        self.assertTrue(all(not job.self_contained for job in self.plan.jobs))

    def test_the_aot_notice_says_why(self) -> None:
        self.assertIn("always self-contained", " ".join(self.plan.notices))


class RequestTests(unittest.TestCase):
    def _request(self, **overrides: object) -> PublishRequest:
        fields = {
            "project": Path("/tmp/Zenkins.Worker.csproj"),
            "output": Path("/tmp/out"),
            "rid": "osx-arm64",
        }
        fields.update(overrides)
        return PublishRequest(**fields)  # type: ignore[arg-type]

    def test_a_jit_publish_turns_aot_off_explicitly(self) -> None:
        self.assertIn("PublishAot=false", self._request().properties())

    def test_a_single_file_publish_carries_both_properties(self) -> None:
        props = self._request(single_file=True).properties()
        self.assertIn("PublishSingleFile=true", props)
        self.assertIn("IncludeNativeLibrariesForSelfExtract=true", props)

    def test_an_aot_publish_says_aot_true_and_nothing_about_single_file(self) -> None:
        props = self._request(aot=True).properties()
        self.assertEqual(props, ["PublishAot=true"])

    def test_aot_without_self_contained_is_refused(self) -> None:
        with self.assertRaises(UsageError):
            self._request(aot=True, self_contained=False)

    def test_aot_with_single_file_is_refused(self) -> None:
        with self.assertRaises(UsageError):
            self._request(aot=True, single_file=True)


class OptionsTests(unittest.TestCase):
    def test_flags_replace_the_default_selection_but_keep_its_shape(self) -> None:
        options = options_from_flags(rids=("linux-arm64",), worker_aot=True)
        self.assertEqual(options.rids, ("linux-arm64",))
        self.assertTrue(options.self_contained)
        self.assertTrue(options.single_file)
        self.assertTrue(options.worker_aot)

    def test_no_flags_means_the_documented_bundle(self) -> None:
        self.assertEqual(options_from_flags().rids, default_options().rids)

    def test_an_empty_selection_is_refused(self) -> None:
        with self.assertRaises(UsageError):
            PublishOptions(rids=())

    def test_an_empty_configuration_is_refused(self) -> None:
        with self.assertRaises(UsageError):
            PublishOptions(configuration="")


if __name__ == "__main__":
    unittest.main()
