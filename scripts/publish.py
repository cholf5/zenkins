#!/usr/bin/env python3
"""The publish matrix: what I-003/I-004 ship, as an interactive or scripted tool.

    scripts/publish.py                       # a terminal asks what to publish; a pipe publishes the bundle
    scripts/publish.py osx-arm64             # just that RID, self-contained, Worker + single-file
    scripts/publish.py --platform Windows Linux --arch x64
    scripts/publish.py osx-arm64 --aot       # adds the NativeAOT Worker (host RID only)
    scripts/publish.py --no-self-contained   # framework-dependent folders
    scripts/publish.py --dry-run             # print the commands, run nothing
    scripts/publish.py --interactive         # force the questions (needs a terminal)

Output (git-ignored): ``artifacts/publish/<rid>/<Server|Worker|WorkerSingleFile|WorkerAot>``.

The Server always publishes as a folder (Blazor Server ships ``wwwroot``). The
Worker publishes as a folder plus a single-file drop, and optionally as one native
executable - only on the machine whose RID it is, because .NET does not
cross-compile NativeAOT. The publish runs with the repository's
warnings-as-errors, so a trim/AOT/NuGet-audit warning fails the row.

Installing the result as a service is ``deploy/`` per OS; see ``docs/install.md``.
"""

from __future__ import annotations

import argparse
import os
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from zt.console import Console
from zt.dotnet import Dotnet, PublishRequest
from zt.errors import Cancelled, ToolError, UsageError
from zt.proc import Command, CommandRunner
from zt.prompt import TerminalPrompt
from zt.publish_plan import PublishJob, PublishOptions, PublishPlan, build_plan
from zt.publish_wizard import ask_options, options_from_flags
from zt.rids import DEFAULT_RIDS, KNOWN_RIDS, PLATFORMS, host_rid_or_none, parse_rids, rids_for
from zt.repo import Repo


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="publish.py",
        description="Publish the Zenkins Server and Worker for one or more RIDs.",
        epilog=(
            "RIDs: " + ", ".join(KNOWN_RIDS) + ". "
            "With no selection flags a terminal asks; without one it publishes the documented bundle "
            "(" + ", ".join(DEFAULT_RIDS) + ")."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("rid", nargs="*", help="runtime identifier(s), e.g. osx-arm64")
    parser.add_argument("--platform", action="append", choices=list(PLATFORMS), help="platform(s) to publish for")
    parser.add_argument("--arch", action="append", choices=["x64", "arm64"], help="architecture(s) to publish for")
    # None (not True/False) is what tells "the operator said nothing" apart from "the
    # operator said yes": a lone --aot must publish AOT, not start a wizard that then
    # answers its own default.
    parser.add_argument(
        "--self-contained",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="bundle the .NET runtime (default: yes)",
    )
    parser.add_argument(
        "--single-file",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="publish the Worker's single-file variant (default: yes, self-contained only)",
    )
    parser.add_argument(
        "--aot",
        "--worker-aot",
        dest="aot",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="also publish the NativeAOT Worker (default: no; host RID only)",
    )
    parser.add_argument("-c", "--configuration", default="Release", help="build configuration")
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        help="output root, relative to the repository root (default: artifacts/publish)",
    )
    parser.add_argument("-i", "--interactive", action="store_true", help="ask what to publish")
    parser.add_argument("-n", "--dry-run", action="store_true", help="print the commands and exit")
    return parser


def main(argv: list[str] | None = None) -> int:
    console = Console()
    args = build_parser().parse_args(argv)
    try:
        return _run(console, args)
    except KeyboardInterrupt:
        console.error_line("interrupted")
        return 130
    except Cancelled as exc:
        console.warn(str(exc))
        return exc.exit_code
    except ToolError as exc:
        console.error(str(exc), detail=exc.detail)
        return exc.exit_code


