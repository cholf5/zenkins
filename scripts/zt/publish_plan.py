"""What to publish: options in, publish jobs out. No I/O, no prompts, no SDK.

The matrix is the historical one (I-003/I-004), now described as data instead of
being hard-coded into a loop:

* the **Server** always publishes as a folder - Blazor Server ships ``wwwroot``,
  and a single-file Server was never a goal;
* the **Worker** publishes as a folder plus a single-file drop for build machines;
* the **Worker's NativeAOT** variant is optional and only for the host RID, because
  .NET does not cross-compile AOT.

Every row's JIT form passes ``PublishAot=false`` explicitly; see
:meth:`zt.dotnet.PublishRequest.properties`.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from .errors import UsageError
from .rids import DEFAULT_RIDS, aot_cross_compile_problem

SERVER = "Server"
WORKER = "Worker"
WORKER_SINGLE_FILE = "WorkerSingleFile"
WORKER_AOT = "WorkerAot"

_PROJECT_KEY = {SERVER: "server", WORKER: "worker", WORKER_SINGLE_FILE: "worker", WORKER_AOT: "worker"}


@dataclass(frozen=True)
class PublishOptions:
    """The operator's answers, before they are turned into commands."""

    rids: tuple[str, ...] = DEFAULT_RIDS
    configuration: str = "Release"
    self_contained: bool = True
    single_file: bool = True
    worker_aot: bool = False

    def __post_init__(self) -> None:
        if not self.rids:
            raise UsageError("nothing to publish: no runtime identifier selected")
        if not self.configuration:
            raise UsageError("nothing to publish: no configuration given")


@dataclass(frozen=True)
class PublishJob:
    """One publish row: one RID, one variant, one output folder."""

    rid: str
    variant: str
    self_contained: bool
    single_file: bool = False
    aot: bool = False
    note: str = ""

    @property
    def project_key(self) -> str:
        return _PROJECT_KEY[self.variant]

    @property
    def label(self) -> str:
        return f"{self.rid}: {self.variant}"

    def output_dir(self, publish_root: Path) -> Path:
        return publish_root / self.rid / self.variant


@dataclass(frozen=True)
class PublishPlan:
    """The jobs to run, plus what the plan deliberately refused to do."""

    options: PublishOptions
    jobs: tuple[PublishJob, ...]
    notices: tuple[str, ...] = ()
    host_rid: str | None = None


def _folder_note(self_contained: bool) -> str:
    return "self-contained folder" if self_contained else "framework-dependent folder (the target needs the .NET runtime)"


def build_plan(options: PublishOptions, host: str | None) -> PublishPlan:
    """Expand the options into the ordered matrix, recording what was skipped and why."""
    notices: list[str] = []
    single_file = options.single_file
    worker_aot = options.worker_aot

    if not options.self_contained:
        # AOT is inherently self-contained; the single-file row is only ever part of
        # the self-contained matrix. Refusing loudly beats publishing something the
        # operator did not ask for.
        if worker_aot:
            notices.append("the NativeAOT variant is always self-contained - not publishing it (--self-contained was off)")
            worker_aot = False
        if single_file:
            notices.append("the Worker single-file drop belongs to the self-contained matrix - not publishing it")
            single_file = False

    jobs: list[PublishJob] = []
    for rid in options.rids:
        jobs.append(
            PublishJob(
                rid=rid,
                variant=SERVER,
                self_contained=options.self_contained,
                note=_folder_note(options.self_contained),
            )
        )
        jobs.append(
            PublishJob(
                rid=rid,
                variant=WORKER,
                self_contained=options.self_contained,
                note=f"{_folder_note(options.self_contained)}; JIT (PublishAot is off explicitly, see I-004)",
            )
        )
        if single_file:
            jobs.append(
                PublishJob(
                    rid=rid,
                    variant=WORKER_SINGLE_FILE,
                    self_contained=True,
                    single_file=True,
                    note="one executable, JIT; the build-machine drop",
                )
            )
        if worker_aot:
            problem = aot_cross_compile_problem(rid, host)
            if problem is None:
                jobs.append(
                    PublishJob(
                        rid=rid,
                        variant=WORKER_AOT,
                        self_contained=True,
                        aot=True,
                        note="one native executable (needs this OS's native toolchain)",
                    )
                )
            else:
                notices.append(f"skipping {rid}: {WORKER_AOT} - {problem}")

    return PublishPlan(options=options, jobs=tuple(jobs), notices=tuple(notices), host_rid=host)


def default_options() -> PublishOptions:
    """The no-argument matrix: the three RIDs a release bundle carries, JIT + single-file."""
    return PublishOptions(rids=DEFAULT_RIDS, self_contained=True, single_file=True, worker_aot=False)
