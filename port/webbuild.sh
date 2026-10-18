#!/usr/bin/env bash
# Builds the WebGL player into webbuild/ without opening the editor: apply.sh, then a batchmode build with the
# patched Unity copy in tools/unity-2017.4.27f1 (README, Building). Unity must be closed. A modded development build
# takes about two hours.
#   bash port/webbuild.sh [--mods] [--release] [--no-apply]
# Log: port/build/webbuild.log (Unity's own log of the build). Exit code 0 only if the loader manifest was rewritten.
set -euo pipefail

PORT="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$PORT")"
MODS=""
METHOD=EtgWebBuild.BuildDevelopment
APPLY=1
for arg in "$@"; do
    case "$arg" in
        --mods) MODS=--mods ;;
        --release) METHOD=EtgWebBuild.BuildRelease ;;
        --no-apply) APPLY=0 ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

if tasklist //FI "IMAGENAME eq Unity.exe" //NH 2>/dev/null | grep -qi "^Unity.exe"; then
    echo "Unity is running - close it first." >&2
    exit 1
fi

if [ "$APPLY" = 1 ]; then
    bash "$PORT/apply.sh" $MODS
fi

UNITY="$ROOT/tools/unity-2017.4.27f1/Editor/Unity.exe"
PROJECT="$(cygpath -w "$ROOT/ripped/ExportedProject")"
MANIFEST="$ROOT/webbuild/Build/webbuild.json"
mkdir -p "$PORT/build"
LOG="$PORT/build/webbuild.log"
LOG_WIN="$(cygpath -w "$LOG")"
STAMP="$PORT/build/webbuild.started"
touch "$STAMP"

echo "== $METHOD started $(date '+%F %T'), log: $LOG"
status=0
"$UNITY" -batchmode -quit -projectPath "$PROJECT" -executeMethod "$METHOD" -logFile "$LOG_WIN" || status=$?
echo "== Unity exited with $status at $(date '+%F %T')"

if [ -f "$MANIFEST" ] && [ "$MANIFEST" -nt "$STAMP" ] && grep -q "\[EtG Web\] WebGL build written to" "$LOG"; then
    echo "== Build OK: $(dirname "$MANIFEST")"
    ls -la "$ROOT/webbuild/Build"
    exit 0
fi
echo "== Build FAILED (webbuild/ still has the previous build). Errors from the log:" >&2
grep -n -i -E "error|exception|failed" "$LOG" | tail -40 >&2 || true
exit 1
