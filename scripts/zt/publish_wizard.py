"""The interactive publish flow: questions in, :class:`PublishOptions` out.

Kept apart from the plan (:mod:`zt.publish_plan`, pure data) and from the CLI
(:mod:`publish`, argument parsing and execution) so each can be reasoned about -
and tested - on its own. The wizard never runs anything; it only answers.
"""

from __future__ import annotations

from dataclasses import replace

from .errors import UsageError
from .prompt import Choice, TerminalPrompt
from .publish_plan import PublishOptions, default_options
from .rids import (
    ARCHES,
    DEFAULT_RIDS,
    PLATFORMS,
    RIDS_BY_PLATFORM,
    KNOWN_RIDS,
    host_rid_or_none,
    rids_for,
)

_PRESET_BUNDLE = "bundle"
_PRESET_HOST = "host"
_PRESET_CHOOSE = "choose"


def ask_options(prompt: TerminalPrompt) -> PublishOptions:
    """Ask the operator what to publish, in the order the answers depend on each other."""
    host = host_rid_or_none()
    prompt.require_interactive()

    rids = _ask_rids(prompt, host)
    self_contained = prompt.confirm(
        "Self-contained? (bundles the .NET runtime; 'no' needs the runtime installed on the target)",
        default=True,
    )

    worker_aot = False
    single_file = False
    if self_contained:
        worker_aot = prompt.confirm(
            f"Add the Worker's NativeAOT variant? (one native executable; needs {host or 'the target'}'s own native toolchain)",
            default=False,
        )
        single_file = prompt.confirm(
            "Add the Worker's single-file variant? (one executable for a build machine)",
            default=True,
        )
    else:
        prompt.note("framework-dependent publishes skip the AOT and single-file rows (both are self-contained only)")

    if not rids:
        raise UsageError("no runtime identifier selected, nothing to publish")
    return PublishOptions(
        rids=rids,
        self_contained=self_contained,
        single_file=single_file,
        worker_aot=worker_aot,
    )


def _ask_rids(prompt: TerminalPrompt, host: str | None) -> tuple[str, ...]:
    presets = [
        Choice(_PRESET_BUNDLE, "The release bundle", f"{', '.join(DEFAULT_RIDS)} (the documented matrix)"),
        Choice(_PRESET_CHOOSE, "Pick platforms and architectures", "ask the next two questions"),
    ]
    if host is not None:
        presets.append(Choice(_PRESET_HOST, "This machine only", host))
    preset = prompt.choose_one("What should be published?", presets, default=1)

    if preset == _PRESET_BUNDLE:
        return DEFAULT_RIDS
    if preset == _PRESET_HOST:
        if host is None:  # pragma: no cover - the preset is not offered without a host
            raise UsageError("this machine's RID is unknown")
        return (host,)

    platforms = prompt.choose_many(
        "Which platform(s)?",
        [
            Choice(
                name,
                name + (" (this machine)" if host is not None and host in RIDS_BY_PLATFORM[name] else ""),
                ", ".join(RIDS_BY_PLATFORM[name]),
            )
            for name in PLATFORMS
        ],
        default=tuple(range(1, len(PLATFORMS) + 1)),
    )
    arches = prompt.choose_many(
        "Which architecture(s)?",
        [Choice(arch, arch, "") for arch in ARCHES],
        default=_default_arches(host),
    )
    rids = rids_for(platforms, arches)
    if not rids:
        raise UsageError(
            "that combination publishes nothing",
            detail=f"Known runtime identifiers: {', '.join(KNOWN_RIDS)}.",
        )
    return rids


def _default_arches(host: str | None) -> tuple[int, ...]:
    if host is not None:
        return (ARCHES.index(host.rsplit("-", 1)[1]) + 1,)
    return (1,)


def options_from_flags(
    *,
    rids: tuple[str, ...] | None = None,
    configuration: str = "Release",
    self_contained: bool = True,
    single_file: bool = True,
    worker_aot: bool = False,
) -> PublishOptions:
    """The non-interactive path: explicit flags, or the documented defaults.

    Every flag the front end parses has to arrive here - a flag that is parsed and
    then dropped is worse than a flag that does not exist, because the plan prints the
    default it silently used.
    """
    base = default_options()
    return replace(
        base,
        rids=tuple(rids) if rids else base.rids,
        configuration=configuration,
        self_contained=self_contained,
        single_file=single_file,
        worker_aot=worker_aot,
    )
