"""Runtime identifiers: what can be published, and which RID this machine is.

One RID per publish - the scripts never guess a RID at run time (.NET's own
``RuntimeIdentifier`` inference also differs between a folder publish and a
single-file publish, which is the drift I-003 closed). This module is the single
place that knows the matrix's RIDs and the platform names the operator sees.
"""

from __future__ import annotations

import platform
import sys
from dataclasses import dataclass
from typing import Iterable, Sequence

from .errors import UsageError

PLATFORM_WINDOWS = "Windows"
PLATFORM_LINUX = "Linux"
PLATFORM_MACOS = "macOS"

PLATFORMS: tuple[str, ...] = (PLATFORM_WINDOWS, PLATFORM_LINUX, PLATFORM_MACOS)
ARCHES: tuple[str, ...] = ("x64", "arm64")

# The matrix the repository publishes, grouped the way the operator thinks about
# it. These are the RIDs with a deploy/ service template story (see docs/install.md).
RIDS_BY_PLATFORM: dict[str, tuple[str, ...]] = {
    PLATFORM_WINDOWS: ("win-x64", "win-arm64"),
    PLATFORM_LINUX: ("linux-x64", "linux-arm64"),
    PLATFORM_MACOS: ("osx-arm64", "osx-x64"),
}

KNOWN_RIDS: tuple[str, ...] = tuple(rid for rids in RIDS_BY_PLATFORM.values() for rid in rids)

# What `publish` publishes when nobody says otherwise: the three RIDs a release
# bundle has always carried.
DEFAULT_RIDS: tuple[str, ...] = ("win-x64", "linux-x64", "osx-arm64")


@dataclass(frozen=True)
class Rid:
    """A known runtime identifier, split into the parts prompts talk about."""

    value: str
    platform_name: str
    arch: str

_RID_TABLE: dict[str, Rid] = {
    rid: Rid(rid, platform_name, rid.rsplit("-", 1)[1])
    for platform_name, rids in RIDS_BY_PLATFORM.items()
    for rid in rids
}


def rids_for(platforms: Sequence[str], arches: Sequence[str]) -> tuple[str, ...]:
    """Expand platform names and arches into the RIDs that exist for them."""
    wanted = set(arches)
    picked: list[str] = []
    for name in platforms:
        for rid in RIDS_BY_PLATFORM.get(name, ()):
            if arch_of(rid) in wanted and rid not in picked:
                picked.append(rid)
    return tuple(picked)


def parse_rids(values: Iterable[str]) -> tuple[str, ...]:
    """Normalise operator input (``osx-arm64``, ``OSX-ARM64,linux-x64``) into RIDs.

    A platform name is not enough on its own: every platform in the matrix carries two
    architectures, so ``windows`` is refused with the two RIDs it could mean.
    """
    picked: list[str] = []
    for raw in values:
        for token in str(raw).replace(",", " ").split():
            rid = _normalise_one(token)
            if rid not in picked:
                picked.append(rid)
    if not picked:
        raise UsageError("no runtime identifier given")
    return tuple(picked)


def _normalise_one(token: str) -> str:
    lowered = token.strip().lower()
    if lowered in KNOWN_RIDS:
        return lowered
    for name, rids in RIDS_BY_PLATFORM.items():
        if lowered == name.lower():
            raise UsageError(
                f"{name} covers more than one runtime identifier: {', '.join(rids)}",
                detail="Name the RID, or use --platform with --arch.",
            )
    raise UsageError(
        f"unknown runtime identifier {token!r}",
        detail=f"Known: {', '.join(KNOWN_RIDS)}.",
    )


def arch_of(rid: str) -> str:
    return _RID_TABLE[rid].arch


def host_rid(system: str | None = None, machine: str | None = None) -> str:
    """This machine's RID. Raises when the platform or architecture is unsupported.

    ``system``/``machine`` exist for tests; callers leave them alone.
    """
    system = system if system is not None else sys.platform
    if system.startswith("win"):
        platform_name = PLATFORM_WINDOWS
        prefix = "win"
    elif system == "darwin":
        platform_name = PLATFORM_MACOS
        prefix = "osx"
    elif system.startswith("linux"):
        platform_name = PLATFORM_LINUX
        prefix = "linux"
    else:
        raise UsageError(f"unsupported host platform {system!r}")

    machine = (machine if machine is not None else platform.machine()).lower()
    if machine in ("x86_64", "amd64", "x64"):
        arch = "x64"
    elif machine in ("arm64", "aarch64"):
        arch = "arm64"
    else:
        raise UsageError(
            f"unsupported host architecture {machine!r}",
            detail=f"{platform_name} RIDs known: {', '.join(RIDS_BY_PLATFORM[platform_name])}.",
        )

    rid = f"{prefix}-{arch}"
    if rid not in _RID_TABLE:
        raise UsageError(
            f"this machine's RID {rid!r} is not in the matrix",
            detail=f"Known: {', '.join(KNOWN_RIDS)}.",
        )
    return rid


def host_rid_or_none() -> str | None:
    """``host_rid()`` for callers that only want to label a menu."""
    try:
        return host_rid()
    except UsageError:
        return None


def aot_cross_compile_problem(rid: str, host: str | None) -> str | None:
    """:data:`None` when ``rid`` can be AOT-published here, else the reason it cannot.

    .NET does not cross-compile NativeAOT: the publish needs the target OS's
    native toolchain, and ILC refuses another OS's runtime pack.
    """
    if host is None:
        return "this machine's RID is unknown, so the NativeAOT variant cannot be attempted"
    if rid == host:
        return None
    return (
        f"the NativeAOT variant is built by the target machine's own toolchain "
        f"(.NET does not cross-compile AOT), and this machine is {host}"
    )
