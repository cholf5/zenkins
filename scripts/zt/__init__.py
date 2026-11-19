"""Shared plumbing for the repository's Python tooling.

The scripts under ``scripts/`` are thin command-line front ends; everything with
behaviour lives here, one concern per module:

``console``      terminal output (headings, notes, warnings, command echoes)
``errors``       the one exception type the front ends translate into an exit code
``proc``         running external commands, echoing them, and failing fast
``repo``         locating the repository and its projects
``rids``         runtime identifiers, platforms, and the host RID
``prompt``       interactive questions (with the parsing kept pure and testable)
``dotnet``       the ``dotnet`` command builders (build, test, publish)
``gate``         the acceptance gate's suite table and its runner
``publish_plan`` what to publish, derived from the operator's options
``duration``     parsing ``dotnet test --output detailed`` into timings
``duration_report`` rendering those timings
"""
