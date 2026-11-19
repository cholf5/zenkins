"""The one exception type the front ends know how to turn into an exit code."""

from __future__ import annotations


class ToolError(RuntimeError):
    """A failure the tool can explain to the operator.

    Front ends catch this, print ``error: <message>`` on stderr and exit with
    :attr:`exit_code`. Anything else escaping is a bug and keeps its traceback.
    Subclasses declare their own code as a class attribute; ``exit_code`` as a
    constructor argument overrides it for a single raise.
    """

    exit_code = 1

    def __init__(self, message: str, *, detail: str = "", exit_code: int | None = None) -> None:
        super().__init__(message)
        self.detail = detail
        if exit_code is not None:
            self.exit_code = exit_code


class UsageError(ToolError):
    """The operator asked for something that makes no sense (bad flag, bad RID)."""

    exit_code = 2


class Cancelled(ToolError):
    """The operator interrupted an interactive prompt or answered "no" to a run."""

    exit_code = 130

    def __init__(self, message: str = "cancelled") -> None:
        super().__init__(message)
