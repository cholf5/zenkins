#!/usr/bin/env bash
#
# P-001: the acceptance gate of docs/development.md §2 in one command. This script IS §2's gate -
# it runs the exact commands §2 documents, in §2's order, and stops at the first red one:
#
#   1. dotnet build -c Release
#   2. the fast suite (the sextuple exclusion filter)
#   3. the six acceptance harnesses, one filtered run each:
#      Milestone (Q-002), Recovery (Q-004), PublishSmoke (I-003), PublishAot (I-004), GitSshd (Q-009),
#      Postgres (F-029)
#
# A red suite names itself and the script exits non-zero immediately; nothing after a red suite
# runs, so the fix cycle starts at the first failing suite every time. Green output ends with the
# gate's wall-clock time.
#
# development.md §2 stays the written gate (the prerequisites, what each harness needs, the counts
# per suite); this script is its executable form and the two must never drift: a filter changed here
# and in §2 is one change. The CI workflow (.github/workflows/ci.yml) runs this script rather than
# transcribing the commands.
#
# Windows: the same script runs under Git Bash (Git for Windows ships one), which is how the CI
# workflow runs it on windows-latest. There is deliberately no second, PowerShell transcript of the
# gate to drift against - the Windows gate is this script, proven by CI.
#
# Prerequisites are §1's, per suite: PublishAot needs the machine's native AOT toolchain (macOS:
# Xcode CLT; Windows: VS C++ workload; Linux: clang + zlib dev headers), GitSshd needs the OpenSSH
# tools (sshd + ssh-keygen, on PATH or the standard locations), and Postgres needs a real PostgreSQL
# server named by the ZENKINS_TEST_POSTGRES environment variable (a disposable database - its public
# schema is dropped and recreated). Where those are absent the suite's facts skip with the reason
# recorded - that is the suites' designed behaviour, not a silent omission - so the gate stays green
# on a machine without them.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

STARTED=$SECONDS

# Runs one §2 command. A non-zero exit names the suite that went red and stops the gate.
run_suite() {
  local name="$1"
  shift
  echo "== verify.sh: $name =="
  echo "+ $*"
  if ! "$@"; then
    echo
    echo "verify.sh: FAILED - the '$name' suite went red. Its output above names the failing test(s)." >&2
    exit 1
  fi
}

run_suite "build (Release)" dotnet build -c Release
run_suite "Fast suite"      dotnet test -c Release --filter "Category!=Milestone&Category!=Recovery&Category!=PublishSmoke&Category!=PublishAot&Category!=GitSshd&Category!=Postgres&Category!=Load"
run_suite "Milestone"       dotnet test -c Release --filter "Category=Milestone"
run_suite "Recovery"        dotnet test -c Release --filter "Category=Recovery"
run_suite "PublishSmoke"    dotnet test -c Release --filter "Category=PublishSmoke"
run_suite "PublishAot"      dotnet test -c Release --filter "Category=PublishAot"
run_suite "GitSshd"         dotnet test -c Release --filter "Category=GitSshd"
run_suite "Postgres"        dotnet test -c Release --filter "Category=Postgres"

echo
echo "verify.sh: gate green - build, fast suite and six harnesses in $((SECONDS - STARTED))s."
