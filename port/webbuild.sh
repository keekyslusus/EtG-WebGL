#!/usr/bin/env bash
# Builds the WebGL player into webbuild6/ without opening the editor: apply.sh, then a batchmode build with Unity 6.6
# (webbuild/ keeps the Unity 2017.4 build while the port moves, port/tasks/unity6-migration.md). Unity must be closed.
#   bash port/webbuild.sh [--mods] [--release] [--no-apply]
# Log: port/build/webbuild.log (Unity's own log of the build). Exit code 0 only if the loader was rewritten.
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

if bash "$PORT/unity_running.sh"; then
    echo "Unity is running - close it first." >&2
    exit 1
fi

if [ "$APPLY" = 1 ]; then
    bash "$PORT/apply.sh" $MODS
fi

UNITY="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.6.5f1/Editor/Unity.exe}"
PROJECT="$(cygpath -w "$ROOT/ripped/Unity6Project")"
MANIFEST="$ROOT/webbuild6/Build/webbuild6.loader.js"
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
    ls -la "$ROOT/webbuild6/Build"
    exit 0
fi
echo "== Build FAILED (webbuild6/ still has the previous build). Errors from the log:" >&2
grep -n -i -E "error|exception|failed" "$LOG" | tail -40 >&2 || true
exit 1