def _run(console: Console, args: argparse.Namespace) -> int:
    repo = Repo.discover()
    publish_root = _output_root(repo, args.output)
    prompt = TerminalPrompt(console)
    asking = args.interactive or (not _given(args) and prompt.is_interactive)
    if args.interactive and not prompt.is_interactive:
        raise UsageError(
            "--interactive needs a terminal",
            detail="Pass the selection flags instead, e.g. `scripts/publish.py osx-arm64 --aot`.",
        )

    options = _options(console, prompt, args, asking)
    plan = build_plan(options, host_rid_or_none())
    dotnet = Dotnet(repo)

    _show_plan(console, plan, dotnet, publish_root, repo.relative(publish_root))
    if args.dry_run:
        console.blank()
        console.note("dry run: nothing was published")
        return 0
    if asking:
        if not prompt.confirm("Run these publishes now?", default=True):
            console.warn("nothing published")
            return Cancelled.exit_code
    else:
        console.note("(publishing without questions - run with no selection flags on a terminal to be asked)")

    _execute(console, CommandRunner(console), dotnet, plan, publish_root)
    _report_outputs(console, repo, plan, publish_root)
    return 0


def _output_root(repo: Repo, given: Path | None) -> Path:
    """``--output`` resolves against the repository root, like every other path here.

    Resolving against the working directory would make the same command line write to
    different places depending on where it was typed - the one thing ``Repo.discover``
    exists to prevent.
    """
    if given is None:
        return repo.publish_root
    expanded = given.expanduser()
    return (expanded if expanded.is_absolute() else repo.root / expanded).resolve()


def _options(
    console: Console,
    prompt: TerminalPrompt,
    args: argparse.Namespace,
    asking: bool,
) -> PublishOptions:
    if asking:
        if _given(args):
            console.warn("the interactive flow asks for the selection - the selection flags are ignored")
        # --configuration is not a selection: it applies to whatever the answers produce.
        return replace(ask_options(prompt), configuration=args.configuration)
    return options_from_flags(
        rids=_rids_from_flags(args),
        configuration=args.configuration,
        self_contained=_unless_given(args.self_contained, True),
        single_file=_unless_given(args.single_file, True),
        worker_aot=_unless_given(args.aot, False),
    )


def _unless_given(given: bool | None, default: bool) -> bool:
    """argparse's ``None`` means "not on the command line", so the documented default applies."""
    return default if given is None else given


def _given(args: argparse.Namespace) -> bool:
    """True when the operator named any part of the selection, not just a RID.

    ``--aot`` alone is a selection ("publish, with AOT"), so it must not open a wizard
    whose defaults would answer the question the operator already answered.
    """
    return any(
        value is not None
        for value in (args.rid or None, args.platform, args.arch, args.self_contained, args.single_file, args.aot)
    )


def _rids_from_flags(args: argparse.Namespace) -> tuple[str, ...] | None:
    picked: list[str] = list(args.rid)
    if args.platform or args.arch:
        picked += list(rids_for(args.platform or list(PLATFORMS), args.arch or ["x64", "arm64"]))
    return parse_rids(picked) if picked else None


def _request_for(plan: PublishPlan, job: PublishJob, project: Path, publish_root: Path) -> PublishRequest:
    return PublishRequest(
        project=project,
        output=job.output_dir(publish_root),
        rid=job.rid,
        configuration=plan.options.configuration,
        self_contained=job.self_contained,
        single_file=job.single_file,
        aot=job.aot,
    )


