"""The ``dotnet`` command builders.

Commands are built, never run, here: that keeps the publish matrix and the gate
inspectable (``--dry-run`` prints what would run) and testable without an SDK.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Sequence

from .errors import UsageError
from .proc import Command
from .repo import Repo

# Suite categories that are not part of the fast loop. The gate, docs/development.md
# §2 and this tuple are one fact: scripts/tests/test_gate.py pins the produced
# filter string against the one the document prints.
FAST_EXCLUSIONS: tuple[str, ...] = (
    "Milestone",
    "Recovery",
    "PublishSmoke",
    "PublishAot",
    "GitSshd",
    "Postgres",
    "Load",
)


def fast_filter(exclusions: Sequence[str] = FAST_EXCLUSIONS) -> str:
    """The fast suite's filter: every acceptance category excluded, all joined with ``&``."""
    return "&".join(f"Category!={category}" for category in exclusions)


def category_filter(category: str) -> str:
    return f"Category={category}"


@dataclass(frozen=True)
class PublishRequest:
    """One ``dotnet publish`` invocation of the matrix."""

    project: Path
    output: Path
    rid: str
    configuration: str = "Release"
    self_contained: bool = True
    single_file: bool = False
    aot: bool = False

    def __post_init__(self) -> None:
        if self.aot and not self.self_contained:
            raise UsageError("the NativeAOT variant is always self-contained")
        if self.aot and self.single_file:
            raise UsageError("the NativeAOT output is already one native executable; drop the single-file flag")

    def properties(self) -> list[str]:
        """``-p:`` pairs, in a stable order so the echoed command is reproducible.

        The JIT rows pass ``PublishAot=false`` explicitly: the Worker csproj
        defaults the property to true (I-004 runs the AOT analyzers on every
        build), so an implicit row would silently drift into an AOT publish.
        """
        props: list[str] = []
        props.append(f"PublishAot={'true' if self.aot else 'false'}")
        if self.single_file:
            props.append("PublishSingleFile=true")
            props.append("IncludeNativeLibrariesForSelfExtract=true")
        return props


@dataclass(frozen=True)
class Dotnet:
    """Builds ``dotnet`` command lines for this repository."""

    repo: Repo
    executable: str = "dotnet"

    def build(self, *, configuration: str = "Release") -> Command:
        """``dotnet build -c <configuration>`` from the root (which picks up the solution)."""
        return Command.of(self.executable, "build", "-c", configuration, cwd=self.repo.root)

    def test(self, *, filter: str | None = None, configuration: str = "Release") -> Command:
        """A ``dotnet test`` run from the root, so it picks up the solution.

        The filter is passed as one argv element (no shell), which is why the
        ``&``-joined exclusions need no quoting here.
        """
        argv = [self.executable, "test", "-c", configuration]
        if filter:
            argv += ["--filter", filter]
        return Command.of(*argv, cwd=self.repo.root)

    def publish(self, request: PublishRequest) -> Command:
        argv = [
            self.executable,
            "publish",
            str(request.project),
            "-c",
            request.configuration,
            "-r",
            request.rid,
            "--self-contained",
            "true" if request.self_contained else "false",
        ]
        argv += [f"-p:{prop}" for prop in request.properties()]
        argv += ["-o", str(request.output)]
        return Command.of(*argv, cwd=self.repo.root)
