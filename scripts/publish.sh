#!/usr/bin/env bash
#
# I-003: the per-RID publish matrix. One RID per publish (no RID guessing at runtime). The Server
# publishes as a folder (Blazor Server ships wwwroot; a single-file Server is not a goal); the Worker
# publishes as a folder and, additionally, as a single-file one-exe drop for build machines
# (PublishSingleFile + IncludeNativeLibrariesForSelfExtract).
#
# I-004: the Worker csproj carries <PublishAot>true</PublishAot> (the AOT analyzers must run on
# every build), so the JIT rows below pass -p:PublishAot=false explicitly - the matrix's behaviour
# is unchanged (self-contained JIT folder + single-file), it just can no longer drift with the
# project default. A NativeAOT Worker variant is published per RID with --worker-aot (output:
# artifacts/publish/<rid>/WorkerAot). AOT needs the RID's native toolchain on the publishing machine
# (macOS: Xcode CLT; Windows: VS C++ workload; Linux: clang + zlib dev) - publishing another OS's
# RID as AOT is not supported by .NET, so only run --worker-aot for the machine's own RID.
#
# Usage:
#   scripts/publish.sh [--worker-aot] [rid ...]   # default RIDs: win-x64 linux-x64 osx-arm64
#
# Output (git-ignored): artifacts/publish/<rid>/{Server,Worker,WorkerSingleFile[,WorkerAot]}
#
# The publish runs with the repo's TreatWarningsAsErrors, so any warning - including trim/AOT and
# NuGet audit - fails the matrix.
#
# Service packaging per OS (install/uninstall):
#   macOS  - deploy/macos/install-launchd.sh + deploy/macos/com.zenkins.server.plist
#   Linux  - deploy/systemd/zenkins-server.service (install commands in the file header)
#   Windows- deploy/windows/install-server-service.ps1
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_ROOT="$REPO_ROOT/artifacts/publish"

WORKER_AOT=false
if [ "${1:-}" = "--worker-aot" ]; then
  WORKER_AOT=true
  shift
fi

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

  echo "== $rid: Worker (self-contained folder; JIT - PublishAot is off explicitly, see I-004) =="
  dotnet publish "$REPO_ROOT/src/Worker/Zenkins.Worker.csproj" \
    -c Release -r "$rid" --self-contained true -p:PublishAot=false \
    -o "$OUT_ROOT/$rid/Worker"

  echo "== $rid: Worker (single-file variant; JIT) =="
  dotnet publish "$REPO_ROOT/src/Worker/Zenkins.Worker.csproj" \
    -c Release -r "$rid" --self-contained true -p:PublishAot=false \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT_ROOT/$rid/WorkerSingleFile"

  if [ "$WORKER_AOT" = true ]; then
    echo "== $rid: Worker (NativeAOT variant; needs this machine's native toolchain) =="
    dotnet publish "$REPO_ROOT/src/Worker/Zenkins.Worker.csproj" \
      -c Release -r "$rid" \
      -o "$OUT_ROOT/$rid/WorkerAot"
  fi
done

echo "Publish matrix complete: $OUT_ROOT"
