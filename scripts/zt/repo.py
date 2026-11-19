"""Locating the repository and the parts of it the scripts touch."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

from .errors import ToolError

SOLUTION_NAME = "Zenkins.slnx"
_ROOT_ENV = "ZENKINS_REPO_ROOT"

_PROJECTS = {
    "server": Path("src/Server/Zenkins.Server.csproj"),
    "worker": Path("src/Worker/Zenkins.Worker.csproj"),
}


def _looks_like_repo(path: Path) -> bool:
    return (path / SOLUTION_NAME).is_file()


@dataclass(frozen=True)
class Repo:
    """The repository root plus the paths every script derives from it."""

    root: Path

    @classmethod
    def discover(cls, start: Path | None = None) -> "Repo":
        """Find the root by walking up from ``start`` (or this file, then cwd)."""
        for candidate in cls._candidates(start):
            if _looks_like_repo(candidate):
                return cls(candidate.resolve())
        raise ToolError(
            f"could not find {SOLUTION_NAME} in any parent directory",
            detail=(
                "Run the script from inside the repository, or point "
                f"{_ROOT_ENV} at the checkout."
            ),
        )

    @staticmethod
    def _candidates(start: Path | None) -> list[Path]:
        """Where to look, best first.

        The checkout this script lives in comes first, then ``ZENKINS_REPO_ROOT``, then
        the working directory: a stale environment variable must not silently point a
        gate run at another checkout.
        """
        seeds: list[Path] = []
        if start is not None:
            seeds.append(Path(start))
        else:
            # scripts/zt/repo.py -> scripts/zt -> scripts -> repository root
            seeds.append(Path(__file__).resolve().parents[2])
        from_env = os.environ.get(_ROOT_ENV)
        if from_env:
            seeds.append(Path(from_env))
        if start is None:
            seeds.append(Path.cwd())

        found: list[Path] = []
        for seed in seeds:
            seed = seed.resolve()
            found.extend([seed, *seed.parents])
        return found

    # -- paths --------------------------------------------------------------

    @property
    def artifacts(self) -> Path:
        return self.root / "artifacts"

    @property
    def publish_root(self) -> Path:
        return self.artifacts / "publish"

    def project(self, key: str) -> Path:
        try:
            relative = _PROJECTS[key]
        except KeyError:  # pragma: no cover - programming error, not operator input
            raise KeyError(f"unknown project key {key!r}; known: {sorted(_PROJECTS)}") from None
        path = self.root / relative
        if not path.is_file():
            raise ToolError(f"project file is missing: {path}")
        return path

    def relative(self, path: Path) -> str:
        """A path as the operator would type it (relative to the root when inside)."""
        try:
            return str(path.resolve().relative_to(self.root))
        except ValueError:
            return str(path)