def _show_plan(
    console: Console,
    plan: PublishPlan,
    dotnet: Dotnet,
    publish_root: Path,
    output_label: str,
) -> None:
    for notice in plan.notices:
        console.warn(notice)
    for job in plan.jobs:
        stale = _stale_output(job, publish_root)
        if stale:
            console.warn(stale)

    options = plan.options
    console.blank()
    console.heading("plan")
    console.rows(
        [
            ("runtime identifiers", ", ".join(options.rids)),
            ("configuration", options.configuration),
            ("self-contained", "yes (bundles the runtime)" if options.self_contained else "no (target needs the runtime)"),
            ("single-file Worker", "yes" if any(job.single_file for job in plan.jobs) else "no"),
            ("NativeAOT Worker", "yes" if any(job.aot for job in plan.jobs) else "no"),
            ("output", output_label),
        ]
    )
    if not plan.jobs:
        return

    console.blank()
    console.heading(f"{len(plan.jobs)} {_plural(len(plan.jobs), 'publish')}")
    for job in plan.jobs:
        console.step(job.label)
        console.note(job.note)
        console.command(_command_for(plan, job, dotnet, publish_root).argv)


#: Files only a self-contained publish puts beside the apphost.
_RUNTIME_MARKERS = (
    "coreclr.dll",
    "libcoreclr.dylib",
    "libcoreclr.so",
    "hostfxr.dll",
    "libhostfxr.dylib",
    "libhostfxr.so",
)


def _stale_output(job: PublishJob, publish_root: Path) -> str:
    """Warn only about the direction that ships the wrong thing.

    ``dotnet publish -o`` merges into the directory - it never cleans it. A self-contained
    publish into a folder that held a framework-dependent one is harmless (files are
    replaced and added); the reverse is not: the old runtime stays, and the install
    guide tars these folders, so a framework-dependent drop would carry ~70 MB of a
    runtime it is not allowed to carry. That case is rare, so the warning is not noise;
    nothing is ever deleted on the operator's behalf.
    """
    if job.self_contained:
        return ""
    directory = job.output_dir(publish_root)
    try:
        leftover = next((name for name in _RUNTIME_MARKERS if (directory / name).exists()), None)
    except OSError:
        return ""
    if leftover is None:
        return ""
    return (
        f"{job.label}: {directory} still holds a self-contained runtime ({leftover}) and dotnet "
        "publish does not clean the folder - this framework-dependent publish would ship it too"
    )


def _plural(count: int, noun: str) -> str:
    return noun if count == 1 else noun + "es"


def _command_for(plan: PublishPlan, job: PublishJob, dotnet: Dotnet, publish_root: Path) -> Command:
    return dotnet.publish(_request_for(plan, job, dotnet.repo.project(job.project_key), publish_root))


def _execute(
    console: Console,
    runner: CommandRunner,
    dotnet: Dotnet,
    plan: PublishPlan,
    publish_root: Path,
) -> None:
    for job in plan.jobs:
        console.blank()
        console.heading(f"publish: {job.label}")
        console.note(job.note)
        code = runner.run(_command_for(plan, job, dotnet, publish_root))
        if code != 0:
            raise ToolError(
                f"the '{job.label}' publish failed",
                detail=(
                    f"exit code {code}. A red publish is usually a warning (this repository treats warnings "
                    "as errors) or, for the AOT row, a missing native toolchain (macOS: Xcode CLT; "
                    "Windows: VS C++ workload; Linux: clang + zlib dev headers)."
                ),
            )


def _report_outputs(console: Console, repo: Repo, plan: PublishPlan, publish_root: Path) -> None:
    rows = []
    for job in plan.jobs:
        directory = job.output_dir(publish_root)
        files, megabytes = _size_of(directory)
        rows.append((job.rid, job.variant, f"{files} files / {megabytes:.1f} MB", repo.relative(directory)))
    console.blank()
    console.heading("published")
    console.rows(rows, aligns=("l", "l", "r", "l"))
    console.blank()
    console.success(f"Publish matrix complete: {publish_root}")


def _size_of(directory: Path) -> tuple[int, float]:
    files = 0
    total = 0
    for parent, _, names in os.walk(directory):
        for name in names:
            path = Path(parent) / name
            files += 1
            try:
                total += path.stat().st_size
            except OSError:
                pass
    return files, total / (1024 * 1024)


if __name__ == "__main__":
    sys.exit(main())
