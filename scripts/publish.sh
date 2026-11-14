#!/usr/bin/env bash
#
# I-003: the per-RID publish matrix. One RID per publish (no RID guessing at runtime); JIT
# self-contained only - trimming and NativeAOT are I-004 and stay off here. The Server publishes
# as a folder (Blazor Server ships wwwroot; a single-file Server is not a goal); the Worker
# publishes as a folder and, additionally, as a single-file one-exe drop for build machines
# (PublishSingleFile + IncludeNativeLibrariesForSelfExtract).
#
# Usage:
#   scripts/publish.sh [rid ...]          # default: win-x64 linux-x64 osx-arm64
#
# Output (git-ignored): artifacts/publish/<rid>/{Server,Worker,WorkerSingleFile}
#
# The publish runs with the repo's TreatWarningsAsErrors, so any warning - including trim/AOT
# (none expected: trimming is off) and NuGet audit - fails the matrix.
#
# Service packaging per OS (install/uninstall):
#   macOS  - deploy/macos/install-launchd.sh + deploy/macos/com.zenkins.server.plist
#   Linux  - deploy/systemd/zenkins-server.service (install commands in the file header)
#   Windows- deploy/windows/install-server-service.ps1
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_ROOT="$REPO_ROOT/artifacts/publish"

if [ "$#" -gt 0 ]; then
  RIDS=("$@")
else
  RIDS=(win-x64 linux-x64 osx-arm64)
fi

for rid in "${RIDS[@]}"; do
  echo "== $rid: Server (self-contained folder) =="
  dotnet publish "$REPO_ROOT/src/Server/Zenkins.Server.csproj" \
    -c Release -r "$rid" --self-contained true \
    -o "$OUT_ROOT/$rid/Server"

  echo "== $rid: Worker (self-contained folder) =="
  dotnet publish "$REPO_ROOT/src/Worker/Zenkins.Worker.csproj" \
    -c Release -r "$rid" --self-contained true \
    -o "$OUT_ROOT/$rid/Worker"

  echo "== $rid: Worker (single-file variant) =="
  dotnet publish "$REPO_ROOT/src/Worker/Zenkins.Worker.csproj" \
    -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT_ROOT/$rid/WorkerSingleFile"
done

echo "Publish matrix complete: $OUT_ROOT"
